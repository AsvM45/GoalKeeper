using Microsoft.Extensions.Caching.Memory;
using ServiceEngine.AI;
using ServiceEngine.IPC;

namespace ServiceEngine.Core;

/// <summary>
/// Central decision engine. Evaluates every foreground window or process
/// launch against the current rules and emits block/friction/allow decisions.
///
/// Performance: IMemoryCache reduces DB reads from 3+ per event to near-zero
/// for repeated apps (60s sliding expiry for category lookups; 5s for
/// nuclear/downtime flags). Cache is optional so tests can pass null.
/// </summary>
public sealed class StateManager
{
    private readonly ScreenTimeLogger _db;
    private readonly AIClient _ai;
    private readonly ILogger<StateManager> _log;
    private readonly PipeServer _pipe;
    private readonly IMemoryCache? _cache;

    // Active per-app session timers (auto-close after allowed session)
    private readonly Dictionary<string, System.Timers.Timer> _sessionTimers = new();
    private readonly object _timerLock = new();

    // Cache key prefixes
    private const string CatDomainPrefix  = "cat_domain:";
    private const string CatTitlePrefix   = "cat_title:";
    private const string CatAppPrefix     = "cat_app:";
    private const string StateNuclear     = "state:nuclear";
    private const string StateDowntime    = "state:downtime";

    private static readonly MemoryCacheEntryOptions _catOptions = new MemoryCacheEntryOptions()
        .SetSlidingExpiration(TimeSpan.FromSeconds(60));

    private static readonly MemoryCacheEntryOptions _stateOptions = new MemoryCacheEntryOptions()
        .SetAbsoluteExpiration(TimeSpan.FromSeconds(5));

    public StateManager(
        ScreenTimeLogger db,
        AIClient ai,
        ILogger<StateManager> log,
        PipeServer pipe,
        IMemoryCache? cache = null)
    {
        _db = db;
        _ai = ai;
        _log = log;
        _pipe = pipe;
        _cache = cache;
    }

    /// <summary>
    /// Called by WindowWatcher / ProcessWatcher when a distraction is detected.
    /// Returns the enforcement decision synchronously (blocking).
    /// </summary>
    public async Task<EnforcementDecision> EvaluateAsync(string appName, string? windowTitle, string? domain)
    {
        _log.LogInformation("Evaluating {App} / {Title}", appName, windowTitle);

        // 0. Emergency stop? — Runtime kill-switch, always allow everything.
        if (SecurityEnforcer.IsEmergencyStopActive())
            return new EnforcementDecision(EnforcementAction.Allow, "Emergency stop file active");

        // 1. Nuclear Mode?
        if (await IsNuclearActiveAsync())
        {
            var mode = await _db.GetStateAsync("ActiveMode") ?? "nuclear_strict";
            return new EnforcementDecision(EnforcementAction.Kill, $"Nuclear mode ({mode}) active");
        }

        // 2. Downtime / Bedtime?
        if (await IsDowntimeAsync())
            return new EnforcementDecision(EnforcementAction.Kill, "Downtime is active");

        // Resolve category (cache-first)
        string? category = null;
        if (!string.IsNullOrEmpty(domain))
            category ??= await GetCachedCategoryForDomainAsync(domain);
        if (!string.IsNullOrEmpty(windowTitle))
            category ??= await GetCachedCategoryForTitleAsync(windowTitle);
        category ??= await GetCachedCategoryForAppAsync(appName);

        // Whitelist (productive category) → always allow
        if (category == "whitelist" || category == "productive")
            return new EnforcementDecision(EnforcementAction.Allow, "Whitelisted");

        // Explicit blacklist → kill immediately
        if (category == "blacklist")
            return new EnforcementDecision(EnforcementAction.Kill, "Explicitly blocked");

        // 3. AI Smart Block (if enabled and category is unknown or distracting)
        if (await IsAIEnabledAsync() && (category == null || category == "distracting"))
        {
            var cacheKey = domain ?? appName;
            var cached = await _db.GetAICacheAsync(cacheKey);

            string aiJudgment;
            if (cached?.UserOverride != null)
            {
                aiJudgment = cached.UserOverride;
            }
            else if (cached != null)
            {
                aiJudgment = cached.Judgment;
            }
            else
            {
                var goals = await GetUserGoalsAsync();
                var result = await _ai.ClassifyAsync(domain ?? appName, windowTitle, appName, goals);
                if (result != null)
                {
                    await _db.UpsertAICacheAsync(cacheKey, result.Judgment, result.Confidence,
                        result.Reason, result.Category);
                    aiJudgment = result.Judgment;
                    category ??= result.Category;
                }
                else
                {
                    aiJudgment = "allow"; // Fail open on AI error
                }
            }

            if (aiJudgment == "block")
                return new EnforcementDecision(EnforcementAction.Kill, "AI Smart Block");

            if (aiJudgment == "allow" && category != "distracting")
                return new EnforcementDecision(EnforcementAction.Allow, "AI approved");
        }

        // If no category matched, treat as neutral → allow with no friction
        if (category == null || category == "neutral")
            return new EnforcementDecision(EnforcementAction.Allow, "No rule matched");

        // 4. Check budget time
        var budget = await _db.GetBudgetAsync(category);
        if (budget != null)
        {
            // An already-allowed session must not be interrupted mid-session.
            // Evaluate the constraint only when deciding whether to START a new session:
            //   ActiveSession = Allowed  iff  U_sec < A_sec  ∧  U_launch < M_launch
            //                  Blocked   otherwise
            lock (_timerLock)
            {
                if (_sessionTimers.ContainsKey(appName))
                    return new EnforcementDecision(EnforcementAction.Allow, "Session active");
            }

            bool timeOk    = budget.UsedSeconds  < budget.AllowedSeconds;
            bool launchOk  = budget.MaxLaunches  < 0 || budget.UsedLaunches < budget.MaxLaunches;

            if (!timeOk)
                return new EnforcementDecision(EnforcementAction.Kill, $"Time budget exhausted for {category}");

            if (!launchOk)
                return new EnforcementDecision(EnforcementAction.Kill, $"Launch limit reached for {category}");

            // 5. Budget intact → show friction before granting the session
            return new EnforcementDecision(
                EnforcementAction.Friction,
                $"Friction required for {category}",
                category,
                budget.FrictionSeconds,
                budget.SessionMinutes);
        }

        return new EnforcementDecision(EnforcementAction.Allow, "No budget configured");
    }

    /// <summary>
    /// Called when UI sends ALLOW_SESSION after friction overlay.
    /// Starts the auto-close session timer.
    /// </summary>
    public async Task OnSessionAllowedAsync(string appName, string category)
    {
        await _db.IncrementUsedLaunchesAsync(category);

        var budget = await _db.GetBudgetAsync(category);
        int sessionMs = (budget?.SessionMinutes ?? 5) * 60 * 1000;

        lock (_timerLock)
        {
            if (_sessionTimers.TryGetValue(appName, out var existing))
            {
                existing.Stop();
                existing.Dispose();
            }

            var timer = new System.Timers.Timer(sessionMs);
            timer.AutoReset = false;
            timer.Elapsed += async (_, _) => await OnSessionExpiredAsync(appName, category);
            timer.Start();
            _sessionTimers[appName] = timer;
        }
    }

    private async Task OnSessionExpiredAsync(string appName, string category)
    {
        _log.LogInformation("Session expired for {App}", appName);
        lock (_timerLock) { _sessionTimers.Remove(appName); }

        // Notify UI then kill the app
        await _pipe.BroadcastAsync(PipeMessage.SessionExpired(appName));

        // Brief delay to let UI show the notification before hard kill
        await Task.Delay(1500);
        await _db.AddUsedSecondsAsync(category, (await _db.GetBudgetAsync(category))?.SessionMinutes * 60 ?? 300);
        KillProcess(appName);
    }

    public void CancelSessionTimer(string appName)
    {
        lock (_timerLock)
        {
            if (_sessionTimers.TryGetValue(appName, out var t))
            {
                t.Stop(); t.Dispose();
                _sessionTimers.Remove(appName);
            }
        }
    }

    // ── Cache-first category helpers ──────────────────────────────────────────

    private async Task<string?> GetCachedCategoryForDomainAsync(string domain)
    {
        if (_cache == null) return await _db.GetCategoryForDomainAsync(domain);
        var key = CatDomainPrefix + domain.ToLowerInvariant();
        if (_cache.TryGetValue(key, out string? cached)) return cached;
        var result = await _db.GetCategoryForDomainAsync(domain);
        _cache.Set(key, result, _catOptions);
        return result;
    }

    private async Task<string?> GetCachedCategoryForTitleAsync(string title)
    {
        if (_cache == null) return await _db.GetCategoryForTitleAsync(title);
        var key = CatTitlePrefix + title;
        if (_cache.TryGetValue(key, out string? cached)) return cached;
        var result = await _db.GetCategoryForTitleAsync(title);
        _cache.Set(key, result, _catOptions);
        return result;
    }

    private async Task<string?> GetCachedCategoryForAppAsync(string app)
    {
        if (_cache == null) return await _db.GetCategoryForAppAsync(app);
        var key = CatAppPrefix + app.ToLowerInvariant();
        if (_cache.TryGetValue(key, out string? cached)) return cached;
        var result = await _db.GetCategoryForAppAsync(app);
        _cache.Set(key, result, _catOptions);
        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public async Task<bool> IsNuclearActiveAsync()
    {
        // Use short cache to avoid DB reads on every window event
        if (_cache != null && _cache.TryGetValue(StateNuclear, out bool cachedNuclear))
            return cachedNuclear;

        var mode = await _db.GetStateAsync("ActiveMode");
        if (string.IsNullOrEmpty(mode) || mode == "none")
        {
            _cache?.Set(StateNuclear, false, _stateOptions);
            return false;
        }

        var epochStr = await _db.GetStateAsync("NuclearEndTimeEpoch");
        if (!long.TryParse(epochStr, out long epoch) || epoch == 0)
        {
            _cache?.Set(StateNuclear, false, _stateOptions);
            return false;
        }

        var endTime = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
        if (DateTime.Now >= endTime)
        {
            // Expired – reset
            await _db.SetStateAsync("ActiveMode", "none");
            await _db.SetStateAsync("NuclearEndTimeEpoch", "0");
            _cache?.Remove(StateNuclear);
            return false;
        }

        _cache?.Set(StateNuclear, true, _stateOptions);
        return true;
    }

    private async Task<bool> IsDowntimeAsync()
    {
        if (_cache != null && _cache.TryGetValue(StateDowntime, out bool cachedDowntime))
            return cachedDowntime;

        var enabled = await _db.GetStateAsync("DowntimeEnabled");
        if (enabled != "1")
        {
            _cache?.Set(StateDowntime, false, _stateOptions);
            return false;
        }

        var startStr = await _db.GetStateAsync("DowntimeStart") ?? "22:00";
        var endStr   = await _db.GetStateAsync("DowntimeEnd")   ?? "07:00";

        if (!TimeOnly.TryParse(startStr, out var start) || !TimeOnly.TryParse(endStr, out var end))
        {
            _cache?.Set(StateDowntime, false, _stateOptions);
            return false;
        }

        var now = TimeOnly.FromDateTime(DateTime.Now);
        bool active = start < end
            ? now >= start && now < end
            : now >= start || now < end; // Overnight window (e.g. 22:00 – 07:00)

        _cache?.Set(StateDowntime, active, _stateOptions);
        return active;
    }

    private async Task<bool> IsAIEnabledAsync()
        => (await _db.GetStateAsync("AIEnabled")) == "1";

    private async Task<List<string>> GetUserGoalsAsync()
    {
        var json = await _db.GetStateAsync("UserGoals") ?? "[]";
        return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new();
    }

    private static void KillProcess(string appName)
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(
                Path.GetFileNameWithoutExtension(appName));
            foreach (var p in procs)
            {
                try { p.Kill(); } catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }
}

// ── Result types ──────────────────────────────────────────────────────────────

public enum EnforcementAction { Allow, Friction, Kill }

public record EnforcementDecision(
    EnforcementAction Action,
    string Reason,
    string? Category = null,
    int FrictionSeconds = 20,
    int SessionMinutes = 5);

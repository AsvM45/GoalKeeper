using ServiceEngine.AI;
using ServiceEngine.IPC;

namespace ServiceEngine.Core;

/// <summary>
/// Central decision engine. Evaluates every foreground window or process
/// launch against the current rules and emits block/friction/allow decisions.
/// </summary>
public sealed class StateManager
{
    private readonly ScreenTimeLogger _db;
    private readonly AIClient _ai;
    private readonly ILogger<StateManager> _log;
    private readonly PipeServer _pipe;

    // Active per-app session timers (auto-close after allowed session)
    private readonly Dictionary<string, System.Timers.Timer> _sessionTimers = new();
    private readonly object _timerLock = new();

    public StateManager(
        ScreenTimeLogger db,
        AIClient ai,
        ILogger<StateManager> log,
        PipeServer pipe)
    {
        _db = db;
        _ai = ai;
        _log = log;
        _pipe = pipe;
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

        // Resolve category
        string? category = null;
        if (!string.IsNullOrEmpty(domain))
            category ??= await _db.GetCategoryForDomainAsync(domain);
        if (!string.IsNullOrEmpty(windowTitle))
            category ??= await _db.GetCategoryForTitleAsync(windowTitle);
        category ??= await _db.GetCategoryForAppAsync(appName);

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
            if (budget.UsedSeconds >= budget.AllowedSeconds)
                return new EnforcementDecision(EnforcementAction.Kill, $"Time budget exhausted for {category}");

            if (budget.MaxLaunches >= 0 && budget.UsedLaunches >= budget.MaxLaunches)
                return new EnforcementDecision(EnforcementAction.Kill, $"Launch limit reached for {category}");

            lock (_timerLock)
            {
                if (_sessionTimers.ContainsKey(appName))
                    return new EnforcementDecision(EnforcementAction.Allow, "Session active");
            }

            // 5. Trigger friction overlay
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    public async Task<bool> IsNuclearActiveAsync()
    {
        var mode = await _db.GetStateAsync("ActiveMode");
        if (string.IsNullOrEmpty(mode) || mode == "none") return false;

        var epochStr = await _db.GetStateAsync("NuclearEndTimeEpoch");
        if (!long.TryParse(epochStr, out long epoch)) return false;
        if (epoch == 0) return false;

        var endTime = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
        if (DateTime.Now >= endTime)
        {
            // Expired – reset
            await _db.SetStateAsync("ActiveMode", "none");
            await _db.SetStateAsync("NuclearEndTimeEpoch", "0");
            return false;
        }
        return true;
    }

    private async Task<bool> IsDowntimeAsync()
    {
        var enabled = await _db.GetStateAsync("DowntimeEnabled");
        if (enabled != "1") return false;

        var startStr = await _db.GetStateAsync("DowntimeStart") ?? "22:00";
        var endStr   = await _db.GetStateAsync("DowntimeEnd")   ?? "07:00";

        if (!TimeOnly.TryParse(startStr, out var start) || !TimeOnly.TryParse(endStr, out var end))
            return false;

        var now = TimeOnly.FromDateTime(DateTime.Now);
        return start < end
            ? now >= start && now < end
            : now >= start || now < end; // Overnight window (e.g. 22:00 – 07:00)
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

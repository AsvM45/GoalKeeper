using Microsoft.Data.Sqlite;

namespace ServiceEngine.Core;

/// <summary>
/// Thread-safe SQLite writer. Applies the schema on first run,
/// handles daily budget resets, and exposes query helpers used by StateManager.
/// </summary>
public sealed class ScreenTimeLogger : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // In-memory write buffer for ScreenTimeLog rows
    private readonly List<ScreenTimeEntry> _buffer = new();
    private readonly System.Timers.Timer _flushTimer;

    public ScreenTimeLogger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var dir = Path.Combine(appData, "GoalKeeper");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "metrics.sqlite");
        _connectionString = $"Data Source={_dbPath};";

        _flushTimer = new System.Timers.Timer(5000);
        _flushTimer.Elapsed += async (_, _) => await FlushBufferAsync();
        _flushTimer.AutoReset = true;
        _flushTimer.Start();
    }

    public string DbPath => _dbPath;

    // ── Initialization ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "..", "Database", "schema.sql");
        // Fall back to embedded schema string if file not found (post-install)
        string schema = File.Exists(schemaPath)
            ? await File.ReadAllTextAsync(schemaPath)
            : EmbeddedSchema.Sql;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(schema, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Screen Time Logging ───────────────────────────────────────────────────

    public void LogWindow(string appName, string? windowTitle, string? category, int durationSeconds, string sessionId)
    {
        lock (_buffer)
        {
            _buffer.Add(new ScreenTimeEntry(appName, windowTitle, category, durationSeconds, sessionId));
        }
    }

    public async Task FlushBufferAsync()
    {
        List<ScreenTimeEntry> snapshot;
        lock (_buffer)
        {
            if (_buffer.Count == 0) return;
            snapshot = new List<ScreenTimeEntry>(_buffer);
            _buffer.Clear();
        }

        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            foreach (var e in snapshot)
            {
                await using var cmd = new SqliteCommand(
                    @"INSERT INTO ScreenTimeLog (AppName, WindowTitle, Category, DurationSeconds, SessionId)
                      VALUES (@app, @title, @cat, @dur, @sid)", conn);
                cmd.Parameters.AddWithValue("@app", e.AppName);
                cmd.Parameters.AddWithValue("@title", e.WindowTitle ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@cat", e.Category ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@dur", e.DurationSeconds);
                cmd.Parameters.AddWithValue("@sid", e.SessionId);
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Pickup Logging ────────────────────────────────────────────────────────

    public async Task LogPickupAsync(string? fromApp, string toApp, string? toCategory)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                @"INSERT INTO PickupLog (FromApp, ToApp, ToCategory)
                  VALUES (@from, @to, @cat)", conn);
            cmd.Parameters.AddWithValue("@from", fromApp ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@to", toApp);
            cmd.Parameters.AddWithValue("@cat", toCategory ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── SystemState ───────────────────────────────────────────────────────────

    public async Task<string?> GetStateAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                "SELECT Value FROM SystemState WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            return (string?)await cmd.ExecuteScalarAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetStateAsync(string key, string value)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                @"INSERT INTO SystemState (Key, Value) VALUES (@key, @val)
                  ON CONFLICT(Key) DO UPDATE SET Value = @val", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", value);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Budgets ───────────────────────────────────────────────────────────────

    public async Task<BudgetRecord?> GetBudgetAsync(string category)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            // Check and apply daily reset first
            await ResetBudgetIfStaleAsync(conn, category);

            await using var cmd = new SqliteCommand(
                @"SELECT Id, Category, AllowedSeconds, UsedSeconds, MaxLaunches, UsedLaunches,
                         SessionMinutes, FrictionSeconds
                  FROM Budgets WHERE Category = @cat", conn);
            cmd.Parameters.AddWithValue("@cat", category);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return new BudgetRecord(
                reader.GetInt32(0), reader.GetString(1),
                reader.GetInt32(2), reader.GetInt32(3),
                reader.GetInt32(4), reader.GetInt32(5),
                reader.GetInt32(6), reader.GetInt32(7));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task IncrementUsedLaunchesAsync(string category)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                "UPDATE Budgets SET UsedLaunches = UsedLaunches + 1 WHERE Category = @cat", conn);
            cmd.Parameters.AddWithValue("@cat", category);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddUsedSecondsAsync(string category, int seconds)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                "UPDATE Budgets SET UsedSeconds = UsedSeconds + @s WHERE Category = @cat", conn);
            cmd.Parameters.AddWithValue("@s", seconds);
            cmd.Parameters.AddWithValue("@cat", category);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task ResetBudgetIfStaleAsync(SqliteConnection conn, string category)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        await using var checkCmd = new SqliteCommand(
            "SELECT LastResetDate FROM Budgets WHERE Category = @cat", conn);
        checkCmd.Parameters.AddWithValue("@cat", category);
        var lastReset = (string?)await checkCmd.ExecuteScalarAsync();

        if (lastReset != today)
        {
            await using var resetCmd = new SqliteCommand(
                @"UPDATE Budgets SET UsedSeconds = 0, UsedLaunches = 0, LastResetDate = @today
                  WHERE Category = @cat", conn);
            resetCmd.Parameters.AddWithValue("@today", today);
            resetCmd.Parameters.AddWithValue("@cat", category);
            await resetCmd.ExecuteNonQueryAsync();
        }
    }

    // ── Category Rule CRUD ────────────────────────────────────────────────────

    public async Task AddCategoryRuleAsync(string pattern, string category, string ruleType)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                "INSERT INTO CategoryRules (Pattern, Category, RuleType) VALUES (@p, @c, @t)", conn);
            cmd.Parameters.AddWithValue("@p", pattern);
            cmd.Parameters.AddWithValue("@c", category);
            cmd.Parameters.AddWithValue("@t", ruleType);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteCategoryRuleAsync(int id)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                "DELETE FROM CategoryRules WHERE Id = @id", conn);
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    // ── Budget CRUD ───────────────────────────────────────────────────────────

    public async Task UpsertBudgetAsync(string category, int allowedSecs, int maxLaunches,
                                         int sessionMinutes, int frictionSecs)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(@"
                INSERT INTO Budgets (Category, AllowedSeconds, MaxLaunches, SessionMinutes, FrictionSeconds)
                VALUES (@cat, @allowed, @launches, @session, @friction)
                ON CONFLICT(Category) DO UPDATE SET
                    AllowedSeconds  = @allowed,
                    MaxLaunches     = @launches,
                    SessionMinutes  = @session,
                    FrictionSeconds = @friction", conn);
            cmd.Parameters.AddWithValue("@cat",      category);
            cmd.Parameters.AddWithValue("@allowed",  allowedSecs);
            cmd.Parameters.AddWithValue("@launches", maxLaunches);
            cmd.Parameters.AddWithValue("@session",  sessionMinutes);
            cmd.Parameters.AddWithValue("@friction", frictionSecs);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteBudgetAsync(string category)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                "DELETE FROM Budgets WHERE Category = @cat", conn);
            cmd.Parameters.AddWithValue("@cat", category);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    // ── Category Rules (read) ─────────────────────────────────────────────────

    public async Task<string?> GetCategoryForAppAsync(string appName)
        => await GetCategoryByPatternAsync(appName, "app");

    public async Task<string?> GetCategoryForDomainAsync(string domain)
        => await GetCategoryByPatternAsync(domain, "domain");

    public async Task<string?> GetCategoryForTitleAsync(string windowTitle)
        => await GetCategoryByPatternAsync(windowTitle, "window_title");

    private async Task<string?> GetCategoryByPatternAsync(string value, string ruleType)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                "SELECT Pattern, Category FROM CategoryRules WHERE RuleType = @type", conn);
            cmd.Parameters.AddWithValue("@type", ruleType);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var pattern = reader.GetString(0);
                var category = reader.GetString(1);
                if (MatchesWildcard(value, pattern))
                    return category;
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool MatchesWildcard(string input, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.StartsWith("*") && pattern.EndsWith("*"))
            return input.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase);
        if (pattern.StartsWith("*"))
            return input.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase);
        if (pattern.EndsWith("*"))
            return input.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
        return input.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    // ── AI Cache ──────────────────────────────────────────────────────────────

    public async Task<AICacheRecord?> GetAICacheAsync(string urlOrApp)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                @"SELECT Judgment, Confidence, Reason, Category, UserOverride
                  FROM AICache
                  WHERE UrlOrApp = @key
                    AND (UserOverride IS NOT NULL
                         OR CachedAt > datetime('now', '-24 hours'))", conn);
            cmd.Parameters.AddWithValue("@key", urlOrApp);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return new AICacheRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertAICacheAsync(string urlOrApp, string judgment, double confidence, string? reason, string? category)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                @"INSERT INTO AICache (UrlOrApp, Judgment, Confidence, Reason, Category)
                  VALUES (@key, @j, @c, @r, @cat)
                  ON CONFLICT(UrlOrApp) DO UPDATE SET
                      Judgment = @j, Confidence = @c, Reason = @r,
                      Category = @cat, CachedAt = datetime('now'), UserOverride = NULL", conn);
            cmd.Parameters.AddWithValue("@key", urlOrApp);
            cmd.Parameters.AddWithValue("@j", judgment);
            cmd.Parameters.AddWithValue("@c", confidence);
            cmd.Parameters.AddWithValue("@r", reason ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@cat", category ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Challenge Tokens ──────────────────────────────────────────────────────

    public async Task StoreTokenAsync(string token)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                "INSERT INTO ChallengeTokens (Token) VALUES (@t)", conn);
            cmd.Parameters.AddWithValue("@t", token);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> ConsumeTokenAsync(string token)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var checkCmd = new SqliteCommand(
                @"SELECT Token FROM ChallengeTokens
                  WHERE Token = @t AND UsedAt IS NULL
                    AND CreatedAt > datetime('now', '-5 minutes')", conn);
            checkCmd.Parameters.AddWithValue("@t", token);
            var found = await checkCmd.ExecuteScalarAsync();
            if (found == null) return false;

            await using var useCmd = new SqliteCommand(
                "UPDATE ChallengeTokens SET UsedAt = datetime('now') WHERE Token = @t", conn);
            useCmd.Parameters.AddWithValue("@t", token);
            await useCmd.ExecuteNonQueryAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        _lock.Dispose();
    }
}

// ── Value records ─────────────────────────────────────────────────────────────

public record ScreenTimeEntry(
    string AppName, string? WindowTitle, string? Category,
    int DurationSeconds, string SessionId);

public record BudgetRecord(
    int Id, string Category,
    int AllowedSeconds, int UsedSeconds,
    int MaxLaunches, int UsedLaunches,
    int SessionMinutes, int FrictionSeconds);

public record AICacheRecord(
    string Judgment, double? Confidence,
    string? Reason, string? Category, string? UserOverride);

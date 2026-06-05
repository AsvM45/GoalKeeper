using System.IO;
using Microsoft.Data.Sqlite;

namespace ConfigUI.Services;

/// <summary>
/// Read/write access to the GoalKeeper SQLite database from the ConfigUI process.
/// The schema is already managed by ServiceEngine; we just query and update config here.
/// WAL journal mode (set by the schema) allows safe concurrent access.
/// </summary>
public sealed class AppDatabase
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GoalKeeper", "metrics.sqlite");

    private static string ConnStr => $"Data Source={DbPath};Default Timeout=5;";

    // ── Dashboard stats ───────────────────────────────────────────────────────

    public async Task<(int ProductiveSecs, int DistractingSecs)> GetTodayTimeAsync()
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Category, COALESCE(SUM(DurationSeconds), 0)
            FROM ScreenTimeLog
            WHERE Date(Timestamp) = Date('now', 'localtime')
              AND Category IN ('productive', 'distracting')
            GROUP BY Category";
        int prod = 0, dist = 0;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            if (r.GetString(0) == "productive") prod = r.GetInt32(1);
            else dist = r.GetInt32(1);
        }
        return (prod, dist);
    }

    public async Task<int> GetTodayPickupsAsync()
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM PickupLog WHERE Date(Timestamp) = Date('now', 'localtime')";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<List<(string Date, int ProductiveSecs, int DistractingSecs)>> GetWeeklyTimeAsync()
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Date(Timestamp, 'localtime') as Dt, Category, SUM(DurationSeconds)
            FROM ScreenTimeLog
            WHERE Timestamp >= datetime('now', '-7 days', 'localtime')
              AND Category IN ('productive', 'distracting')
            GROUP BY Dt, Category
            ORDER BY Dt ASC";
        
        var dict = new Dictionary<string, (int Prod, int Dist)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var dt = r.GetString(0);
            var cat = r.GetString(1);
            var secs = r.GetInt32(2);
            
            if (!dict.ContainsKey(dt)) dict[dt] = (0, 0);
            
            var curr = dict[dt];
            if (cat == "productive") dict[dt] = (curr.Prod + secs, curr.Dist);
            else dict[dt] = (curr.Prod, curr.Dist + secs);
        }
        
        return dict.Select(kvp => (kvp.Key, kvp.Value.Prod, kvp.Value.Dist)).ToList();
    }

    public async Task<List<(string AppName, int DurationSeconds)>> GetTopAppsTodayAsync(int limit)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT AppName, SUM(DurationSeconds) as TotalSecs
            FROM ScreenTimeLog
            WHERE Date(Timestamp) = Date('now', 'localtime')
            GROUP BY AppName
            ORDER BY TotalSecs DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);
        
        var list = new List<(string, int)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add((r.GetString(0), r.GetInt32(1)));
        }
        return list;
    }

    /// <summary>Hourly app activity from ScreenTimeLog (Screen Time: App &amp; Website Activity).</summary>
    public async Task<List<(int Hour, int ProductiveSecs, int DistractingSecs, int NeutralSecs)>> GetTodayHourlyActivityAsync()
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT CAST(strftime('%H', Timestamp, 'localtime') AS INTEGER) AS Hr,
                   Category, COALESCE(SUM(DurationSeconds), 0)
            FROM ScreenTimeLog
            WHERE Date(Timestamp) = Date('now', 'localtime')
            GROUP BY Hr, Category
            ORDER BY Hr";
        var buckets = Enumerable.Range(0, 24)
            .ToDictionary(h => h, _ => (Prod: 0, Dist: 0, Neut: 0));
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            int hr = r.GetInt32(0);
            var cat = r.GetString(1);
            int secs = r.GetInt32(2);
            if (!buckets.ContainsKey(hr)) continue;
            var curr = buckets[hr];
            buckets[hr] = cat switch
            {
                "productive"  => (curr.Prod + secs, curr.Dist, curr.Neut),
                "distracting" => (curr.Prod, curr.Dist + secs, curr.Neut),
                _             => (curr.Prod, curr.Dist, curr.Neut + secs)
            };
        }
        return buckets.Select(kvp => (kvp.Key, kvp.Value.Prod, kvp.Value.Dist, kvp.Value.Neut)).ToList();
    }

    /// <summary>Hourly context-switch frequency from PickupLog (Screen Time: Notifications proxy).</summary>
    public async Task<List<(int Hour, int Count)>> GetTodayPickupFrequencyAsync()
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT CAST(strftime('%H', Timestamp, 'localtime') AS INTEGER) AS Hr,
                   COUNT(*) AS Cnt
            FROM PickupLog
            WHERE Date(Timestamp) = Date('now', 'localtime')
            GROUP BY Hr
            ORDER BY Hr";
        var buckets = Enumerable.Range(0, 24).ToDictionary(h => h, _ => 0);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            buckets[r.GetInt32(0)] = r.GetInt32(1);
        return buckets.Select(kvp => (kvp.Key, kvp.Value)).ToList();
    }

    /// <summary>First-look triggers: distracting apps opened after productive/neutral context (Screen Time: Pickups).</summary>
    public async Task<List<(string ToApp, int Count)>> GetFirstLookTriggersAsync(int days = 7)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ToApp, COUNT(*) AS Cnt
            FROM PickupLog
            WHERE Timestamp >= datetime('now', @days, 'localtime')
              AND ToApp IS NOT NULL
            GROUP BY ToApp
            ORDER BY Cnt DESC
            LIMIT 12";
        cmd.Parameters.AddWithValue("@days", $"-{days} days");
        var list = new List<(string, int)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add((r.GetString(0), r.GetInt32(1)));
        return list;
    }

    /// <summary>Weekly stacked activity by category from ScreenTimeLog.</summary>
    public async Task<List<(string Date, int ProductiveSecs, int DistractingSecs, int NeutralSecs)>> GetWeeklyActivityAsync()
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Date(Timestamp, 'localtime') AS Dt, Category, SUM(DurationSeconds)
            FROM ScreenTimeLog
            WHERE Timestamp >= datetime('now', '-7 days', 'localtime')
            GROUP BY Dt, Category
            ORDER BY Dt ASC";
        var dict = new Dictionary<string, (int Prod, int Dist, int Neut)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var dt = r.GetString(0);
            var cat = r.GetString(1);
            var secs = r.GetInt32(2);
            if (!dict.ContainsKey(dt)) dict[dt] = (0, 0, 0);
            var curr = dict[dt];
            dict[dt] = cat switch
            {
                "productive"  => (curr.Prod + secs, curr.Dist, curr.Neut),
                "distracting" => (curr.Prod, curr.Dist + secs, curr.Neut),
                _             => (curr.Prod, curr.Dist, curr.Neut + secs)
            };
        }
        return dict.Select(kvp => (kvp.Key, kvp.Value.Prod, kvp.Value.Dist, kvp.Value.Neut)).ToList();
    }

    // ── System state ──────────────────────────────────────────────────────────

    public async Task<string?> GetStateAsync(string key)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM SystemState WHERE Key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    public async Task SetStateAsync(string key, string value)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO SystemState (Key, Value) VALUES (@key, @val)
            ON CONFLICT(Key) DO UPDATE SET Value = @val";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@val", value);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Category rules ────────────────────────────────────────────────────────

    public async Task<List<CategoryRuleRecord>> GetCategoryRulesAsync()
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Pattern, Category, RuleType FROM CategoryRules ORDER BY Category, RuleType";
        var list = new List<CategoryRuleRecord>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public async Task AddCategoryRuleAsync(string pattern, string category, string ruleType)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO CategoryRules (Pattern, Category, RuleType) VALUES (@p, @c, @t)";
        cmd.Parameters.AddWithValue("@p", pattern);
        cmd.Parameters.AddWithValue("@c", category);
        cmd.Parameters.AddWithValue("@t", ruleType);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteCategoryRuleAsync(int id)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM CategoryRules WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Budgets ───────────────────────────────────────────────────────────────

    public async Task<List<BudgetEntry>> GetBudgetsAsync()
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Category, AllowedSeconds, UsedSeconds, MaxLaunches,
                   UsedLaunches, SessionMinutes, FrictionSeconds
            FROM Budgets ORDER BY Category";
        var list = new List<BudgetEntry>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new(r.GetString(0), r.GetInt32(1), r.GetInt32(2),
                         r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6)));
        return list;
    }

    public async Task UpsertBudgetAsync(string category, int allowedSecs, int maxLaunches,
                                         int sessionMinutes, int frictionSecs)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Budgets (Category, AllowedSeconds, MaxLaunches, SessionMinutes, FrictionSeconds)
            VALUES (@cat, @allowed, @launches, @session, @friction)
            ON CONFLICT(Category) DO UPDATE SET
                AllowedSeconds  = @allowed,
                MaxLaunches     = @launches,
                SessionMinutes  = @session,
                FrictionSeconds = @friction";
        cmd.Parameters.AddWithValue("@cat",      category);
        cmd.Parameters.AddWithValue("@allowed",  allowedSecs);
        cmd.Parameters.AddWithValue("@launches", maxLaunches);
        cmd.Parameters.AddWithValue("@session",  sessionMinutes);
        cmd.Parameters.AddWithValue("@friction", frictionSecs);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteBudgetAsync(string category)
    {
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Budgets WHERE Category = @cat";
        cmd.Parameters.AddWithValue("@cat", category);
        await cmd.ExecuteNonQueryAsync();
    }
}

public record CategoryRuleRecord(int Id, string Pattern, string Category, string RuleType);

public record BudgetEntry(
    string Category, int AllowedSeconds, int UsedSeconds,
    int MaxLaunches, int UsedLaunches, int SessionMinutes, int FrictionSeconds);

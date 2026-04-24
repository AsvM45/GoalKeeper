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

    private static string ConnStr => $"Data Source={DbPath};";

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

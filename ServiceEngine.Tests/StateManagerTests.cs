using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceEngine.AI;
using ServiceEngine.Core;
using Xunit;

namespace ServiceEngine.Tests;

/// <summary>
/// Tests the StateManager evaluation pipeline using a real temp-file SQLite database.
/// PipeServer is passed as null because EvaluateAsync never touches it — only
/// OnSessionExpiredAsync (timer-driven) does, and that path is never reached here.
/// AIClient points to an unreachable URL so all AI calls fail-open (return null).
/// </summary>
public class StateManagerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ScreenTimeLogger _db;
    private readonly StateManager _state;

    public StateManagerTests()
    {
        _dbPath = Path.GetTempFileName();
        _db = new ScreenTimeLogger(_dbPath);
        _db.InitializeAsync().GetAwaiter().GetResult();

        // AIClient pointed at unreachable URL → always fails open (returns null)
        var ai = new AIClient(NullLogger<AIClient>.Instance, _db);
        _state = new StateManager(_db, ai, NullLogger<StateManager>.Instance, null!);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task EvaluateAsync_NoRule_ReturnsAllow()
    {
        var decision = await _state.EvaluateAsync("Notepad.exe", "Untitled", null);
        Assert.Equal(EnforcementAction.Allow, decision.Action);
    }

    [Fact]
    public async Task EvaluateAsync_NuclearActive_ReturnsKill()
    {
        await _db.SetStateAsync("ActiveMode", "nuclear_strict");
        await _db.SetStateAsync("NuclearEndTimeEpoch",
            DateTimeOffset.Now.AddHours(1).ToUnixTimeSeconds().ToString());

        var decision = await _state.EvaluateAsync("chrome.exe", "YouTube", null);
        Assert.Equal(EnforcementAction.Kill, decision.Action);
        Assert.Contains("Nuclear", decision.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_Downtime_ReturnsKill()
    {
        await _db.SetStateAsync("DowntimeEnabled", "1");
        await _db.SetStateAsync("DowntimeStart", "00:00");
        await _db.SetStateAsync("DowntimeEnd", "23:59");

        var decision = await _state.EvaluateAsync("chrome.exe", "YouTube", null);
        Assert.Equal(EnforcementAction.Kill, decision.Action);
        Assert.Contains("Downtime", decision.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_Whitelist_ReturnsAllow()
    {
        await _db.AddCategoryRuleAsync("*code*", "whitelist", "app");

        var decision = await _state.EvaluateAsync("code.exe", "VS Code", null);
        Assert.Equal(EnforcementAction.Allow, decision.Action);
        Assert.Contains("Whitelisted", decision.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_Productive_ReturnsAllow()
    {
        await _db.AddCategoryRuleAsync("*vscode*", "productive", "app");

        var decision = await _state.EvaluateAsync("vscode.exe", "VS Code", null);
        Assert.Equal(EnforcementAction.Allow, decision.Action);
    }

    [Fact]
    public async Task EvaluateAsync_Blacklist_ReturnsKill()
    {
        await _db.AddCategoryRuleAsync("*game*", "blacklist", "app");

        var decision = await _state.EvaluateAsync("game.exe", "My Game", null);
        Assert.Equal(EnforcementAction.Kill, decision.Action);
        Assert.Contains("blocked", decision.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_BudgetExhausted_ReturnsKill()
    {
        await _db.AddCategoryRuleAsync("*reddit*", "distracting", "app");
        await _db.UpsertBudgetAsync("distracting", 3600, -1, 5, 20);
        await _db.AddUsedSecondsAsync("distracting", 3600); // exhaust it

        var decision = await _state.EvaluateAsync("reddit.exe", "Reddit", null);
        Assert.Equal(EnforcementAction.Kill, decision.Action);
        Assert.Contains("exhausted", decision.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_BudgetAvailable_ReturnsFriction()
    {
        await _db.AddCategoryRuleAsync("*twitter*", "distracting", "app");
        await _db.UpsertBudgetAsync("distracting", 3600, -1, 5, 20);
        // No time used — budget is available → friction overlay

        var decision = await _state.EvaluateAsync("twitter.exe", "Twitter", null);
        Assert.Equal(EnforcementAction.Friction, decision.Action);
    }

    [Fact]
    public async Task EvaluateAsync_LaunchLimitReached_ReturnsKill()
    {
        await _db.AddCategoryRuleAsync("*slack*", "distracting", "app");
        await _db.UpsertBudgetAsync("distracting", 3600, maxLaunches: 3, sessionMinutes: 5, frictionSecs: 20);
        // Simulate 3 launches already used
        await _db.IncrementUsedLaunchesAsync("distracting");
        await _db.IncrementUsedLaunchesAsync("distracting");
        await _db.IncrementUsedLaunchesAsync("distracting");

        var decision = await _state.EvaluateAsync("slack.exe", "Slack", null);
        Assert.Equal(EnforcementAction.Kill, decision.Action);
        Assert.Contains("Launch limit", decision.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_EmergencyStopFile_ReturnsAllow()
    {
        // Even with active nuclear + blacklist, emergency stop overrides everything
        await _db.AddCategoryRuleAsync("*blocked*", "blacklist", "app");
        await _db.SetStateAsync("ActiveMode", "nuclear_strict");
        await _db.SetStateAsync("NuclearEndTimeEpoch",
            DateTimeOffset.Now.AddHours(1).ToUnixTimeSeconds().ToString());

        const string stopPath = @"C:\goalkeeper_emergency_stop.txt";
        try
        {
            File.WriteAllText(stopPath, "stop");
            var decision = await _state.EvaluateAsync("blocked.exe", "Blocked App", null);
            Assert.Equal(EnforcementAction.Allow, decision.Action);
            Assert.Contains("Emergency", decision.Reason);
        }
        finally
        {
            if (File.Exists(stopPath)) try { File.Delete(stopPath); } catch { }
        }
    }
}

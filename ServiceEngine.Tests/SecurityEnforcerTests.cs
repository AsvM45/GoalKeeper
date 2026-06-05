using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceEngine.Core;
using Xunit;

namespace ServiceEngine.Tests;

public class SecurityEnforcerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ScreenTimeLogger _db;

    public SecurityEnforcerTests()
    {
        _dbPath = Path.GetTempFileName();
        _db = new ScreenTimeLogger(_dbPath);
        _db.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task RunDiagnosticsAsync_ReturnsSuccess_OnHealthySystem()
    {
        var enforcer = new SecurityEnforcer(_db, devMode: false, NullLogger<SecurityEnforcer>.Instance);

        var result = await enforcer.RunDiagnosticsAsync();

        Assert.True(result.Success);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void ClampNuclearDuration_CapsAt24Hours()
    {
        var clamped = SecurityEnforcer.ClampNuclearDuration(2000);

        Assert.Equal(SecurityEnforcer.MaxNuclearDurationMinutes, clamped);
    }

    [Fact]
    public void ClampNuclearDuration_DefaultsTo60_WhenNegative()
    {
        var clamped = SecurityEnforcer.ClampNuclearDuration(-5);

        Assert.Equal(60, clamped);
    }

    [Fact]
    public void ClampNuclearDuration_DefaultsTo60_WhenZero()
    {
        var clamped = SecurityEnforcer.ClampNuclearDuration(0);

        Assert.Equal(60, clamped);
    }

    [Fact]
    public void ClampNuclearDuration_PassesThrough_ValidDuration()
    {
        var clamped = SecurityEnforcer.ClampNuclearDuration(120);

        Assert.Equal(120, clamped);
    }

    [Fact]
    public async Task ArmSystemAsync_InDevMode_IsNoOp_AndDoesNotSetIsArmed()
    {
        var enforcer = new SecurityEnforcer(_db, devMode: true, NullLogger<SecurityEnforcer>.Instance);

        // Should not throw, should not set IsArmed = "1"
        await enforcer.ArmSystemAsync();

        var isArmed = await _db.GetStateAsync("IsArmed");
        Assert.NotEqual("1", isArmed);
    }

    [Fact]
    public static void IsEmergencyStopActive_ReturnsFalse_WhenFileAbsent()
    {
        // Ensure the file doesn't exist for this check
        if (File.Exists(SecurityEnforcer.EmergencyStopPath))
            return; // Skip if file happens to exist on this machine

        Assert.False(SecurityEnforcer.IsEmergencyStopActive());
    }

    [Fact]
    public void IsEmergencyStopActive_ReturnsTrue_WhenFilePresent()
    {
        try
        {
            File.WriteAllText(SecurityEnforcer.EmergencyStopPath, "stop");
            Assert.True(SecurityEnforcer.IsEmergencyStopActive());
        }
        finally
        {
            if (File.Exists(SecurityEnforcer.EmergencyStopPath))
                try { File.Delete(SecurityEnforcer.EmergencyStopPath); } catch { }
        }
    }
}

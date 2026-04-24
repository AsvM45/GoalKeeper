using System;
using System.IO;
using System.Threading.Tasks;
using ServiceEngine.Core;
using Xunit;

namespace ServiceEngine.Tests;

public class ScreenTimeLoggerTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly ScreenTimeLogger _logger;

    public ScreenTimeLoggerTests()
    {
        _testDbPath = Path.GetTempFileName();
        _logger = new ScreenTimeLogger(_testDbPath);
        // Ensure initialized synchronously for tests
        _logger.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    [Fact]
    public async Task InitializeAsync_CreatesTables()
    {
        // Simple write and read to verify base schema
        await _logger.SetStateAsync("TestKey", "TestVal");
        var val = await _logger.GetStateAsync("TestKey");
        Assert.Equal("TestVal", val);
    }

    [Fact]
    public async Task SetStateAsync_UpsertsProperly()
    {
        await _logger.SetStateAsync("Mode", "Armed");
        await _logger.SetStateAsync("Mode", "Nuclear");
        var val = await _logger.GetStateAsync("Mode");
        Assert.Equal("Nuclear", val);
    }

    [Fact]
    public async Task BudgetCRUD_Works()
    {
        await _logger.UpsertBudgetAsync("distracting", 3600, 5, 5, 10);
        var b = await _logger.GetBudgetAsync("distracting");
        Assert.NotNull(b);
        Assert.Equal("distracting", b?.Category);
        Assert.Equal(3600, b?.AllowedSeconds);
        Assert.Equal(0, b?.UsedSeconds);
        
        await _logger.AddUsedSecondsAsync("distracting", 60);
        b = await _logger.GetBudgetAsync("distracting");
        Assert.Equal(60, b?.UsedSeconds);
        
        await _logger.DeleteBudgetAsync("distracting");
        b = await _logger.GetBudgetAsync("distracting");
        Assert.Null(b);
    }

    [Fact]
    public async Task CategoryRule_MatchesWildcards()
    {
        await _logger.AddCategoryRuleAsync("*notepad*", "distracting", "app");
        var cat = await _logger.GetCategoryForAppAsync("Notepad.exe");
        Assert.Equal("distracting", cat);

        var miss = await _logger.GetCategoryForAppAsync("winword.exe");
        Assert.Null(miss);
    }

    [Fact]
    public async Task Tokens_StoreAndConsume_SingleUse()
    {
        await _logger.StoreTokenAsync("token123");
        
        bool consumed1 = await _logger.ConsumeTokenAsync("token123");
        Assert.True(consumed1);
        
        bool consumed2 = await _logger.ConsumeTokenAsync("token123");
        Assert.False(consumed2); // single use
        
        bool consumedInvalid = await _logger.ConsumeTokenAsync("fake");
        Assert.False(consumedInvalid);
    }
}

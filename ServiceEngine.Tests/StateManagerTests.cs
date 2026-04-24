using System.Threading.Tasks;
using Xunit;
using Moq;
using ServiceEngine.Core;
using ServiceEngine.IPC;

namespace ServiceEngine.Tests;

public class StateManagerTests
{
    [Fact]
    public async Task EvaluateAsync_NoCategory_ReturnsAllow()
    {
        var db = new Mock<ScreenTimeLogger>();
        var pipe = new Mock<PipeServer>();
        var state = new StateManager(db.Object, pipe.Object, null!);

        db.Setup(d => d.GetCategoryForTitleAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        db.Setup(d => d.GetCategoryForAppAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var decision = await state.EvaluateAsync("Notepad.exe", "Untitled", null);
        Assert.Equal(EnforcementAction.Allow, decision.Action);
    }

    [Fact]
    public async Task EvaluateAsync_NuclearActive_ReturnsKill()
    {
        var db = new Mock<ScreenTimeLogger>();
        var pipe = new Mock<PipeServer>();
        
        // Setup ActiveMode = nuclear, and an active end time
        db.Setup(d => d.GetStateAsync("ActiveMode")).ReturnsAsync("nuclear");
        db.Setup(d => d.GetStateAsync("NuclearEndTimeEpoch")).ReturnsAsync(System.DateTimeOffset.Now.AddHours(1).ToUnixTimeSeconds().ToString());
        
        var state = new StateManager(db.Object, pipe.Object, null!);

        var decision = await state.EvaluateAsync("Notepad.exe", "Untitled", null);
        Assert.Equal(EnforcementAction.Kill, decision.Action);
    }

    [Fact]
    public async Task EvaluateAsync_BudgetExhausted_ReturnsKill()
    {
        var db = new Mock<ScreenTimeLogger>();
        var pipe = new Mock<PipeServer>();
        
        db.Setup(d => d.GetStateAsync("ActiveMode")).ReturnsAsync("none");
        db.Setup(d => d.GetCategoryForAppAsync("Game.exe")).ReturnsAsync("distracting");
        
        // Exhausted budget
        var budget = new BudgetRecord(1, "distracting", 3600, 3600, 5, 0, 5, 10);
        db.Setup(d => d.GetBudgetAsync("distracting")).ReturnsAsync(budget);
        
        var state = new StateManager(db.Object, pipe.Object, null!);

        var decision = await state.EvaluateAsync("Game.exe", "Game", null);
        Assert.Equal(EnforcementAction.Kill, decision.Action);
        Assert.Contains("exhausted", decision.Reason);
    }
}

using System.Text.Json;
using ServiceEngine.IPC;

namespace ServiceEngine.Tests;

public class PipeProtocolTests
{
    [Fact]
    public void SerializeDeserialize_RoundTrip_Works()
    {
        var msg = PipeMessage.ShowFriction("Notepad.exe", "distracting", 10);
        var json = msg.Serialize();
        
        var parsed = PipeMessage.Deserialize(json);
        Assert.NotNull(parsed);
        Assert.Equal(MessageType.ShowFriction, parsed.Type);
        Assert.Equal("Notepad.exe", parsed.GetString("app"));
        Assert.Equal("distracting", parsed.GetString("category"));
        Assert.Equal(10, parsed.GetInt("delaySeconds"));
    }

    [Fact]
    public void FactoryMethods_CreateCorrectMessages()
    {
        var stateMsg = PipeMessage.StateUpdate("strict", 123456789L, true);
        Assert.Equal(MessageType.StateUpdate, stateMsg.Type);
        Assert.Equal("strict", stateMsg.GetString("mode"));
        Assert.Equal(123456789L, stateMsg.GetLong("nuclearEnd"));
        Assert.True(stateMsg.GetBool("isArmed"));

        var diagMsg = PipeMessage.DiagResult(false, "Failed");
        Assert.Equal(MessageType.DiagnosticResult, diagMsg.Type);
        Assert.False(diagMsg.GetBool("success"));
        Assert.Equal("Failed", diagMsg.GetString("reason"));
    }

    [Fact]
    public void HelperMethods_HandleMissingKeys_Gracefully()
    {
        var emptyMsg = PipeMessage.SessionExpired("Discord");
        // "app" exists, "missing" does not
        Assert.Equal("Discord", emptyMsg.GetString("app"));
        Assert.Null(emptyMsg.GetString("missing"));
        Assert.Equal(0, emptyMsg.GetInt("missing"));
        Assert.False(emptyMsg.GetBool("missing"));
        Assert.Equal(0L, emptyMsg.GetLong("missing"));
    }
}

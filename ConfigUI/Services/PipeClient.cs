using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace ConfigUI.Services;

/// <summary>
/// Named pipe client that connects to the ServiceEngine.
/// Maintains a persistent connection and fires events when server pushes messages.
/// </summary>
public sealed class PipeClient : IDisposable
{
    public const string PipeName = "GoalKeeperPipe";

    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    // Events the UI can subscribe to
    public event Action<PipeMessage>? MessageReceived;
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task ConnectAsync()
    {
        _cts = new CancellationTokenSource();
        while (!_disposed)
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough);

                await _pipe.ConnectAsync(3000, _cts.Token);
                ConnectionChanged?.Invoke(true);

                _ = Task.Run(() => ReadLoopAsync(_cts.Token));
                return;
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                await Task.Delay(3000);
                _pipe?.Dispose();
                _pipe = null;
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];
        try
        {
            while (_pipe!.IsConnected && !ct.IsCancellationRequested)
            {
                int read = await _pipe.ReadAsync(buffer, ct);
                if (read == 0) break;

                var json = Encoding.UTF8.GetString(buffer, 0, read);
                var msg = PipeMessage.Deserialize(json);
                if (msg != null)
                {
                    Application.Current.Dispatcher.Invoke(() => MessageReceived?.Invoke(msg));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Application.Current.Dispatcher.Invoke(() => ConnectionChanged?.Invoke(false));
            // Auto-reconnect
            if (!_disposed)
                _ = Task.Run(() => ConnectAsync());
        }
    }

    public async Task<PipeMessage?> SendAsync(PipeMessage message)
    {
        if (_pipe?.IsConnected != true) return null;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.Serialize());
            await _pipe.WriteAsync(bytes);
            await _pipe.FlushAsync();

            // Read the synchronous response (for request/response patterns)
            var buffer = new byte[65536];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int read = await _pipe.ReadAsync(buffer, cts.Token);
            if (read == 0) return null;
            return PipeMessage.Deserialize(Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _disposed = true;
        _cts?.Cancel();
        _pipe?.Dispose();
    }
}

// ── Pipe message (mirrored from ServiceEngine, no shared project reference needed at runtime) ──

public sealed class PipeMessage
{
    public string Type { get; init; } = "";
    public JsonElement Payload { get; init; }

    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static PipeMessage? Deserialize(string json) =>
        JsonSerializer.Deserialize<PipeMessage>(json, _opts);

    public string Serialize() => JsonSerializer.Serialize(this, _opts);

    public static PipeMessage Create<T>(string type, T payload)
    {
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, _opts));
        return new PipeMessage { Type = type, Payload = doc.RootElement };
    }

    public string? GetString(string key) =>
        Payload.TryGetProperty(key, out var p) ? p.GetString() : null;

    public int GetInt(string key, int def = 0) =>
        Payload.TryGetProperty(key, out var p) && p.TryGetInt32(out int v) ? v : def;

    public bool GetBool(string key, bool def = false) =>
        Payload.TryGetProperty(key, out var p) ? p.GetBoolean() : def;

    public long GetLong(string key, long def = 0) =>
        Payload.TryGetProperty(key, out var p) && p.TryGetInt64(out long v) ? v : def;
}

public static class MessageType
{
    public const string ShowFriction     = "SHOW_FRICTION";
    public const string SessionExpired   = "SESSION_EXPIRED";
    public const string StateUpdate      = "STATE_UPDATE";
    public const string DiagnosticResult = "DIAGNOSTIC_RESULT";
    public const string AllowSession     = "ALLOW_SESSION";
    public const string EnforceClose     = "ENFORCE_CLOSE";
    public const string UpdateSettings   = "UPDATE_SETTINGS";
    public const string ArmSystem        = "ARM_SYSTEM";
    public const string ActivateNuclear  = "ACTIVATE_NUCLEAR";
    public const string RunDiagnostics   = "RUN_DIAGNOSTICS";
    public const string GetState         = "GET_STATE";
    public const string SetAIKey         = "SET_AI_KEY";
    public const string OverrideAI       = "OVERRIDE_AI";
    public const string ResumeProcess    = "RESUME_PROCESS";
}

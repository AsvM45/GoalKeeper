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

    // #region agent log
    private static readonly string _logPath = Path.Combine(
        @"C:\Users\asvat\OneDrive\Documents\GitHub\GoalKeeper\GoalKeeper", "debug-df88ca.log");
    private static void DbgLog(string msg, string hyp, object? data = null)
    {
        try
        {
            var entry = JsonSerializer.Serialize(new
            {
                sessionId = "df88ca", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                location = "PipeClient.cs", hypothesisId = hyp, message = msg, data
            });
            File.AppendAllText(_logPath, entry + "\n");
        }
        catch { }
    }
    // #endregion

    public async Task ConnectAsync()
    {
        _cts = new CancellationTokenSource();
        int attempt = 0;
        while (!_disposed)
        {
            attempt++;
            try
            {
                _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough);

                await _pipe.ConnectAsync(3000, _cts.Token);
                // #region agent log
                DbgLog("ConnectAsync succeeded", "B", new { attempt });
                // #endregion
                ConnectionChanged?.Invoke(true);

                _ = Task.Run(() => ReadLoopAsync(_cts.Token));
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // #region agent log
                DbgLog("ConnectAsync failed, retrying", "B", new { attempt, error = ex.Message });
                // #endregion
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
                // #region agent log
                DbgLog("ReadLoopAsync received message", "A/C", new { type = PipeMessage.Deserialize(json)?.Type });
                // #endregion
                var msg = PipeMessage.Deserialize(json);
                if (msg != null)
                {
                    Application.Current.Dispatcher.Invoke(() => MessageReceived?.Invoke(msg));
                }
            }
            // #region agent log
            DbgLog("ReadLoopAsync loop ended (read==0 or disconnected)", "A/C",
                new { isConnected = _pipe?.IsConnected });
            // #endregion
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // #region agent log
            DbgLog("ReadLoopAsync exception — triggering reconnect", "A/C", new { error = ex.Message });
            // #endregion
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

    /// <summary>
    /// Sends a message and does NOT wait for a response.
    /// Use for config-write commands where the server returns null (no ACK).
    /// Avoids the cancellation-on-timeout pattern that breaks the pipe handle on Windows.
    /// </summary>
    public async Task FireAndForgetAsync(PipeMessage message)
    {
        // #region agent log
        DbgLog("FireAndForgetAsync called", "D", new
        {
            msgType   = message.Type,
            pipeNull  = _pipe == null,
            isConnected = _pipe?.IsConnected
        });
        // #endregion
        if (_pipe?.IsConnected != true) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.Serialize());
            await _pipe.WriteAsync(bytes);
            await _pipe.FlushAsync();
            // #region agent log
            DbgLog("FireAndForgetAsync sent OK", "D", new { msgType = message.Type });
            // #endregion
        }
        catch (Exception ex)
        {
            // #region agent log
            DbgLog("FireAndForgetAsync write failed", "D", new { msgType = message.Type, error = ex.Message });
            // #endregion
        }
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

    // Config writes — routed through service (admin write rights to DB)
    public const string SetState           = "SET_STATE";
    public const string AddCategoryRule    = "ADD_CATEGORY_RULE";
    public const string DeleteCategoryRule = "DELETE_CATEGORY_RULE";
    public const string UpsertBudget       = "UPSERT_BUDGET";
    public const string DeleteBudget       = "DELETE_BUDGET";
}

using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace ConfigUI.Services;

/// <summary>
/// Named pipe client that connects to the ServiceEngine.
/// Architecture: ReadLoopAsync is the ONLY reader. SendAsync writes a message
/// then waits for the ReadLoop to deliver the response via TaskCompletionSource.
/// FireAndForgetAsync just writes (server returns null for these).
/// A write lock serializes all writes.
/// </summary>
public sealed class PipeClient : IDisposable
{
    public const string PipeName = "GoalKeeperPipe";

    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    // Serializes writes only (reads are always done by ReadLoopAsync)
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // When SendAsync is waiting for a response, ReadLoopAsync delivers it here
    private TaskCompletionSource<PipeMessage?>? _pendingResponse;
    private readonly object _pendingLock = new();

    // Events the UI can subscribe to
    public event Action<PipeMessage>? MessageReceived;
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _pipe?.IsConnected == true;

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
                _pipe.ReadMode = PipeTransmissionMode.Message;
                ConnectionChanged?.Invoke(true);

                // ReadLoopAsync is the ONLY thing that reads from the pipe
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

    /// <summary>
    /// The ONLY reader on the pipe. Runs continuously.
    /// If SendAsync is awaiting a response, delivers it via _pendingResponse.
    /// Otherwise, raises MessageReceived for server-pushed broadcasts.
    /// </summary>
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
                if (msg == null) continue;

                // Check if SendAsync is waiting for a response
                TaskCompletionSource<PipeMessage?>? pending;
                lock (_pendingLock)
                {
                    pending = _pendingResponse;
                    _pendingResponse = null;
                }

                if (pending != null)
                {
                    // Deliver this message as the response to SendAsync
                    pending.TrySetResult(msg);
                }
                else
                {
                    // Server-pushed broadcast — deliver to subscribers
                    Application.Current?.Dispatcher.Invoke(() => MessageReceived?.Invoke(msg));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail any pending response
            lock (_pendingLock)
            {
                _pendingResponse?.TrySetResult(null);
                _pendingResponse = null;
            }
        }

        // Disconnected
        Application.Current?.Dispatcher.Invoke(() => ConnectionChanged?.Invoke(false));
        if (!_disposed)
            _ = Task.Run(() => ConnectAsync());
    }

    /// <summary>
    /// Sends a message and waits for a synchronous response.
    /// The ReadLoopAsync thread will deliver the response.
    /// </summary>
    public async Task<PipeMessage?> SendAsync(PipeMessage message)
    {
        if (_pipe?.IsConnected != true) return null;

        var tcs = new TaskCompletionSource<PipeMessage?>();
        lock (_pendingLock) { _pendingResponse = tcs; }

        await _writeLock.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.Serialize());
            await _pipe.WriteAsync(bytes);
            await _pipe.FlushAsync();
        }
        catch
        {
            lock (_pendingLock) { _pendingResponse = null; }
            return null;
        }
        finally
        {
            _writeLock.Release();
        }

        // Wait up to 5 seconds for the ReadLoop to deliver the response
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        cts.Token.Register(() => tcs.TrySetResult(null));
        return await tcs.Task;
    }

    /// <summary>
    /// Sends a message and does NOT wait for a response.
    /// Used for config-write commands where the server returns null.
    /// </summary>
    public async Task FireAndForgetAsync(PipeMessage message)
    {
        if (_pipe?.IsConnected != true) return;
        await _writeLock.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.Serialize());
            await _pipe.WriteAsync(bytes);
            await _pipe.FlushAsync();
        }
        catch { /* pipe broken — ReadLoop will trigger reconnect */ }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cts?.Cancel();
        _pipe?.Dispose();
    }
}

// ── Pipe message (mirrored from ServiceEngine) ──

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

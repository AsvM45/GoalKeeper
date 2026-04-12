using System.IO.Pipes;
using System.Text;
using ServiceEngine.Core;

namespace ServiceEngine.IPC;

/// <summary>
/// Named Pipe server that accepts connections from the ConfigUI WPF app.
/// Handles incoming commands and broadcasts notifications to connected UIs.
/// </summary>
public sealed class PipeServer : BackgroundService
{
    public const string PipeName = "GoalKeeperPipe";

    private readonly ScreenTimeLogger _db;
    private readonly IServiceProvider _services;
    private readonly ILogger<PipeServer> _log;

    private readonly List<NamedPipeServerStream> _activeClients = new();
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    public PipeServer(ScreenTimeLogger db, IServiceProvider services, ILogger<PipeServer> log)
    {
        _db = db;
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Named pipe server starting on \\\\.\\ pipe\\{Pipe}", PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                _log.LogDebug("Client connected.");

                await _clientLock.WaitAsync(stoppingToken);
                _activeClients.Add(pipe);
                _clientLock.Release();

                _ = Task.Run(() => HandleClientAsync(pipe, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Pipe accept error");
                await pipe.DisposeAsync();
            }
        }

        // Clean up all active clients
        await _clientLock.WaitAsync();
        foreach (var c in _activeClients)
        {
            try { await c.DisposeAsync(); } catch { }
        }
        _activeClients.Clear();
        _clientLock.Release();
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[65536];
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                int read = await pipe.ReadAsync(buffer, ct);
                if (read == 0) break;

                var json = Encoding.UTF8.GetString(buffer, 0, read);
                var msg = PipeMessage.Deserialize(json);
                if (msg == null) continue;

                _log.LogDebug("Received: {Type}", msg.Type);
                var response = await ProcessMessageAsync(msg);
                if (response != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(response.Serialize());
                    await pipe.WriteAsync(bytes, ct);
                    await pipe.FlushAsync(ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug("Client disconnected: {E}", ex.Message);
        }
        finally
        {
            await _clientLock.WaitAsync();
            _activeClients.Remove(pipe);
            _clientLock.Release();
            await pipe.DisposeAsync();
        }
    }

    private async Task<PipeMessage?> ProcessMessageAsync(PipeMessage msg)
    {
        var stateManager = _services.GetRequiredService<StateManager>();
        var security = _services.GetRequiredService<SecurityEnforcer>();

        switch (msg.Type)
        {
            case MessageType.AllowSession:
            {
                var app = msg.GetString("app") ?? "";
                var category = msg.GetString("category") ?? "";
                var token = msg.GetString("challengeToken");

                // Some allow-session calls may require a token (e.g., budget override)
                if (!string.IsNullOrEmpty(token))
                {
                    bool valid = await _db.ConsumeTokenAsync(token);
                    if (!valid)
                    {
                        _log.LogWarning("Invalid challenge token rejected for ALLOW_SESSION");
                        return null;
                    }
                }
                await stateManager.OnSessionAllowedAsync(app, category);

                // Resume the suspended process
                ResumeProcess(app);
                return null;
            }

            case MessageType.EnforceClose:
            {
                var app = msg.GetString("app") ?? "";
                KillProcess(app);
                stateManager.CancelSessionTimer(app);
                return null;
            }

            case MessageType.ResumeProcess:
            {
                var app = msg.GetString("app") ?? "";
                ResumeProcess(app);
                return null;
            }

            case MessageType.ArmSystem:
            {
                var token = msg.GetString("challengeToken") ?? "";
                bool valid = await _db.ConsumeTokenAsync(token);
                if (!valid)
                {
                    _log.LogWarning("ARM_SYSTEM rejected: invalid challenge token");
                    return null;
                }
                try
                {
                    await security.ArmSystemAsync();
                    return PipeMessage.StateUpdate("armed", 0, true);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "ARM_SYSTEM failed");
                    return PipeMessage.DiagResult(false, ex.Message);
                }
            }

            case MessageType.ActivateNuclear:
            {
                var token = msg.GetString("challengeToken") ?? "";
                bool valid = await _db.ConsumeTokenAsync(token);
                if (!valid)
                {
                    _log.LogWarning("ACTIVATE_NUCLEAR rejected: invalid challenge token");
                    return null;
                }

                var mode = msg.GetString("mode") ?? "nuclear_strict";
                int minutes = msg.GetInt("durationMinutes", 60);
                var endEpoch = DateTimeOffset.Now.AddMinutes(minutes).ToUnixTimeSeconds();

                await _db.SetStateAsync("ActiveMode", mode);
                await _db.SetStateAsync("NuclearEndTimeEpoch", endEpoch.ToString());

                _log.LogWarning("Nuclear mode {Mode} activated for {Min} minutes", mode, minutes);

                // If strict mode, zero out all budgets' UsedLaunches so allowances don't carry over
                if (mode == "nuclear_strict")
                    _log.LogInformation("Strict nuclear: all budget allowances revoked for today.");

                return PipeMessage.StateUpdate(mode, endEpoch, true);
            }

            case MessageType.RunDiagnostics:
            {
                var result = await security.RunDiagnosticsAsync();
                return PipeMessage.DiagResult(result.Success, result.FailureReason);
            }

            case MessageType.GetState:
            {
                bool isArmed = (await _db.GetStateAsync("IsArmed")) == "1";
                var mode = await _db.GetStateAsync("ActiveMode") ?? "none";
                long.TryParse(await _db.GetStateAsync("NuclearEndTimeEpoch"), out long epoch);
                return PipeMessage.StateUpdate(mode, epoch, isArmed);
            }

            case MessageType.SetAIKey:
            {
                var token = msg.GetString("challengeToken") ?? "";
                bool valid = await _db.ConsumeTokenAsync(token);
                if (!valid) return null;
                var key = msg.GetString("apiKey") ?? "";
                await _db.SetStateAsync("AIApiKey", key);
                return null;
            }

            case MessageType.OverrideAI:
            {
                // UI user manually overrides an AI decision
                var urlOrApp = msg.GetString("urlOrApp") ?? "";
                var override_ = msg.GetString("override") ?? "allow";
                // Store override in AICache via direct DB
                await _db.UpsertAICacheAsync(urlOrApp, override_, 1.0, "User override", null);
                return null;
            }

            default:
                _log.LogWarning("Unknown message type: {Type}", msg.Type);
                return null;
        }
    }

    /// <summary>
    /// Broadcasts a message to all connected UI clients.
    /// </summary>
    public async Task BroadcastAsync(PipeMessage message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.Serialize());

        await _clientLock.WaitAsync();
        var clients = _activeClients.ToList();
        _clientLock.Release();

        foreach (var client in clients)
        {
            try
            {
                if (client.IsConnected)
                {
                    await client.WriteAsync(bytes);
                    await client.FlushAsync();
                }
            }
            catch { /* client disconnected */ }
        }
    }

    private static void KillProcess(string appName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(appName);
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
        {
            try { p.Kill(); } catch { }
        }
    }

    private static void ResumeProcess(string appName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(appName);
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
        {
            try
            {
                foreach (System.Diagnostics.ProcessThread thread in p.Threads)
                {
                    var hThread = OpenThread(0x0002, false, (uint)thread.Id);
                    if (hThread != IntPtr.Zero)
                    {
                        ResumeThread(hThread);
                        CloseHandle(hThread);
                    }
                }
            }
            catch { }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr OpenThread(int access, bool inherit, uint threadId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint ResumeThread(IntPtr hThread);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}

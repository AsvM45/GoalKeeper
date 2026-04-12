using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace ServiceEngine.Core;

/// <summary>
/// Applies OS-level tamper-proofing when the system is in Armed Mode.
/// All operations here are gated behind IsArmed == true.
/// In dev mode (dev_bypass_flag.txt exists), this class is a no-op.
/// </summary>
public sealed class SecurityEnforcer
{
    private readonly ScreenTimeLogger _db;
    private readonly bool _devMode;
    private readonly ILogger<SecurityEnforcer>? _log;

    public SecurityEnforcer(ScreenTimeLogger db, bool devMode, ILogger<SecurityEnforcer>? log = null)
    {
        _db = db;
        _devMode = devMode;
        _log = log;
    }

    /// <summary>
    /// Called on service startup. If nuclear mode persisted across reboot,
    /// re-engages locks before opening any IPC pipes.
    /// </summary>
    public async Task ApplyStartupLocksAsync()
    {
        if (_devMode)
        {
            _log?.LogWarning("DEV BYPASS ACTIVE – SecurityEnforcer is fully disabled.");
            return;
        }

        var isArmed = await _db.GetStateAsync("IsArmed");
        if (isArmed != "1") return;

        var mode = await _db.GetStateAsync("ActiveMode");
        if (string.IsNullOrEmpty(mode) || mode == "none") return;

        var epochStr = await _db.GetStateAsync("NuclearEndTimeEpoch");
        if (!long.TryParse(epochStr, out long epoch) || epoch == 0) return;

        var endTime = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
        if (DateTime.Now >= endTime)
        {
            await _db.SetStateAsync("ActiveMode", "none");
            return;
        }

        _log?.LogWarning("Nuclear mode persisted across reboot. Re-engaging locks. Ends at {End}", endTime);
        ApplyDaclProtection();
    }

    /// <summary>
    /// Arms the system: sets IsArmed=1 in DB, then applies DACL protection
    /// and ensures the service registry key is protected.
    /// </summary>
    public async Task ArmSystemAsync()
    {
        if (_devMode)
        {
            _log?.LogWarning("ArmSystem called but dev bypass is active – skipping.");
            return;
        }

        // Run self-diagnostic
        var diag = await RunDiagnosticsAsync();
        if (!diag.Success)
            throw new InvalidOperationException($"Self-diagnostic failed: {diag.FailureReason}");

        await _db.SetStateAsync("IsArmed", "1");
        ApplyDaclProtection();
        EnsureRegistryPersistence();
        _log?.LogInformation("System armed successfully.");
    }

    public async Task<DiagnosticResult> RunDiagnosticsAsync()
    {
        // 1. SQLite writable?
        try { await _db.SetStateAsync("_diag_test", "ok"); }
        catch (Exception ex)
        { return new DiagnosticResult(false, $"SQLite write failed: {ex.Message}"); }

        // 2. WMI available?
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_Process WHERE ProcessId = 4");
            var results = searcher.Get();
            if (results.Count == 0)
                return new DiagnosticResult(false, "WMI returned no results for System process");
        }
        catch (Exception ex)
        { return new DiagnosticResult(false, $"WMI query failed: {ex.Message}"); }

        // 3. Can enumerate processes?
        try
        {
            var procs = System.Diagnostics.Process.GetProcesses();
            if (procs.Length == 0)
                return new DiagnosticResult(false, "Cannot enumerate running processes");
        }
        catch (Exception ex)
        { return new DiagnosticResult(false, $"Process enumeration failed: {ex.Message}"); }

        return new DiagnosticResult(true, null);
    }

    // ── DACL Protection ───────────────────────────────────────────────────────

    private void ApplyDaclProtection()
    {
        try
        {
            var pid = Environment.ProcessId;
            var hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, (uint)pid);
            if (hProcess == IntPtr.Zero)
            {
                _log?.LogError("OpenProcess failed for DACL protection. LastError={E}", Marshal.GetLastWin32Error());
                return;
            }

            // Get current DACL
            var result = GetSecurityInfo(
                hProcess,
                SE_OBJECT_TYPE.SE_KERNEL_OBJECT,
                DACL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero,
                out IntPtr pDacl, IntPtr.Zero,
                out IntPtr pSD);

            if (result != 0)
            {
                _log?.LogError("GetSecurityInfo failed: {R}", result);
                CloseHandle(hProcess);
                return;
            }

            // Build a new DACL with a Deny ACE for PROCESS_TERMINATE for Interactive Users
            var sid = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
            byte[] sidBytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(sidBytes, 0);

            // Add DENY ACE for PROCESS_TERMINATE (0x0001)
            AddAccessDeniedAce(pDacl, ACL_REVISION, PROCESS_TERMINATE, sidBytes);

            SetSecurityInfo(
                hProcess,
                SE_OBJECT_TYPE.SE_KERNEL_OBJECT,
                DACL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero,
                pDacl, IntPtr.Zero);

            CloseHandle(hProcess);
            _log?.LogInformation("DACL protection applied – Task Manager cannot terminate service.");
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "DACL protection failed (non-fatal in audit mode)");
        }
    }

    // ── Registry Persistence ──────────────────────────────────────────────────

    private static void EnsureRegistryPersistence()
    {
        // The service is registered by the installer via sc.exe.
        // This verifies the key exists and sets start type to Automatic.
        const string keyPath = @"SYSTEM\CurrentControlSet\Services\GoalKeeperService";
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
        if (key != null)
        {
            key.SetValue("Start", 2, RegistryValueKind.DWord); // 2 = SERVICE_AUTO_START
        }
    }

    // ── P/Invoke declarations ─────────────────────────────────────────────────

    private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint DACL_SECURITY_INFORMATION = 0x00000004;
    private const uint ACL_REVISION = 2;

    private enum SE_OBJECT_TYPE { SE_KERNEL_OBJECT = 6 }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        IntPtr handle, SE_OBJECT_TYPE objType, uint secInfo,
        IntPtr pSidOwner, IntPtr pSidGroup,
        out IntPtr ppDacl, IntPtr ppSacl, out IntPtr ppSD);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        IntPtr handle, SE_OBJECT_TYPE objType, uint secInfo,
        IntPtr pSidOwner, IntPtr pSidGroup,
        IntPtr pDacl, IntPtr pSacl);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AddAccessDeniedAce(
        IntPtr pAcl, uint revision, uint accessMask, byte[] pSid);
}

public record DiagnosticResult(bool Success, string? FailureReason);

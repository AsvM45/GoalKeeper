using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace ServiceEngine.Core;

/// <summary>
/// Applies OS-level tamper-proofing when the system is in Armed Mode.
/// All operations here are gated behind IsArmed == true.
/// In dev mode (dev_bypass_flag.txt exists), this class is a no-op.
/// Emergency stop: if C:\goalkeeper_emergency_stop.txt exists, all protection is skipped.
/// </summary>
public sealed class SecurityEnforcer
{
    private readonly ScreenTimeLogger _db;
    private readonly bool _devMode;
    private readonly ILogger<SecurityEnforcer>? _log;

    /// <summary>Maximum allowed Nuclear Mode duration (24 hours).</summary>
    public const int MaxNuclearDurationMinutes = 1440;

    /// <summary>Path to the runtime emergency stop file.</summary>
    public const string EmergencyStopPath = @"C:\goalkeeper_emergency_stop.txt";

    public SecurityEnforcer(ScreenTimeLogger db, bool devMode, ILogger<SecurityEnforcer>? log = null)
    {
        _db = db;
        _devMode = devMode;
        _log = log;
    }

    /// <summary>
    /// Checks whether the runtime emergency stop file exists.
    /// </summary>
    public static bool IsEmergencyStopActive() => File.Exists(EmergencyStopPath);

    /// <summary>
    /// Called on service startup. If nuclear mode persisted across reboot,
    /// re-engages locks before opening any IPC pipes.
    /// Includes startup self-test: if diagnostics fail, auto-disarms.
    /// </summary>
    public async Task ApplyStartupLocksAsync()
    {
        if (_devMode)
        {
            _log?.LogWarning("DEV BYPASS ACTIVE – SecurityEnforcer is fully disabled.");
            return;
        }

        if (IsEmergencyStopActive())
        {
            _log?.LogWarning("EMERGENCY STOP FILE DETECTED ({Path}) – skipping all security enforcement.", EmergencyStopPath);
            return;
        }

        var isArmed = await _db.GetStateAsync("IsArmed");
        if (isArmed != "1") return;

        // Startup self-test: auto-disarm if diagnostics fail
        var diag = await RunDiagnosticsAsync();
        if (!diag.Success)
        {
            _log?.LogCritical("Startup self-test FAILED: {Reason}. Auto-disarming to prevent bricking.", diag.FailureReason);
            await _db.SetStateAsync("IsArmed", "0");
            return;
        }

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

        if (IsEmergencyStopActive())
        {
            _log?.LogWarning("ArmSystem blocked by emergency stop file.");
            throw new InvalidOperationException("Emergency stop file is present. Remove it before arming.");
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

    /// <summary>
    /// Validates a Nuclear Mode duration request. Caps at MaxNuclearDurationMinutes.
    /// </summary>
    public static int ClampNuclearDuration(int requestedMinutes)
    {
        if (requestedMinutes <= 0) return 60; // Default 1 hour
        return Math.Min(requestedMinutes, MaxNuclearDurationMinutes);
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
        if (IsEmergencyStopActive())
        {
            _log?.LogWarning("DACL protection skipped – emergency stop file is present.");
            return;
        }

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
                out IntPtr pOldDacl, IntPtr.Zero,
                out IntPtr pSD);

            if (result != 0)
            {
                _log?.LogError("GetSecurityInfo failed: {R}", result);
                CloseHandle(hProcess);
                return;
            }

            // Build a new DACL by getting old ACL info, allocating a larger buffer, and copying ACEs
            if (!GetAclInformation(pOldDacl, out ACL_SIZE_INFORMATION aclInfo, (uint)Marshal.SizeOf<ACL_SIZE_INFORMATION>(), ACL_INFORMATION_CLASS.AclSizeInformation))
            {
                _log?.LogError("GetAclInformation failed. LastError={E}", Marshal.GetLastWin32Error());
                CloseHandle(hProcess);
                return;
            }

            // Build SIDs for Interactive Users (S-1-5-4) and Everyone (S-1-1-0).
            // Both are denied PROCESS_TERMINATE so neither Task Manager nor any user
            // process can kill this service while Armed Mode is active.
            var interactiveSid = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
            byte[] interactiveSidBytes = new byte[interactiveSid.BinaryLength];
            interactiveSid.GetBinaryForm(interactiveSidBytes, 0);

            var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            byte[] worldSidBytes = new byte[worldSid.BinaryLength];
            worldSid.GetBinaryForm(worldSidBytes, 0);

            // New DACL size: existing bytes + two ACCESS_DENIED ACEs (header=4, mask=4, SID)
            uint aceSize1 = (uint)(8 + interactiveSidBytes.Length);
            uint aceSize2 = (uint)(8 + worldSidBytes.Length);
            uint newDaclSize = aclInfo.AclBytesInUse + aceSize1 + aceSize2;

            IntPtr pNewDacl = Marshal.AllocHGlobal((int)newDaclSize);
            try
            {
                if (!InitializeAcl(pNewDacl, newDaclSize, ACL_REVISION))
                {
                    _log?.LogError("InitializeAcl failed. LastError={E}", Marshal.GetLastWin32Error());
                    return;
                }

                // Deny ACEs must precede Allow ACEs in a well-formed DACL.
                if (!AddAccessDeniedAce(pNewDacl, ACL_REVISION, PROCESS_TERMINATE, interactiveSidBytes))
                {
                    _log?.LogError("AddAccessDeniedAce(Interactive) failed. LastError={E}", Marshal.GetLastWin32Error());
                    return;
                }

                if (!AddAccessDeniedAce(pNewDacl, ACL_REVISION, PROCESS_TERMINATE, worldSidBytes))
                {
                    _log?.LogError("AddAccessDeniedAce(World) failed. LastError={E}", Marshal.GetLastWin32Error());
                    return;
                }

                // Copy existing ACEs from the old DACL
                for (uint i = 0; i < aclInfo.AceCount; i++)
                {
                    if (GetAce(pOldDacl, i, out IntPtr pAce))
                        AddAce(pNewDacl, ACL_REVISION, 0xFFFFFFFF, pAce, (uint)Marshal.ReadInt16(pAce, 2));
                }

                // Apply the new DACL to the process handle
                var setResult = SetSecurityInfo(
                    hProcess,
                    SE_OBJECT_TYPE.SE_KERNEL_OBJECT,
                    DACL_SECURITY_INFORMATION,
                    IntPtr.Zero, IntPtr.Zero,
                    pNewDacl, IntPtr.Zero);

                if (setResult != 0)
                    _log?.LogError("SetSecurityInfo failed: {R}", setResult);
                else
                    _log?.LogInformation("DACL protection applied – Interactive+World denied PROCESS_TERMINATE.");
            }
            finally
            {
                Marshal.FreeHGlobal(pNewDacl);
            }

            if (pSD != IntPtr.Zero)
                LocalFree(pSD);

            CloseHandle(hProcess);

            // Apply identical deny-write DACL to the usage database.
            ApplyDaclToSqliteFile();
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "DACL protection failed (non-fatal in audit mode)");
        }
    }

    /// <summary>
    /// Restricts write access to metrics.sqlite so only the SYSTEM account can
    /// modify it. Interactive users and Everyone are denied FILE write rights,
    /// preventing manual tampering with the usage database.
    /// </summary>
    private void ApplyDaclToSqliteFile()
    {
        if (IsEmergencyStopActive()) return;

        var dbPath = _db.DbPath;
        if (!File.Exists(dbPath))
        {
            _log?.LogWarning("metrics.sqlite not found at {Path} – skipping file DACL.", dbPath);
            return;
        }

        try
        {
            // Get current DACL for the file
            var result = GetNamedSecurityInfo(
                dbPath,
                SE_OBJECT_TYPE.SE_FILE_OBJECT,
                DACL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero,
                out IntPtr pOldDacl, IntPtr.Zero,
                out IntPtr pSD);

            if (result != 0)
            {
                _log?.LogError("GetNamedSecurityInfo for sqlite failed: {R}", result);
                return;
            }

            if (!GetAclInformation(pOldDacl, out ACL_SIZE_INFORMATION aclInfo,
                    (uint)Marshal.SizeOf<ACL_SIZE_INFORMATION>(), ACL_INFORMATION_CLASS.AclSizeInformation))
            {
                _log?.LogError("GetAclInformation(sqlite) failed. LastError={E}", Marshal.GetLastWin32Error());
                return;
            }

            var interactiveSid = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
            byte[] interactiveSidBytes = new byte[interactiveSid.BinaryLength];
            interactiveSid.GetBinaryForm(interactiveSidBytes, 0);

            var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            byte[] worldSidBytes = new byte[worldSid.BinaryLength];
            worldSid.GetBinaryForm(worldSidBytes, 0);

            uint aceSize1 = (uint)(8 + interactiveSidBytes.Length);
            uint aceSize2 = (uint)(8 + worldSidBytes.Length);
            uint newDaclSize = aclInfo.AclBytesInUse + aceSize1 + aceSize2;

            IntPtr pNewDacl = Marshal.AllocHGlobal((int)newDaclSize);
            try
            {
                if (!InitializeAcl(pNewDacl, newDaclSize, ACL_REVISION))
                {
                    _log?.LogError("InitializeAcl(sqlite) failed. LastError={E}", Marshal.GetLastWin32Error());
                    return;
                }

                // Deny write operations for Interactive and Everyone.
                // SYSTEM's existing Allow ACEs are preserved below.
                if (!AddAccessDeniedAce(pNewDacl, ACL_REVISION, FILE_WRITE_ACCESS, interactiveSidBytes))
                {
                    _log?.LogError("AddAccessDeniedAce(sqlite/Interactive) failed. LastError={E}", Marshal.GetLastWin32Error());
                    return;
                }

                if (!AddAccessDeniedAce(pNewDacl, ACL_REVISION, FILE_WRITE_ACCESS, worldSidBytes))
                {
                    _log?.LogError("AddAccessDeniedAce(sqlite/World) failed. LastError={E}", Marshal.GetLastWin32Error());
                    return;
                }

                for (uint i = 0; i < aclInfo.AceCount; i++)
                {
                    if (GetAce(pOldDacl, i, out IntPtr pAce))
                        AddAce(pNewDacl, ACL_REVISION, 0xFFFFFFFF, pAce, (uint)Marshal.ReadInt16(pAce, 2));
                }

                var setResult = SetNamedSecurityInfo(
                    dbPath,
                    SE_OBJECT_TYPE.SE_FILE_OBJECT,
                    DACL_SECURITY_INFORMATION,
                    IntPtr.Zero, IntPtr.Zero,
                    pNewDacl, IntPtr.Zero);

                if (setResult != 0)
                    _log?.LogError("SetNamedSecurityInfo(sqlite) failed: {R}", setResult);
                else
                    _log?.LogInformation("sqlite DACL applied – writes restricted to SYSTEM only.");
            }
            finally
            {
                Marshal.FreeHGlobal(pNewDacl);
            }

            if (pSD != IntPtr.Zero)
                LocalFree(pSD);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "sqlite DACL protection failed (non-fatal)");
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
    // FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_WRITE_EA | FILE_WRITE_ATTRIBUTES | DELETE | WRITE_DAC | WRITE_OWNER
    private const uint FILE_WRITE_ACCESS = 0x00100116;

    private enum SE_OBJECT_TYPE { SE_KERNEL_OBJECT = 6, SE_FILE_OBJECT = 1 }
    private enum ACL_INFORMATION_CLASS { AclSizeInformation = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct ACL_SIZE_INFORMATION
    {
        public uint AceCount;
        public uint AclBytesInUse;
        public uint AclBytesFree;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetNamedSecurityInfo(
        string pObjectName, SE_OBJECT_TYPE objectType, uint secInfo,
        IntPtr pSidOwner, IntPtr pSidGroup,
        out IntPtr ppDacl, IntPtr ppSacl, out IntPtr ppSD);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint SetNamedSecurityInfo(
        string pObjectName, SE_OBJECT_TYPE objectType, uint secInfo,
        IntPtr pSidOwner, IntPtr pSidGroup,
        IntPtr pDacl, IntPtr pSacl);

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
    private static extern bool InitializeAcl(IntPtr pAcl, uint nAclLength, uint dwAclRevision);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AddAccessDeniedAce(
        IntPtr pAcl, uint revision, uint accessMask, byte[] pSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetAclInformation(
        IntPtr pAcl, out ACL_SIZE_INFORMATION pAclInformation, uint nAclInformationLength,
        ACL_INFORMATION_CLASS dwAclInformationClass);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetAce(IntPtr pAcl, uint dwAceIndex, out IntPtr pAce);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AddAce(
        IntPtr pAcl, uint dwAclRevision, uint dwStartingAceIndex, IntPtr pAceList, uint nAceListLength);
}

public record DiagnosticResult(bool Success, string? FailureReason);

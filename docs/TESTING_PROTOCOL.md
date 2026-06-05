# GoalKeeper Testing Protocol

This is the single source-of-truth for testing GoalKeeper safely.
**Always follow this protocol in order. Each phase is a gate — do not advance until the prior one passes.**

> **CRITICAL RULE**: Armed Mode testing (Phases 2–3) MUST be performed in a VM with a snapshot. Never arm the system on your development machine.

---

## Quick Reference

| Phase | Where | What | Risk |
|-------|-------|------|------|
| 0 – Build Gate | Dev machine | dotnet build + test | None |
| 1 – Audit Mode | Dev machine (bypass flag present) | Full feature smoke test | Very low |
| 2 – Armed Mode | VM ONLY (with snapshot) | DACL, Nuclear Mode, reboot | Medium (VM-safe) |
| 3 – Safe Mode Recovery | VM ONLY (new snapshot) | Uninstall from Safe Mode | Medium (VM-safe) |

---

## Pre-Flight Check

Run this before any testing session:

```powershell
cd GoalKeeper\scripts
.\preflight-safety-check.ps1
```

The script checks:
- Whether you are inside a VM (required for Phases 2–3)
- Whether `C:\dev_bypass_flag.txt` exists (required for Phase 1 on dev machine)
- Whether `GoalKeeperService` is already installed
- Whether a leftover `C:\goalkeeper_emergency_stop.txt` exists

---

## Phase 0 — Build Gate (Dev Machine)

**Goal**: Verify the codebase compiles and all unit tests pass before any manual testing.

### Steps

1. **Confirm dev bypass flag exists**:
   ```powershell
   Test-Path C:\dev_bypass_flag.txt
   # Must output: True
   # If not: New-Item C:\dev_bypass_flag.txt -ItemType File
   ```

2. **Build the full solution**:
   ```powershell
   cd GoalKeeper
   dotnet build GoalKeeper.sln -c Debug --nologo
   ```
   **Expected**: `Build succeeded. 0 Error(s)`

3. **Run all unit tests**:
   ```powershell
   dotnet test GoalKeeper.sln --nologo --verbosity minimal
   ```
   **Expected**: `Passed! - Failed: 0, Passed: 26+, Total: 26+`

4. **Run the full publish verification script**:
   ```powershell
   cd scripts
   .\verify-build.ps1
   ```
   **Expected**: `Build Verification Successful!`

### Pass Criteria
- [ ] `dotnet build` exits with code 0
- [ ] All unit tests pass (0 failures)
- [ ] Both projects publish without error

---

## Phase 1 — Audit Mode Smoke Test (Dev Machine)

**Goal**: Verify the full feature set works correctly (friction, enforcement, IPC, dashboard) while the system is safely in Audit Mode (DACL locks disabled).

**Prerequisites**:
- `C:\dev_bypass_flag.txt` must exist
- Phase 0 passed
- Two terminal windows (one for service, one for UI)

### Steps

**1. Start the service (admin PowerShell required)**:
```powershell
cd GoalKeeper\ServiceEngine
dotnet run
```
Wait for: `Named pipe server starting`

**2. Start the UI (normal PowerShell)**:
```powershell
cd GoalKeeper\ConfigUI
dotnet run
```

**3. Verify connection**:
- Green dot appears in the top bar: `Service connected`
- Shield icon shows `Audit Mode`

**4. Test emergency stop kill-switch**:
```powershell
New-Item C:\goalkeeper_emergency_stop.txt -ItemType File
```
Open any app — it should launch without any friction.
```powershell
Remove-Item C:\goalkeeper_emergency_stop.txt
```
Friction should resume on next launch.

**5. Add a test category rule**:
- Navigate to **Categories** page
- Add rule: Pattern=`*notepad*`, Category=`distracting`, Type=`app`
- Status shows: `Rule added.`

**6. Add a test budget**:
- Navigate to **Budgets** page
- Add budget: Category=`distracting`, 60 min, 5 launches, 5 min session, **10s friction**
- Status shows: `Budget saved.`

**7. Test friction overlay**:
- Open Notepad (`Win+R → notepad → Enter`)
- Expected: Full-screen friction overlay appears with 10-second countdown and breathing prompt
- Wait for countdown to complete — `Continue` button becomes enabled

**8. Test Cancel path** (Notepad should close):
- Open Notepad again
- When overlay appears, click **Cancel**
- Expected: Notepad closes immediately

**9. Test Continue path** (Notepad stays open):
- Open Notepad again
- Wait for countdown → click **Continue**
- Expected: Notepad opens and stays open for 5 minutes (session timer runs)

**10. Verify dashboard updates**:
- Navigate to **Dashboard** page
- Expected: Summary cards show non-zero productive/distracting time
- Weekly bar chart renders (may be empty for first day)
- Top Apps table shows Notepad

**11. Verify Task Manager CAN kill the service (Audit Mode)**:
- Open Task Manager → Details tab → find `ServiceEngine.exe`
- Right-click → End Task
- Expected: Service ends successfully (this confirms Audit Mode is correctly NOT armed)
- Restart: `dotnet run` from ServiceEngine terminal

**12. Test Nuclear Mode**:
- Navigate to **Nuclear** tab
- Select `Strict Blocklist`, Duration `1 hour`
- Click **Activate Nuclear Mode**
- Complete the typing challenge (type the paragraph without backspace)
- Expected: Red `NUCLEAR HH:MM:SS` badge appears in top bar
- Open Notepad — expected: killed immediately without friction overlay

**13. Cleanup**:
- Close ConfigUI and ServiceEngine
- Remove the test category rule and budget from the DB (or delete `C:\ProgramData\GoalKeeper\metrics.sqlite` to reset)

### Pass Criteria
- [ ] Green connection dot appears
- [ ] Friction overlay appears when opening a distracting app
- [ ] Cancel path: app closes
- [ ] Continue path: app stays open
- [ ] Dashboard charts render without crash
- [ ] Task Manager CAN kill ServiceEngine (Audit Mode confirmed)
- [ ] Emergency stop file disables enforcement
- [ ] Nuclear Mode badge appears after typing challenge

---

## Phase 2 — Armed Mode Test (VM ONLY)

> **STOP**: This phase MUST be run in a VM. The DACL changes applied in Armed Mode will prevent Task Manager from killing the service and will persist across reboots. If you run this on your dev machine you may need to boot into Safe Mode to recover.

### VM Preparation

1. **Set up a Windows 10/11 VM** (see [VM_SETUP.md](VM_SETUP.md))
2. **Take a snapshot** named `Pre-GoalKeeper` before installing anything
3. **Ensure .NET 8 Runtime is installed** in the VM:
   ```powershell
   dotnet --version
   ```
4. Copy the published binaries to the VM (or build inside the VM)

### Steps

**1. Confirm dev bypass flag is ABSENT in the VM**:
```powershell
Test-Path C:\dev_bypass_flag.txt
# Must output: False
```
If it exists, delete it: `Remove-Item C:\dev_bypass_flag.txt`

**2. Start ServiceEngine as admin**:
```powershell
cd ServiceEngine\publish  # or wherever you published
.\ServiceEngine.exe
```

**3. Start ConfigUI**:
```powershell
cd ConfigUI\publish
.\ConfigUI.exe
```

**4. Run Setup Wizard → Diagnostics**:
- Navigate to **Security** page → click **Setup Wizard**
- Click **Run Diagnostics**
- Expected: All three checks pass (SQLite writable, WMI available, Process enumeration)

**5. Arm the system**:
- Click **Arm System**
- Read and acknowledge the warning dialog
- Complete the typing challenge
- Expected: Success dialog — `System armed successfully!`
- Shield icon changes to red `Armed`

**6. Verify Task Manager CANNOT kill the service**:
- Open Task Manager → Details → find `ServiceEngine.exe`
- Right-click → End Task
- Expected: **Access is denied** error
- This confirms DACL protection is working

**7. Activate Nuclear Mode for 5 minutes**:
- Nuclear tab → Strict Blocklist → 5 minutes → Activate → type challenge
- Red countdown badge appears: `NUCLEAR 05:00`

**8. Verify Nuclear Mode blocks apps**:
- Open Notepad, Chrome, any app
- Expected: killed immediately on launch

**9. Reboot the VM**:
```powershell
Restart-Computer
```

**10. After reboot, verify Nuclear Mode persists**:
- Log in normally (do NOT boot into Safe Mode)
- ServiceEngine should start automatically
- Expected: Apps are still being killed; Nuclear countdown continues from where it left off

**11. Wait for timer to expire**:
- Wait until the 5-minute timer expires
- Expected: Normal operation resumes; no more automatic kills

**12. Verify emergency stop still works in Armed Mode**:
```powershell
New-Item C:\goalkeeper_emergency_stop.txt -ItemType File
```
- Launch a distracting app — expected: no friction/kill
```powershell
Remove-Item C:\goalkeeper_emergency_stop.txt
```

**13. Restore snapshot** when done: revert to `Pre-GoalKeeper`

### Pass Criteria
- [ ] Diagnostics all pass
- [ ] Arm System completes successfully
- [ ] Task Manager CANNOT kill ServiceEngine (Access Denied)
- [ ] Nuclear Mode kills apps immediately
- [ ] Reboot: Nuclear Mode persists and re-engages locks
- [ ] Timer expiry: Normal operation resumes
- [ ] Emergency stop works even in Armed + Nuclear Mode

---

## Phase 3 — Safe Mode Recovery Test (VM ONLY)

> **Goal**: Verify that a user who accidentally locks themselves in Nuclear Mode can recover by booting into Safe Mode and uninstalling normally.

### VM Preparation

1. Start from a clean VM (restore `Pre-GoalKeeper` snapshot or take a new one `Pre-Recovery-Test`)
2. Install GoalKeeper and arm the system (follow Phase 2 steps 1–5)

### Steps

**1. Activate Nuclear Mode for 12 hours**:
- Nuclear tab → Strict → 720 minutes → Activate
- Confirm the red badge shows

**2. Verify the system is locked**:
- Task Manager: cannot kill ServiceEngine
- Opening distracting apps: killed immediately

**3. Reboot into Safe Mode**:
- `Start Menu → Power → Hold Shift → Restart`
- Choose: `Troubleshoot → Advanced options → Startup Settings → Restart`
- Press **`4`** to boot into Safe Mode (or `5` for Safe Mode with Networking)

**4. Verify GoalKeeperService is NOT running in Safe Mode**:
```powershell
Get-Service GoalKeeperService
# Status should be: Stopped
```
Or: Task Manager → Services tab — GoalKeeperService should show `Stopped`

**5. Uninstall GoalKeeper**:
- `Settings → Apps → GoalKeeper → Uninstall`
- Or: `Control Panel → Programs and Features → GoalKeeper → Uninstall`
- Complete the uninstaller

**6. Reboot into normal mode**:
```powershell
Restart-Computer
```
Do NOT hold Shift — just normal restart.

**7. Verify GoalKeeper is fully removed**:
```powershell
Get-Service GoalKeeperService
# Should fail with error: "Cannot find any service with service name 'GoalKeeperService'"

Test-Path "C:\Program Files\GoalKeeper"
# Should output: False

Test-Path "C:\ProgramData\GoalKeeper\metrics.sqlite"
# Note: data directory is NOT removed by uninstaller to preserve logs
```

**8. Verify no residual enforcement**:
- Open Notepad — expected: opens normally, no friction
- Open Chrome, any browser — expected: no interference

### Pass Criteria
- [ ] GoalKeeperService NOT running in Safe Mode
- [ ] Uninstall completes successfully from Safe Mode
- [ ] Normal reboot succeeds
- [ ] No service installed after removal
- [ ] No app enforcement on normal apps after removal

---

## Troubleshooting

### Friction overlay doesn't appear
1. Check ServiceEngine log: `C:\ProgramData\GoalKeeper\logs\service-engine-*.txt`
2. Check ConfigUI log: `C:\ProgramData\GoalKeeper\logs\config-ui-*.txt`
3. Verify the IPC connection (green dot in top bar)
4. Verify a category rule exists for the test app

### "Service not running" after reboot (Audit Mode)
- ServiceEngine was launched with `dotnet run` which doesn't register as a Windows Service
- For reboot-persistence tests, install as a service: `sc.exe create GoalKeeperService binPath="..." start=auto`
- Or just re-launch manually after each reboot during Audit Mode tests

### Nuclear Mode countdown resets to 0 after reboot
- This indicates `NuclearEndTimeEpoch` in SQLite is not persisting
- Check `C:\ProgramData\GoalKeeper\metrics.sqlite` exists and is writable
- Check ServiceEngine logs for startup errors

### Uninstall fails in Armed Mode (non-Safe-Mode)
- This is expected behavior when the system is Armed and Nuclear Mode is active
- Solution: Boot into Safe Mode (Phase 3 procedure)

---

## Log File Locations

| Log | Path |
|-----|------|
| ServiceEngine | `C:\ProgramData\GoalKeeper\logs\service-engine-YYYYMMDD.txt` |
| ConfigUI | `C:\ProgramData\GoalKeeper\logs\config-ui-YYYYMMDD.txt` |
| SQLite Database | `C:\ProgramData\GoalKeeper\metrics.sqlite` |

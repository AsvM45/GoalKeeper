# VM Setup Guide for Safe GoalKeeper Testing

> **Critical**: Never test Armed Mode or Nuclear Mode on your main machine until you are extremely confident in your configuration. Always use a VM with snapshots.

---

## Why a VM?

GoalKeeper in Armed Mode:
- Strips its own `PROCESS_TERMINATE` rights (Task Manager cannot kill it)
- Persists Nuclear Mode across reboots via SQLite + service auto-start
- Modifies DACL permissions at the OS kernel level

A VM snapshot lets you restore your environment in seconds if something goes wrong.

---

## Option A: Hyper-V (Recommended – Free, Built Into Windows)

### Prerequisites
- Windows 10/11 **Pro, Enterprise, or Education** (not Home)
- At least 8GB RAM, 60GB free disk

### Step 1: Enable Hyper-V
1. Press `Win + R`, type `optionalfeatures`, press Enter
2. Check **Hyper-V** (both sub-items) and click OK
3. Restart when prompted

### Step 2: Get a Windows 11 VM Image
1. Go to [microsoft.com/en-us/software-download/windows11](https://www.microsoft.com/en-us/software-download/windows11)
2. Download the **Windows 11 Installation Media**
3. OR use: [microsoft.com/en-us/evalcenter/evaluate-windows-11-enterprise](https://www.microsoft.com/en-us/evalcenter/evaluate-windows-11-enterprise) for the 90-day evaluation ISO (the old pre-built VM download page no longer works as of early 2026)

### Step 3: Create the VM
```powershell
# Run in PowerShell as Administrator
New-VM -Name "GoalKeeperTest" -MemoryStartupBytes 4GB -Generation 2 -SwitchName "Default Switch"
Set-VM -Name "GoalKeeperTest" -ProcessorCount 2
Set-VMFirmware -VMName "GoalKeeperTest" -EnableSecureBoot Off
Add-VMDvdDrive -VMName "GoalKeeperTest" -Path "C:\path\to\Windows11.iso"
New-VHD -Path "C:\Hyper-V\GoalKeeperTest.vhdx" -SizeBytes 60GB -Dynamic
Add-VMHardDiskDrive -VMName "GoalKeeperTest" -Path "C:\Hyper-V\GoalKeeperTest.vhdx"
```

### Step 4: Install Windows in the VM
1. Start VM from Hyper-V Manager
2. Complete Windows Setup (no product key needed for testing – use "I don't have a product key")
3. Create a local account (no Microsoft account needed)

### Step 5: CRITICAL – Create a Checkpoint Before Testing
```
Hyper-V Manager → Select VM → Action → Checkpoint
Name it: "Clean Install – Before GoalKeeper"
```

To restore if something goes wrong:
```
Hyper-V Manager → Select VM → Checkpoints → Right-click → Apply
```

---

## Option B: VirtualBox (Free, Works on Windows Home)

### Step 1: Download VirtualBox
[virtualbox.org/wiki/Downloads](https://www.virtualbox.org/wiki/Downloads)
Download: `VirtualBox-x.x.x-Win.exe` and install it.

### Step 2: Download a Windows 11 VM Image
> **Note**: Microsoft's pre-built Windows 11 dev VM page (`developer.microsoft.com/en-us/windows/downloads/virtual-machines/`) no longer works — it redirects to an unrelated page as of early 2026.

Use one of these alternatives instead:
- **Windows 11 Enterprise Evaluation (ISO)**: [microsoft.com/en-us/evalcenter/evaluate-windows-11-enterprise](https://www.microsoft.com/en-us/evalcenter/evaluate-windows-11-enterprise) — free 90-day eval, download the ISO and create the VM manually in VirtualBox.
- **Windows 11 Installation Media**: [microsoft.com/en-us/software-download/windows11](https://www.microsoft.com/en-us/software-download/windows11) — download the ISO, use "I don't have a product key" during setup.

### Step 3: Import the VM
1. Open VirtualBox → File → Import Appliance
2. Select the downloaded `.ova` file
3. Set RAM to at least 4096 MB, CPU to 2 cores
4. Click Import

### Step 4: CRITICAL – Take a Snapshot
```
VirtualBox → Select VM → Machine → Take Snapshot
Name it: "Clean Install – Before GoalKeeper"
```

To restore: Machine → Restore Snapshot

---

## Testing Protocol

### Phase 1: Audit Mode Testing (Safe on any machine)
1. Install GoalKeeper normally
2. Configure your categories, budgets, blocklist
3. Verify friction overlays appear correctly
4. Verify tracking works
5. Try to kill the process via Task Manager – it should work in Audit Mode

### Phase 2: Armed Mode Testing (VM only, with snapshot)

> **Take a VM snapshot NOW before proceeding**

1. Create the developer bypass flag on your main dev machine:
   ```
   echo "" > C:\dev_bypass_flag.txt
   ```
   This prevents accidental arming during development.

2. In the VM: delete the bypass flag if it exists
   ```
   del C:\dev_bypass_flag.txt
   ```

3. In the VM: run the Setup Wizard → Run Diagnostics → Arm System
4. Verify Task Manager cannot kill GoalKeeperService
5. Activate Nuclear Mode for 30 minutes
6. Reboot the VM – verify Nuclear Mode persists

### Phase 3: Safe Mode Recovery Test (VM only)

> **Take a VM snapshot NOW before proceeding**

1. With Nuclear Mode active and system Armed in the VM:
2. Go to Settings → Update & Security → Recovery → Advanced Startup → Restart Now
3. Choose: Troubleshoot → Advanced → Startup Settings → Restart
4. Press **4** (Safe Mode)
5. Verify GoalKeeperService is NOT running: `sc query GoalKeeperService`
6. Open Add/Remove Programs → Uninstall GoalKeeper
7. Reboot normally – verify GoalKeeper is gone

---

## Sharing Code Between Host and VM

### VirtualBox
Enable Shared Folders:
```
VM Settings → Shared Folders → Add
Host Path: C:\Users\YourName\OneDrive\Documents\GitHub\GoalKeeper
Mount Point: Z:\
Auto-mount: Yes
```

### Hyper-V
Use either:
- Enhanced Session Mode (allows clipboard/drive sharing)
- Map a network share from host to VM

---

## Quick Troubleshooting

| Problem | Solution |
|---------|---------|
| Locked out of VM (Nuclear + Armed) | Restore snapshot |
| Service won't stop on uninstall | Boot Safe Mode, then uninstall |
| VM won't boot after GoalKeeper changes | Restore checkpoint/snapshot |
| Task Manager can kill service | Armed Mode isn't active (expected in Audit Mode) |
| AI service not responding | Check if Python is running on port 8099 |

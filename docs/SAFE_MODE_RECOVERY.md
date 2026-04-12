# Safe Mode Recovery Guide

> Read this BEFORE arming the system. Understanding this procedure is your safety net.

---

## When Do You Need This?

You need Safe Mode recovery if:
- You armed the system AND activated Nuclear Mode
- You forgot to whitelist a critical Windows process
- You cannot access the GoalKeeper UI to deactivate locks
- The system is in a state where normal uninstall fails

---

## How Safe Mode Helps

Windows Safe Mode loads the **minimum required drivers and services** only.
GoalKeeper's background service (`GoalKeeperService`) is NOT a minimum required service,
so it will **not start** in Safe Mode.

This means: in Safe Mode, GoalKeeper has no active locks, no DACL protection,
and you can uninstall it normally.

---

## Step-by-Step Recovery

### Method 1: From Windows Settings (Preferred)

1. Open **Start Menu** → **Settings** ⚙️
2. Go to **System** → **Recovery**
3. Under "Advanced startup", click **Restart now**
4. After reboot, choose:
   - **Troubleshoot** → **Advanced options** → **Startup Settings** → **Restart**
5. Press **4** or **F4** to enter Safe Mode

### Method 2: Shift + Restart (If Settings is Accessible)

1. Click **Start** → **Power**
2. Hold **Shift** and click **Restart**
3. Same menu as Method 1 step 4

### Method 3: From Login Screen (If Locked Out of Desktop)

1. At the login screen, click the **Power** icon (bottom right)
2. Hold **Shift** and click **Restart**
3. Same menu as Method 1 step 4

### Method 4: Force Reboot (Last Resort)

1. Hold the power button until the PC shuts off
2. Turn it back on
3. As it boots, hold **F8** (or **Shift + F8** on some systems)
4. This may show the Advanced Boot Options menu
5. Select **Safe Mode**

> Note: F8 doesn't work on all modern Windows 11 systems. Methods 1-3 are more reliable.

---

## Once in Safe Mode

1. Open **Task Manager** (`Ctrl + Shift + Esc`)
2. Verify `GoalKeeperService` is NOT listed – it should be absent
3. Open **Control Panel** → **Programs** → **Programs and Features**
   (or Settings → Apps → Installed apps)
4. Find **GoalKeeper** and click **Uninstall**
5. The uninstaller will stop and delete the service
6. Reboot normally

### Manual Removal (If Uninstaller Fails)

Open an **Administrator Command Prompt** in Safe Mode:

```cmd
:: Stop the service (may already be stopped in Safe Mode)
sc stop GoalKeeperService

:: Delete the service registration
sc delete GoalKeeperService

:: Remove program files
rmdir /s /q "C:\Program Files\GoalKeeper"

:: Remove app data
rmdir /s /q "C:\ProgramData\GoalKeeper"
```

---

## Prevention Tips

Before arming the system:

1. **Whitelist all critical Windows processes** you use:
   - `explorer.exe` – Windows Explorer / File Manager
   - `taskmgr.exe` – Task Manager (you may want this blocked though!)
   - Any background sync tools (OneDrive, Dropbox, etc.)
   - Your VPN client
   - Any hardware management tools

2. **Test your whitelist thoroughly** in Audit Mode first

3. **Write down the Safe Mode recovery steps** on paper before arming

4. **Tell someone you trust** that you're arming the system and how to help if needed

5. **Use Nuclear Mode sparingly** – start with 30-minute sessions to test

---

## Developer Notes

If you're developing GoalKeeper itself, create this file to prevent any accidental arming:

```
C:\dev_bypass_flag.txt
```

As long as this file exists, `IsArmed` is forced to `false` regardless of the database state.
The service checks for this file on every startup.

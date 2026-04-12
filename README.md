# GoalKeeper – Ultimate Productivity System

> Apple Screen Time + ScreenZen + StayFocusd, merged into one tamper-proof Windows app.

[![Build and Release](https://github.com/yourusername/GoalKeeper/actions/workflows/build-and-release.yml/badge.svg)](https://github.com/yourusername/GoalKeeper/actions)

---

## What Is This?

GoalKeeper is a Windows Desktop productivity application that merges the three most effective digital wellness philosophies:

- **Apple Screen Time** – Analytics, shared time budgets, downtime scheduling
- **ScreenZen** – Mindful friction overlays, launch limits, per-session auto-close
- **StayFocusd** – Irrevocable Nuclear Mode, typing challenges, OS-level tamper-proofing

It runs as a Windows Service using event-driven OS APIs (WMI + SetWinEventHook) with **zero browser extensions** and **zero polling**. It works across all browsers, all apps, and even incognito mode.

An optional **Groq AI Smart Block** feature intelligently classifies websites and apps against your stated goals, replacing static blocklists with context-aware judgment.

---

## Features

### Analytics (Screen Time)
- Passive tracking of all app/website usage, categorized as productive/distracting/neutral
- "Pickup count" tracking (context switches to distracting content)
- Productivity score, daily trends, weekly charts
- Shared time pools across categories (e.g. "1h total for all social media")

### Mindful Friction (ScreenZen)
- Full-screen breathing overlay when you try to open a distracting app
- Configurable countdown (10–30 seconds) before you can continue
- Cancel button immediately closes the distraction
- Launch limits (e.g., "Reddit only 5 times/day")
- Auto-close after session timer expires (e.g., 5 minutes)

### Enforcement (StayFocusd)
- **Nuclear Mode** – irrevocable lockdown with three sub-modes:
  - *Full Offline* – blocks all browsers and network apps
  - *Whitelist Only* – blocks everything except your approved tools
  - *Strict Blocklist* – revokes all remaining daily allowances immediately
- **Typing Challenge** – must type a 250-word paragraph without backspace to change settings
- **Partner Lock** – lock settings with a password held by an accountability partner

### AI Smart Block (Groq)
- Powered by `llama3-70b-8192` via Groq API
- Classifies websites/apps against your stated goals in real time
- 24-hour cache avoids redundant API calls
- Override AI decisions manually
- Falls back to static rules if Groq is offline

### Safe Deployment
- Starts in **Audit Mode** – full functionality, but Task Manager can still kill it
- **Arm System** gateway with self-diagnostic check + typing challenge
- **Developer bypass** – create `C:\dev_bypass_flag.txt` to disable arming
- Safe Mode recovery for locked-out users (see below)

---

## Architecture

```
ServiceEngine (C# Worker Service – SYSTEM level)
  ├── ProcessWatcher      – WMI __InstanceCreationEvent (app launches)
  ├── WindowWatcher       – SetWinEventHook (zero polling, foreground events)
  ├── StateManager        – Evaluation pipeline (nuclear → downtime → AI → budget → friction)
  ├── ScreenTimeLogger    – Thread-safe SQLite writer
  ├── SecurityEnforcer    – DACL protection + reboot immunity (Armed Mode only)
  └── PipeServer          – Named Pipe IPC server

ConfigUI (C# WPF Application – user facing)
  ├── Dashboard           – Analytics charts, pickup counts, productivity score
  ├── FrictionOverlay     – Full-screen ScreenZen pause with countdown
  ├── TypingChallenge     – StayFocusd-style text challenge
  └── SetupWizard         – Audit Mode → Armed Mode transition

AIService (Python FastAPI – localhost:8099)
  └── Groq llama3-70b     – Website/app classification against user goals

Database (SQLite – C:\ProgramData\GoalKeeper\metrics.sqlite)
  ├── ScreenTimeLog       – Per-window usage records
  ├── Budgets             – Shared time pools + launch limits
  ├── SystemState         – IsArmed, NuclearMode, Downtime, etc.
  ├── AICache             – 24h Groq classification cache
  └── PickupLog           – Context-switch tracking
```

---

## ⚠️ SAFE MODE RECOVERY ⚠️

**Read this before arming the system.**

If you activate Nuclear Mode and the system is Armed, GoalKeeper applies OS-level locks.
If you accidentally block a critical process, here's how to recover:

1. **Hold Shift and click Restart** from the Start Menu (or Power button on login screen)
2. Choose: **Troubleshoot → Advanced options → Startup Settings → Restart**
3. Press **4** to boot into **Safe Mode**
4. In Safe Mode, `GoalKeeperService` will NOT start
5. Open **Add/Remove Programs** and uninstall GoalKeeper normally
6. Reboot into normal mode

> See [docs/SAFE_MODE_RECOVERY.md](docs/SAFE_MODE_RECOVERY.md) for detailed instructions.

---

## Developer Setup

> **IMPORTANT**: Create `C:\dev_bypass_flag.txt` before running. This prevents any DACL locks from activating during development.

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Python 3.11+](https://www.python.org/downloads/)
- Windows 10/11 (64-bit)
- A [Groq API key](https://console.groq.com/) (free, optional)

### Quick Start

```powershell
# 1. Clone the repo
git clone https://github.com/yourusername/GoalKeeper.git
cd GoalKeeper

# 2. Safety first
echo "" > C:\dev_bypass_flag.txt

# 3. Set up AI service
cd AIService
copy .env.example .env
# Edit .env and add your GROQ_API_KEY
pip install -r requirements.txt
python main.py  # Runs on localhost:8099

# 4. Build and run the service (separate terminal, as Admin)
cd ServiceEngine
dotnet run

# 5. Run the UI (separate terminal)
cd ConfigUI
dotnet run
```

### Testing on a VM

**Always test Armed Mode in a VM with snapshots.** See [docs/VM_SETUP.md](docs/VM_SETUP.md) for:
- Hyper-V setup (free, Windows 10/11 Pro)
- VirtualBox setup (free, works on Home)
- Snapshot strategy
- Full testing protocol

---

## Building the Installer

```powershell
# Publish both projects
dotnet publish ServiceEngine -c Release -r win-x64 --self-contained false
dotnet publish ConfigUI -c Release -r win-x64 --self-contained false

# Compile installer (requires Inno Setup)
choco install innosetup
iscc Installer\setup.iss
# Output: Installer\Output\GoalKeeperSetup.exe
```

---

## CI/CD

Pushing a git tag triggers the GitHub Actions workflow:
```
git tag v1.0.0
git push origin v1.0.0
```

This builds, packages, and publishes `GoalKeeperSetup.exe` to GitHub Releases automatically.

---

## Directory Structure

```
/GoalKeeper
├── GoalKeeper.sln
├── README.md
├── .gitignore
├── .github/workflows/
│   └── build-and-release.yml
├── ServiceEngine/          C# Worker Service (SYSTEM-level)
│   ├── Workers/            WMI + SetWinEventHook watchers
│   ├── Core/               StateManager, Logger, SecurityEnforcer
│   ├── IPC/                Named Pipe server + protocol
│   └── AI/                 HTTP client to Python AI service
├── ConfigUI/               C# WPF Application (user-facing)
│   ├── Views/              MainWindow, FrictionOverlay, TypingChallenge, SetupWizard
│   ├── ViewModels/         MVVM ViewModels
│   └── Services/           Named Pipe client
├── AIService/              Python FastAPI (Groq AI)
├── Database/               schema.sql reference
├── Installer/              Inno Setup script
├── docs/                   VM_SETUP.md, SAFE_MODE_RECOVERY.md
└── legacy/                 Original Python prototype (archived)
```

---

## License

MIT License. See LICENSE file.

---

## Acknowledgments

Inspired by Apple Screen Time, ScreenZen, and StayFocusd.
Built with .NET 8, WPF, Material Design, Groq AI, and SQLite.

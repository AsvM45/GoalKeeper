#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Pre-flight safety check for GoalKeeper testing sessions.
    Must pass before running Phase 2 (Armed Mode) or Phase 3 (Recovery) tests.

.DESCRIPTION
    Checks:
    1. Whether you are inside a VM (required for Armed Mode tests)
    2. Whether C:\dev_bypass_flag.txt exists (required for Audit Mode dev testing)
    3. Whether GoalKeeperService is already installed
    4. Whether a leftover C:\goalkeeper_emergency_stop.txt exists

    Exit codes:
      0 = All checks passed, safe to proceed with requested test phase
      1 = One or more critical checks failed
#>

param(
    [ValidateSet("audit", "armed", "recovery", "any")]
    [string]$Phase = "any"
)

$ErrorActionPreference = "Continue"
$failed = $false

function Write-Check {
    param([string]$Label, [bool]$OK, [string]$Detail = "")
    if ($OK) {
        Write-Host "  [PASS] $Label" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] $Label" -ForegroundColor Red
        if ($Detail) { Write-Host "         $Detail" -ForegroundColor Yellow }
    }
}

function Write-Warn {
    param([string]$Label, [string]$Detail = "")
    Write-Host "  [WARN] $Label" -ForegroundColor Yellow
    if ($Detail) { Write-Host "         $Detail" -ForegroundColor DarkYellow }
}

Write-Host ""
Write-Host "GoalKeeper Pre-Flight Safety Check" -ForegroundColor Cyan
Write-Host "====================================" -ForegroundColor Cyan
Write-Host "Phase: $Phase"
Write-Host ""

# ── 1. VM Detection ────────────────────────────────────────────────────────────
Write-Host "[ VM Detection ]" -ForegroundColor White

$isVM = $false
$vmReason = ""

try {
    $csModel = (Get-WmiObject Win32_ComputerSystem -ErrorAction Stop).Model
    $csManuf = (Get-WmiObject Win32_ComputerSystem -ErrorAction Stop).Manufacturer

    $vmIndicators = @("Virtual", "VMware", "VirtualBox", "Hyper-V", "QEMU", "KVM", "Bochs", "Xen")
    foreach ($indicator in $vmIndicators) {
        if ($csModel -like "*$indicator*" -or $csManuf -like "*$indicator*") {
            $isVM = $true
            $vmReason = "Model='$csModel' / Manufacturer='$csManuf'"
            break
        }
    }

    # Also check BIOS
    if (-not $isVM) {
        $biosVersion = (Get-WmiObject Win32_BIOS -ErrorAction SilentlyContinue).BIOSVersion
        if ($biosVersion -join "," -match "VBOX|VMWARE|VIRTUAL|QEMU|XEN") {
            $isVM = $true
            $vmReason = "BIOS: $($biosVersion -join ', ')"
        }
    }
} catch {
    Write-Warn "Could not query WMI for VM detection" "Assuming physical machine. Error: $($_.Exception.Message)"
}

if ($isVM) {
    Write-Check "Running inside a VM ($vmReason)" $true
} else {
    if ($Phase -in @("armed", "recovery")) {
        Write-Check "Running inside a VM" $false "Armed Mode / Recovery tests MUST run in a VM. This appears to be a physical machine."
        $failed = $true
    } else {
        Write-Warn "Not detected as a VM" "This is OK for Phase 0 and Phase 1 (Audit Mode). Do NOT run Phase 2+ here."
    }
}

# ── 2. Dev Bypass Flag ─────────────────────────────────────────────────────────
Write-Host ""
Write-Host "[ Developer Safety Bypass ]" -ForegroundColor White

$bypassExists = Test-Path "C:\dev_bypass_flag.txt"

if ($bypassExists) {
    if ($Phase -in @("armed", "recovery")) {
        Write-Check "C:\dev_bypass_flag.txt is ABSENT (required for Armed Mode)" $false `
            "Delete this file in the VM before running Phase 2/3: Remove-Item C:\dev_bypass_flag.txt"
        $failed = $true
    } else {
        Write-Check "C:\dev_bypass_flag.txt EXISTS (Audit Mode is safe to test)" $true
    }
} else {
    if ($Phase -in @("audit", "any")) {
        Write-Warn "C:\dev_bypass_flag.txt is ABSENT" `
            "For Phase 1 Audit Mode on dev machine, create it: New-Item C:\dev_bypass_flag.txt -ItemType File"
    } else {
        Write-Check "C:\dev_bypass_flag.txt is ABSENT (correct for Phase 2/3)" $true
    }
}

# ── 3. Service Installation Status ────────────────────────────────────────────
Write-Host ""
Write-Host "[ GoalKeeperService Status ]" -ForegroundColor White

try {
    $svc = Get-Service "GoalKeeperService" -ErrorAction Stop
    Write-Warn "GoalKeeperService is installed (Status: $($svc.Status))" `
        "If this is a fresh test, consider uninstalling first or restoring a VM snapshot."
} catch {
    Write-Check "GoalKeeperService is NOT installed (clean state)" $true
}

# ── 4. Emergency Stop File ────────────────────────────────────────────────────
Write-Host ""
Write-Host "[ Emergency Stop File ]" -ForegroundColor White

$emergencyExists = Test-Path "C:\goalkeeper_emergency_stop.txt"
if ($emergencyExists) {
    Write-Warn "C:\goalkeeper_emergency_stop.txt EXISTS" `
        "This disables ALL enforcement. Remove it when done testing: Remove-Item C:\goalkeeper_emergency_stop.txt"
} else {
    Write-Check "C:\goalkeeper_emergency_stop.txt is absent (normal state)" $true
}

# ── 5. Log Directory ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "[ Log Directory ]" -ForegroundColor White

$logDir = "C:\ProgramData\GoalKeeper\logs"
if (Test-Path $logDir) {
    $logFiles = Get-ChildItem $logDir -Filter "*.txt" | Sort-Object LastWriteTime -Descending | Select-Object -First 3
    if ($logFiles) {
        Write-Host "  [INFO] Recent log files:" -ForegroundColor Cyan
        foreach ($f in $logFiles) {
            Write-Host "         $($f.Name) ($(($f.Length / 1KB).ToString('0.#')) KB, $($f.LastWriteTime.ToString('HH:mm:ss')))" -ForegroundColor DarkCyan
        }
    } else {
        Write-Check "Log directory exists (no logs yet)" $true
    }
} else {
    Write-Host "  [INFO] Log directory not yet created (service hasn't run)" -ForegroundColor DarkGray
}

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "====================================" -ForegroundColor Cyan

if ($failed) {
    Write-Host "PRE-FLIGHT FAILED — resolve the issues above before proceeding." -ForegroundColor Red
    Write-Host ""
    exit 1
} else {
    Write-Host "PRE-FLIGHT PASSED — system is ready for testing." -ForegroundColor Green
    Write-Host ""

    if ($Phase -in @("armed", "recovery") -and $isVM) {
        Write-Host "REMINDER: Take a VM snapshot named 'Pre-GoalKeeper' before proceeding." -ForegroundColor Yellow
        Write-Host "          If anything goes wrong, you can restore to this snapshot." -ForegroundColor Yellow
        Write-Host ""
    }

    exit 0
}

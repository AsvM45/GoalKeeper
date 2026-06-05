# GoalKeeper disposable sandbox bootstrap
# Installs .NET 8 Desktop Runtime silently and starts WinAppDriver for Appium UI tests.
# Runs inside Windows Sandbox — does NOT affect the host.

$ErrorActionPreference = 'Stop'
$logDir = 'C:\GoalKeeper\sandbox\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir 'bootstrap.log'

function Write-Log([string]$Message) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message"
    Add-Content -Path $logFile -Value $line
}

Write-Log 'bootstrap_sandbox.ps1 started'

# ── .NET 8 Desktop Runtime (silent) ───────────────────────────────────────────
$dotnetDesktop = Get-ItemProperty 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App' -ErrorAction SilentlyContinue
$hasNet8 = $dotnetDesktop -and ($dotnetDesktop.GetEnumerator() | Where-Object { $_.Name -match '^8\.' })

if (-not $hasNet8) {
    Write-Log 'Installing .NET 8 Desktop Runtime…'
    $installer = Join-Path $env:TEMP 'windowsdesktop-runtime-8-win-x64.exe'
    $url = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.16/windowsdesktop-runtime-8.0.16-win-x64.exe'
    Invoke-WebRequest -Uri $url -OutFile $installer -UseBasicParsing
    $proc = Start-Process -FilePath $installer -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    Write-Log ".NET installer exit code: $($proc.ExitCode)"
}
else {
    Write-Log '.NET 8 Desktop Runtime already present'
}

# ── WinAppDriver for Appium (127.0.0.1:4723) ────────────────────────────────
$winAppDriverPaths = @(
    'C:\GoalKeeper\sandbox\tools\WinAppDriver\WinAppDriver.exe',
    "${env:ProgramFiles(x86)}\Windows Application Driver\WinAppDriver.exe",
    "$env:LOCALAPPDATA\WinAppDriver\WinAppDriver.exe"
)

$winAppDriver = $winAppDriverPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $winAppDriver) {
    Write-Log 'WinAppDriver not found — download from GitHub releases into sandbox\tools\WinAppDriver'
}
else {
    Write-Log "Starting WinAppDriver: $winAppDriver"
    Start-Process -FilePath $winAppDriver -ArgumentList '127.0.0.1', '4723' -WindowStyle Hidden
    Write-Log 'WinAppDriver listening on 127.0.0.1:4723'
}

# ── Launch GoalKeeper in Audit Mode (safe — no host DACL locks) ───────────────
$bypassFlag = 'C:\GoalKeeper\sandbox\dev_bypass_flag.txt'
if (-not (Test-Path $bypassFlag)) {
    Set-Content -Path $bypassFlag -Value 'sandbox-audit-mode' -Encoding ASCII
}

$configUi = @(
    'C:\GoalKeeper\ConfigUI\bin\Release\net8.0-windows\win-x64\ConfigUI.exe',
    'C:\GoalKeeper\ConfigUI\bin\Debug\net8.0-windows\win-x64\ConfigUI.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1

$serviceEngine = @(
    'C:\GoalKeeper\ServiceEngine\bin\Release\net8.0-windows\win-x64\ServiceEngine.exe',
    'C:\GoalKeeper\ServiceEngine\bin\Debug\net8.0-windows\win-x64\ServiceEngine.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($serviceEngine) {
    Write-Log "Starting ServiceEngine: $serviceEngine"
    Start-Process -FilePath $serviceEngine -WindowStyle Hidden
    Start-Sleep -Seconds 3
}

if ($configUi) {
    Write-Log "Starting ConfigUI: $configUi"
    Start-Process -FilePath $configUi
}
else {
    Write-Log 'ConfigUI binary not found — run dotnet build before launching sandbox'
}

Write-Log 'bootstrap_sandbox.ps1 complete'

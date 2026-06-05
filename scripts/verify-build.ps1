$ErrorActionPreference = "Stop"

Write-Host "Verifying Solution Build..." -ForegroundColor Cyan
dotnet build ..\GoalKeeper.sln -c Debug --nologo
dotnet build ..\GoalKeeper.sln -c Release --nologo

Write-Host "Running Unit Tests..." -ForegroundColor Cyan
dotnet test ..\ServiceEngine.Tests\ServiceEngine.Tests.csproj --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Tests failed - see output above." -ForegroundColor Red
    exit 1
}

Write-Host "Verifying Publish Configuration (ServiceEngine)..." -ForegroundColor Cyan
dotnet publish ..\ServiceEngine\ServiceEngine.csproj -c Release -r win-x64 --self-contained false -o pub_se --nologo

Write-Host "Verifying Publish Configuration (ConfigUI)..." -ForegroundColor Cyan
dotnet publish ..\ConfigUI\ConfigUI.csproj -c Release -r win-x64 --self-contained false -o pub_ui --nologo

Write-Host "Verifying output files exist..." -ForegroundColor Cyan
if (-not (Test-Path "pub_se\ServiceEngine.exe")) {
    Write-Host "FAIL: ServiceEngine.exe not found in publish output" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path "pub_ui\ConfigUI.exe")) {
    Write-Host "FAIL: ConfigUI.exe not found in publish output" -ForegroundColor Red
    exit 1
}

Write-Host "Cleaning up publish directories..." -ForegroundColor Cyan
Remove-Item -Recurse -Force pub_se
Remove-Item -Recurse -Force pub_ui

Write-Host "Build Verification Successful!" -ForegroundColor Green

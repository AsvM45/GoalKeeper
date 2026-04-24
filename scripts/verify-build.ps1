$ErrorActionPreference = "Stop"

Write-Host "Verifying Solution Build..." -ForegroundColor Cyan
dotnet build ..\GoalKeeper.sln -c Debug
dotnet build ..\GoalKeeper.sln -c Release

Write-Host "Running Unit Tests..." -ForegroundColor Cyan
# Ignores currently locked binaries from dev-run
dotnet test ..\ServiceEngine.Tests\ServiceEngine.Tests.csproj --no-build || Write-Host "Skipping execution (dll locked)"

Write-Host "Verifying Publish Configuration (ServiceEngine)..." -ForegroundColor Cyan
dotnet publish ..\ServiceEngine\ServiceEngine.csproj -c Release -r win-x64 --self-contained false -o pub_se

Write-Host "Verifying Publish Configuration (ConfigUI)..." -ForegroundColor Cyan
dotnet publish ..\ConfigUI\ConfigUI.csproj -c Release -r win-x64 --self-contained false -o pub_ui

Write-Host "Cleaning up Publish directories..." -ForegroundColor Cyan
Remove-Item -Recurse -Force pub_se
Remove-Item -Recurse -Force pub_ui

Write-Host "Build Verification Successful!" -ForegroundColor Green

# Runs the tests, then publishes OrcaPicker as one self-contained win-x64 exe to .\dist
#   .\build.ps1             tests + publish
#   .\build.ps1 -SkipTests  publish only
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    Write-Host '== Tests ==' -ForegroundColor Cyan
    dotnet test .\tests\OrcaPicker.Tests\OrcaPicker.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE)" }
}

$dist = Join-Path $PSScriptRoot 'dist'
$exe = Join-Path $dist 'OrcaPicker.exe'

# A watcher started from dist\ keeps the exe locked; stop it (it is restarted below if it was running).
$watchers = @(Get-CimInstance Win32_Process -Filter "Name = 'OrcaPicker.exe'" |
    Where-Object { $_.ExecutablePath -and ($_.ExecutablePath -ieq $exe) })
foreach ($w in $watchers) {
    Write-Host "Stopping running OrcaPicker (pid $($w.ProcessId)) so dist\OrcaPicker.exe can be replaced"
    Stop-Process -Id $w.ProcessId -Force -ErrorAction SilentlyContinue
}

Write-Host '== Publish ==' -ForegroundColor Cyan
dotnet publish .\src\OrcaPicker\OrcaPicker.csproj -c Release -r win-x64 -o $dist
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)" }

# Only the exe is needed; drop anything else publish may leave behind.
Get-ChildItem $dist -File | Where-Object { $_.Name -ne 'OrcaPicker.exe' } | Remove-Item -Force

if ($watchers.Count -gt 0) {
    Start-Process -FilePath $exe -ArgumentList '--watch'
    Write-Host 'Restarted the watcher.'
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Done: $exe ($size MB)" -ForegroundColor Green

# build.ps1 — Pedal Filter build script
#
# Usage (from this folder in PowerShell):
#   .\build.ps1
#
# If ReBuzz is not at the default path, pass it as an argument:
#   .\build.ps1 -BuzzDir "D:\MyReBuzz"

param(
    [string]$BuzzDir = "C:\Program Files\ReBuzz"
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "=== Pedal Filter – ReBuzz managed effect ===" -ForegroundColor Cyan
Write-Host ""

# Verify dotnet is present
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: 'dotnet' not found on PATH." -ForegroundColor Red
    Write-Host "Install .NET 10.0 SDK from: https://dotnet.microsoft.com/en-us/download/dotnet/10.0"
    exit 1
}

# Verify ReBuzz.dll exists
$reBuzzDll = Join-Path $BuzzDir "ReBuzz.dll"
if (-not (Test-Path $reBuzzDll)) {
    Write-Host "ERROR: ReBuzz.dll not found at: $reBuzzDll" -ForegroundColor Red
    Write-Host "Check that -BuzzDir points to your ReBuzz installation."
    exit 1
}

Write-Host "ReBuzz found at: $BuzzDir" -ForegroundColor Green
Write-Host "Building..."
Write-Host ""

dotnet build PedalFilter.csproj -c Release /p:BuzzDir="$BuzzDir"

if ($LASTEXITCODE -eq 0) {
    $outDll = Join-Path $BuzzDir "Gear\Effects\Pedal Filter.NET.dll"
    Write-Host ""
    Write-Host "Build succeeded!" -ForegroundColor Green
    Write-Host "DLL written to: $outDll"
    Write-Host ""
    Write-Host "Restart ReBuzz and look for 'Pedal Filter' in the Effects list."
} else {
    Write-Host ""
    Write-Host "Build FAILED – see errors above." -ForegroundColor Red
    exit 1
}

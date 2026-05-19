param([string]$BuzzDir = "C:\Program Files\ReBuzz")
$ErrorActionPreference = "Stop"
Write-Host "=== Pedal Filter ===" -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Write-Host "dotnet not found" -ForegroundColor Red; exit 1 }
if (-not (Test-Path (Join-Path $BuzzDir "ReBuzz.dll"))) { Write-Host "ReBuzz.dll not found at $BuzzDir" -ForegroundColor Red; exit 1 }
dotnet build PedalFilter.csproj -c Release /p:BuzzDir="$BuzzDir"
if ($LASTEXITCODE -eq 0) { Write-Host "Done. Restart ReBuzz." -ForegroundColor Green } else { exit 1 }

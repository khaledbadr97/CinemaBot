param(
    [string]$PublishRoot = "C:\CinemaBot"
)

$ErrorActionPreference = "Stop"
$serviceExe = Join-Path $PublishRoot "Service\CinemaBot.Service.exe"
$watchdogExe = Join-Path $PublishRoot "Watchdog\CinemaBot.Watchdog.exe"

if (-not (Test-Path $serviceExe)) { throw "Missing: $serviceExe" }
if (-not (Test-Path $watchdogExe)) { throw "Missing: $watchdogExe" }

sc.exe stop CinemaBot 2>$null | Out-Null
sc.exe stop CinemaBotWatchdog 2>$null | Out-Null
sc.exe delete CinemaBot 2>$null | Out-Null
sc.exe delete CinemaBotWatchdog 2>$null | Out-Null
Start-Sleep -Seconds 2

sc.exe create CinemaBot binPath= ('"' + $serviceExe + '"') start= auto DisplayName= "CinemaBot"
sc.exe description CinemaBot "Cinema monitoring, Telegram dashboards, booking alerts, and Coming Soon tracking."
sc.exe failure CinemaBot reset= 86400 actions= restart/5000/restart/15000/restart/30000

sc.exe create CinemaBotWatchdog binPath= ('"' + $watchdogExe + '"') start= auto DisplayName= "CinemaBot Watchdog"
sc.exe description CinemaBotWatchdog "Monitors CinemaBot and sends bilingual offline/recovery Telegram alerts."
sc.exe failure CinemaBotWatchdog reset= 86400 actions= restart/5000/restart/15000/restart/30000

sc.exe start CinemaBotWatchdog
sc.exe start CinemaBot
Write-Host "CinemaBot and Watchdog installed." -ForegroundColor Green

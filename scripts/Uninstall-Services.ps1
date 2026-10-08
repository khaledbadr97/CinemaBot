$ErrorActionPreference = "Continue"
sc.exe stop CinemaBot
sc.exe stop CinemaBotWatchdog
Start-Sleep -Seconds 2
sc.exe delete CinemaBot
sc.exe delete CinemaBotWatchdog
Write-Host "CinemaBot services removed." -ForegroundColor Yellow

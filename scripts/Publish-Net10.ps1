$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "publish"

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item $out -ItemType Directory | Out-Null

dotnet restore (Join-Path $root "CinemaBot.sln")
dotnet publish (Join-Path $root "src\CinemaBot.Service\CinemaBot.Service.csproj") -c Release -o (Join-Path $out "Service")
dotnet publish (Join-Path $root "src\CinemaBot.Watchdog\CinemaBot.Watchdog.csproj") -c Release -o (Join-Path $out "Watchdog")

Write-Host "Published to $out" -ForegroundColor Green

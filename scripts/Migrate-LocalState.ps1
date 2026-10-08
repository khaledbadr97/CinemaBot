param(
    [Parameter(Mandatory = $true)]
    [string]$DatabaseUrl,

    [string]$DataDirectory = "$env:ProgramData\CinemaBot"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot

$env:Storage__UsePostgres = "true"
$env:DATABASE_URL = $DatabaseUrl

Write-Host "Migrating CinemaBot state from: $DataDirectory" -ForegroundColor Cyan
Write-Host "The database URL is used only through the current process environment and is not written to source control." -ForegroundColor DarkGray

dotnet run --project (Join-Path $root "src\CinemaBot.Service\CinemaBot.Service.csproj") -- --migrate-local-state $DataDirectory

if ($LASTEXITCODE -ne 0) {
    throw "CinemaBot state migration failed with exit code $LASTEXITCODE."
}

Write-Host "CinemaBot state migration completed." -ForegroundColor Green

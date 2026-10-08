$root = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $root "src\CinemaBot.Service\CinemaBot.Service.csproj")

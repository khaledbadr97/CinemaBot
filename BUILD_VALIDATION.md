# Build validation

- All C# files passed delimiter and structural checks.
- All JSON configuration files were parsed successfully.
- All `.csproj` files were parsed as valid XML.
- No `bin`, `obj`, or `.vs` directories are included.
- No real Telegram bot token is included.
- The current execution environment does not contain the .NET SDK, so a real `dotnet restore/build` could not be executed here.

Run on a machine with the .NET 10 SDK:

```powershell
cd <CinemaBot_Net10-Render folder>
.\scripts\Publish-Net10.ps1
```

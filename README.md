# CinemaBot (.NET 10 / Render)

CinemaBot monitors VOX cinemas, Telegram dashboards, showtime changes, booking availability, and Coming Soon releases.

## Projects

- `CinemaBot.Service`: main cross-platform worker.
- `CinemaBot.Watchdog`: optional Windows-only watchdog for local installations. It is **not** required on Render; Render manages the worker process lifecycle.

## Render architecture

`GitHub -> Render Background Worker -> Render Postgres`

The main service is containerized with the repository `Dockerfile`. Render's filesystem is ephemeral by default, so persistent application state is stored in PostgreSQL when `DATABASE_URL` is configured. Render documents that background workers run continuously and that the default filesystem is ephemeral; managed datastores are the recommended way to preserve data across deploys.

## Persistent state

The existing JSON state model is preserved logically, but the following state files are stored as PostgreSQL `jsonb` documents in the `cinemabot_state` table when PostgreSQL is enabled:

- Telegram user preferences
- Current Movie preferences
- Coming Soon preferences
- Coming Soon baseline/release state
- Channel subscriber registry
- Telegram admin settings
- Telegram user activity
- Scan range settings
- Telegram update offset

When `DATABASE_URL` is not configured, the application keeps the previous local-JSON behavior for development/local Windows usage.

On first startup with PostgreSQL enabled, the application creates the `cinemabot_state` table automatically. If old local JSON files are present, they are migrated into PostgreSQL on first read.

## Secrets

Do **not** commit the Telegram bot token to Git. Use the Render environment variable `Telegram__BotToken`. The Blueprint intentionally declares this as `sync: false`.

## Render deployment

1. Create or connect the GitHub repository containing this project.
2. In Render, create a Blueprint from the repository (the root `render.yaml` is already included).
3. Render creates the background worker and PostgreSQL database in Frankfurt and wires `DATABASE_URL` to the worker's internal database connection.
4. Enter the new Telegram bot token when Render prompts for `Telegram__BotToken`.
5. Enter the Telegram group/chat ID in `Telegram__ChatId`.
6. Deploy. The worker creates the database table automatically and then starts Telegram long polling and VOX scanning.

The included Blueprint uses a paid background-worker plan and a small paid PostgreSQL plan so that the service is not dependent on free-instance expiration. Render's current documentation states that free Postgres databases expire after 30 days and that free instances are intended for testing/hobby use.

## One-time migration of existing local users/subscriptions

If the current Windows installation already has data under `%ProgramData%\CinemaBot`, migrate that data before moving the bot to Render. Set `DATABASE_URL` to the Render Postgres **external** connection URL temporarily on the Windows machine, then run:

```powershell
$env:Storage__UsePostgres = "true"
$env:DATABASE_URL = "<Render external Postgres connection URL>"
dotnet run --project .\src\CinemaBot.Service\CinemaBot.Service.csproj -- --migrate-local-state "$env:ProgramData\CinemaBot"
```

The migration imports existing JSON state only when the corresponding PostgreSQL key does not already exist. It does not delete the local files. Do not put the database URL in source control.

## Local development

The solution is targeted at .NET 10. .NET 10 is an active LTS release.

For local development without PostgreSQL, `Storage:UsePostgres` is false and `Storage:DataDirectory` is `./data`. To test the production-style PostgreSQL path locally, set:

```text
Storage__UsePostgres=true
DATABASE_URL=<your PostgreSQL connection string>
```

## Windows service support

The main service and optional watchdog still support Windows Service hosting when running on Windows. On Linux/container environments, the Windows Service registration is skipped automatically.

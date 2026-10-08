# Render deployment checklist

1. Push the repository to GitHub.
2. In Render, use **New -> Blueprint** and select the repository.
3. Review the generated resources:
   - `cinemabot-worker` (Background Worker)
   - `cinemabot-db` (PostgreSQL)
4. Set the secret `Telegram__BotToken` to a newly rotated Telegram bot token.
5. Set `Telegram__ChatId` to the chat/group ID used for dashboards/alerts.
6. Keep `DATABASE_URL` managed by the Blueprint. It should come from the database's internal connection string.
7. Deploy and inspect worker logs. The first successful startup should contain a message that PostgreSQL persistence is ready.

The Watchdog project is intentionally not deployed to Render. The main worker is the service that Render manages.

## Existing local user data

If the old Windows install already contains `%ProgramData%\CinemaBot\*.json` state, run `scripts\Migrate-LocalState.ps1 -DatabaseUrl "<Render external Postgres connection URL>"` before retiring the local instance. Use Render's external Postgres connection URL only as a temporary environment variable; never commit it.

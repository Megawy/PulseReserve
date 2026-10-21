# Local development with Docker

One command starts PostgreSQL, the API (`dotnet watch`) and the Angular dev server (HMR). Verified on Windows 10 + Docker Desktop.

| Service | Container | Host URL | Notes |
|---|---|---|---|
| Web (Angular `ng serve`) | `web` | http://localhost:4200 | HMR; `/api` is proxied to the API (same origin, no CORS) |
| API (ASP.NET Core, .NET 10) | `api` | http://localhost:5080 | `/health/live`, `/health/ready`, Swagger at `/swagger` |
| PostgreSQL 17 | `postgres` | not published | reachable only from the compose network as `postgres:5432` |

Redis/Kafka are not part of this repository's architecture today and are therefore not started.

## First run

```bash
scripts/dev.ps1 setup     # Windows PowerShell (macOS/Linux: scripts/dev.sh setup) – creates .env with a random DB password
scripts/dev.ps1 up        # builds images, starts everything, waits until healthy, applies migrations
scripts/dev.ps1 seed      # optional: dev admin account + one on-sale demo event (real API calls)
```

Open http://localhost:4200. The first start runs `npm ci` and NuGet restore inside the containers (a few minutes).
The seed script prints no secrets: the dev admin password is stored in your git-ignored `.env` (`DEV_ADMIN_PASSWORD`,
email `admin@pulsereserve.test`).

## Daily commands

| Task | Command |
|---|---|
| Start | `scripts/dev.ps1 up` |
| Stop (data kept) | `scripts/dev.ps1 stop` |
| Status / logs | `scripts/dev.ps1 status` / `scripts/dev.ps1 logs api` |
| Restart one service | `scripts/dev.ps1 restart api` |
| Rebuild image (Dockerfile / SDK change) | `scripts/dev.ps1 rebuild api` |
| Apply new migrations | `scripts/dev.ps1 migrate` (restarts the API, which applies pending ones) |
| Backend tests | `scripts/dev.ps1 test-backend` (integration tests start their own disposable PostgreSQL via Testcontainers) |
| Frontend lint + tests + build | `scripts/dev.ps1 test-frontend` |
| SQL shell | `scripts/dev.ps1 psql` |

Equivalent raw commands: `docker compose -f compose.dev.yaml up -d --build --wait`, `... stop`, `... logs -f web`.
**Do not use `docker compose down -v`** unless you intend to delete the database (volume `pulsereserve-dev_pgdata`).

## How live reload works

* **Frontend**: `frontend/pulsereserve-web` is bind-mounted; `node_modules` and `.angular` live in named volumes (host Windows
  binaries would break Linux). `ng serve --poll 1000` detects edits on the bind mount and pushes them to the browser through
  Angular's HMR WebSocket (same port 4200) – template/style edits apply without a page reload.
* **Backend**: `src/` is bind-mounted; `dotnet watch` (polling file watcher) rebuilds and restarts the API on save. Build output goes
  to the `api-artifacts` volume (`UseArtifactsOutput`), so host `bin/`/`obj/` never collide with Linux builds. Expect ~10–40 s for a restart.
  Cookie sessions survive restarts only within the dev data-protection keys of a run: after an API restart you may need to sign in again.

## Database and migrations

The host never migrates by itself. In this compose file only, `Database__MigrateOnStartup=true` makes the API apply pending EF migrations
(catalog, identity, reservation, payments) at start; the setting is refused outside the Development environment. Migrations are additive and the
data volume persists across restarts.

## Configuration and secrets

`.env` (git-ignored; template `.env.example`) holds the DB password, host ports and the dev admin password/user id. Change `WEB_PORT`/`API_PORT`
there if they clash with other projects. The payment simulator and development notification adapter are enabled only in this dev stack and the app
refuses to start with them outside Development/Testing.

## Troubleshooting

* `web` unhealthy right after the first start: `npm ci` is still running – `scripts/dev.ps1 logs web`.
* API unhealthy: `scripts/dev.ps1 logs api` (build errors show there; fix the code and `dotnet watch` rebuilds).
* Port in use: set another `WEB_PORT`/`API_PORT` in `.env`, then `scripts/dev.ps1 up`.
* After editing `package.json`/`package-lock.json`, restart the web service: `scripts/dev.ps1 restart web` (it re-runs `npm ci` when the lock file changed).
* Edits not picked up: confirm the project sits on a drive shared with Docker Desktop; polling is already enabled.

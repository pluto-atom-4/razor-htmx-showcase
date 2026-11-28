# razor-htmx-showcase

ASP.NET Core Razor Pages + HTMX manufacturing workflow showcase for three phases: Build, Part and Test run. Pages are rendered on the server and data lives in SQL Server through EF Core. No GraphQL.

Plan: [discussion 1](https://github.com/pluto-atom-4/razor-htmx-showcase/discussions/1) (blueprint), [discussion 2](https://github.com/pluto-atom-4/razor-htmx-showcase/discussions/2) (sprint plans).

## What works today

- Cookie sign-in. Pages require a signed-in user, except the sign-in page and `/health`.
- Board at `/Board`: four columns, one per build status (Pending, Running, Complete, Failed).
- Each card has a station dropdown (ASSEMBLY-1, TEST-BAY-1, TEST-BAY-2) and one button per move the transition table allows.
- The server enforces the same tables (`FactoryApp.Domain/Transitions.cs`). A rejected move returns 422 and the message is shown on the card.
- Each accepted move writes one `WorkflowHistory` row with the station and the operator's email. A rejected move writes nothing.
- Antiforgery is checked on every POST, including htmx requests. `app.js` sends the token in the `RequestVerificationToken` header.

## Planned, not built yet

- Live updates over SSE. The htmx SSE extension is vendored and loaded by the layout, but no SSE endpoint exists.
- Per-phase analytics: cycle time, yield and a failure Pareto.
- Build detail page and traceability timeline.
- Deployment image.

## Requirements

- .NET SDK 10.0.301 (`global.json`; later patches roll forward).
- Docker. It runs SQL Server 2022 for local use, and the tests start their own SQL Server containers through Testcontainers.
- `dotnet-ef` 10.0.x, only for `scripts/apply-migrations.sh`: `dotnet tool update -g dotnet-ef --version "10.0.*"`. CI installs the same version as `Microsoft.EntityFrameworkCore.Tools` in `FactoryApp.Domain/FactoryApp.Domain.csproj`.

## Run it

Run these from the repository root.

### 1. Configure

```bash
cp .env.example .env
```

Edit `.env`: set `MSSQL_SA_PASSWORD` to a strong password of your own, and put the same value in the `Password=` part of `FACTORY_CONNECTION`. The value in `.env.example` is a placeholder. `.env` is git-ignored; do not commit it.

### 2. Start SQL Server

```bash
docker compose up -d
```

The app does not retry database connections. Wait a few seconds after the container starts before step 3; `docker compose logs sql-server | tail` shows the server log. If you change `MSSQL_PORT` from 1433, change the port in `FACTORY_CONNECTION` too.

### 3. Run the app with demo data

```bash
set -a; . ./.env; set +a
export FACTORY_DEV_SEED=true ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080
dotnet run --project FactoryApp.Web --no-launch-profile
```

`dotnet run` does not read `.env`, so the first line loads it. The `export` line overrides the `FACTORY_DEV_SEED=false` value in `.env`. With `FACTORY_DEV_SEED=true`, the app applies migrations and seeds demo users, builds, parts, test runs and history on startup. This works only in the Development environment.

### 4. Sign in

Open http://localhost:5080, sign in as `admin@example.com` with password `SecurePassword123!`, then open **Board**.

This is a fixture password from `FactoryApp.Domain/TestFixtures/FixtureSeeder.cs`. All three seeded users (`test@example.com`, `admin@example.com`, `user@example.com`) share it, and it exists only when the dev seed runs. The seeder refuses to run outside Development. Never use the seed or this password outside development.

### Without the seed

The app does not migrate on startup unless the dev seed is on. To apply migrations only (the script loads `.env` itself and needs `dotnet-ef`):

```bash
./scripts/apply-migrations.sh
```

Then run the app as in step 3 with `FACTORY_DEV_SEED=false`.

### Stop

```bash
docker compose down
```

`docker compose down -v` also deletes the SQL Server data volume.

### Troubleshooting: line endings

`.gitattributes` forces LF for `.env.example` and `*.sh`, so a fresh clone is fine even when `git config core.autocrlf` is `true` (common on Windows and WSL setups). A clone made before that file existed can have CRLF in those files, and then `.env` gets CRLF too:

- after `set -a; . ./.env; set +a`, `docker compose` fails with `invalid hostPort: 1433`;
- `./scripts/apply-migrations.sh` fails with `set: pipefail: invalid option name`.

Fix, then run the step again:

```bash
sed -i 's/\r$//' .env scripts/apply-migrations.sh
```

## Tests

```bash
dotnet build
dotnet test
```

Docker must be running. The integration tests start SQL Server containers through Testcontainers (see `FactoryApp.Web.Tests/WebAppFactory.cs`), so expect a run of about two minutes. CI (`.github/workflows/ci.yml`) runs `dotnet build -c Release`, `dotnet test -c Release` and `dotnet ef database update` against a SQL Server service.

## Layout

- `FactoryApp.Domain/`: entities, events, `FactoryDbContext`, migrations and dev fixtures (`TestFixtures/`). Vendored from `ng-graphql-playground`, with migrations and seed changes made here (for example `AddStationOperatorFailureCode`). Hand-added here: `Transitions.cs` (build and test-run transition tables), `BuildService.cs` (applies a status change in a transaction and writes its history row), `TransitionResult.cs`, and `DesignTimeFactoryDbContextFactory.cs` (lets `dotnet ef` run without a web host).
- `FactoryApp.Web/`: `Program.cs` (auth, antiforgery, dev seed, `/health`), Razor Pages in `Pages/` (Board, Login, Index and shared partials), `Services/AuthService.cs` (password check and claims), and `wwwroot/` (`app.js`, plus htmx 2.0.11 and the htmx SSE extension 2.2.4 pinned under `lib/htmx/`, see `VERSIONS.md`).
- `FactoryApp.Web.Tests/`: xUnit tests. `WebAppFactory` runs the app against a Testcontainers SQL Server.

Rules that matter:

- No GraphQL, Elsa, Dapper or Redis.
- htmx stays on 2.x. htmx 4 changes SSE handling.
- Secrets live only in `.env`. `.env.example` is committed with placeholders only.
- Events are published after commit. `BuildService` creates the status-changed event after the transaction commits and returns it; nothing sends it to clients yet. Publishing it is part of the planned SSE work.

## MES coverage

This is a manufacturing execution slice, not a full MES. The planned end state is real-time lot status, phase tracking, a traceability timeline and per-phase performance analytics. Today it is the board with enforced moves and recorded history.

| MESA-11 function | Today | Planned |
|---|---|---|
| Process management | Transitions enforced on the server | None |
| Resource allocation | Station recorded on every accepted move | Per-station metrics |
| Labor management | Operator recorded on every accepted move | None |
| Quality management | `FailureCode` column on test runs; no UI, no Pareto | Failure codes entered in the UI (stretch issues S1-13 and S1-14, not merged) and failure Pareto |
| Product tracking | Parts belong to a build; one history row per move | Traceability timeline per build |
| Performance analytics | Not built | Per-phase cycle time, yield and failure Pareto |
| Scheduling | None | None |
| Document control | None | None |
| Maintenance | None | None |

## Workflow

The project is built with an architect, builder and reviewer agent loop. The owner merges every PR.
## License

[MIT](LICENSE). The vendored htmx files in `FactoryApp.Web/wwwroot/lib/htmx/` keep their own licenses (packages and versions in `VERSIONS.md`).

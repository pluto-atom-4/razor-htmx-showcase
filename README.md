# razor-htmx-showcase

ASP.NET Core + Razor Pages + HTMX showcase: manufacturing Build / Test-run workflow with SSE live updates, server-side rendering and per-phase analytics. No GraphQL.

Plan: [discussion 1](https://github.com/pluto-atom-4/razor-htmx-showcase/discussions/1) (blueprint), [discussion 2](https://github.com/pluto-atom-4/razor-htmx-showcase/discussions/2) (sprint plans).

## Run the database

```bash
cp .env.example .env        # then set a real MSSQL_SA_PASSWORD and FACTORY_CONNECTION
docker compose up -d
./scripts/apply-migrations.sh
```

Needs .NET SDK 10.0.301 (pinned in `global.json`), Docker, and `dotnet tool install -g dotnet-ef`.

## Layout

- `FactoryApp.Domain/`: vendored snapshot of the domain from `ng-graphql-playground`, extended here (migrations, seed).
- `FactoryApp.Web/` and `FactoryApp.Web.Tests/`: added in later sprint 1 issues.

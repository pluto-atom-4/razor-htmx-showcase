# razor-htmx-showcase

Manufacturing workflow showcase (Build, Part, Test-run): ASP.NET Core minimal API + Razor Pages + HTMX, live updates over SSE, per-phase analytics. **No GraphQL.** Sprint 1 is in progress (issues S1-01 to S1-15).

## Read first

- Discussion 1: blueprint and its four decisions. Discussion 2: three sprint plans, issue ids (S1-01 to S3-12), budget and checkpoints. Both in `pluto-atom-4/razor-htmx-showcase` (`gh api` or `gh discussion view`).
- Start with S1-01 (setup, done by the main session, not a builder).

## Rules

- Stack: .NET SDK 10.0.301, SQL Server 2022 in Docker, EF Core, htmx 2.x and its SSE extension pinned (htmx 4 changes SSE handling).
- `FactoryApp.Domain` is a vendored snapshot from `~/Documents/stoke-full-stack/ng-graphql-playground/backend/src/FactoryApp.Domain`. Copy `AuthService.cs` from the GraphQL project. Do not reference or copy Hot Chocolate, Elsa, Dapper or Redis.
- Builder cap: 2 production files plus their tests per issue.
- Events are published after commit, never before. Every accepted status change writes one `WorkflowHistory` row (station, operator).
- Secrets only in `.env`; commit `.env.example`.

## Workflow

- Use `issue-build-review-loop`: architect (Sonnet), Haiku builder, slop-hunter and cavecrew-reviewer, at most 4 rounds. Agents never merge; the owner merges.
- A sprint is 25 to 30 percent of the weekly usage meter, at most 12 committed issues, extra issues are stretch. Ask the owner for the meter reading at the start and at the checkpoints in discussion 2. Stop at the sprint limit.
- Comment rules come from the user-level CLAUDE.md. Extend this file in S1-01, do not duplicate the discussions here.

## Commands

- `docker compose up -d` then `./scripts/apply-migrations.sh` (needs `.env` copied from `.env.example`; quote `FACTORY_CONNECTION`, it contains spaces).
- `dotnet build` and `dotnet test` at the repo root. CI runs both plus the migrations against a SQL Server service.
- `FactoryApp.Domain/DesignTimeFactoryDbContextFactory.cs` and `FactoryApp.Domain/Transitions.cs` are hand-added to the vendored domain; the first lets `dotnet ef` work without a web host, the second holds the build and test-run transition tables.
- Branches `feat/issue-N-short-desc`, one PR per issue, `Closes #N` in the PR body.

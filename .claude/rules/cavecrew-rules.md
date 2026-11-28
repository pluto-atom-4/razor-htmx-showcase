# Cavecrew rules

Text rules for the repo agents. A `SubagentStart` hook in `.claude/settings.json` is configured to inject this file into `repo-cavecrew-builder`, `slop-hunter`, `caveman:cavecrew-builder` and `caveman:cavecrew-reviewer` (injection not verified, see Honesty). `architect`, `investigator` and `gate-enforcer` are not covered by the hook; each agent file should tell its agent to read this file. A rule is enforced only where a hook or permission enforces it.

## Agents

- `architect`: sonnet. Plans, files issues, returns spec, risk and questions for the owner. Graph and GitHub tools. No code edits.
- `repo-cavecrew-builder`: haiku. One issue, 2 production files plus their tests (new files count). Read/Grep/Glob/Edit/Write and graph read tools. No Bash.
- `slop-hunter`: haiku. Read-only dead-code and doc-slop reporter. Bash intended only for `code-review-graph dead-code`, `dead-code --json`, `architecture`, `large-functions` (enforcement unverified).
- `investigator`: haiku. Locator, used read-only. Has Bash; the read-only limit is an instruction only.
- `gate-enforcer`: sonnet. Checks plan and CI gates and reports evidence; cannot approve or merge. Has Bash; read-only use is an instruction only.
- Diff review is the plugin `caveman:cavecrew-reviewer` (input: `base: <ref>` or a pasted diff, always with `iteration: N`). There is no repo-specific reviewer here. Plugin agents `caveman:cavecrew-builder` and `caveman:cavecrew-investigator` are separate agents; project files do not extend them.

## Defaults

- Builders edit at most 2 production files per run plus their tests. Reviewer, slop-hunter and investigator are read-only on source files.
- The main session runs git, `dotnet build`, `dotnet test`, `dotnet ef` and graph updates. Agents never run graph update commands. A `PostToolUse` hook in `.claude/settings.json` runs `code-review-graph update` after an Edit or Write when the tool is installed.
- The owner merges. Agents never merge, close issues or delete branches.
- Refusal and output tokens: `ambiguous. ask:`, `too-big. split:`, `needs-confirm. op:` (also for requests to edit protected or generated files), `ESCALATE to human`, `No issues.`, `No slop found.`

## Handover

- Builder: files changed with line ranges; whether a migration or seed change needs `dotnet ef` or `scripts/apply-migrations.sh` run by the main session; acceptance commands (usually `dotnet build` and `dotnet test --filter <name>`); anything not verified.
- Main session: commands run with real output; not-run list; open questions.
- Never claim a test passed unless it was run.

## Gates

- Generated or vendored files, not edited by hand: `FactoryApp.Domain/Migrations/*.Designer.cs`, `FactoryDbContextModelSnapshot.cs`, `wwwroot/css/site.css`, the pinned htmx and htmx SSE extension files, anything under `bin/` or `obj/`. Migrations come from `dotnet ef`, run by the main session.
- The `protect-files.sh` hook is meant to refuse file edits to `.claude/settings.json`, `.claude/settings.local.json`, `.claude/hooks/protect-files.sh`, `.mcp.json`, `*/Migrations/*.Designer.cs`, `*/Migrations/FactoryDbContextModelSnapshot.cs` and `wwwroot/css/site.css`. The guard is not a security boundary; owner review of the diff is the control. If an issue asks for such an edit, stop with `needs-confirm. op: <change>`; the owner applies it.
- `CLAUDE.md`: the main session edits it when the owner asks. Agents do not edit it.
- No GraphQL, Elsa, Dapper or Redis.
- `FactoryApp.Domain` is a vendored snapshot: edit only the files an issue names.
- Events are published after commit, never before. Every accepted status change writes exactly one `WorkflowHistory` row (station, operator); a rejected move writes none.
- Do not weaken `Transitions`, the antiforgery check, or authorization on a route without a design issue.
- In repository files, secrets live only in `.env`. `GITHUB_MCP_PAT` is set in the shell environment, outside the repository. Never write a real password or token into a tracked file or an issue.
- htmx 2.x and its SSE extension stay pinned. SSE responses send `Cache-Control: no-cache, no-transform` and `X-Accel-Buffering: no`.
- Feature branches `feat/issue-N-short-desc`, one PR per issue with `Closes #N` in the body. No commit to main. No `rm`. No remote branch deletion. No force push.

## Severity

| Emoji | Severity | Meaning |
| ----- | -------- | ------- |
| 🔴 | bug | wrong output, crash, data loss, auth or antiforgery hole, secret in a tracked file, false validation claim |
| 🟡 | risk | edge case, missing guard, unverified claim, missing test or migration, event published before commit |
| 🔵 | nit | style or naming |
| ❓ | question | needs author answer |

Applies to review output; the plugin `caveman:cavecrew-reviewer` keeps its own format. Report 🔵 only when the caller asks for a thorough review.

## Loop cap

- At most 4 builder and reviewer rounds per PR.
- The caller passes `iteration: N` to the reviewer; the main session counts rounds itself (a missing iteration is treated as 1, which gives no cap).
- At round 4, if any 🔴 or 🟡 finding is still open, escalate to the owner with the open findings. Otherwise report normally. The reviewer output token is `ESCALATE to human`.
- Info findings and out-of-scope refactors are listed, not looped.

## Honesty

- Nothing here is validated beyond local runs and CI. Say "not run" for anything not run.
- Planned features (SSE, analytics, timeline, replay, role-gated dispatch) are described as planned until their issue is merged.
- Dead-code results for C# are unproven: a graph hit is a candidate, never proof. Razor handlers, partials and DI services are often found only by name or convention.
- Tool lists in agent files express intent. Whether Claude Code enforces them for subagents is unverified.
- The hooks were dry-run from the shell. The guard is not a security boundary.
- Guard check, 2026-11-28: a requested write of a file named `.mcp.json` was refused in the main session and in a subagent.
- Not verified: that the `SubagentStart` hook adds this file for `repo-cavecrew-builder`.

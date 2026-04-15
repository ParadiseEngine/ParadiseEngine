# Agent Workflow

PM-centric architecture. All agents defined in `.claude/agents/`.

| Agent | Model | Role | Lifecycle |
|-------|-------|------|-----------|
| **pm** | opus | Central coordinator — triages issues, designs solutions, breaks down features, dispatches agents, tracks PLAN.md | Long-running (session) |
| **dev** | sonnet | Architecture, implementation, bug fixes, tests | Ephemeral per-issue (worktree) |
| **qa** | sonnet | Post-merge verification (build, tests, sample smoke test) | Ephemeral (worktree) |

**Flow**: GitHub issue → PM triages → spawns dev agent in worktree → dev implements → self-reviews (multi-AI) → merges → PM spawns QA → QA verifies → done.

## Key Conventions

- **Worktree isolation** — all dev/QA work happens in git worktrees, never on root project
- **Self-review** — dev agents run multi-AI review before merging
- **Squash merge** — all PRs merged via `gh pr merge --squash --delete-branch`
- **Warnings as errors** — build must pass with `-p:TreatWarningsAsErrors=true`
- **AOT flag** — tests require `-p:PublishAot=false` because TUnit needs it disabled at test time

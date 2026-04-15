# Development Workflow

All dev agents follow this standard lifecycle.

## Lifecycle

1. Spawned by PM in an isolated **git worktree** (auto-created branch)
2. Receive an issue number from PM (issue body contains detailed specs from PM)
3. Read the issue and understand the requirements
4. Rename the worktree branch to `issue-<NUMBER>-<short-description>`
5. Implement the changes
6. Write tests for new code
7. Build and test: `dotnet build --solution ParadiseEngine.slnx -p:TreatWarningsAsErrors=true && dotnet test --solution ParadiseEngine.slnx -p:PublishAot=false --output normal`
8. Commit, push, and create a PR (referencing the issue)
9. **Self-review**: run multi-AI review → fix findings → re-review (max 3 iterations)
10. When clean → merge the PR
11. Report completion to PM, shut down

**CRITICAL**: You are always running in a worktree — NEVER modify files in the root project directory (`/home/quabug/ParadiseEngine/`). All work MUST happen in your agent's worktree. The root project must always stay on the `main` branch.

## Implementation Phase

```bash
# Rename the auto-created worktree branch
git branch -m issue-<NUMBER>-<short-description>

# ... implement changes ...

# Build and test
dotnet build --solution ParadiseEngine.slnx -p:TreatWarningsAsErrors=true
dotnet test --solution ParadiseEngine.slnx -p:PublishAot=false --output normal

# Commit with issue reference
git add <specific files>
git commit -m "<description>

Closes #<NUMBER>"

# Push and create PR
git push -u origin issue-<NUMBER>-<short-description>
gh pr create --title "[dev] <title>" --label "agent:dev" --body "Closes #<NUMBER>

## Summary
- ...

## Test plan
- ..."
```

## Self-Review Phase

After creating the PR, review your own changes using multi-AI tools.

### Step 1: Generate Diff
```bash
BASE_BRANCH=$(gh pr view <PR_NUMBER> --json baseRefName -q .baseRefName)
mkdir -p pr-reviews
git diff ${BASE_BRANCH}...HEAD > pr-reviews/pr<PR_NUMBER>.diff
```

### Step 2: Run Multi-AI Review (sequentially in foreground)

**IMPORTANT**: Run each review tool in the **foreground** (do NOT use `run_in_background`). Background task results are routed to the team-lead, not the teammate agent. Run each sequentially with `timeout: 600000`:

- **Codex**: `codex exec --dangerously-bypass-approvals-and-sandbox "$(printf 'Review this PR diff for correctness, bugs, safety issues, and code quality. Focus on logic errors and edge cases. Output findings with severity labels (critical/major/minor):\n\n'; cat pr-reviews/pr<PR_NUMBER>.diff)"`
- **Gemini**: `gemini --yolo -p "Read the file pr-reviews/pr<PR_NUMBER>.diff and review it as a PR diff. Check for correctness, architecture, and best practices. Output findings with severity labels."`
- **Qwen (GLM-5)**: `qwen -y -m glm-5 "$(cat <<'PROMPT_EOF'
Read the file pr-reviews/pr<PR_NUMBER>.diff and review it as a PR diff. Check for correctness, performance, and maintainability. Output findings with severity labels.
PROMPT_EOF
)" 2>&1 | sed 's/\x1b\[[0-9;]*m//g'`
- **Qwen (Kimi-K2.5)**: `qwen -y -m kimi-k2.5 "$(cat <<'PROMPT_EOF'
Read the file pr-reviews/pr<PR_NUMBER>.diff and review it as a PR diff. Output findings with severity labels.
PROMPT_EOF
)" 2>&1 | sed 's/\x1b\[[0-9;]*m//g'`
- **Qwen (MiniMax-M2.5)**: `qwen -y -m minimax-m2.5 "$(cat <<'PROMPT_EOF'
Read the file pr-reviews/pr<PR_NUMBER>.diff and review it as a PR diff. Output findings with severity labels.
PROMPT_EOF
)" 2>&1 | sed 's/\x1b\[[0-9;]*m//g'`
- **Qwen (Qwen3.5-Plus)**: `qwen -y -m qwen3.5-plus "$(cat <<'PROMPT_EOF'
Read the file pr-reviews/pr<PR_NUMBER>.diff and review it as a PR diff. Output findings with severity labels.
PROMPT_EOF
)" 2>&1 | sed 's/\x1b\[[0-9;]*m//g'`

### Step 3: Synthesize & Fix (max 3 iterations)
1. Collect all results, categorize (critical/major/minor), deduplicate, add your own review
2. If no critical/major issues → proceed to merge
3. Otherwise: fix issues, run tests, commit, push, and re-review yourself (do NOT re-run multi-AI on subsequent iterations)

### Step 4: Merge
```bash
gh pr comment <PR_NUMBER> --body "## Review Summary
- **Iterations**: N
- **Issues fixed**: [list]
- **Remaining minor**: [list or none]

Reviewed by: Claude Code + Codex + Gemini + Qwen (GLM-5, Kimi-K2.5, MiniMax-M2.5, Qwen3.5-Plus)"

gh pr merge <PR_NUMBER> --squash --delete-branch
```

### Step 5: Cleanup
```bash
rm -f pr-reviews/pr<PR_NUMBER>.diff
rmdir pr-reviews 2>/dev/null
```

### Step 6: Report Completion to PM
```
SendMessage to PM: "Completed issue #<NUMBER>. PR #<PR_NUMBER> merged (squash). Ready for shutdown."
```

## Guidelines

- Follow **SOLID**, **KISS**, **YAGNI** principles
- All node types must be unmanaged structs for AOT/serialization compatibility
- Maintain dual-target compatibility (netstandard2.1 + net10.0)
- If the issue spec is unclear, comment on the issue asking PM for clarification and shut down
- If an issue requires work outside your scope, comment explaining what's needed and shut down

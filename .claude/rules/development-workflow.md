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
9. **Wait for OpenCara review** on the PR — do NOT run local multi-AI review, do NOT merge immediately
10. Fix any valid review comments from OpenCara, push fixes, and wait for re-review
11. Once OpenCara review passes (all checks green, no blocking comments) → merge the PR
12. Report completion to PM, shut down

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

## Review Phase — Wait for OpenCara

**Do NOT run local multi-AI review.** After creating the PR, wait for **OpenCara** (the external bot reviewer) to review the PR on GitHub.

### Step 1: Wait for OpenCara Review

Poll the PR until OpenCara posts its review. Do NOT merge before this.

```bash
# Poll for OpenCara review (check every 30 seconds, max ~10 minutes)
for i in $(seq 1 20); do
  REVIEWS=$(gh pr view <PR_NUMBER> --json reviews --jq '[.reviews[] | select(.author.login == "opencara" or .author.login == "opencara[bot]")] | length')
  if [ "$REVIEWS" -gt "0" ]; then
    echo "OpenCara review received."
    break
  fi
  echo "Waiting for OpenCara review... (attempt $i/20)"
  sleep 30
done

# Also check PR comments from OpenCara
gh pr view <PR_NUMBER> --json comments \
  --jq '.comments[] | select(.author.login == "opencara" or .author.login == "opencara[bot]") | .body'

# Check review status
gh pr view <PR_NUMBER> --json reviews \
  --jq '.reviews[] | select(.author.login == "opencara" or .author.login == "opencara[bot]") | {state: .state, body: .body}'
```

### Step 2: Fix Valid Review Comments (max 3 iterations)

1. Read all OpenCara review comments (both PR review and inline comments)
2. For each valid finding: fix the issue in code
3. Run build+test: `dotnet build --solution ParadiseEngine.slnx -p:TreatWarningsAsErrors=true && dotnet test --solution ParadiseEngine.slnx -p:PublishAot=false --output normal`
4. Commit and push fixes
5. Wait for OpenCara to re-review (poll again as in Step 1)
6. Repeat until no critical/major issues remain (max 3 iterations)

**Do NOT dismiss or ignore valid review feedback.** Only proceed to merge when OpenCara has no blocking comments.

### Step 3: Merge

Only merge after OpenCara review passes and all valid comments are addressed:
```bash
gh pr merge <PR_NUMBER> --squash --delete-branch
```

### Step 4: Cleanup
```bash
rm -rf pr-reviews 2>/dev/null
```

### Step 5: Report Completion to PM
```
SendMessage to PM: "Completed issue #<NUMBER>. PR #<PR_NUMBER> merged (squash). Ready for shutdown."
```

## Guidelines

- Follow **SOLID**, **KISS**, **YAGNI** principles
- All node types must be unmanaged structs for AOT/serialization compatibility
- Maintain dual-target compatibility (netstandard2.1 + net10.0)
- If the issue spec is unclear, comment on the issue asking PM for clarification and shut down
- If an issue requires work outside your scope, comment explaining what's needed and shut down

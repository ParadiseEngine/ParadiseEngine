commit e6ec19289865ef5ddfd5cdcfe3a81fba5bc11ef8
Author: quabug <quabug@gmail.com>
Date:   Wed Apr 15 01:45:03 2026 -0700

    Remove BehaviorNodeType enum and build-time validation
    
    The runtime BT never uses BehaviorNodeType — it was only for build-time
    child-count validation and serialization metadata. The factory methods
    already enforce correct child counts through their signatures.
    
    - Delete BehaviorNodeKind.cs
    - Remove NodeTypeKind from IRuntimeNodeFactory, BehaviorNodeDefinition
    - Remove GetNodeBehaviorType() from BehaviorTree public API
    - Remove ValidateDefinition() from BehaviorTreeBuilder
    - Remove NodeType field from BehaviorTreeBlobNode (breaking blob format)
    - Simplify BehaviorNodes factory (no type parameter needed)
    - Update development workflow to use OpenCara review instead of local multi-AI
    
    Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>

diff --git a/.claude/rules/development-workflow.md b/.claude/rules/development-workflow.md
index 31c7efc..5b40351 100644
--- a/.claude/rules/development-workflow.md
+++ b/.claude/rules/development-workflow.md
@@ -12,9 +12,10 @@ All dev agents follow this standard lifecycle.
 6. Write tests for new code
 7. Build and test: `dotnet build --solution ParadiseEngine.slnx -p:TreatWarningsAsErrors=true && dotnet test --solution ParadiseEngine.slnx -p:PublishAot=false --output normal`
 8. Commit, push, and create a PR (referencing the issue)
-9. **Self-review**: run multi-AI review → fix findings → re-review (max 3 iterations)
-10. When clean → merge the PR
-11. Report completion to PM, shut down
+9. **Wait for OpenCara review** on the PR — do NOT run local multi-AI review, do NOT merge immediately
+10. Fix any valid review comments from OpenCara, push fixes, and wait for re-review
+11. Once OpenCara review passes (all checks green, no blocking comments) → merge the PR
+12. Report completion to PM, shut down
 
 **CRITICAL**: You are always running in a worktree — NEVER modify files in the root project directory (`/home/quabug/ParadiseEngine/`). All work MUST happen in your agent's worktree. The root project must always stay on the `main` branch.
 
@@ -47,64 +48,59 @@ gh pr create --title "[dev] <title>" --label "agent:dev" --body "Closes #<NUMBER
 - ..."
 ```
 
-## Self-Review Phase
+## Review Phase — Wait for OpenCara
 
-After creating the PR, review your own changes using multi-AI tools.
+**Do NOT run local multi-AI review.** After creating the PR, wait for **OpenCara** (the external bot reviewer) to review the PR on GitHub.
+
+### Step 1: Wait for OpenCara Review
+
+Poll the PR until OpenCara posts its review. Do NOT merge before this.
 
-### Step 1: Generate Diff
 ```bash
-BASE_BRANCH=$(gh pr view <PR_NUMBER> --json baseRefName -q .baseRefName)
-mkdir -p pr-reviews
-git diff ${BASE_BRANCH}...HEAD > pr-reviews/pr<PR_NUMBER>.diff
+# Poll for OpenCara review (check every 30 seconds, max ~10 minutes)
+for i in $(seq 1 20); do
+  REVIEWS=$(gh pr view <PR_NUMBER> --json reviews --jq '[.reviews[] | select(.author.login == "opencara" or .author.login == "opencara[bot]")] | length')
+  if [ "$REVIEWS" -gt "0" ]; then
+    echo "OpenCara review received."
+    break
+  fi
+  echo "Waiting for OpenCara review... (attempt $i/20)"
+  sleep 30
+done
+
+# Also check PR comments from OpenCara
+gh pr view <PR_NUMBER> --json comments \
+  --jq '.comments[] | select(.author.login == "opencara" or .author.login == "opencara[bot]") | .body'
+
+# Check review status
+gh pr view <PR_NUMBER> --json reviews \
+  --jq '.reviews[] | select(.author.login == "opencara" or .author.login == "opencara[bot]") | {state: .state, body: .body}'
 ```
 
-### Step 2: Run Multi-AI Review (sequentially in foreground)
-
-**IMPORTANT**: Run each review tool in the **foreground** (do NOT use `run_in_background`). Background task results are routed to the team-lead, not the teammate agent. Run each sequentially with `timeout: 600000`:
-
-- **Codex**: `codex exec --dangerously-bypass-approvals-and-sandbox "$(printf 'Review this PR diff for correctness, bugs, safety issues, and code quality. Focus on logic errors and edge cases. Output findings with severity labels (critical/major/minor):\n\n'; cat pr-reviews/pr<PR_NUMBER>.diff)"`
-- **Gemini**: `gemini --yolo -p "Read the file pr-reviews/pr<PR_NUMBER>.diff and review it as a PR diff. Check for correctness, architecture, and best practices. Output findings with severity labels."`
-- **Qwen (GLM-5)**: `qwen -y -m glm-5 "$(cat <<'PROMPT_EOF'
-Read the file pr-reviews/pr<PR_NUMBER>.diff and review it as a PR diff. Check for correctness, performance, and maintainability. Output findings with severity labels.
-PROMPT_EOF
-)" 2>&1 | sed 's/\x1b\[[0-9;]*m//g'`
-- **Qwen (Kimi-K2.5)**: `qwen -y -m kimi-k2.5 "$(cat <<'PROMPT_EOF'
-Read the file pr-reviews/pr<PR_NUMBER>.diff and review it as a PR diff. Output findings with severity labels.
-PROMPT_EOF
-)" 2>&1 | sed 's/\x1b\[[0-9;]*m//g'`
-- **Qwen (MiniMax-M2.5)**: `qwen -y -m minimax-m2.5 "$(cat <<'PROMPT_EOF'
-Read the file pr-reviews/pr<PR_NUMBER>.diff and review it as a PR diff. Output findings with severity labels.
-PROMPT_EOF
-)" 2>&1 | sed 's/\x1b\[[0-9;]*m//g'`
-- **Qwen (Qwen3.5-Plus)**: `qwen -y -m qwen3.5-plus "$(cat <<'PROMPT_EOF'
-Read the file pr-reviews/pr<PR_NUMBER>.diff and review it as a PR diff. Output findings with severity labels.
-PROMPT_EOF
-)" 2>&1 | sed 's/\x1b\[[0-9;]*m//g'`
-
-### Step 3: Synthesize & Fix (max 3 iterations)
-1. Collect all results, categorize (critical/major/minor), deduplicate, add your own review
-2. If no critical/major issues → proceed to merge
-3. Otherwise: fix issues, run tests, commit, push, and re-review yourself (do NOT re-run multi-AI on subsequent iterations)
-
-### Step 4: Merge
-```bash
-gh pr comment <PR_NUMBER> --body "## Review Summary
-- **Iterations**: N
-- **Issues fixed**: [list]
-- **Remaining minor**: [list or none]
+### Step 2: Fix Valid Review Comments (max 3 iterations)
 
-Reviewed by: Claude Code + Codex + Gemini + Qwen (GLM-5, Kimi-K2.5, MiniMax-M2.5, Qwen3.5-Plus)"
+1. Read all OpenCara review comments (both PR review and inline comments)
+2. For each valid finding: fix the issue in code
+3. Run build+test: `dotnet build --solution ParadiseEngine.slnx -p:TreatWarningsAsErrors=true && dotnet test --solution ParadiseEngine.slnx -p:PublishAot=false --output normal`
+4. Commit and push fixes
+5. Wait for OpenCara to re-review (poll again as in Step 1)
+6. Repeat until no critical/major issues remain (max 3 iterations)
 
+**Do NOT dismiss or ignore valid review feedback.** Only proceed to merge when OpenCara has no blocking comments.
+
+### Step 3: Merge
+
+Only merge after OpenCara review passes and all valid comments are addressed:
+```bash
 gh pr merge <PR_NUMBER> --squash --delete-branch
 ```
 
-### Step 5: Cleanup
+### Step 4: Cleanup
 ```bash
-rm -f pr-reviews/pr<PR_NUMBER>.diff
-rmdir pr-reviews 2>/dev/null
+rm -rf pr-reviews 2>/dev/null
 ```
 
-### Step 6: Report Completion to PM
+### Step 5: Report Completion to PM
 ```
 SendMessage to PM: "Completed issue #<NUMBER>. PR #<PR_NUMBER> merged (squash). Ready for shutdown."
 ```

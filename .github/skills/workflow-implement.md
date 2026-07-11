# Implement Workflow

Load on `/implement`. Apply `copilot-instructions.md` for all quality, tech, git, and communication rules.

**Purpose:** Implement approved plan steps or accepted review findings **exactly** — complete every item in scope, match plan/finding intent, no skipped or diverging work.

## Stage Order

1. Prepare
2. Execute Steps
3. Final Verification

## Stage 1 — Prepare

- Require approved plan or accepted review findings per `copilot-instructions.md` Section 6; stop otherwise.
- Run Tech Load Protocol per Section 3.
- Re-read Context Anchor and current step or finding block (`What`, `How`, `Verify`).
- Resume at first `⚠️`, else first `⬜`.

## Stage 2 — Execute Steps

- **Checklist status:** `⚠️` before first edit · `✅` after Verify, alignment, Step NR clean — all status lines in artifact, before next item.
- Process **every** checklist step or finding in scope; skip none.
- Follow strict topological order per plan dependencies.
- Implement **only** current step or finding scope — match `What` and `How` exactly.
- Do not substitute, simplify, or extend beyond scope without user approval.
- Run `Verify` from plan step or finding; require pass.
- **Alignment check:** compare plan/finding target vs actual result; confirm goal met and no deviation.
- Confirm misuse/abuse checklist from plan is satisfied when new public APIs are in scope.
- Run `/review` at each Step NR; zero Errors before next step.
- Persist review file in complex-task mode per `workflow-complex-task.md`.
- Stop with blocker after two failed remediation attempts for same Error root cause.

## Stage 3 — Final Verification

- Confirm **every** scoped step or finding is `✅`; list any remaining `⬜` or `⚠️` as blocker.
- Re-run alignment check for full scope: plan done criteria or all accepted findings resolved.
- Run full build and all tests in Release.
- Output Implementation Status Table (every step/finding listed, none omitted).

## Implementation Status Table

```markdown
| Step / Finding | Status |
|----------------|--------|
| Step 1 - {title} | ✅ Complete |
| Step 1R - Review Step 1 | ✅ Clean - 0 Errors |
| E1 - {title} | ✅ Fixed |
```

# Review-Loop Workflow

Load on `/review-loop`. Orchestrates `workflow-review.md` and `workflow-implement.md` in a loop until the scope is clean.

**Purpose:** Review in-scope code, fix every open finding, re-review, repeat — no plan artifact required.

## Stage Order

1. Define Scope
2. Review/Remediate Loop
3. Stop Conditions
4. Resume
5. Final Report

## Stage 1 — Define Scope

- Execute `workflow-review.md` Stage 1 — Define Scope.
- Treat scope argument as confirmed when provided.
- Record scope slug for review artifact naming (`review_<slug>_<iteration>.md`).
- Do not enter the loop before scope is confirmed.

## Stage 2 — Review/Remediate Loop

For each iteration starting at `1`:

1. **Review** — Execute `workflow-review.md` Stages 2–5 on current scope.
   - Persist output to `reviews/review_<slug>_<iteration>.md` (file mode).
   - In chat: bucket counts, release verdict, artifact path, prioritized action list only.
2. **Assess** — Count open findings (`⬜` or `⚠️`) per bucket in the latest review artifact.
3. **Stop** — When open-finding count is zero per Stage 3 success criteria, exit loop.
4. **Remediate** — Execute `workflow-implement.md` for every open finding in the latest review artifact.
   - Process findings in Priority Action List order.
   - Mark each finding `✅` in the review artifact only after Verify pass, alignment confirmed, and commit done.
   - Do not skip Cosmetic, Refactoring, or Performance findings unless user explicitly defers them in this session.
5. **Increment** — `iteration += 1`; return to step 1.

## Stage 3 — Stop Conditions

- **Success (default):** Latest review iteration has zero open **Error** findings.
- **Success (full clean):** When user requests zero findings or full clean — latest iteration has zero open findings in **all** buckets, or every remaining finding is explicitly deferred with user approval documented in Closing Assessment.
- **Block:** Same Error root cause persists after two remediation attempts in the same iteration scope — stop and report blocker ID.
- **Cap:** Stop after 10 review iterations; report remaining open findings as blocker.

## Stage 4 — Resume

- When a review artifact path is provided, resume at first open finding in the latest iteration file.
- Preserve iteration numbering; next full review writes `review_<slug>_<n+1>.md`.
- Re-read scope from the artifact Scope section when scope argument is omitted.

## Stage 5 — Final Report

- Output review iteration table: path, open Error count, open total count, status.
- Output implementation status table for every remediated finding ID.
- Run full build and all tests in Release.
- State release verdict from latest review Closing Assessment.
- List deferred findings with deferral reason when user approved deferral.
- Use terse chat per Section 2; cite artifact paths only.

### Review Iteration Table

```markdown
| Iteration | Path | Errors | Open Total | Status |
|-----------|------|--------|------------|--------|
| 1 | reviews/review_<slug>_1.md | 2 | 5 | Remediated |
| 2 | reviews/review_<slug>_2.md | 0 | 0 | ✅ Clean |
```

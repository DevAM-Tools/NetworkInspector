# Review-Loop Workflow

Load on `/review-loop`. Orchestrates `workflow-review.md` and `workflow-implement.md` in a loop until the scope is clean.

**Purpose:** Review in-scope code, fix every open finding in all buckets, re-review, repeat — no plan artifact required. Default success is full clean.

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
3. **Remediate** — If any finding is still open, execute `workflow-implement.md` for every
   open finding in Priority Action List order. Do not skip Cosmetic, Refactoring, or
   Performance unless the user explicitly defers them in this session. Mark each finding
   `✅` in the review artifact only after Verify pass and alignment confirmed. Skip
   implement Stage 5 Exam. Run Stage 4 (extensive **briefing**). If nothing is open, skip
   remediations.
4. **Stop** — When Stage 3 success is met on the **latest review** (zero open findings in
   all buckets, or every leftover finding explicitly deferred), exit the loop. Do not treat
   zero Errors alone as enough while other buckets are still open. Else go to Increment.
5. **Increment** — `iteration += 1`; return to step 1 (re-review after remediations).

Do not exit between Review and Remediate while Cosmetic, Refactoring, or Performance
findings are still `⬜` or `⚠️` unless they are deferred.

## Stage 3 — Stop Conditions

- **Success (default):** Latest review iteration has zero open findings in **all** buckets
  (Error, Cosmetic, Refactoring, Performance), or every remaining finding is explicitly
  deferred with user approval documented in the review Summary.
- **Success (errors only):** When the user says `errors only` / `nur Errors` — latest
  iteration has zero open **Error** findings. Other buckets may remain. `/review` Ready
  stays zero Errors (`workflow-review.md`); this loop’s default is stricter.
- **Block:** Same Error root cause persists after two remediation attempts in the same
  iteration scope — stop and report blocker ID.
- **Cap:** Stop after 10 review iterations; report remaining open findings as blocker.

## Stage 4 — Resume

- When a review artifact path is provided, resume at first open finding in the latest iteration file.
- Preserve iteration numbering; next full review writes `review_<slug>_<n+1>.md`.
- Re-read scope from the artifact Scope section when scope argument is omitted. Read that file **in full**.

## Stage 5 — Final Report

- Output review iteration table: path, open Error count, open total count, status.
- Output implementation status table for every remediated finding ID.
- Run full build and all tests in optimized/Release. Commands from the loaded tech skill.
- State release verdict from the latest review Summary.
- List deferred findings with deferral reason when user approved deferral.
- Chat: artifact paths; do not recap finding bodies.

### Review Iteration Table

```markdown
| Iteration | Path | Errors | Open Total | Status |
|-----------|------|--------|------------|--------|
| 1 | reviews/review_<slug>_1.md | 2 | 5 | Remediated |
| 2 | reviews/review_<slug>_2.md | 0 | 0 | ✅ Clean |
```

# Implement Workflow

Load on `/implement`. Apply `copilot-instructions.md` for all quality, tech, git, and communication rules.

**Purpose:** Implement approved plan steps or accepted review findings **exactly** — complete every item in scope, match plan/finding intent, no skipped or diverging work. Close with a review brief, then a council Exam of the built result.

## Stage Order

1. Prepare
2. Execute Steps
3. Final Verification
4. Review Brief
5. Closing Exam

## Stage 1 — Prepare

- Require approved plan or accepted review findings per `copilot-instructions.md` Section 6; stop otherwise.
- Run Tech Load Protocol per Section 3.
- Re-read Context Anchor, Requirements (User View), and current step or finding block (`What`, `How`, `Verify`).
- Resume at first `⚠️`, else first `⬜`.

## Stage 2 — Execute Steps

- **Checklist status:** `⚠️` before first edit · `✅` after Verify, alignment, Step NR clean.
- Tick **all three** plan surfaces together before the next item: Step Overview Status, Shared Block status line, Task Checklist. Same emoji. Same moment. Never leave the overview stale.
- Process **every** checklist step or finding in scope; skip none.
- Follow strict topological order per plan dependencies.
- Implement **only** current step or finding scope — match `What` and `How` exactly.
- Do not substitute, simplify, or extend beyond scope without user approval.
- Run `Verify` from plan step or finding; require pass.
- **Alignment check:** compare plan/finding target vs actual result; confirm goal met and no deviation.
- On the Requirements-fit step: walk as a user. Check every R{n}. Mark Met ✅ only when Done-when holds. Any ❌ or leftover ⬜ = blocker.
- Confirm misuse/abuse checklist from plan is satisfied when new public APIs are in scope.
- Run `/review` at each Step NR; zero Errors before next step.
- Persist review file in complex-task mode per `workflow-complex-task.md`.
- Stop with blocker after two failed remediation attempts for same Error root cause.

## Stage 3 — Final Verification

- Confirm **every** scoped step or finding is `✅`; list any remaining `⬜` or `⚠️` as blocker.
- Re-run alignment check for full scope: every R{n} Met ✅, plan done criteria, or all accepted findings resolved.
- Run full build and all tests in optimized/Release configuration. Use Verify/build commands from the loaded tech skill.
- Output Implementation Status Table (every step/finding listed, none omitted).
- Do not enter Stage 4 until this stage is green.

## Stage 4 — Review Brief

Write the brief before Stage 5. Exam skipped → still write it. No complete without it. Chat: path only. Do not apply chat terse style to the brief.

The brief is a review aid. A reviewer must judge, from the card, why the change is required and how it pays `R{n}`/`E{n}`. A card that does not explain that is worthless — expand before Stage 5.

- Path: `reviews/brief_<slug>.md`. Single item: `reviews/brief_<slug>_<item>.md` (`step{n}` / finding ID). Slug from plan or review scope. Before Exam: full-scope file.
- List every created, edited, or deleted path from the working tree. Omit none. Rewrite on remediation.
- Built result only. Name symbols, behavior, contracts. No diffs.
- Header: **Expect**, **Done when**, **Why**, **Out**, then **Requirements**. Link every in-scope `R{n}`/`E{n}` plus a short outcome. Separator ` · `.
- `R{n}` → `[Rn](../plans/plans_<slug>.md#requirements-user-view)`. `E{n}` → the review file. Reuse these links in **How it serves** and **Serves**.
- Existing heading: linked backtick path (template card 1). Deleted: backtick path + `(deleted)` — no link. Repo-root path, forward slashes. Sibling files may share one card; comma-separate heading links.
- Field order: **Changed** → **Why needed** → **How it serves** → **Depends on** → **Serves**.
- **Changed:** name the symbols and behavior that shipped.
- **Why needed:** explain the failure without this file. Not a slogan.
- **How it serves:** the review payload. Per cited `R{n}`/`E{n}`, lead with the linked ID, then explain the causal contribution: which symbols, what exists or is gone, what a caller can or cannot do, which Done-when this file is responsible for. Write full sentences. Expand until a reviewer can check the claim without reconstructing the argument. Unique to this file — do not copy the same paragraph onto every card. Ban slogans, R{n} titles as the whole explanation, empty purpose, and “as planned” / “cleanup” / “refactor” without necessity. Say when the card does not prove a requirement.
- **Serves:** linked IDs only. No prose. Workflow/host-only: `(none — {reason})`; still write a full **How it serves**.
- Order for reading, not git: contracts/types → implementations → cutover → tests → docs. State depends-on.
- Numbered cards. No tables. Stack fields.

```markdown
# Review Brief — {scope}

**Expect:** {outcome; R{n}}
**Done when:** {check}
**Why:** {problem without this work; what stays wrong if it does not ship}
**Out:** {exclusions}

**Requirements:** [R1](../plans/plans_{slug}.md#requirements-user-view) {short outcome} · [R2](../plans/plans_{slug}.md#requirements-user-view) {short outcome}

1. [`{path}`](../{path})
   - **Changed:** {symbols/behavior}
   - **Why needed:** {what fails without this file}
   - **How it serves:** [R1](../plans/plans_{slug}.md#requirements-user-view): {which symbols; what exists or is gone; what a caller can or cannot do; which Done-when this file owns}
   - **Depends on:** —
   - **Serves:** [R1](../plans/plans_{slug}.md#requirements-user-view)

2. [`{path}`](../{path})
   - **Changed:** {symbols/behavior}
   - **Why needed:** {what fails without this file}
   - **How it serves:** [R2](../plans/plans_{slug}.md#requirements-user-view): {causal contribution unique to this file}. [E1](../reviews/review_{slug}.md): {causal contribution unique to this file}
   - **Depends on:** 1
   - **Serves:** [R2](../plans/plans_{slug}.md#requirements-user-view), [E1](../reviews/review_{slug}.md)

3. `{path}` (deleted)
   - **Changed:** Type removed
   - **Why needed:** {what the deleted type blocked or enabled wrongly}
   - **How it serves:** [R1](../plans/plans_{slug}.md#requirements-user-view): {type is gone; callers must use …; Done-when that this deletion closes}
   - **Depends on:** 2
   - **Serves:** [R1](../plans/plans_{slug}.md#requirements-user-view)
```

## Stage 5 — Closing Exam

- Load `workflow-council.md`. Run **Exam** mode on the built result in this invocation's scope (plan R{n}, review brief, touched files, tests, latest Step NR reviews).
- Skip Exam when parent is `workflow-complex-task.md` Stage 3 per-item or `workflow-review-loop.md` Stage 2. Still run Stage 4.
- User `quick`/`lite` → Lite Exam (Skeptic addendum still required). Else Full Exam.
- Chairman **Holds** → implement complete. Cite the brief and the exam artifact. Add the Exam row to the status table.
- Chairman **Does not hold** → remediate kill shots and §4 violations in this run (same gates as Step NR Errors: Verify, alignment, two-attempt cap). Re-run Stage 3 for touched scope, rewrite Stage 4, then re-Exam `_<n>`.
- Grill Me follow-ups → ask user; do not mark implement complete.
- Do not treat Exam as `/review`. Do not skip Step NR reviews because Exam will run.

## Implementation Status Table

```markdown
| Step / Finding | Status |
|----------------|--------|
| Step 1 - {title} | ✅ Complete |
| Step 1R - Review Step 1 | ✅ Clean - 0 Errors |
| Step {N} - Requirements fit | ✅ Every R{n} Met |
| R1 - {outcome} | ✅ Met |
| E1 - {title} | ✅ Fixed |
| Review Brief | ✅ reviews/brief_<slug>.md |
| Closing Exam | ✅ Holds · councils/council_<slug>-exam.md |
```

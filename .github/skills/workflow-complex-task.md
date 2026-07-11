# Complex-Task Workflow

Load on `/complex-task`. Orchestrates plan, implement, and review skills.

## Stage Order

1. Plan
2. Checkpoint
3. Implement/Review Loop
4. Stop Conditions
5. Resume
6. Final Report

## Stage 1 — Plan

- Execute `workflow-plan.md`.
- Write plan artifact per naming rules in that skill.

## Stage 2 — Checkpoint

- Ask immediately after plan creation: continue now or pause for review.
- Stop and report artifact path on pause.

## Stage 3 — Implement/Review Loop

- Iterate checklist in topological order for each `⬜` or `⚠️`.
- Execute `workflow-implement.md` per item.
- Run `/review` on every remediation iteration via implement.
- Persist every review iteration to `reviews/review_<slug>_<iteration>.md`.
- Default review output to file mode.
- Count remediation only after re-review.

## Stage 4 — Stop Conditions

- Success: latest review iteration has zero Error findings per step.
- Defer Cosmetic, Refactoring, and Performance findings.
- Block when same Error root cause persists after two remediation attempts in same step scope.

## Stage 5 — Resume

- Resume at first `⚠️`, else first `⬜` when plan artifact is provided.
- Preserve review iteration numbering.

## Stage 6 — Final Report

- Output implementation status table for all steps and gates.
- Output review iteration table: path, error count, status.
- List deferred Cosmetic, Refactoring, Performance findings.
- State goal-achievement verdict vs plan done criteria.
- Use terse chat per Section 2; cite artifact paths.

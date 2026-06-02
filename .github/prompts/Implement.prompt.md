---
name: implement
description: Execute approved plan/review findings step-by-step with review gates
argument-hint: Reference approved plan artifact or accepted review findings
agent: agent
---

You are executing the implement workflow as a precision-obsessed craftsman.
Verify every assumption before touching code, implement with surgical accuracy, and never mark a step done until the build, all tests, and the review gate are fully clean.
All quality rules, step constraints, review-gate expectations, and output templates are defined in `../copilot-instructions.md`.
Do not restate those rules here. Apply them exactly.

## Stage 1 - Prepare

- Verify approved plan or accepted review findings exist.
- If neither exists, stop and direct user to run `/plan` or `/review` first.
- Check for concurrent edits/conflicts.
- Load applicable technology sections from `../copilot-instructions.md`.
- Re-read plan Context Anchor.
- Confirm compatibility strategy when public contracts are touched.
- Determine resume position (first `⚠️`, else first `⬜`).

## Stage 2 - Execute Steps

- Iterate in strict topological order.
- Mark current step in progress.
- Implement exactly step scope.
- Run step Verify command.
- Run `/review` on step output and remediate until zero Error findings.
- If the same Error remains unresolved after two remediation attempts, stop with blocker report and ask user decision.
- In complex-task mode, persist review iterations to `plans/reviews/<plan-slug>_review_<iteration>.md`.
- Commit per Git rules.
- Mark step complete.

## Stage 3 - Final Verification

- Run full build and all tests in Release mode.
- Perform completeness audit against checklist/findings.
- Confirm done criteria explicitly.
- Output Implementation Status Table.
- Report deferred Cosmetic/Refactoring/Performance findings.

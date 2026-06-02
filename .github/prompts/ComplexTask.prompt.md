---
name: complex-task
description: Orchestrate plan -> checkpoint -> implement/review loop end-to-end
argument-hint: Describe task, optionally include existing plan artifact path to resume
agent: agent
---

You are executing the complex-task workflow as a methodical orchestrator who combines meticulous planning with relentless execution precision.
Plan exhaustively, execute exactly, and never accept "good enough" at any stage.
Detailed behavior is defined in `../copilot-instructions.md`.
Do not restate those rules here. Apply them exactly.

## Stage 1 - Plan

- Run `/plan` behavior and write plan artifact using naming rules.

## Stage 2 - Checkpoint

- Immediately after plan artifact creation, ask user:
  - continue implementation now, or
  - pause for manual plan review.
- If pause: stop and report plan artifact path.

## Stage 3 - Implement/Review Loop

- Iterate checklist items in topological order (`⬜` or `⚠️`).
- For each item, run `/implement` behavior.
- Ensure `/implement` executes `/review` for each remediation iteration.
- Persist each `/review` iteration output file.

## Stage 4 - Stop Conditions

- Success: latest `/review` iteration has zero Error findings.
- Cosmetic/Refactoring/Performance findings may be deferred and do not block success.
- Blocked: same Error (same Error-class root cause in the same step scope after remediation and re-review) persists after two remediation attempts.

## Stage 5 - Resume

- If a plan artifact is provided:
  - resume from first `⚠️`, else first `⬜`,
  - preserve review iteration numbering.

## Stage 6 - Final Report

- Output implementation status table for all steps/review gates.
- Output review-iteration table (path, error count, status).
- List deferred Cosmetic/Refactoring/Performance findings.
- Provide explicit goal-achievement verdict versus plan done criteria.

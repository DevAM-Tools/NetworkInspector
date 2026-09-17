# Complex-Task Workflow

Load on `/complex-task`. Orchestrates requirements (if needed), plan, implement, review, and a closing council Exam.

## Stage Order

1. Requirements
2. Plan
3. Checkpoint
4. Implement/Review Loop
5. Stop Conditions
6. Resume
7. Final Report

## Stage 1 — Requirements

- If a requirements artifact or attached requirements doc already exists, **read it in full** and use it.
- Else execute `workflow-requirements.md`.

## Stage 2 — Plan

- Execute `workflow-plan.md` (Stage 0 will reuse Stage 1 output).
- Write plan artifact per naming rules in that skill.

## Stage 3 — Checkpoint

- Ask immediately after plan creation: continue now or pause for review.
- Stop and report artifact paths (requirements + plan) on pause.

## Stage 4 — Implement/Review Loop

- Iterate checklist in topological order for each `⬜` or `⚠️` (including dedicated test-case steps).
- Execute `workflow-implement.md` per item. Skip implement Stage 5 Exam. Run Stage 4 (extensive **briefing**). This workflow runs one Exam after the loop is Error-clean.
- Run `/review` on every remediation iteration via implement.
- Persist every review iteration to `reviews/review_<slug>_<iteration>.md`.
- Default review output to file mode.
- Count remediation only after re-review.

## Stage 5 — Stop Conditions

- Preview success: latest review iteration has zero Error findings per step, every scoped step and `TEST{n}` `✅`.
- Require full-scope `reviews/briefing_<slug>.md`. Rewrite if missing or step-only. Legacy `reviews/brief_<slug>.md` may be read; new writes use `briefing_`.
- Then run one **Exam** (`workflow-council.md` Exam mode) on the full built scope. Same agent; no subagents.
- Exam **Holds** → Success.
- Exam `exam-fail` → return to Stage 4 for those kill shots; then re-Exam `_<n>`.
- Defer Cosmetic, Refactoring, and Performance findings. This is intentional: `/complex-task`
  is a planned delivery loop. Step `{n}R` gates on `/review` Ready (zero Errors). Nits stay
  deferred so the plan can finish and Exam can run. They are not this workflow’s job.
  `/review-loop` is the full-clean loop (zero findings in all buckets unless deferred).
  Do not copy that default here.
- Block when same Error root cause persists after two remediation attempts in same step scope, or the same Exam root fails twice.

## Stage 6 — Resume

- Resume at first `⚠️`, else first `⬜` when plan artifact is provided.
- Read the plan and requirements **in full** before continuing.
- Preserve review iteration numbering.

## Stage 7 — Final Report

- Output implementation status table for all steps, `TEST{n}` rows, gates, Briefing, and Closing Exam.
- Output review iteration table: path, error count, status.
- Cite the briefing and the Exam artifact. Goal verdict requires Exam **Holds**.
- List deferred Cosmetic, Refactoring, Performance findings.
- State goal-achievement verdict vs every `REQ{n}` with Done-when, every `TEST{n}`, and plan done criteria.
- Chat: artifact paths; do not recap bodies.

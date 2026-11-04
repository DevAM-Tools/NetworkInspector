# Complex-Task Workflow

Load on `/complex-task`. Orchestrates requirements (if needed), plan, implement with step reviews, and one Closing Review.

## Stage Order

1. Requirements
2. Plan
3. Checkpoint
4. Implement/Review Loop
5. Closing Review
6. Resume
7. Final Report

## Stage 1 — Requirements

- A requirements artifact or attached requirements doc exists → **read it in full** and use it.
- Else execute `workflow-requirements.md`.

## Stage 2 — Plan

- Execute `workflow-plan.md` (its Stage 0 reuses Stage 1 output). Write the plan artifact per that skill’s naming rules.

## Stage 3 — Checkpoint

- Ask right after the plan exists: continue now or pause for review.
- Pause → stop and report the requirements and plan paths.

## Stage 4 — Implement/Review Loop

- Execute `workflow-implement.md` Stages 1–4 for each checklist item in topological order (`⬜`, `🚧`, or `⚠️`, dedicated test steps included). Skip implement Stage 5; this workflow runs one Closing Review in Stage 5.
- Persist every Step `{n}R` iteration to `reviews/review_<slug>_<iteration>.md` (file mode). Count a remediation only after its re-review.
- Step gate: zero Error and zero High Performance findings. Fix High Performance like an Error (Section 4.4).
- Defer Cosmetic, Refactoring, and other Performance findings. This is intentional: `/complex-task` is a planned delivery loop, and nits must not stop the plan from finishing. `/review-loop` is the full-clean loop; do not copy its default here.
- Block when the same Error root cause persists after two remediation attempts in the same step scope. Report per `copilot-instructions.md` Section 4.16.

## Stage 5 — Closing Review

- Require every scoped step and test ID `✅` and a full-scope `reviews/briefing_<slug>.md` (rewrite it if missing or step-only; legacy `reviews/brief_<slug>.md` may be read, new writes use `briefing_`).
- Run `workflow-implement.md` Stage 5 on the full built scope. Its deferral rule is this workflow’s Stage 4 rule.
- **Ready** → Success. **Blocked** → return to Stage 4 for those findings, then the next Closing Review `_<n+1>`. The same root cause failing twice → blocker (Section 4.16).
- Exam only on user request (implement Stage 5).

## Stage 6 — Resume

- A plan artifact is given → resume at the first `⚠️`, else the first `🚧`, else the first `⬜`.
- Read the plan and requirements **in full** before continuing.
- Keep review iteration numbering.

## Stage 7 — Final Report

- Implementation Status Table for all steps, test ID rows, gates, Briefing, and Closing Review.
- Review iteration table: path, Error count, status (Step `{n}R` and Closing Review files).
- Cite the briefing and the latest Closing Review. The goal verdict requires Closing Review **Ready** (and Exam **Holds** when an Exam ran).
- List deferred Cosmetic, Refactoring, and Performance findings.
- State the goal verdict against every requirement ID with Done-when, every test ID, and the plan done criteria.
- Chat: the run report (Section 4.16; item mapping in `workflow-implement.md` Stage 6) on every exit after Stage 4 starts, including block and pause. **Not done** lists deferred findings. Do not recap bodies.

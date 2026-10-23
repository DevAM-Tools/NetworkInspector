---
name: workflow-review
description: Skeptical auditor review. Answer ready-for-public-release for the scope. Summary is a findings table plus a hunt-results table. Ignore prior reviews unless named. Load skills from the Section 3 table. Locked finding How. Default runs tests; static on explicit reduced scope. Use on /review.
argument-hint: Describe files/features/PR to review
---

Execute `/review` workflow.

- Read `.github/copilot-instructions.md` unless it is already in context.
- Read `.github/skills/workflow-review.md`.
- Apply rules exactly; do not restate them here.

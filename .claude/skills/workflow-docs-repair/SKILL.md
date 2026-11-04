---
name: workflow-docs-repair
description: Realign docs and code comments with the implementation in an existing project and bring them to the tech-docs standard. Evidence decides doc-side fixes; context-rich DRIFT decision cards go to a person when it does not; code-side fixes become findings for implement. Resumable ledger in docs-repair/. Use on /docs-repair or when docs and code have drifted apart.
argument-hint: Name the scope (whole repo, components, or doc set), or "resume"
---

Execute `/docs-repair` workflow.

- Read `.github/copilot-instructions.md` unless it is already in context.
- Read `.github/skills/workflow-docs-repair.md`.
- Apply rules exactly; do not restate them here.

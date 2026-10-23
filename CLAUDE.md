# Claude Code bootstrap

Rules and skills are **not** duplicated here. The single source of truth is under `.github/`.

@.github/copilot-instructions.md

If `custom_instructions.md` exists at the repository root, read and apply it (Section 1 above). Missing is not an error.

## Workflow entry in Claude Code

- Skills live in `.claude/skills/<name>/SKILL.md`. Each is a pointer to its `.github/skills/<name>.md` file. Cursor and VS Code read the same folder, so there is one set of pointers for all three tools.
- Invoke a workflow by skill name (`/workflow-plan`, `/workflow-implement`, `/workflow-review`, `/workflow-review-loop`, `/workflow-port-dotnet-rust`, `/workflow-port-dotnet-rust-loop`, …) or by its natural-language trigger from Section 6 above. Short names such as `/review` or `/plan` may belong to built-in Claude Code commands; the `/workflow-*` name always reaches the repository workflow.
- Before planning, implementation, or review edits, run the Tech Load Protocol (Section 3) by reading the matching `.github/skills/*.md` files with `Read`.

## Tool mapping

- “File-read tool” (Section 3) is `Read`. “Subagent or delegation tool” (Section 4.14) is `Agent`: do not use it for council, advisors, peer review, or Exam.
- Section 4.11 (Git read-only) applies to every Bash and PowerShell call.
- Integrated browser (Sections 4.8 and 4.15): the desktop app browser pane; in the CLI and the VS Code extension, the Claude in Chrome tools when connected. Without either, say so once and use headed Playwright or a trace (`tech-playwright.md`).
- Section 4.7 forbids ad-hoc scripts. Use the native toolchain commands from the loaded tech skill.

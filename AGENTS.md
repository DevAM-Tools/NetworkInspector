# Agent Instructions

Structured agent workflow with **single source of truth** in `.github/`. Tool-specific files under `.cursor/`, `.claude/`, and `CLAUDE.md` are thin pointers only — no rule duplication.

## SSOT map

| Concern | Location |
|---------|----------|
| Always-on policy, quality contract, tech triggers, terms | [`.github/copilot-instructions.md`](.github/copilot-instructions.md) |
| Optional overlay (skip if missing) | `custom_instructions.md` at repo root |
| Workflow stages | [`.github/skills/workflow-*.md`](.github/skills/) |
| Technology rules | [`.github/skills/tech-*.md`](.github/skills/) |

Workflows: `/requirements`, `/plan`, `/implement`, `/review`, `/review-loop`, `/complex-task`, `/council`, `/commit-message`, `/illustrate`, `/port-dotnet-rust`, `/port-dotnet-rust-loop`.

Tech: C#, Rust, Python, .NET → Rust port mapping, tests (`tech-test.md` plus TUnit / pytest / Rust / Playwright), Web UI (`tech-web.md`), Blazor, SourceGen, build.

## Shared skill pointers

| Mechanism | Path | Read by |
|-----------|------|---------|
| Agent skills | [`.claude/skills/`](.claude/skills/) | Claude Code, Cursor, VS Code (GitHub Copilot) |

One pointer set for all tools. Each `SKILL.md` points to its `.github/skills/` file. Do not add a second set under `.cursor/skills/`: Cursor loads both folders and lists every skill twice.

## Claude Code integration (CLI and VS Code extension)

| Mechanism | Path | Role |
|-----------|------|------|
| Bootstrap | [`CLAUDE.md`](CLAUDE.md) | Imports `.github/copilot-instructions.md`; tool mapping |
| Workflows | `/workflow-plan`, `/workflow-review`, … | Skill names; `/plan` and `/review` are built-in Claude Code commands |

## Cursor integration

| Mechanism | Path | Role |
|-----------|------|------|
| Always-on rule | [`.cursor/rules/copilot-ai-workflow.mdc`](.cursor/rules/copilot-ai-workflow.mdc) | Bootstrap + SSOT paths |
| Slash commands | [`.cursor/commands/`](.cursor/commands/) | Same names as workflows above |

## GitHub Copilot integration

| Mechanism | Path |
|-----------|------|
| Repository instructions | [`.github/copilot-instructions.md`](.github/copilot-instructions.md) |
| Custom prompts | [`.github/prompts/`](.github/prompts/) |

All tools share the same `.github/` content. Update rules in one place only.

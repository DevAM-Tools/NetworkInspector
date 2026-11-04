# Instruction and Skill Authoring

Load when `.github/copilot-instructions.md`, `.github/skills/**`, `.github/prompts/**`, `.cursor/**`, `.claude/skills/**`, `AGENTS.md`, `CLAUDE.md`, or `custom_instructions.md` is in scope.

The reader is an agent. Every token is paid on every load. Quality wins over length: a shorter text that changes agent behavior for the worse is a regression.

## Rules

- Write imperative, one rule per bullet. The subject is the agent: `Do X.` · `Never X.`
- Mark with ❗ only a rule whose miss is an Error in review.
- State the observable: what must exist, where, in which form. Ban adjectives without a check (“clean”, “robust”, “good”).
- Give a reason only when it lets the agent decide an unlisted case. One clause.
- Prefer a Wrong/Right pair, a template, or a table over a paragraph. Label worked examples “copy the depth, not the content”.
- Write closed sets (statuses, kinds, IDs) as a table or fence. Write thresholds as numbers.
- Use the terms of `copilot-instructions.md` §5.1. A new term gets one row there.

```text
Wrong: It is important to make sure that documentation is always kept reasonably up to date.
Right: ❗ A change to behavior updates every affected doc in the same change (Sync check, tech-docs.md).
```

## Structure

- One home per rule. Elsewhere, point to it by section name (`tech-docs.md` § Sync check); names survive renumbering. Never paraphrase another file’s rule; paraphrases drift.
- `copilot-instructions.md` holds intent, ❗ rules, and routers. Mechanisms live in skills that load on a trigger.
- A workflow skill: purpose line, load trigger, stage order, stages, templates, completion.
- Move templates that only some runs need into a companion skill with its own trigger (`tech-docs-templates.md`).
- Pointer files (`.claude/skills/*/SKILL.md`, `.github/prompts/*`, `.cursor/commands/*`) hold no rules: name, description, argument hint, read list.

## Compaction without loss

1. List the behaviors the text causes (one line each). That list is the contract.
2. Cut: restated rules, reasons that change nothing, hedges, filler (“please”, “make sure to”, “it is important”), synonym lists, a second example of the same depth.
3. Keep: every ❗, number, closed set, template field, Wrong/Right pair, trigger, and cross-file reference.
4. Re-read the compacted text against the list. A behavior that no longer follows from the text is a lost rule: restore it.
5. A compaction never changes a rule. A rule change is its own edit with its own CHANGELOG line.

## Change coupling (workflow kit)

These files change together. Nothing fails when one is forgotten.

| When you | Also update |
|----------|-------------|
| Add, rename, or remove a workflow | `.github/skills/workflow-<name>.md` · `.claude/skills/workflow-<name>/SKILL.md` · `.cursor/commands/<name>.md` · `.github/prompts/<Name>.prompt.md` · `copilot-instructions.md` §1 trigger list and §6 table · `.cursor/rules/copilot-ai-workflow.mdc` list and table · `AGENTS.md` workflow list · `CLAUDE.md` workflow list · `README.md` (structure, On-Demand skills, Workflow overview, Cursor usage) · `CHANGELOG.md` |
| Add, rename, or remove a tech skill | `.github/skills/tech-<name>.md` · `.claude/skills/tech-<name>/SKILL.md` · `copilot-instructions.md` §3 table · `README.md` On-Demand skills · `AGENTS.md` Tech line · `CHANGELOG.md` |
| Change a workflow description | `.claude/skills/*/SKILL.md` `description` · `.github/prompts/*` `description` · `README.md` Workflow overview |
| Change a stage name another skill cites | Grep the old name in `.github/`, `README.md`, `CHANGELOG.md` |
| Add a term | `copilot-instructions.md` §5.1 |
| Change a Shared Block field or Summary row | Every skill that ticks or reads it (`workflow-implement.md`, `workflow-review-loop.md`, `workflow-complex-task.md`) |
| Change a run report section (`copilot-instructions.md` Section 4.16) | `workflow-implement.md` Stage 6 · `workflow-review-loop.md` · `workflow-complex-task.md` · `workflow-docs-repair.md` · `workflow-port-dotnet-rust-loop.md` Final Report |
| Change the doc set or a doc’s sections | `copilot-instructions.md` Section 4.6 table · `tech-docs.md` § Doc set · `tech-docs-templates.md` · `workflow-docs-repair.md` |

## Review

- Walk each changed rule against `copilot-instructions.md`, the router tables, the pointer descriptions, `README.md`, and `AGENTS.md`.
- A rule stated in two files is an Error: keep the home, point from the other.
- A pointer file that states a rule is an Error.

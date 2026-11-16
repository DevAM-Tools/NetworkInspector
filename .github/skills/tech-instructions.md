# Instruction and Skill Authoring

Load when `.github/copilot-instructions.md`, `.github/skills/**`, `.github/prompts/**`, `.cursor/**`, `.claude/skills/**`, `AGENTS.md`, `CLAUDE.md`, or `custom_instructions.md` is in scope.

The reader is an agent, sometimes a weak one with 100k tokens. Every token is paid on every load. Quality wins over length: a shorter text that changes agent behavior for the worse is a regression.

## Rules

- Write imperative, one rule per bullet. The subject is the agent: `Do X.` · `Never X.`
- Mark with ❗ only a rule whose miss is an Error in review.
- State the observable: what must exist, where, in which form. Ban adjectives without a check (“clean”, “robust”, “good”).
- Give a reason only when it lets the agent decide an unlisted case. One clause.
- Prefer a Wrong/Right pair, a template, or a table over a paragraph. Label worked examples “copy the depth, not the content”.
- Write closed sets (statuses, kinds, IDs) as a table or fence. Write thresholds as numbers.
- Use the terms of `copilot-instructions.md` §5.1. A new term gets one row there.
- Never write “if needed”, “when appropriate”, “everything important”, or “relevant” without the condition or the list.
- ❗ Every step leaves a trace an agent or reviewer can check: a line, a table row, a status, a file. A step without a trace (“walk the topics”, “compare target and actual”) gets skipped.
- ❗ Every artifact section a workflow demands has a template or a filled example. Each workflow skill carries at least one filled example of its main artifact (a plan step, a finding, a requirement entry, a state file).
- A term that draws a line (Error vs Cosmetic, slice, hot path, Decision class) gets a decision rule and one example per side.
- A table cell that only names a topic (“Boundaries”, “Section 4.7”) is not a rule: say what to settle or check.
- Keep an example only when it shows what the rule’s words cannot: a trap, a form, or a Wrong/Right contrast. Cut one that repeats the rule. Show the smallest fragment that carries the point.

```text
Wrong: It is important to make sure that documentation is always kept reasonably up to date.
Right: ❗ A change to behavior updates every affected doc in the same change (Sync check, tech-docs.md).
```

## Structure

- One home per rule. Elsewhere, point to it by section name (`tech-docs.md` § Sync check); names survive renumbering. Never paraphrase another file’s rule; paraphrases drift.
- ❗ A pointer replaces a rule text, never a checklist. A check table or review hunt names what to check, also when the rule lives elsewhere: an agent mid-review does not open the pointed-to section.

```text
Wrong: | Repo | Section 4.7 |
Right: | Repo | Section 4.7: OS and CPU matrix; UTF-8 console; Debug = Release; line length; copyright; lint; dependencies; license |
```
- `copilot-instructions.md` holds intent, ❗ rules, and routers. Mechanisms live in skills that load on a trigger.
- A workflow skill: purpose line, load trigger, stage order, stages, completion.
- ❗ Name the skill to load in the stage that needs it: `Read x.md now.` A load that only a far-away table implies gets forgotten.
- Move templates into a companion skill loaded at the writing stage (`workflow-plan-templates.md`, `workflow-review-templates.md`).
- Move implementation into a package or project and name it in one bullet. Move rationale into a decision record.
- Pointer files (`.claude/skills/*/SKILL.md`, `.github/prompts/*`, `.cursor/commands/*`) hold no rules: name, description, argument hint, read list. A `description` states the load trigger: tools pick skills from it.
- ❗ Separate artifacts by reader (`tech-reporting.md`). A workflow never requires a person to read an agent handoff.

## Size budgets

| File | Budget |
|------|--------|
| `copilot-instructions.md` | 24 KB |
| Workflow skill (without templates) | 20 KB |
| Templates companion | 16 KB |
| Language skill (`tech-csharp.md`, `tech-rust.md`, `tech-python.md`) | 20 KB |
| `tech-reporting.md`: every form a person reads | 15 KB |
| Other tech skill loaded on most runs | 12 KB |

A larger file needs a reason in its decision record, or a split by trigger.

❗ A workflow skill loads once, for the task it describes. There precision wins over size: never cut a decision rule, a trace, a template, or an example to meet a budget. `copilot-instructions.md` and tech skills load on most runs; there size wins, and detail moves to the skill that needs it.

## Compaction without loss

1. List the behaviors the text causes (one line each). That list is the contract.
2. Cut: restated rules, reasons that change nothing, hedges, filler (“please”, “make sure to”, “it is important”), synonym lists, a second example of the same depth.
3. Keep: every ❗, number, closed set, template field, Wrong/Right pair, trigger, and cross-file reference. Keep each sentence that excludes a known shortcut (`never because …`, `… alone is not enough`, `never only X and skip Y`): it is not a restated rule, it closes a loophole. Keep the examples inside a definition (`procedure: stage order, git, …`) and the check keywords of a check table.
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
| Change a Shared Block field or Summary row | Every skill that ticks or reads it (`workflow-implement.md`) and the companion templates |
| Change a run report section (`tech-reporting.md`) | `workflow-implement.md` Stage 6 and § Review until clean · `workflow-docs-repair.md` · `workflow-port-dotnet-rust-loop.md` Final Report · `workflow-evolve.md` · `workflow-dream.md` |
| Change the doc set or a doc’s sections | `tech-docs.md` § Doc set · `tech-docs-templates.md` · `workflow-docs-repair.md` |
| Change the test layers or the Code tests ledger line | `tech-test.md` § Two layers, § Code tests · `tech-spec.md` § Test card · `workflow-implement.md` Stage 2 item 3, Stage 3 · `workflow-evolve.md` Stage 4, § Builder · `workflow-review.md` § Tests · `copilot-instructions.md` §4.5 |
| Change the spec format or an integrity check | `tech-spec.md` · `tech-reqtrace.md` · `workflow-requirements.md` · `workflow-plan-templates.md` · `workflow-dream.md` · `workflow-review.md` § Trace |
| Change a `reqtrace` command, option, rule, or output line | `src/ReqTrace/` · `docs/agents.md` · `tech-reqtrace.md` (§ When to run what, § Findings, examples) · `src/ReqTrace/PACKAGE_README.md` |
| Change when `accept` runs | `tech-reqtrace.md` § When to run what, § Accept · `workflow-requirements.md` § Completion · `workflow-plan.md` § Completion · `workflow-implement.md` Stage 6 · `workflow-evolve.md` Gate A and Gate B |
| Change a gate or `spec/state.md` | `workflow-evolve.md` (§ Progress note reads **Queue** and **Position**) · `tech-reporting.md` · §5.1 |
| Change a progress note field or point | `tech-reporting.md` § Progress note · `workflow-implement.md` Stage 2 item 12 · `workflow-evolve.md` § Progress note, Stage 0, Gate A, Stage 4, Stage 5, Completion · §5.1 |
| Change a breakpoint | `tech-web.md` · `tech-playwright.md` |

## Review

- Walk each changed rule against `copilot-instructions.md`, the router tables, the pointer descriptions, `README.md`, and `AGENTS.md`.
- A rule stated in two files is an Error: keep the home, point from the other.
- A pointer file that states a rule is an Error.
- A compaction that drops a shortcut-excluding sentence, a definition’s examples, or a check table’s keywords is an Error: diff the old text against the behavior list of § Compaction without loss.

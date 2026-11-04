# Documentation Standards

Load whenever code, comments, or project docs are in scope: every `/plan`, `/implement`, `/review`, `/review-loop`, `/complex-task`, `/docs-repair`. The rules are in `copilot-instructions.md` Section 4.6. This skill adds the details and the checks. Language syntax: the language skill’s **Comments and docs** section (§ Language contract). Templates: `tech-docs-templates.md`.

**Goal:** docs and code say the same thing for the whole life of the project, so the next agent decides from facts. Few docs, one job each, each kept true by every change.

## Doc set

| Doc | Holds | A fact belongs here when |
|-----|-------|--------------------------|
| `README.md` | What it is, quickstart, build and test, links to the docs | A newcomer needs it first |
| `docs/architecture.md` | **Overview** (component table: folder, entry, responsibility, may depend on); per component: flows, invariants, **Easy to miss** | It says which part does what and who may call whom |
| `docs/concepts.md` | Ideas of the solution that span components: models, algorithms, formats, state machines | It explains how our solution works inside |
| `docs/domain.md` | Glossary, domain rules with their source, external facts | It stays true if the code were rewritten from scratch |
| `docs/decisions/<slug>.md` | One decision record: context, decision, rejected options, consequences | It answers “why A and not B” |

- Minimum per project: `README.md` and `docs/architecture.md`. A single-component project may keep the architecture as a `## Architecture` section in `README.md`.
- Create `concepts.md`, `domain.md`, and a decision record with their first fact. Never create an empty shell.
- Also allowed: `docs/guides/<slug>.md` (how to do a task) and `CHANGELOG.md` (user-visible change per release).
- Code is a home too: the contract sits in the doc comment, the why sits at the code. A doc links the symbol and never copies a signature.
- `plans/`, `reviews/`, `councils/`, `illustrations/`, `docs-repair/` are workflow history and git-ignored. A product doc never cites them. A durable outcome found there (a decision, a domain fact, a user answer) moves to its home in the same run.

## Reading tiers

Pay for context only where the task is. Read a whole doc only when the task covers the whole doc.

| Tier | Read | When |
|------|------|------|
| 1 | `README.md`, architecture **Overview** | Before any plan, edit, or review |
| 2 | The architecture section of each component in scope | The component is in scope |
| 3 | Concept, domain, and decision entries | The task uses the term or touches the component: grep the term, read that heading |

- Keep tier 1 short: the Overview fits on one screen. Detail belongs in component sections.
- Record what you read in the plan Context Anchor or the review Scope: `Read: architecture Overview, § Import · domain § Credit note`.
- A missing doc: say so there, continue from the code, name `/docs-repair` as the next step.

## Architecture stays true

- The Overview column **May depend on** is the dependency rule. A dependency not listed is a defect, or it needs a user decision, an Overview update, and a decision record (Section 4.6).
- The entry type of each component (or its module or namespace doc) links its architecture section, so an agent that starts in the code finds the rule.
- Architecture names types and paths. It holds no signatures and no per-file lists; those drift.

## Doc comments

❗ Every type and member, private included. When the name says everything, state what the name cannot: unit, range, empty meaning, ownership, cost.

| Field | States | When |
|-------|--------|------|
| Summary | What it does or is, in caller terms; never the name reworded | Always |
| Parameters | Meaning, unit, valid range, empty or absent meaning | Each parameter |
| Returns | Meaning; the value on failure | Returns a value |
| Errors | Each error or exception and its condition | Can fail |
| Side effects | I/O, state change, events, logging | Has any |
| Thread safety | Per language skill | Shared types |
| Cost | Complexity, allocation, blocking | Hot path or not obvious |
| Omitted validation | What is unchecked, why, what the caller guarantees | Section 4.1 |
| Example | A typical call and the call order | Public entry points; non-obvious use |
| Related | § References | Bound items exist |

Types add: role, lifecycle, ownership, invariants.

```text
Wrong: Gets the rejected count.
Right: Counts the invoice rows in the file that failed validation; 0 when the file has none.
```

## References

A reference tells the next editor what else to check. It does not map the code base.

- **Related** (1–4 links in the doc comment). Link only:
  - items bound in meaning: the other half of a pair (parser ↔ formatter, request ↔ response), the type that holds an invariant this item relies on, the options or limits type, the error type;
  - items this one depends on in a way its signature does not show;
  - items that must change with this one and are easy to forget.
- ❗ Never list call sites or usages. Tools find them, and the list goes stale with every caller.
- Use the language’s checked link form, so a moved target fails the build.
- **`Sync:`** marks a coupling the compiler cannot check (config key, CLI option ↔ README table, schema file ↔ type, event name string, fixture ↔ format). Write it at **both** ends and name the other end (path and symbol, or doc section). More than two parties, or one end is prose: add the coupling to the component’s **Easy to miss** line in the architecture.

```text
Wrong (usages):  Related: called by ImportCommand, ReplayCommand, BatchScheduler.
Right (meaning): Related: InvoiceBatch (the model this reader fills).
                 Sync: README.md § Options table (--input).
```

## Inline comments

- ❗ Lead each semantic block of a non-trivial body with a comment: intent and what to watch.
- ❗ **Comment-only read:** signatures, doc comments, and block comments alone tell the flow.
- Write what the code cannot say: purpose, motivation, background (link the domain or concept entry), the rejected simpler approach and why, the edge case a branch handles.
- Start these comments with a tag so a grep finds each one:

| Tag | Use for | Must state |
|-----|---------|------------|
| `Invariant:` | A condition other code relies on | The condition; who relies on it |
| `Perf:` | An optimization or hot-path constraint | What it saves; its cost; what would break it |
| `Security:` | A security decision (Section 4.2) | Threat; mitigation; rejected option; residual risk |
| `Sync:` | A coupling the compiler does not check | The other end; what must match |

- ❗ Never write: change history (“was”, “now”, “previously”), commented-out code, author names, dates, `TODO` / `FIXME` / `HACK` (Section 4.1), tracking IDs, a restatement of the line below.

```text
Wrong: // Loop over the lines
       // Changed to span split (was string split before)
Right: // Walk the report line by line. Perf: splitting a view keeps this loop allocation-free;
       // a string split allocates one string per line. Windows reports keep a trailing '\r',
       // so the parser trims it before the prefix check.
```

## Large files

- Group members into named sections with the language’s section marker. A grep for the marker lists the outline.
- Group by content: a section holds what serves one responsibility (its state, entry points, and helpers), never one member kind (`Fields`, `Private helpers`).
- Order sections by the flow, in call order. Name them for what they do (`Line parsing`, not `Helpers 2`).
- ❗ No file header that describes the file. The type doc (or module doc) is that description; a second one drifts.
- A file with two unrelated purposes is split (Section 4.9). Sections do not excuse it.

## Language contract

Each language skill’s **Comments and docs** section defines these four mechanisms. A new language skill defines all four before its first plan.

| Need | C# · Rust · Python (examples) |
|------|-------------------------------|
| Doc comment form with the fields | XML docs · `///` with `# Errors`, `# Panics` · Google docstrings |
| Checked link for Related | `<see cref>` · intra-doc link · qualified name in backticks (grep-checked) |
| Section marker | `#region` · `// region:` · `# region` |
| Doc lint that fails the build | CS1591, CS1574 · `missing_docs`, `rustdoc::broken_intra_doc_links` · ruff `D` |

## Impact search (before editing)

Run in `/plan` Stage 1 and before the first edit in each file. It produces the affected-file list of Section 4.1.

1. Read the architecture section of the component, including **Easy to miss**.
2. Read the doc comments, **Related**, and `Sync:` lines of the items to change.
3. Grep each changed, renamed, or removed symbol, path, option, key, and message across the repository, docs and config included.
4. Grep the touched items inside other files’ `Sync:` lines.
5. Book each hit: affected (edit) or unaffected (one-line reason).

**Touched code meets the standard.** New and changed members get doc comments, Related links, and block comments. A coupling the change creates gets its `Sync:` pair. Gaps elsewhere in the file are outside the change: name them once (plan Out, run report **Not done**) with `/docs-repair` as the next step.

## Sync check (before ✅)

Run at plan-step close, per finding fix, and as the review **Docs** hunt. Scope: the touched files plus every Impact-search hit.

1. Re-read the doc comment and block comments of each changed member against the new body: parameters, returns, errors, branches, units, cost, tags, Related.
2. Each touched architecture section, concept, domain entry, guide, README section, decision record, and `Sync:` partner states the new truth.
3. The change keeps every documented boundary, dependency, flow, and invariant, or the architecture and a decision record changed with it.
4. Knowledge learned in this run is in its home.
5. Each link, path, and backticked symbol resolves (grep). Each documented command in a touched doc runs (full review) or matches the build files (static review).
6. No doc or comment describes history. No fact is copied outside its home.

Record one line where the step or finding closes:

```text
Sync check: updated docs/architecture.md § Run (Easy to miss), docs/domain.md § Retention · unaffected README.md (no option change)
```

## Review severity

- **Error:** missing or contract-less doc comment; Related that lists usages; body that fails the comment-only read; stale doc or comment; history comment; architecture broken without an architecture update and decision record; learned knowledge not captured in its home; file header that repeats the type doc; non-compiler coupling without `Sync:` at both ends; broken link, path, or symbol; documented command that fails; fact copied outside its home.
- **Cosmetic:** comment that restates the code; a comment that should carry a tag; a section named without content (`Helpers 2`); an Overview longer than one screen.
- A change-scoped review (Step `{n}R`, a finding fix) files gaps the change did not touch once, as one Refactoring finding that names `/docs-repair`; they do not block the change. A file-scoped `/review` files them as Errors.

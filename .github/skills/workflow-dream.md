# Dream Workflow

Load on `/dream`, or from `workflow-evolve.md` Stage 5. Also load now: `tech-spec.md`, `tech-reqtrace.md`, `tech-reporting.md`, `tech-docs.md`. Apply `copilot-instructions.md`.

**Purpose:** Consolidate and repair the repository’s knowledge: domain, requirements, test content, the trace between them and the code, docs. Work piles up by addition; this pass folds it back into one consistent state.

Edits knowledge only. ❗ Never change production code or tests here; a needed change becomes a finding.

## Modes

| Mode | Scope | When |
|------|-------|------|
| `touched` | The areas and domain topics of the last slice | End of each `/evolve` slice |
| `full` | All areas, one at a time | On request; `/evolve` proposes it after 10 slices or 20 inbox lines |

`full` on a context window ≤ 200k tokens: one area per session. Record the position in `spec/state.md`.

## Authority

Decide alone only when one holds and nothing contradicts it:

- The meaning stays the same: a duplicate merged, a typo fixed, an entry moved to its home, an index row added.
- Code and a passing test both show the fact and no requirement states otherwise.
- A trace link is stale and a search finds its single new target.

Always a card (`tech-reporting.md`):

- A requirement’s obligation would change.
- Two active requirements conflict.
- Requirement, test, and code disagree in any combination.
- Two domain sources disagree.
- A requirement’s code is gone.

Retire a duplicate requirement with `Replaced by`. Never rename an ID. Never delete an entry that holds a fact until the fact has a home.

## Stages

Per area. Re-read each file before changing it (`tech-spec.md` § Editing shared knowledge).

### 1 Check

Run `reqtrace check` (call form: `tech-reqtrace.md` § Call form), then by search the integrity checks of `tech-spec.md` it does not cover. Book each Error, each open gap of the area, and each miss. `full` with .NET tests: run `check --results` after the test run of stage 5.

### 2 Domain

1. Sort `inbox.md`: place each line on its page, or start a page. Empty the inbox line by line.
2. Merge entries that say the same. Keep the sources.
3. Conflicts → card. Keep both, marked, until answered.
4. Remove what is no longer true and what is not domain knowledge: move it to its home.
5. Split a page over ~200 lines by subject. Refresh `index.md`.
6. Compare glossary terms with code names; a mismatch is a finding or a glossary fix.

### 3 Requirements

1. Read the area as one contract. Grep its key terms across other areas; read those entries.
2. Conflicts and overlaps → retire a duplicate, or card. Before retiring or merging an entry: `reqtrace show <ID>`; move each card it lists to the entry that stays.
3. Done-when that is an adjective or needs source knowledge: tighten it when the claim stays; else card.
4. Move leftover proposed entries to a card.

### 4 Test content

1. Walk `tech-test.md` classes per requirement. A missing class: add a `proposed` card, or `Out` with a reason.
2. Merge duplicate cards. A card without `Content` is a gap.
3. Repair markers: a card whose test lost its marker gets it back on the test that proves it. No such test → `Status ⬜` and a finding.

### 5 Code and docs

1. Repair `Built in` the same way.
2. Run the Sync check (`tech-docs.md`) on docs that name the area. Fix doc-side drift under § Authority. A large drift → recommend `/docs-repair`; do not start it.
3. `full`: run the full test suite. Reset `Met` to `❌` for a failing requirement and write a finding.

### 6 Close

- Run `reqtrace check` → zero Errors, except C3 on a card whose requirement this dream reworded: list it under **Not done** for the next review's **Meaning** hunt; the writer never confirms its own change.
- Never run `accept`: no person approved the dream's changes. The next approval records them.
- Code-side problems become findings in `reviews/review_dream-<slug>_<n>.md` (Shared Block of `workflow-review-templates.md`). `/implement` executes them.
- Fill `Last dream` in `spec/state.md` from `git rev-parse --short HEAD`.

## Completion

Chat: the run report (`tech-reporting.md`). **Outcome** names counts per stage: fixed, merged, retired, cards, findings. Do not recap fixed items.

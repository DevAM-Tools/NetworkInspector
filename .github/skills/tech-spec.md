# Living Spec and Domain Knowledge

Load on `/requirements`, `/plan`, `/implement`, `/review`, `/evolve`, `/dream`, and when `spec/**` or `docs/domain/**` is in scope. This skill is the format. Authoring rules: `workflow-requirements.md`. Repair: `workflow-dream.md`.

The spec is the system of record for what the software must do. It is tracked in git and outlives every plan.

## Layout

```text
spec/index.md                  System picture, user groups, boundary, scope; one row per area
spec/requirements/<area>.md    REQ entries of one area
spec/tests/<area>.md           TEST cards of one area
spec/state.md                  Loop position (workflow-evolve.md)
spec/open.md                   Decision cards waiting for a person
docs/domain/index.md           One row per domain page
docs/domain/<topic>.md         One topic
docs/domain/inbox.md           Captured facts not yet sorted
```

- `<area>` is the lowercase area token: `REQ-EXPORT-…` lives in `spec/requirements/export.md`.
- Split a file over ~300 lines into `<area>-<topic>.md`. IDs do not change. Add the file to the index.
- Create a file with its first entry. Never create an empty shell.
- A requirement file may hold prose above its first `###` heading and stories under `## User stories` (`workflow-requirements.md` Stage 3); retired entries go under `## Retired`.
- A person may name another requirements file. Keep its IDs. Import it into `spec/` unless told not to.

`spec/index.md` rows:

```markdown
| Area | Requirements | Tests | Covers |
|------|--------------|-------|--------|
| Export | [export](requirements/export.md) | [export](tests/export.md) | Creating and listing exports |
```

## IDs

`REQ-<AREA>-<TOPIC>` · `TEST-<AREA>-<TOPIC>` · `DRIFT-<AREA>-<TOPIC>`.

- Prefix plus 2 to 4 tokens. A token is `A–Z` and digits, holds at least one letter, and tokens split on one `-`.
- First token: the area (subject). Other tokens: hint at the claim or case. The sentence is the requirement.
- English. ❗ Unique across `spec/`. ❗ Never renamed, never reused after retirement.
- Requirements on one subject share one area token. Prefer a short name the user already uses (`MCP`, `UI`).
- Kind stays in the sentence. Never put `SHALL`, `SHALL-NOT`, `HAPPY`, or `ERROR` in an ID. Never use `C{n}`, `Q-…`, or `E{n}` as a requirement ID.
- When the case is that requirement, reuse its topic: `REQ-EXPORT-NO-DUPLICATE` → `TEST-EXPORT-NO-DUPLICATE`. Another case adds a token: `TEST-EXPORT-EMPTY-NAME`.
- A wording fix that keeps the claim keeps the ID. A changed obligation retires the ID and mints a new one.
- ❗ Never write these IDs into production code or comments. One exception: a test carries the marker of its card (§ Trace).

```text
Accept: REQ-EXPORT-FAIL-CLOSED · REQ-SESSION-STABLE-ID · TEST-EXPORT-NO-DUPLICATE
Reject: REQ1 · REQ-1 · REQ-0042 · REQ-EXPORT-1 · REQ-SHALL-NOT-REMOTE · TEST-ExportFormTests
```

## Requirement entry

```markdown
### Export

- **REQ-EXPORT-NO-DUPLICATE** — The system shall not add a second row when Report is submitted again; a duplicate-name message is visible.
  - **Done when:** The operator submits Report again, still sees one row, and sees the duplicate-name message.
  - **State:** active · **Met:** ⬜
  - **Proof:** test
  - **Built in:** [`src/Export.cs`](../../src/Export.cs) `Export.Create`
```

Closed sets:

| Field | Values |
|-------|--------|
| State | `proposed` (until a person approves it: Gate A, a Spec delta, or a plan) · `active` · `retired` |
| Met | `⬜` not proven · `✅` Done-when ran and held · `❌` ran and failed |
| Proof | `test` · `demo — {reason}` · `review — {reason}` · `waived — {reason}` |

- `Proof` is how the requirement is shown to hold. Use `test` unless no test can show it.

- `Built in`: 1 to 3 entry symbols. Fill at step close. Never list call sites.
- Retired entries move to `## Retired` in the same file and keep their text:

```markdown
- **REQ-EXPORT-CSV-ONLY** — The system shall export CSV only.
  - **State:** retired · **Replaced by:** REQ-EXPORT-FORMATS · **Why:** the user added XLSX.
```

## Test card

Test content is durable. It lives here, not in a plan.

```markdown
### TEST-EXPORT-NO-DUPLICATE

- **Proves:** REQ-EXPORT-NO-DUPLICATE
- **Class:** error
- **Layer:** UI
- **Content:** An export named Report exists. The user submits Report again. A duplicate-name error is visible and the list still has one row. Catches silent overwrite.
- **Out:** concurrent submits (TEST-EXPORT-NO-DUPLICATE-CONCURRENT)
- **State:** active · **Status:** ⬜
```

| Field | Values |
|-------|--------|
| Class | happy · error · boundary · collection · absence · concurrency · trust-boundary |
| Layer | unit · integration · UI |
| State | `proposed` · `active` · `retired` |
| Status | `⬜` no test yet · `✅` the Content exists as a test that can fail |

- Require `Proves`, `Content`, `State`. `Proves: none — {reason}` covers a case that guards a rule, not a requirement.
- Omit `Class`, `Layer`, or `Out` when they add nothing.
- Never let a class tag, file name, or method name replace `Content`.
- The cards are the minimum to prove. Code tests (`tech-test.md` § Code tests) need no card and carry no marker.
- ❗ Never delete or weaken a test to get green. A card test changes only through its card.

## Trace

Each link has one home. Coverage is derived by search.

| Link | Home |
|------|------|
| Requirement → code | `Built in` on the requirement |
| Test → requirement | `Proves` on the card |
| Test → code | A marker on the test: the card ID, once per test |

Marker forms. Only tests carry them; never production code.

```text
C#:     [Test, Property("Card", "TEST-EXPORT-NO-DUPLICATE")]
Python: @pytest.mark.card("TEST-EXPORT-NO-DUPLICATE")
Rust:   // card: TEST-EXPORT-NO-DUPLICATE
```

## Integrity checks

❗ Run `reqtrace check` after each change to `spec/` or to tests. Fix every Error before ✅. Read `tech-reqtrace.md` now: call form, setup, when to run which command, and what to do per finding. `reqtrace syntax <kind>` prints the form the tool reads.

- The tool covers I1, I2, I4, I5, I11, the marker half of I6, and I7 with `--results` (.NET tests). Run the rest by search in `/dream` and the review **Trace** hunt; do not write a script.
- `reqtrace` not available: run all checks by search and say so in the run report (`tech-reqtrace.md` § Without the tool).
- Only `accept` and `confirm` write the accepted state `spec/trace.lock`; when each may run: `tech-reqtrace.md` § Accept, § Confirm.

| Check | Holds when |
|-------|------------|
| I1 Unique | Each ID heads exactly one entry in `spec/` |
| I2 Form | Each ID follows § IDs |
| I3 No reuse | A retired ID has no second entry; `Replaced by` names an existing ID or `none` |
| I4 Proven | Each active requirement is named in `Proves` of an active card |
| I5 No orphan | Each active card names active requirements or `none — {reason}` |
| I6 Resolves | Each `Built in` path exists and its symbol is found there; each marker names an existing card |
| I7 Honest | `Met ✅` only when each proving card is `✅`; `Status ✅` only with a marker |
| I8 Clean code | No `REQ-`, `TEST-`, or `DRIFT-` ID in production source or comments; a test holds only its marker |
| I9 Indexed | Each spec and domain file has an index row; each row resolves |
| I10 Consistent | No two active requirements conflict; one term means one thing |
| I11 Closed | Each entry has every required field; `proposed` entries exist only until their approval |

## Editing shared knowledge

Applies to `spec/**`, `docs/domain/**`, `docs/architecture.md`, `docs/concepts.md`. Half-read files cause duplicates.

1. ❗ Read the index or the headings. Search the terms. Read the target section in full before writing.
2. ❗ Update the entry that holds the fact. Add an entry only when the search found none.
3. Insert at the place the topic belongs. Never append by default.
4. Change only the entries in scope. Never reformat or reorder others.
5. A fact that contradicts an entry: never overwrite silently. Decide per `copilot-instructions.md` Section 4.16.

## Domain knowledge

The domain is true even if the code were rewritten: glossary, rules with their source, external facts.

- ❗ Capture every domain fact in the run that hears it, also one mentioned in passing, also from an error message or a third-party spec.
- Known topic: edit that page per § Editing shared knowledge. Unknown topic, or no time to place it: add a line to `inbox.md`.
- Read `index.md`, then grep the term, then read that heading. Read a whole page only when the task covers it.
- Keep pages under ~200 lines. `/dream` splits them.
- Never put plans, implementation detail, or history into a domain page.

```markdown
# docs/domain/index.md
| Topic | Holds | Page |
|-------|-------|------|
| Invoices | Credit notes, totals, rounding rules | [invoices](invoices.md) |

# docs/domain/invoices.md
## Credit note
An invoice with a negative total that cancels an earlier one. Say "credit note", not "refund" (a refund is a payment). **Source:** user answer. **In code:** `CreditNote`.

# docs/domain/inbox.md
- Rounding happens per line, not per invoice. — Source: user, in passing.
```

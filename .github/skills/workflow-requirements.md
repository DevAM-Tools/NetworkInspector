# Requirements Workflow

Load on `/requirements`, from `/plan` or `/evolve`. Also load now: `tech-spec.md`, `tech-reqtrace.md`. Apply `copilot-instructions.md`. Do not implement. Do not edit production code. Do not write a plan.

**Keep one list of atomic, contradiction-free requirements for the whole project.** Each entry is one obligation: the system shall do it, shall not do it, or shall exhibit it. Stay implementation-far: types, files, APIs, frameworks, and tests belong in `/plan`.

## Modes

| Mode | When | Result |
|------|------|--------|
| Full | First requirements of a project; `/requirements` | `spec/index.md` picture and area files |
| Delta | `spec/` exists; `/evolve`; a new request | Changes to area files plus a Spec delta. Skips Stage 2; a new user group, boundary, or scope cut still updates `spec/index.md` |

The user already supplied requirements → use them. Read the file in full. Keep their IDs. Import per `tech-spec.md`. If the text is a task list or a design, ask before lifting items to shall-statements.

## What gets an ID

- Give an ID only to one shall, one shall-not, or one property. Format: `tech-spec.md` § IDs.
- No ID on: intro, headings, context, boundary, stories, examples, notes, cross-references, open questions.
- The list is the only place an obligation is stated. A prose sentence that is an obligation moves into the list. A cut that forbids a behavior is a requirement.
- Never keep a second list of behaviors, limits, or KPIs.

## Atomic

- One entry is one obligation a reader can accept or reject alone.
- Two outcomes that pass or fail independently: split.
- A split that only names steps or widgets of one outcome: keep one ID. Do not split into implementation tasks. Do not pad.
- One claim, one ID. Cite an existing ID instead of restating it.
- Do not cap the count. Omit nothing the system requires.

```text
Split:  "The system shall export a CSV file and send it by mail."
        → REQ-EXPORT-CSV and REQ-EXPORT-MAIL: the mail can fail while the export holds.
Keep:   "The list shall show name, date, and size of each export." → one ID: three columns of one outcome.
Wrong:  REQ-EXPORT-ADD-SERVICE "Add an ExportService class." → an implementation task; it belongs in /plan.
Wrong:  REQ-EXPORT-GOOD-UX "The export shall be user-friendly." → no outcome a reader can accept or reject.
```

## Contradiction-free

❗ Check every new entry against the existing spec before writing it: read its area file in full, grep its key terms in the other areas, read those entries.

- Two entries conflict when one situation cannot satisfy both. Merge, drop, or decide. Never publish both.
- A narrower entry may add a limit the broader still allows; name the broader ID. A narrower entry that denies the broader conflicts.
- A new statement that contradicts an active requirement:

| Situation | Do |
|-----------|----|
| The user clearly replaces it | Retire the old ID, mint a new one, show both under **Conflicts resolved** |
| Unclear which side holds | Card (Section 4.16). Keep the old one active |

- One term means one thing in every entry. Use the glossary in `docs/domain/`.
- An undecided fork is a question. Never a requirement marked open or TBD.

```text
Conflict: REQ-EXPORT-OFFLINE "shall create exports without a network" · new: "shall upload each export on creation".
          Creating an export offline cannot satisfy both → card; the old one stays active.
Narrower: REQ-IMPORT-FILE "shall import a file the user picks" · new: REQ-IMPORT-SIZE-LIMIT
          "shall reject a file above the import limit (limits REQ-IMPORT-FILE)" → both hold.
Term:     "user" means the operator in one entry and the API client in another → name each actor.
```

## Stages

1. Gather
2. System picture (Full mode only)
3. Behavior and properties
4. Agent access
5. Grill Me
6. Write
7. Coverage

### Stage 1 — Gather

Read every attached architecture, guide, brief, and requirement in full. Read `spec/index.md`. Record only what sources and the user state. Do not invent groups, neighbors, KPIs, or limits. Capture domain facts (`tech-spec.md` § Domain knowledge).

### Stage 2 — System picture

Write `spec/index.md` so a first-time reader does not need the code. Prose only. No IDs.

```markdown
# Spec — {system}

{Who asked, the job, why now, what this is, who it serves, where it sits.}

## User groups
- {group, at least one} — {what they expect; how that differs}

## Boundary
{Where and when it runs (desk, CI, inside another app, unattended, on- or offline): operating context, not hosting design. Inside vs outside. Neighbors at the edge. Inputs and outputs at the system boundary, not function parameters: what a group provides, what it receives, how it recognizes the result.}

## In scope
## Out of scope
{Cuts. Do not restate a shall-not that has an ID.}

## Areas
{Table per tech-spec.md}
```

Omit a section only when it does not apply and that is settled. Never leave one blank or fill it with “n/a”. Do not invent types, folders, frameworks, or test files. A product or constraint the user required may appear in a shall-statement; the ID still names the subject.

### Stage 3 — Behavior and properties

Write as many entries as the system needs, grouped by area. Entry shape: `tech-spec.md` § Requirement entry. New entries are `proposed`.

- **Shall** — *The system shall …*
- **Shall not** — *The system shall not …*
- **Property** (quality, KPI, performance, limit) — *The system shall exhibit …* Name measure, target, and who cares. Not allocation recipes or cache types.

Stories (*As a {group}, I {interaction}, so that {outcome}. → REQ-…*) go under `## User stories` in the area file when a group interacts. They do not replace requirements.

**Done-when** names **actor**, **boundary action**, **observable**. A later agent or person runs it without reading source. Rewrite it until it is an example, not an adjective; when that needs a fact only the user has, it is Decision class.

```text
Reject: Export works · API is robust · performant · add ExportService
        · summary.json projects.length==2 · use a Dictionary cache
        · The system shall let an operator create an export (a shall-sentence, not a check)
        · it builds · tests exist · docs updated
Accept: The operator submits the name Report and sees one row titled Report.
Accept: The agreed typical workload on the agreed machine class finishes in ≤ 30s, measured from submit to the result row.
Accept: The user raises the import limit in Settings from 1 GB to 4 GB, sees why the limit exists and its allowed range, and imports a 3 GB file.
```

### Stage 4 — Agent access

For a new application or a new user-visible capability: add requirements for the agent surface per `tech-agent-access.md`, or a waiver `REQ-AGENT-NONE` with a reason. Ask only when the surface is Decision class; else propose it in the Spec delta.

### Stage 5 — Grill Me

Ask only Decision-class points (Section 4.16). Everything else becomes an assumption under **Assumptions**. Ask all at once. Never re-ask; follow up only on new ambiguity and cite the prior answer.

Topics that are often Decision class: user groups with differing expectations; boundary; what is out; hard constraints (legal, platform, offline, data that must not leave the machine); limits per `tech-limits.md` in user terms (value or range, what it protects, who may change it, secure or open by default and why); conflicts between requirements.

Never ask about: API signatures, test content, Playwright journeys, file shape, types (`/plan`).

```text
Ask:        "May exports leave the machine?" → changes the security posture: Decision class.
Assumption: "Which date format does the list use?" → the operator's locale; log it, say what changes if it is wrong.
Never ask:  "Should the store be an interface?" → /plan decides.
```

Form: Section 4.16, with `**Source:** Requirements`. Under `/evolve`, write cards to `spec/open.md`. Never leave an open fork in the list.

### Stage 6 — Write

- Edit per `tech-spec.md` § Editing shared knowledge. Update `spec/index.md`.
- Before retiring, replacing, or rewording an existing entry: `reqtrace show <ID>`. List each card and test it names under **Conflicts resolved** or in the Spec delta; `/plan` changes them.
- After writing: `reqtrace check` → zero Errors. New entries are `proposed`, so they raise no L4 yet; F1–F5 and L1 do.
- `/plan` links area files. Never copy requirements into a plan.
- Never put requirement IDs in code or comments.
- `Met` starts `⬜`. Tick only after Done-when **ran**.
- Delta mode: write the Spec delta (`tech-reporting.md`).

### Stage 7 — Coverage

Re-read the conversation and attached docs in full. Map every ask, group, boundary fact, input, output, exclusion, shall, shall-not, property, and non-goal. An obligation lands on an ID; a heading is not a landing spot. Orientation may land on a prose section. The same obligation under two IDs, two IDs that cannot both hold, or an unmapped obligation is a gap: patch and re-run.

The table goes into the Spec delta (`tech-reporting.md`). In Full mode without a Spec delta, write it to `reviews/delta_<slug>.md`. Never into `spec/`.

## Completion

Chat: the delta path, a one-line goal verdict, and every risk ordered by impact; do not cap or drop risks. A blocking open question follows Section 4.16.

Approval sets the new entries `active`:

| Run | Who approves | Then |
|-----|--------------|------|
| `/requirements` alone | The person, in chat; ask for it with the delta path | Set the entries `active`. `reqtrace accept --allow-gap "cards and tests follow in /plan"` (`tech-reqtrace.md` § Accept) |
| From `/plan` Stage 0 | The plan approval | `workflow-plan.md` § Completion |
| From `/evolve` | Gate A | `workflow-evolve.md` § Gate A |

❗ Only explicit words approve. A question or a comment on one entry is no approval; the entries stay `proposed`.

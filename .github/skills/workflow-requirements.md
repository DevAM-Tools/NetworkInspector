# Requirements Workflow

Load on `/requirements`, or from `/plan` / `/complex-task` when no usable
requirements exist. Apply `copilot-instructions.md` Sections 2–4. Do not
implement. Do not edit production code. Do not write a plan.

**Build one list of atomic, contradiction-free requirements.** Each entry is
one obligation: the system shall do it, shall not do it, or shall exhibit it.
The ID names the subject, so a citation is recognizable without the sentence.
Headings, context, and notes stay prose. Stay implementation-far. Types, files,
APIs, frameworks, and tests belong in `/plan`.

## When

- User runs `/requirements` (or equivalent).
- `/plan` has no requirements file, attached doc, or usable list → run this
  skill, then plan.
- User already supplied requirements → **use them**. Read the file **in full**.
  Do not reinvent. Keep IDs they already assigned. Fill gaps with Grill Me.
  New IDs follow this skill. If the text is a task list or design, ask before
  lifting items to user-level shall-statements.

## IDs

Form: `REQ-<AREA>-<TOPIC>`.

- `REQ-` plus 2 to 4 tokens. A token is uppercase `A–Z` and digits, contains at
  least one letter, and tokens split on a single `-`.
- The first token is the **area** (the subject: `SESSION`, `EXPORT`, `MCP`).
- The other tokens **hint at the claim** (`STABLE-ID`, `FAIL-CLOSED`,
  `V1-LOCAL`). A reader can tell the topic from the ID. The sentence is the
  requirement.
- English. Unique in the file. Stable: inserting an entry does not rename
  others. Do not reuse an ID after delete. When the obligation itself changes,
  retire the ID and mint a new one. A wording fix that keeps the same claim
  keeps the ID.
- Requirements about the same subject share one area token. The heading is
  that area in words (`### Export` holds `REQ-EXPORT-…`).
- Kind (shall, shall not, property) stays in the sentence. A claim hint such
  as `NO-CONFIRM` or `LOCAL-ONLY` may name the topic. Do not put `SHALL` or
  `SHALL-NOT` in the ID.
- Prefer a short name the user already uses (`MCP`, `UI`, `API`). Avoid a
  token that only its author can expand.

```text
Accept: REQ-EXPORT-FAIL-CLOSED · REQ-SESSION-STABLE-ID · REQ-MCP-V1-LOCAL
Reject: REQ1 · REQ01 · REQ-1 · REQ-0042 · REQ-EXPORT-1
        · REQ-SHALL-NOT-REMOTE · REQ-THE-SYSTEM-SHALL-FAIL-CLOSED
```

Cite `REQ-EXPORT-FAIL-CLOSED`. Link
`[REQ-EXPORT-FAIL-CLOSED](../requirements/req_<slug>.md)`. Add a heading
`### REQ-EXPORT-FAIL-CLOSED` only when a fragment target is needed. The list
is bullets under area headings, not one heading per requirement.

Do not use `C{n}`, `TEST-<AREA>-<TOPIC>`, `Q{n}`, or `E{n}` as requirement IDs.

`Met` applies to requirement entries (each has Done-when). Tick only after
Done-when **ran**.

## What gets an ID

An ID means this entry is part of the contract.

Give an ID only to an atomic requirement: one shall, one shall-not, or one
property.

Leave these unlabeled:

- Title, intro, and area headings
- Intention, audience, context, boundary, inputs and outputs, scope narrative
- User stories, examples, rationale, and notes
- A cross-reference to another ID
- An open question (Grill Me, `Q{n}`)

The requirement list is the only place an obligation is stated. When a prose
sentence is an obligation, move it into the list. When a cut forbids a
behavior, that forbid is a requirement; the scope prose does not restate it.

Do not keep a second unlabeled list of behaviors, limits, or KPIs.

## Atomic and contradiction-free

One entry is one obligation a reader can accept or reject alone.

- When the sentence names two outcomes that could pass or fail independently,
  split it.
- When a split would only name steps, parts, or widgets of one outcome, keep
  one ID.
- Do not split an obligation into implementation tasks. Do not add mid/low
  “levels”. Do not pad.
- One claim, one ID. Cite an existing ID instead of restating its obligation
  in a second entry.
- This skill does not cap the count. Omit nothing the system requires.

Before writing the artifact, read the list as one contract.

- Two entries conflict when one situation cannot satisfy both. Merge, drop, or
  Grill Me. Do not publish both.
- A narrower entry may add a limit the broader entry still allows. Name the
  broader ID in the narrower sentence. When the narrower entry denies the
  broader one, they conflict.
- One term means one thing in every entry. When the same word names two actors
  or two objects, say which.
- An undecided fork is a question. It is not two requirements, and it is not a
  requirement marked open, provisional, or TBD.

## Stage Order

1. Gather
2. System picture
3. Behavior and properties
4. Grill Me
5. Write artifact
6. Coverage

## Stage 1 — Gather

Read every attached architecture, guide, brief, and requirement **in full**.
Record only what sources and the user state. Do not invent groups, neighbors,
KPIs, or limits.

## Stage 2 — System picture

Write the system so a first-time reader does not need the codebase. Prose
only. No requirement IDs on this picture.

- **Intention** — who asked, job, why now, success picture.
- **System** — one paragraph: what this is, who it serves, where it sits.
- **User groups** — every distinct group; how expectations differ. Name at
  least one.
- **Context of use** — where and when it runs (desk, CI, inside another app,
  unattended, on/offline). Operating context, not hosting design.
- **Boundary** — in this system vs outside. Neighbors at the edge only.
- **Inputs / outputs** — at the **system** boundary, not function parameters.
  What a group provides; what they receive; how they recognize it.
- **In scope / out of scope** — endeavor vs explicit cuts.
- **User stories** — when a group interacts: *As a {group}, I {interaction},
  so that {outcome}.* In the artifact, point each story at one or more
  requirement IDs. Stories do not replace those requirements and do not get
  IDs.

When a sentence is an obligation, it belongs in Stage 3.

Do not invent types, folders, frameworks, class names, or test files. A
product or constraint the user required may appear in a shall-statement. The
ID still names the subject (`REQ-CHART-TIME-SERIES`), not a type or a file.

## Stage 3 — Behavior and properties

Write as many requirements as the **system** needs. One shall-statement per
ID. Group them by area. The heading has no ID.

- **Shall** — required behavior. *The system shall …*
- **Shall not** — forbidden behavior. *The system shall not …*
- **Property** — expected characteristic (quality, KPI, performance, limit).
  *The system shall exhibit …* Name measure, target, and who cares. Not
  allocation recipes or cache types (`/plan`).

Give every requirement Done-when. Shall, shall-not, and property share one
list. Do not split the file into kind-sections.

Run the atomic and contradiction-free checks here. Unresolved conflicts go to
Grill Me. They do not stay in the list.

### Done-when

Done-when names **actor**, **boundary action**, **observable**. A later agent
or human must run it without reading source. Shallower than plan-step
Acceptance.

```text
Reject: Export works · API is robust · performant · add ExportService
        · summary.json projects.length==2 · use a Dictionary cache
Accept: The system shall let an operator create an export named Report
        and show one row titled Report
Accept: The system shall not add a second row when Report is submitted again;
        a duplicate-name message is visible
Accept: The system shall exhibit wall time ≤ 30s for the agreed typical
        workload on the agreed machine class
```

Ban slogans. Ban “it builds” / “tests exist” / “docs updated” (Section 4).
Ban Done-when that holds only if you know the code.

## Stage 4 — Grill Me

Ask all unresolved questions in one round. Do not re-ask. Follow up only on
new ambiguity; cite the prior answer.

Cover if still open: groups and differing expectations; context; boundary;
inputs/outputs; in/out of scope; shall / shall-not / properties until
Done-when is an example, not an adjective; conflicts between draft
requirements; limits (value, constant vs configurable in user terms); hard
constraints (legal, platform, offline, data that must not leave the machine).

Do not Grill Me: API signatures, `TEST-<AREA>-<TOPIC>` content, Playwright journeys, file
shape, types. Those are `/plan`.

```markdown
## Q{n} — {topic}
**Source:** Requirements
**Context:** {1-3 sentences}
**Question:** {single-part question}
**Options:** 1) {option} · 2) {option} · 3) {option} · or free-text
```

Do not proceed while blocking ambiguities remain. Do not leave an open fork
in the requirement list.

## Stage 5 — Write artifact

- Path: user path, else `requirements/req_<slug>.md`. English (Section 4.6).
  Slug: lowercase; punctuation/whitespace → `-`; collapse `-`; trim; fallback
  `task`.
- Omit a section only when it does not apply **and** Grill Me settled that.
  Do not leave a required section blank. Do not fill with “n/a”.
- `/plan` **links** this file. Do not copy the requirement list into the plan.
- Do not put requirement IDs in code or comments. Cite them in plans,
  briefings, and coverage.
- Requirement entries use the bullet shape below. An obligation with no ID is
  incomplete. A heading or note with an ID is incomplete.

Required sections: opening picture, Boundary, In scope, Out of scope,
Requirements. Coverage is required and is filled in Stage 6.

Include User stories when a group interacts. Omit Notes when empty. When
Grill Me recorded no properties, say that in Out of scope prose. Do not mint
an ID for “none”.

```markdown
# Requirements — {title}

{Who asked, the job, why now, what this is, who it serves, where it sits.}

## Boundary

{Where and when it runs. Inside vs outside. Neighbors. What crosses the
boundary, and how a group recognizes the result.}

## In scope

{The endeavor in the user's words.}

## Out of scope

{Cuts. Do not restate a shall-not that already has an ID.}

## User stories

- As a {group}, I {interaction}, so that {outcome}. → REQ-EXPORT-CREATE

## Requirements

### Export

- **REQ-EXPORT-CREATE** — The system shall let an operator create an export
  named Report and show one row titled Report.
  - **Done when:** The operator submits the name Report and sees one row
    titled Report.
  - **Met:** ⬜
- **REQ-EXPORT-NO-DUPLICATE** — The system shall not add a second row when
  Report is submitted again; a duplicate-name message is visible.
  - **Done when:** The operator submits Report again, still sees one row, and
    sees the duplicate-name message.
  - **Met:** ⬜
- **REQ-EXPORT-TIME-BUDGET** — The system shall exhibit wall time ≤ 30s for
  the agreed typical workload on the agreed machine class.
  - **Done when:** That workload on that machine class finishes in ≤ 30s.
  - **Met:** ⬜

## Notes

{Preference or doc link. Omit this section when empty.}
```

IDs in the template are examples. Shall-statements stay implementation-far.
Done-when stays checkable. Area order is reading order.

## Stage 6 — Coverage

Re-read the conversation and attached docs **in full**. Map every user ask,
group, boundary fact, I/O, exclusion, shall, shall-not, property, and
non-goal.

- An obligation lands on a requirement ID.
- A heading or section name is not a landing spot for an obligation.
- Orientation that does not constrain the system may cite a prose section.
- The same obligation under two IDs is a gap: keep one.
- Two IDs that cannot both hold are a gap.
- An unmapped obligation is a gap. Patch until clean.

Append the table to the artifact. Coverage rows are not requirements and get
no requirement ID. Re-run this stage after any patch.

```markdown
## Coverage

| Item | Source | Lands in |
|------|--------|----------|
| {obligation} | User · Q{n} · Doc | REQ-EXPORT-CREATE |
| {who asked, why now} | User | opening picture |
```

## Completion

Return the artifact path. Status table, goal verdict, risks ≤5. Chat: path
only. Do not recap tables.

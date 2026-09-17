# Requirements Workflow

Load on `/requirements`, or from `/plan` / `/complex-task` when no usable requirements exist. Apply `copilot-instructions.md` Sections 2–4. Do not implement. Do not edit production code. Do not write a plan.

**Record required behavior and expected properties** of the system from the user view. State what the system **shall** do and **shall not** do, and which properties it **shall** exhibit. Stay implementation-far. Types, files, APIs, frameworks, and tests belong in `/plan`.

## When

- User runs `/requirements` (or equivalent).
- `/plan` has no requirements file, attached doc, or usable list → run this skill, then plan.
- User already supplied requirements → **use them**. Read the file **in full**. Do not reinvent. Fill gaps with Grill Me. If the text is a task list or design, ask before lifting items to user-level shall-statements.

## IDs

Format: `REQ{n}`. `n` is an integer ≥ 1. No padding (`REQ1`, not `REQ01` or `REQ-1`). Unique in the file. One sequence for the **whole** artifact — do not restart per section. Do not reuse after delete.

Every list item and table row is `REQ{n}`, whatever the category.

Cite `REQ1`. Link `[REQ1](../requirements/req_<slug>.md)`. Heading `### REQ1` when a fragment target is needed.

Do not use `C{n}`, `TEST{n}`, `Q{n}`, or `E{n}` here.

`Met` applies only to `REQ{n}` that have Done-when (shall, shall-not, property). Tick only after Done-when **ran**.

## Stage Order

1. Gather
2. System picture
3. Behavior and properties
4. Grill Me
5. Write artifact
6. Coverage

## Stage 1 — Gather

Read every attached architecture, guide, brief, and requirement **in full**. Record only what sources and the user state. Do not invent groups, neighbors, KPIs, or limits.

## Stage 2 — System picture

Write the system so a first-time reader does not need the codebase. Give every bullet and row a `REQ{n}`.

- **Intention** — who asked, job, why now, success picture.
- **System** — one paragraph: what this is, who it serves, where it sits.
- **User groups** — every distinct group; how expectations differ. Name at least one.
- **Context of use** — where and when it runs (desk, CI, inside another app, unattended, on/offline). Operating context, not hosting design.
- **Boundary** — in this system vs outside. Neighbors at the edge only.
- **Inputs / outputs** — at the **system** boundary, not function parameters. What a group provides; what they receive; how they recognize it.
- **In scope / out of scope** — endeavor vs explicit cuts.
- **User stories** — when a group interacts: *As a {group}, I {interaction}, so that {outcome}.* Map each story `REQ{n}` to at least one shall / shall-not / property `REQ{n}`. Stories do not replace those.

Ban: types, folders, frameworks, class names, test files.

## Stage 3 — Behavior and properties

Write as many `REQ{n}` as the **system** needs. This skill does not cap the count. One shall-statement per ID. Do not split into implementation tasks. Do not add mid/low “levels”. Do not pad. Omit nothing the system requires.

- **Shall** — required behavior. *The system shall …*
- **Shall not** — forbidden behavior. *The system shall not …*
- **Property** — expected characteristic (quality, KPI, performance, limit). *The system shall exhibit …* Name measure, target, and who cares. Not allocation recipes or cache types (`/plan`).

Give every shall, shall-not, and property a `REQ{n}` and Done-when. Do not keep a parallel un-IDed KPI list.

### Done-when

Done-when names **actor**, **boundary action**, **observable**. A later agent or human must run it without reading source. Shallower than plan-step Acceptance.

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

Ban slogans. Ban “it builds” / “tests exist” / “docs updated” (Section 4). Ban Done-when that holds only if you know the code.

## Stage 4 — Grill Me

Ask all unresolved questions in one round. Do not re-ask. Follow up only on new ambiguity; cite the prior answer.

Cover if still open: groups and differing expectations; context; boundary; inputs/outputs; in/out of scope; shall / shall-not / properties until Done-when is an example, not an adjective; limits (value, constant vs configurable in user terms); hard constraints (legal, platform, offline, data that must not leave the machine).

Do not Grill Me: API signatures, `TEST{n}` content, Playwright journeys, file shape, types. Those are `/plan`.

```markdown
## Q{n} — {topic}
**Source:** Requirements
**Context:** {1-3 sentences}
**Question:** {single-part question}
**Options:** 1) {option} · 2) {option} · 3) {option} · or free-text
```

Do not proceed while blocking ambiguities remain.

## Stage 5 — Write artifact

- Path: user path, else `requirements/req_<slug>.md`. English (Section 4.6). Slug: lowercase; punctuation/whitespace → `-`; collapse `-`; trim; fallback `task`.
- Omit a section only when it does not apply **and** Grill Me settled that. Do not leave a required section blank. Do not fill with “n/a”.
- `/plan` **links** this file. Do not copy these tables into the plan.
- Do not put `REQ{n}` in code or comments. Cite them in plans, briefings, and coverage.
- Label every list item and table row with `REQ{n}`. An unlabeled entry is incomplete.

Required sections: Intention, User groups, Context of use, System boundary, Inputs and outputs, In scope, Out of scope, Must not happen, Required behavior, Expected properties, Coverage.

Include User stories when a group interacts. Omit Expected properties only when Grill Me recorded none — then state that as an out-of-scope `REQ{n}`.

```markdown
# Requirements — {title}

## Intention

- **REQ1** — {who, job, why now, success}
- **REQ2** — {what this is, who it serves, where it sits}

## User groups

| ID | Group | Expects | Differs how |
|----|-------|---------|-------------|
| REQ3 | {name} | {what good looks like} | {vs other groups, or —} |

## Context of use

- **REQ4** — {where/when it runs}

## System boundary

- **REQ5** — {in vs outside; neighbors}

## Inputs and outputs

| ID | Dir | What | At the boundary |
|----|-----|------|-----------------|
| REQ6 | In | {provided} | {how it arrives} |
| REQ7 | Out | {received} | {how it is recognized} |

## In scope

- **REQ8** — {endeavor as the user means it}

## Out of scope

- **REQ9** — {cut and why}

## Must not happen

| ID | The system shall not | Done when | Met |
|----|----------------------|-----------|-----|
| REQ10 | {forbidden outcome} | {actor, action, observable} | ⬜ |

## User stories

- **REQ11** — As a {group}, I {interaction}, so that {outcome}. → REQ12 · REQ3

## Required behavior

| ID | The system shall | Done when | Met |
|----|------------------|-----------|-----|
| REQ12 | {required behavior} | {actor, action, observable} | ⬜ |

## Expected properties

| ID | The system shall exhibit | Done when | Met |
|----|--------------------------|-----------|-----|
| REQ13 | {property, measure, target} | {how observed} | ⬜ |

## Notes

- **REQ14** — {preference or doc link}
```

Keep tables to about 3–4 columns. Shall-statements stay implementation-far. Done-when stays checkable. Continue `REQ{n}` in document order; the numbers above are examples, not a fixed map.

## Stage 6 — Coverage

Re-read the conversation and attached docs **in full**. Map every user ask, group, boundary fact, I/O, exclusion, shall, shall-not, property, and non-goal. Lands in must cite `REQ{n}`, not section names. Unmapped = gap. Patch until clean.

```markdown
**Coverage (conversation → requirements):**

| Item | Source | Lands in |
|------|--------|----------|
| {one-line item} | User · Q{n} · Doc | REQ3 · REQ12 · REQ9 |
```

## Completion

Return the artifact path. Status table, goal verdict, risks ≤5. Chat: path only. Do not recap tables.

# Plan Workflow

Load on `/plan`. Apply `copilot-instructions.md` Sections 2–4.

Plans are for **human acceptance** and for a **weaker executing agent**. Before/After, locked `How`, test **content** (`TEST{n}`), and step acceptance criteria are the handoff. Prefer fenced snippets over prose when describing types, APIs, algorithms, control flow, or file shape; prose carries why, constraints, and what code cannot show. Extra illustrative snippets (call-site usage, data examples, test shape) are welcome throughout the plan. They do not replace locked Before/After or TEST{n} **Content**. Do not treat existing files under `plans/` as style examples unless the user points at one. Do not compress step `How`, Test case **Content**, or Requirements-fit. Do not copy the requirements table into the plan.

## Stage Order

0. Requirements
1. Gather Context
2. Perspective Sweep
3. Grill Me
4. Reconcile
5. Decision Loop
6. Write Plan
7. Coverage Check

## Stage 0 — Requirements

- If a requirements file already exists (user path, attached workspace doc, or `requirements/req_<slug>.md`): **read it in full** and use it. Do not rewrite outcomes that are already clear. That file is the high-level **user-view** system description (`workflow-requirements.md`). Do not turn it into a task list in the plan.
- Else execute `workflow-requirements.md` (write `requirements/req_<slug>.md` from an explicit list without reinventing, or run the full requirements workflow), then continue.
- The plan **links** that file. It does **not** copy requirement tables or Done-when text. Section 4.6: every file reference is a clickable relative Markdown link (from `plans/`, or the user-given path). Bare backticks are not enough.
- Map every `REQ{n}` that has Done-when in Target Solution and Coverage. Cite other `REQ{n}` when a coverage row maps to them. Steps do **not** cite `REQ{n}` in `How`. Tick `Met` in the **requirements file** on those Done-when rows when the check runs (step close and Requirements-fit).

## Stage 1 — Gather Context

- Read relevant code, tests, docs, interfaces, build config.
- Read every user-attached architecture, guide, requirement, or brief **in full**.
- Enumerate all affected files before planning.
- Run Tech Load Protocol per `copilot-instructions.md` Section 3.
- Identify interface candidates, hot paths, and (for web UI) Playwright journeys.
- Build test **content** (Stage 6 Test cases). Apply `tech-test.md`.
- Identify public API surface when this is a library or other public contract.

## Stage 2 — Perspective Sweep

- Sweep per `workflow-council.md`. Same agent. No subagents. No `councils/` file.
- Promote Grill Me → Stage 3, Council candidates → Stage 5, Act → plan constraints.
- Record `workflow-council.md` in Context Anchor `Loaded skills:`.

## Stage 3 — Grill Me

Use this template. Ask all unresolved questions in one round. Do not re-ask answered questions. Follow-ups only for new ambiguities; cite the prior answer.

Include unresolved Sweep/Council follow-ups. Tag `Source`. Council candidates → Stage 5.

Cover every topic before finalizing scope:

- functional outcomes and **concrete** acceptance criteria (not “it builds”)
- test **content** (minimum the plan must cover): important scenarios; edges; special constellations; contradictions; gaps; what is out; time budget (`tech-test.md`). Class and Layer may be named. Test code may appear later in the plan; agree content here.
- public API snippet + usage when a public surface exists
- web UI: load per Section 3 (`tech-web.md`, `tech-playwright.md`, `tech-test.md`, stack UI skill); dark mode (other themes out of scope); mandatory responsive layout and locked breakpoints (`tech-web.md`); journeys; debug story
- performance and allocations (hot paths, budgets) — performance is a feature
- edge cases and error handling
- security boundaries and STRIDE
- trust-boundary limits: proposed defaults; constant vs configurable (where, who, unset default)
- concurrency, TOCTOU, async interleaving
- compatibility, migration, breaking change
- new dependencies: id, what it does, why needed, license, alternatives
- architecture boundaries
- agentic debug path (smallest command, filterable tests, traces)
- API misuse / abuse vectors
- automation when the same edit hits more than ten call sites (approved script or codemod)

```markdown
## Q{n} — {topic}
**Source:** Sweep | Council | Plan | Requirements
**Context:** {1-3 sentences}
**Question:** {single-part question}
**Options:** 1) {option} · 2) {option} · 3) {option} · or free-text
```

Do not proceed while ambiguities remain. New blocking fork → Stage 5 before Write Plan.

## Stage 4 — Reconcile

- Cross-check request, requirements artifact, Grill-Me answers, Sweep, council verdicts, code, docs, ADRs, and Section 4.
- Record a **preference** when both sides cannot hold (`C{n}`).
- Apply Rule Priority (§4.13), then explicit user choice, then scope split.
- Align docs ↔ code ↔ tests when source-of-truth is unclear; ask.
- Gate: Write Plan when no undecided preference blocks scope. Else Stage 5.

## Stage 5 — Decision Loop

Grill Me ↔ Council per `workflow-council.md` until no blocking fork. Lite default; Full if `/council` or security / public-API / irreversible. Verdict → `C{n}`. Cap 3. Do not Write Plan while open.

## Stage 6 — Write Plan

- User path when provided; else `plans/plans_<slug>.md`.
- English (Section 4.6).
- Slug: lowercase, punctuation/whitespace → `-`, collapse `-`, trim, fallback `task`.
- Step Overview at top. Status starts `⬜`.
- Next: Requirements — **link only**. Real Markdown link to the Stage 0 file. Do not paste requirement tables.
- Map every `REQ{n}` that has Done-when in Target Solution (design completeness). Unmapped = incomplete. Extra design with no matching `REQ{n}`: justify or cut. Do not restate full requirement text here. **Do not** cite `REQ{n}` inside step `How`.
- Test cases are first-class **content** (index + `TEST{n}` cards). Cover everything **important** in the plan. Extra tests at implement time are allowed. An executing agent must be able to prove each card without inventing the scenario.
- End with Requirements fit, then its Step `{n}R`.
- Record `Loaded skills:` (include `workflow-council.md` and `tech-test.md` when tests exist), Sweep table, council paths as Section 4.6 links, Decision Loop count, step dependencies. Leave Coverage for Stage 7. Every file named in the plan (Context, Where, Coverage, Target Solution) is a clickable relative link (Section 4.6).
- Every step needs a fully specified `How` and Before/After (Shared Block below). Prefer snippets over a prose-only `How` or Target Solution. Extra illustration snippets are welcome.
- Do not present the plan for approval. Run Stage 7.

### Step Overview

Narrow table. Experience lives in the step block, not as an extra column.

```markdown
| Step | Status | Delivers |
|------|--------|----------|
| Step 1 — {title} | ⬜ | {one sentence} |
| Step 1R — Review Step 1 | ⬜ | Zero Error findings; iterate until clean |
| Step {N} — Requirements fit | ⬜ | Every REQ{n} with Done-when Met |
| Step {N}R — Review Step {N} | ⬜ | Zero Error findings; iterate until clean |
```

### Shared Block (plan steps)

Field order: `What` → `Why` → `How` → `Experience` → `Acceptance` → `Tests` → `[Public API]` → `[Size]` → `[Context]` → `[Where]` → `Verify` → `Debug` → `[If it fails]`.

Always require `What`, `Why`, `How`, `Experience`, `Acceptance`, `Verify`.
Require `Tests` when the step ships behavior (cite `TEST{n}` this step adds or runs; **Content** lives on the card).
Require `Public API` when the step ships or changes a public surface.
Require `Size` when the step may exceed the soft budget (see Step Rules).
Omit `Context` only when neither constraints nor sources exist. Omit `Where` when no file is touched.
Require `If it fails` for schema, state, or external-system risks.
Require `Debug` so the executing agent can probe a red `Verify` (command, Playwright, collision).

❗Specify the concrete implementation. Intent-only `How` is incomplete.
❗Write `How` so another agent can implement without inventing types, items, signatures, algorithms, control flow, or file structure.
❗Write `How` exhaustively: types, items, visibility, signatures, parameters, return values, call-site edits, validation, error paths, control flow, data flow, thread-safety / performance / security constraints, prerequisite state, decision rationale, and important edge cases.
❗Include fenced **Before** and **After** in every step `How` — current code, then Target Solution shape (real signatures and key bodies); anchor with path/symbol. Not stubs, not pseudocode-only, not an intermediate shape later steps will replace. New file: After only. Requirements-fit: skip unless a gap needs a fix.
Prefer those fences over a paragraph that restates the same shape. Extra snippets that illustrate usage, edges, or the test that proves the step are welcome.
❗Cite a concrete source in `Context` when an external reference exists.
Test code in `How` or on a `TEST{n}` card is allowed and welcome. It does not replace Test case **Content**.

`Where`: clickable relative Markdown link (Section 4.6), approximate lines, symbol. Mark `primary` (create/rewrite) or `call-site` or `additive` (append-only).
`Verify`: exact command in optimized/Release per loaded tech skill, plus expected result.
`Experience`: what a person can try after this step, what they should see, how to try it, and what is **not yet** true.

```markdown
## {ID} - {Title}
Status: ⬜ {Initial} · {Depends on}
### What
### Why
### How
Fenced **Before** / **After** (required). Prefer snippets over prose. Extra illustration snippets welcome.
### Experience
After this step a person can: …
They should see: …
How to try: {command | Playwright spec | usage snippet}
Not yet: …
### Acceptance
- [ ] {observable check for this step — may be deeper than R-level}
### Tests
- TEST{n} {short name} — {in this step: add | run}
### Public API
### Size
Prod files: {n} · ~LOC: {n} (tests excluded) · over budget because: {or n/a}
### Context
### Where
### Verify
### Debug
Reproduce: …
First probe if red: …
UI: {Playwright trace / headed / n/a}
Collision: other agents locking build/test?
### If it fails
```

## Plan Structure

1. Step Overview
2. Requirements — Markdown link to the requirements file (no copied table)
3. Test cases (index + content cards)
4. Summary / Context Anchor (include Coverage table)
5. Target Solution (Vision) — include Public API snippet when applicable
6. Phases (optional; >10 steps or multiple areas)
7. Slices
8. Steps (Shared Block; last = Requirements fit + `{n}R`)
9. Edge Cases and Risks
10. Decisions & Trade-offs (`C{n}`; omit when none)
11. Open Questions
12. Closing Summary
13. Task Checklist (Step N, tests that are their own steps, Step `{n}R`; include Requirements fit)

## Requirements

```markdown
## Requirements

Source: [requirements/req_<slug>.md](../requirements/req_<slug>.md)
```

- Use the real relative (or user-given) path. The label should match the file name. Do not paste requirement tables.
- `Met` lives in the requirements file on every `REQ{n}` that has Done-when. Fill it only when Done-when **ran**. Start `⬜` there.
- Reject the plan when a `REQ{n}` with Done-when in the linked file cannot be observed by a user.
- Step `How` does not say “implements REQ12”. At step close the agent re-reads the **linked** file and ticks rows whose Done-when now holds.

## Test cases

First-class **content**. The cards are the **minimum** the implementing agent must prove. Cover everything important here (strategy classes that apply, plus content-level edges, special constellations, contradictions, and gaps). Extra tests at implement time are allowed and are not scope creep unless they contradict agreed Out.

Index (narrow). Details live in the cards, not extra columns.

```markdown
## Test cases

| ID | Case | Step | Status |
|----|------|------|--------|
| TEST1 | {short name} | Step 2 | ⬜ |
```

Every `TEST{n}` needs a card. **Content** is required. **Class** and **Layer** may be named. Test code or a short fenced example of setup / expected observation is welcome; it does not replace Content.

```markdown
### TEST{n} — {short name}

- **Class:** happy · error · boundary · collection · absence · concurrency · trust-boundary
- **Layer:** unit · UI
- **Content:** {domain scenario: what is set up, what happens, what must be observable. Name the edges, special constellations, contradictions, or gaps this case exists to catch when they apply.}
- **Out:** {what this case does not cover}
```

Omit **Class**, **Layer**, or **Out** when they add nothing. Do not omit **Content**. Do not use Class/Layer tags, a test file name, or a method name as the only description.

Accept:

```markdown
### TEST1 — Duplicate export name is rejected

- **Class:** error
- **Layer:** UI
- **Content:** An export named Report already exists. The user submits Report again. A duplicate-name error is visible and the list still has one row. This catches silent overwrite versus the uniqueness rule.
```

Reject as the whole card: `TEST1 — error · UI` · `TEST1 — ExportFormTests.cs` · `TEST1 — add tests`.

Exhaustive = the **classes** in `tech-test.md` plus named content (edges, constellations, contradictions, gaps), not every integer. Keep the suite fast. An applicable class with no `TEST{n}` and no Out = gap.

## Target Solution (Vision)

- Concrete end-state: types, files, APIs, data flow, invariants, algorithms. Show that shape in snippets, not only in prose. Not a slogan.
- Map every `REQ{n}` that has Done-when to a design element here (completeness). Do not paste Done-when from the requirements file. Steps then apply this shape.
- SSOT for final file shape. A primary-file `After` that differs from this section is an incomplete plan.
- Do not use step order here.

### Public API (when a public surface exists)

Short snippet + usage. This is what a human reviews. Changing it after approval → new Grill Me.

````markdown
### Public API

```csharp
public sealed class GapRunOrchestrator
{
    public static Task<int> RunAsync(CliOptions options, CancellationToken ct);
}
```

Usage:

```csharp
int code = await GapRunOrchestrator.RunAsync(opts, ct);
```
````

## Slices

- Group steps by what becomes **tryable** (Experience), not by stub-then-fill.
- Cut a real thin slice. Do not fake end-to-end. `Not yet:` must be honest.
- Ban scaffolding that later steps delete (`NotImplemented`, dummy types, temporary wrappers).
- Build each cross-cutting concern in target shape. Later slices call it.
- Extract a repeated pattern before a second slice copies it.
- Preserve layer boundaries.

## Step Rules

Soft budgets — justify when exceeded; do not split an atomic cutover just to hit a number:

- Cap a step at ≤12 production files and ≤~3000 production LOC. Tests do not count.
- Make edits additive (`Where` = `additive`) when the file shape can stay. Rewrite when the shape must change.
- Do not list the same file as `primary` in many steps unless each increment is a complete Experience and splitting would be worse.
- Leave each step as something a person can see or try (`Experience`).

Still required:

- Freeze file shape in Target Solution. `After` is that shape, not an intermediate.
- Write new types complete (signature, body, errors, tests) before consumers call them.
- Keep rename/move separate from behavior change.
- Ship tests with the production behavior they prove. Dedicated test steps are welcome when the cases are large; do not dump all tests at the end by default.
- Size a step so a human can accept the Before/After.
- Analyze dependencies. Order topologically. State depends-on.
- Reject a `How` that allows more than one implementation, or whose `After` is not Target Solution for primary files.
- Close with Requirements fit.

## Stage 7 — Coverage Check

Run after the plan file is written. **Walk the plan twice.** Patch until both walks are clean. Do not enter Completion with gaps.

1. **Requirements walk** — Re-read the **linked** requirements file **in full**. Do not treat anything in the plan as a copy of those tables. Every `REQ{n}` that has Done-when, and every `TEST{n}`, must land in Target Solution and in a step `How` / `Tests` / `Acceptance`. A `TEST{n}` without **Content** = gap. An applicable `tech-test.md` class with no `TEST{n}` and no Out = gap. Unmapped = gap.
2. **Conversation walk** — Re-read the conversation, Grill Me Q/A, Sweep, council, and attached docs **in full**, then the **entire** written plan. Every relevant user ask, constraint, non-goal, named type/path/command, accepted proposal, rejected option with leftover constraint, and named edge / constellation / contradiction / gap must land somewhere (`REQ{n}`, `TEST{n}` **Content**, Out, `C{n}`, or out of scope). Unmapped = gap. “Implied” without a citation = gap.

Include every `REQ{n}` with Done-when, every `TEST{n}`, and every Grill Me answer as a row. Record dropped items with reason. Write both tables into Context Anchor. Re-run this stage after any patch.

```markdown
**Coverage (requirements → plan):**

| ID | Lands in |
|----|----------|
| REQ12 | Target Solution · Step 2 Acceptance |
| TEST1 | Step 2 Tests · TEST1 Content |

**Coverage (conversation → plan):**

| Item | Source | Lands in |
|------|--------|----------|
| {one-line item} | User · Q{n} · Sweep · Council · Doc | REQ12 · TEST1 · Step {n} · C{n} · Out of scope ({reason}) |
```

## Requirements Fit (last step)

```markdown
## Step {N} - Requirements fit
Status: ⬜ Depends on all prior steps
### What
Walk the built solution as a user. Check every REQ{n} with Done-when and every TEST{n}.
### Why
A green build can still miss the user outcome.
### How
- Re-read the linked requirements file and Test cases. Ignore implementer intent.
- For each REQ{n} with Done-when: run Done-when. Cite evidence (command, UI, API, file, output). Tick Met in the requirements file.
- For each TEST{n}: confirm the Content exists as a test that can fail (extra tests beyond TEST{n} are fine).
- Mark Met / Status ✅ only when the check holds with no caveats.
- Any ❌ or leftover ⬜ = blocker.
- Skip Before/After unless a gap needs a code fix; then stop and file the gap.
### Experience
A person can execute every Done-when without reading the source.
### Acceptance
- [ ] Every REQ{n} with Done-when Met = ✅
- [ ] Every TEST{n} Status = ✅
### Verify
Every REQ{n} with Done-when Met = ✅. Every TEST{n} ✅. Zero leftover ⬜.
### Debug
Re-run the failing Done-when in isolation. Check agent collision before redesign.
```

## Checklist Rules

- Flat ordered list: Step N, any dedicated test steps, Step `{n}R`. Include Requirements fit.
- Tick Status on every transition on **all three** surfaces: Step Overview, Shared Block, Task Checklist.
- Zero Error findings at each review gate before the next step.

## Completion

- Stage 7 clean: both coverage walks green (requirements **and** conversation).
- Return plan path. Cite requirements path and council paths if any.
- Chat: path only. Do not recap Coverage.
- Wait for explicit user approval per Section 6 before implementation.

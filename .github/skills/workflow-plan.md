# Plan Workflow

Load on `/plan`. Apply `copilot-instructions.md` Sections 2–4.

## Stage Order

1. Gather Context
2. Perspective Sweep
3. Grill Me
4. Reconcile Requirements
5. Decision Loop
6. Write Plan
7. Coverage Check

## Stage 1 — Gather Context

- Read relevant code, tests, docs, interfaces, build config.
- Enumerate all affected files before planning.
- Run Tech Load Protocol per `copilot-instructions.md` Section 3.
- Read definition and docs for every involved type or item.
- Identify interface candidates and expected hot paths.
- Build test-coverage matrix: behavior × tests (errors, boundaries, concurrency, security).
- Apply test-planning rules from loaded tech skills.
- Summarize findings tersely per Section 2.

## Stage 2 — Perspective Sweep

- Sweep per `workflow-council.md`. No subagents. No `councils/` file.
- Promote Grill Me → Stage 3, Council candidates → Stage 5, Act → plan constraints.
- Record `workflow-council.md` in Context Anchor `Loaded skills:`.

## Stage 3 — Grill Me

Use this template for every question. Ask all unresolved questions in one round. Do not re-ask answered questions. Run follow-up rounds only for unresolved ambiguities. Reference the prior answer that created each follow-up ambiguity.

Include unresolved Sweep/Council follow-ups in the same round. Tag `Source`. Do not drop a mandatory topic because a view was silent. Council candidates → Stage 5; Grill Me only user facts the council still needs.

Cover every topic below before finalizing plan scope:
- functional requirements and acceptance criteria (user-observable outcomes; become R{n}),
- edge cases and error handling,
- performance, scalability, and memory limits,
- security boundaries and STRIDE,
- concurrency, TOCTOU, and async interleaving,
- compatibility, migration, and breaking-change strategy,
- new dependencies per loaded tech skill New Dependency Protocol,
- testing strategy, non-trivial coverage obligations, and definition of done,
- architecture boundaries and shared cross-cutting patterns,
- API misuse and abuse vectors (how can the solution be used wrongly or exploited),
- automation when the same change pattern affects more than ten call sites (script or codemod in plan).

```markdown
## Q{n} — {topic}
**Source:** Sweep | Council | Plan
**Context:** {one sentence}
**Question:** {single-part question}
**Options:** 1) {option} · 2) {option} · 3) {option} · or free-text
```

Do not proceed while ambiguities remain. New blocking fork from an answer → Stage 5 before Write Plan.

## Stage 4 — Reconcile Requirements

- Cross-check request, Grill-Me answers, Sweep, council verdicts, code, docs, ADRs, and Section 4 for mismatches and competing goals.
- Full resolution is not always possible; record a **preference** when both sides cannot hold.
- Prefer Rule Priority (§4.13), then explicit user choice, then scope split or phasing.
- Align cross-file drift in scope (docs ↔ code, plan ↔ tests, comments/docs ↔ behavior); ask when source-of-truth is unclear.
- Document preferences in **Decisions & Trade-offs** (`C{n}`: conflict, choice, rationale), including council `C{n}`; omit section when none.
- Break the endeavor into R{n}. Write user-observable outcomes, not implementation.
- Gate: Write Plan when no undecided preference blocks scope. Else Stage 5.

## Stage 5 — Decision Loop

Execute Grill Me ↔ Council in `workflow-council.md` until no blocking fork. Lite default; Full if `/council` or security/public-API/irreversible. Verdict → `C{n}`. Follow-ups → Stage 3, then Stage 4. Cap 3. Do not Write Plan while open.

## Stage 6 — Write Plan

- Use user path when provided; else write `plans/plans_<slug>.md`.
- Build slug: lowercase, punctuation/whitespace → `-`, collapse `-`, trim, fallback `task`.
- Put Step Overview table at top per template below. Include Status. Start every row `⬜`.
- Put Requirements (User View) next. Carry R{n} from Stage 4.
- Map every R{n} in Target Solution. Unmapped R{n} = incomplete plan. Extra design with no R{n}: justify or cut.
- End the step list with Requirements fit, then its Step NR. Depend on all prior steps.
- Record `Loaded skills:` (include `workflow-council.md`), Sweep table, council paths, Decision Loop count, and step dependencies in Context Anchor. Leave Coverage for Stage 7.
- Size steps per Step Rules (write-once, not stub-then-fill). Every step still needs a fully specified `How`.
- Put each step’s tests in that step. Name non-trivial coverage (behaviors, error paths, boundaries, concurrency, security), not only test file names.
- No plan-level context required beyond the Shared Block.
- Do not present the plan for approval. Run Stage 7.

### Step Overview Table

Place at top of plan file, before Requirements. In chat: end of plan message, after Task Checklist.
Keep Status in lockstep with Shared Block and Task Checklist.

```markdown
| Step | Status | Delivers |
|------|--------|----------|
| Step 1 — {title} | ⬜ | {one sentence} |
| Step 1R — Review Step 1 | ⬜ | Zero Error findings; iterate until clean |
| Step {N} — Requirements fit | ⬜ | Every R{n} met from the user view |
| Step {N}R — Review Step {N} | ⬜ | Zero Error findings; iterate until clean |
```

### Shared Block (plan steps)

Field order: `What` → `Why` → `How` → `[Context]` → `[Where]` → `Verify` → `[If it fails]`.
Always require `What`, `Why`, `How`, `Verify`.
Omit `Context` only when neither constraints nor sources exist. Omit `Where` when no file is touched.
Require `If it fails` for schema, state, or external-system risks.
❗Specify the concrete implementation. Intent-only, outline-only, or slogan-only `How` is incomplete.
❗Write `How` so another agent can implement without inventing types, items, signatures, algorithms, control flow, or file structure.
❗Write `How` exhaustively: types, items, visibility, signatures, parameters, return values, call-site edits, validation, error paths, control flow, data flow, thread-safety/performance/security constraints, prerequisite state, decision rationale, and important edge cases.
❗Include fenced **Before** and **After** code in every step `How` — current code, then Target Solution shape for those symbols (real signatures and key bodies); anchor with path/symbol. Do not substitute stubs, pseudocode-only, comments-as-code, or an intermediate shape later steps will replace. Exception: Requirements-fit may skip Before/After unless a gap needs a code fix.
❗Name non-trivial test coverage in every step `How`: behaviors, error paths, boundaries, concurrency, security — not only test file names.
❗Cite a concrete source in every step `Context` when an external reference exists: URL, official doc title + section, API reference, RFC. Else cite skill/ADR/path/symbol.
Put `Where` as path, approximate line numbers, and searchable symbol anchor. Mark each path `primary` (create/rewrite) or `call-site`.
Put `Verify` as exact command in optimized/Release configuration per loaded tech skill, and expected result.
Use bullets in `How`. Prefer implementation detail over brevity. Do not compress `How`.

```markdown
## {ID} - {Title}
Status: ⬜ {Initial} · {Depends on / Severity}
### What
### Why
### How
### Context
### Where
### Verify
### If it fails
```

## Plan Structure

1. Step Overview (top; Status column)
2. Requirements (User View)
3. Summary / Context Anchor (include Coverage table)
4. Target Solution (Vision)
5. Phases (optional; use when >10 steps or multiple areas)
6. Slices
7. Steps (Shared Block as above; last = Requirements fit + NR)
8. Edge Cases and Risks
9. Decisions & Trade-offs (`C{n}` from Stages 4–5; omit when none)
10. Open Questions
11. Closing Summary
12. Task Checklist (alternating Step N and Step NR; include Requirements fit)

## Requirements (User View)

Place after Step Overview, before Summary. Write what the user will have, not how you will build it.

```markdown
## Requirements (User View)

| ID | Requirement | Done when | Met |
|----|-------------|-----------|-----|
| R1 | {observable user outcome} | {check a later agent can execute} | ⬜ |
```

- Write imperative, testable outcomes. One outcome per R{n}.
- Ban slogans, implementation tasks, and internal refactors as R{n} unless the user asked for them.
- Fill Met only in the Requirements-fit step. Start every row `⬜`.
- Reject the plan when an R{n} cannot be observed by a user.

## Target Solution (Vision)

- Describe the target end-state in concrete terms: types, files, APIs, data flow, invariants, and key algorithms. Not a slogan vision.
- Map every R{n} to a design element. Cite R{n} IDs. Unmapped R{n} = incomplete. Design with no R{n}: justify or cut.
- Specify the solution so steps apply it. Do not invent or evolve shape in later steps. Do not use step order here.
- Treat this section as SSOT for final file shape. A primary-file `After` that differs from this section is an incomplete plan.
- Plan steps may cite Target Solution (Vision) and R{n} in `Context` so the agent can re-read end-state on demand.

## Slices

- Group steps by which R{n} becomes observable. Slices are labels, not a stub-then-fill sequence.
- Do not wire unfinished pieces for an early end-to-end.
- Build each cross-cutting concern once, in target shape. Later slices only call it.
- Extract a repeated pattern before a second slice copies it.
- Preserve layer boundaries.

## Step Rules

- Freeze file shape in Target Solution. Steps apply that shape; they do not evolve toward it.
- Give each file one create-or-rewrite step. Leave it in Target Solution shape.
- List a path as `primary` in one step only. Later steps may list it as `call-site` only.
- Ban scaffolding: no dummy types, `NotImplemented`, temporary wrappers, or files later steps delete.
- Write new types complete (signature, body, errors, tests) before any consumer calls them.
- Put wiring in one cutover step. Do not grow the same orchestrator or entry file across many steps.
- Split a large rewrite: extract new files at target shape, then one cutover in the old file.
- Keep rename/move separate from behavior change.
- Ship tests with the production file they prove. Do not park coverage in a final dump step.
- Size a step so review can hold the delta and the kernel will not be rewritten later.
- Analyze dependencies. Order steps topologically. State each step’s depends-on.
- Reject a `How` that allows more than one implementation, or whose `After` is not Target Solution for primary files.
- Close with Requirements fit. Check the built solution against every R{n} from the user view. Gaps = blockers.

## Stage 7 — Coverage Check

Run after the plan file is written. Re-read conversation and artifact. Patch until clean. Do not enter Completion with gaps.

- Re-read the full conversation, Grill Me Q/A, Sweep promotions, council verdicts/`C{n}`, Stage 4 `R{n}`, and the written plan.
- Enumerate every relevant discussed item: user asks, Grill Me answers, constraints, non-goals, named types/paths/commands, accepted proposals, rejected options, test obligations, STRIDE/perf/concurrency/compat/migration, Sweep Act, council `C{n}`.
- Include every `R{n}` and every Grill Me answer as a row. Missing row = gap.
- Skip chatter, process talk, and ideas the user walked back with no residual constraint.
- Map each item to `R{n}`, Target Solution, a step `How`, `C{n}`, Edge Cases, Open Questions, or Out of scope plus reason. Unmapped = gap. “Implied” without a citation = gap.
- Record dropped items with reason. Do not omit them.
- Patch the plan for every gap. Re-run this stage. Completion is blocked while any item is unmapped.
- Write the table into Context Anchor. Every relevant item is a row.

```markdown
**Coverage (conversation → plan):**

| Item | Source | Lands in |
|------|--------|----------|
| {one-line item} | User · Q{n} · Sweep · Council | R{n} · Step {n} · C{n} · Out of scope ({reason}) |
```

## Requirements Fit (last step)

Mandatory closing step after all build/review gates except its own Step NR.

```markdown
## Step {N} - Requirements fit
Status: ⬜ Depends on all prior steps
### What
Walk the built solution as a user. Check every R{n}.
### Why
A green build can still miss the user outcome.
### How
- Re-read Requirements (User View). Ignore implementer intent.
- For each R{n}: run the Done-when check. Cite evidence the user can observe (command, UI, API, file, output).
- Mark Met ✅ only when Done-when holds with no caveats. Partial, hidden, or "works if you know the code" = ❌.
- Fail R{n} when tests do not cover the Done-when check.
- Any ❌ or leftover ⬜ = blocker. Do not mark this step ✅.
- Skip Before/After code unless a gap needs a code fix; then stop and file the gap, do not close.
### Verify
Every R{n} Met = ✅. Zero ❌. Zero leftover ⬜.
```

## Checklist Rules

- Use flat ordered list with Step N and Step NR gates. Include Requirements fit.
- Tick Status on every transition in **all three** surfaces: Step Overview, Shared Block, Task Checklist. Same emoji. Same moment.
- Never leave Step Overview stale.
- Require zero Error findings at each review gate before next step.

## Completion

- Require Stage 7 clean: Coverage table complete, zero unmapped items.
- Return plan artifact path. Cite council paths if Decision Loop ran.
- Summarize scope, constraints, risks tersely per Section 2. Do not recap the Coverage table in chat.
- Wait for explicit user approval per Section 6 before implementation.

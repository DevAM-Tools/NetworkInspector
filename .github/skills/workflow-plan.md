# Plan Workflow

Load on `/plan`. Apply `copilot-instructions.md` Sections 2–4.

## Stage Order

1. Gather Context
2. Perspective Sweep
3. Grill Me
4. Reconcile Requirements
5. Decision Loop
6. Write Plan

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

Include unresolved Sweep/Council follow-ups in the same round. Tag `Source`. Do not drop a mandatory topic because a lens was silent. Council candidates → Stage 5; Grill Me only user facts the council still needs.

Cover every topic below before finalizing plan scope:
- functional requirements and acceptance criteria,
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
- Gate: Write Plan when no undecided preference blocks scope. Else Stage 5.

## Stage 5 — Decision Loop

Execute Grill Me ↔ Council in `workflow-council.md` until no blocking fork. Lite default; Full if `/council` or security/public-API/irreversible. Verdict → `C{n}`. Follow-ups → Stage 3, then Stage 4. Cap 3. Do not Write Plan while open.

## Stage 6 — Write Plan

- Use user path when provided; else write `plans/plans_<slug>.md`.
- Build slug: lowercase, punctuation/whitespace → `-`, collapse `-`, trim, fallback `task`.
- Put Step Overview table at top per template below.
- Record `Loaded skills:` (include `workflow-council.md`), Sweep table, council paths, Decision Loop count, and step dependencies in Context Anchor.
- State per-step test obligations: covered vs new/updated tests; name non-trivial coverage (behaviors, error paths, boundaries, concurrency, security), not only test file names.
- Prefer many small steps over few large ones. Small steps still require a fully specified `How`.
- No plan-level context required beyond the Shared Block.

### Step Overview Table

Place at top of plan file, before Summary / Context Anchor. In chat: end of plan message, after Task Checklist.

```markdown
| Step | Delivers |
|------|----------|
| Step 1 — {title} | {one sentence} |
| Step 1R — Review Step 1 | Zero Error findings; iterate until clean |
```

### Shared Block (plan steps)

Field order: `What` → `Why` → `How` → `[Context]` → `[Where]` → `Verify` → `[If it fails]`.
Always require `What`, `Why`, `How`, `Verify`.
Omit `Context` only when neither constraints nor sources exist. Omit `Where` when no file is touched.
Require `If it fails` for schema, state, or external-system risks.
❗Specify the concrete implementation. Intent-only, outline-only, or slogan-only `How` is incomplete.
❗Write `How` so another agent can implement without inventing types, items, signatures, algorithms, control flow, or file structure.
❗Write `How` exhaustively: types, items, visibility, signatures, parameters, return values, call-site edits, validation, error paths, control flow, data flow, thread-safety/performance/security constraints, prerequisite state, decision rationale, and important edge cases.
❗Include fenced **Before** and **After** code in every step `How` — current code, then target code with real signatures and key bodies; anchor with path/symbol. Do not substitute stubs, pseudocode-only, or comments-as-code for the solution.
❗Name non-trivial test coverage in every step `How`: behaviors, error paths, boundaries, concurrency, security — not only test file names.
❗Cite a concrete source in every step `Context` when an external reference exists: URL, official doc title + section, API reference, RFC. Else cite skill/ADR/path/symbol.
Put `Where` as path, approximate line numbers, and searchable symbol anchor.
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

1. Step Overview (top)
2. Summary / Context Anchor
3. Target Solution (Vision)
4. Phases (optional; use when >10 steps or multiple areas)
5. Vertical Slices
6. Steps (Shared Block as above)
7. Edge Cases and Risks
8. Decisions & Trade-offs (`C{n}` from Stages 4–5; omit when none)
9. Open Questions
10. Closing Summary
11. Task Checklist (alternating Step N and Step NR)

## Target Solution (Vision)

- Describe the target end-state in concrete terms: types, APIs, data flow, invariants, and key algorithms. Not a slogan vision.
- Specify the solution in enough detail that steps refine it rather than invent it. Do not use step order here.
- Plan steps may put references to Target Solution (Vision) in `Context` so the agent can re-read end-state on demand.

## Vertical Slices

- Choose thinnest viable vertical slices by time-to-first-result.
- Make first slice architectural foundation when multi-slice.
- Preserve layer boundaries across slices.
- Design cross-cutting concerns once as shared infrastructure.
- Extract repeated patterns before duplicating in a second slice.

## Step Rules

- Analyze dependencies between all sub-steps before ordering.
- Order steps in strict topological order by documented dependencies.
- State explicit dependencies per step in Shared Block `Context` or status line.
- Include status, dependencies, output per step.
- Define test coverage obligations per step; name non-trivial behaviors, error paths, and boundaries tests must cover.
- Document interface vs concrete decisions per step.
- Add recovery path for high-risk steps.
- Keep steps small and self-contained.
- Reject a step whose `How` could match several different implementations; expand until one is uniquely specified.

## Checklist Rules

- Use flat ordered list with Step N and Step NR gates.
- Update status on every transition.
- Require zero Error findings at each review gate before next step.

## Completion

- Return plan artifact path. Cite council paths if Decision Loop ran.
- Summarize scope, constraints, risks tersely per Section 2.
- Wait for explicit user approval per Section 6 before implementation.

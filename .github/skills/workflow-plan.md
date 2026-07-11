# Plan Workflow

Load on `/plan`. Apply `copilot-instructions.md` Sections 2–4.

## Stage Order

1. Gather Context
2. Grill Me
3. Reconcile Requirements
4. Write Plan

## Stage 1 — Gather Context

- Read relevant code, tests, docs, interfaces, build config.
- Enumerate all affected files before planning.
- Run Tech Load Protocol per `copilot-instructions.md` Section 3.
- Read definition and docs for every involved member or type.
- Identify interface candidates and expected hot paths.
- Build test-coverage matrix: behavior × tests (errors, boundaries, concurrency, security).
- When `.razor` is in scope: plan exhaustive bUnit coverage per `tech-blazor.md`.
- Summarize findings tersely per Section 2.

## Stage 2 — Grill Me

Use this template for every question. Ask all unresolved questions in one round. Do not re-ask answered questions. Run follow-up rounds only for unresolved ambiguities. Reference the prior answer that created each follow-up ambiguity.

Cover every topic below before finalizing plan scope:
- functional requirements and acceptance criteria,
- edge cases and error handling,
- performance, scalability, and memory limits,
- security boundaries and STRIDE,
- concurrency, TOCTOU, and async interleaving,
- compatibility, migration, and breaking-change strategy,
- new dependencies per `tech-solution.md` New Dependency Protocol,
- testing strategy and definition of done,
- architecture boundaries and shared cross-cutting patterns,
- API misuse and abuse vectors (how can the solution be used wrongly or exploited),
- automation when the same change pattern affects more than ten call sites (script or codemod in plan).

```markdown
## Q{n} — {topic}
**Context:** {one sentence}
**Question:** {single-part question}
**Options:** 1) {option} · 2) {option} · 3) {option} · or free-text
```

Do not proceed while ambiguities remain.

## Stage 3 — Reconcile Requirements

- Cross-check request, Grill-Me answers, code, docs, ADRs, and Section 4 for mismatches and competing goals.
- Full resolution is not always possible; record a **preference** when both sides cannot hold.
- Prefer Rule Priority (§4.13), then explicit user choice, then scope split or phasing.
- Align cross-file drift in scope (docs ↔ code, plan ↔ tests, comments/XML ↔ behavior); ask when source-of-truth is unclear.
- Document preferences in plan section **Decisions & Trade-offs** (`C{n}`: conflict, choice, rationale); omit section when none.
- Gate: Write Plan when no undecided preference blocks scope.

## Stage 4 — Write Plan

- Use user path when provided; else write `plans/plans_<slug>.md`.
- Build slug: lowercase, punctuation/whitespace → `-`, collapse `-`, trim, fallback `task`.
- Put Step Overview table at top per template below.
- Record `Loaded skills:` and step dependency summary in Context Anchor.
- State per-step test obligations: covered vs new/updated tests.
- Prefer many small steps over few large ones; No plan-level context required beyond the Shared Block.

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
Omit `Context` when no non-obvious constraint exists. Omit `Where` when no file is touched.
Require `If it fails` for schema, state, or external-system risks.
❗Write `How` exhaustively: concrete APIs, signatures, validation, error paths, thread-safety/performance/security constraints, prerequisite state, and before/after snippets or mini examples where behavior is non-obvious.
Put `Where` as path, approximate line numbers, and searchable symbol anchor.
Put `Verify` as exact command (`-c Release` when applicable) and expected result.
Use bullets in `How`; avoid redundant prose — detail over brevity in plan artifacts.

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
8. Decisions & Trade-offs (preferences from Stage 3; omit when none)
9. Open Questions
10. Closing Summary
11. Task Checklist (alternating Step N and Step NR)

## Target Solution (Vision)

- Describe the target solution in a structured, comprehensive, detailed, illustrated way not implementation steps.
- Plan steps may put references to Target Solution (Vision in `Context` so the agent can re-read end-state on demand.

## Vertical Slices

- Describe horizontal alternative.
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
- Define test coverage obligations per step.
- Document interface vs concrete decisions per step.
- Add recovery path for high-risk steps.
- Keep steps small and self-contained.

## Checklist Rules

- Use flat ordered list with Step N and Step NR gates.
- Update status on every transition.
- Require zero Error findings at each review gate before next step.

## Completion

- Return plan artifact path.
- Summarize scope, constraints, risks tersely per Section 2.
- Wait for explicit user approval per Section 6 before implementation.

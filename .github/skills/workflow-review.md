# Review Workflow

Load on `/review`. Apply `copilot-instructions.md` Sections 2–4.

## Stage Order

1. Define Scope
2. Load Rules
3. Gather Context
4. Review
5. Output

## Stage 1 — Define Scope

- Treat scope argument as confirmed when provided.
- Otherwise ask in-scope items, exclusions, and focus.
- Do not review before scope is confirmed.

## Stage 2 — Load Rules

- Run Tech Load Protocol per `copilot-instructions.md` Section 3.

## Stage 3 — Gather Context

- Enumerate in-scope files.
- Read in-scope files, related tests, direct dependencies.
- Read definition and docs for involved members and types.
- Build coverage checklist: file × criterion.
- Build test-coverage matrix; list gaps explicitly.
- Load `tech-tunit.md` when exit-point coverage verification is required.
- Record exit-gap count from ExitPointGaps per `tech-tunit.md` when production code is in scope.
- C# in scope: run `dotnet build` on the solution.

## Stage 4 — Review

- **Consistency first:** cross-check plan, request, code, tests, docs, and XML/comments for mismatches (e.g. documented behavior ≠ implementation, `Verify` ≠ reality, API contract ≠ call sites).
- Fix cross-file drift in finding `How` when source-of-truth is clear; cite `C{n}` when plan already chose. Undocumented mismatch → Error.
- Competing goals without recorded preference → Error; require user decision before release verdict.
- Review exhaustively and adversarially.
- Evaluate all in-scope files against `copilot-instructions.md` Section 4 criteria plus loaded tech-skill rules.
- Compare requested target vs observed result.
- Evaluate test coverage for behaviors, errors, boundaries.
- Require 100% exit-point coverage.
- Flag missing misuse/abuse analysis in plan as Error when new public APIs are introduced.
- Evaluate interface vs hot-path concrete decisions.
- For hot paths, evaluate `[ThreadStatic]` vs pooling per Section 4.4.
- Never stop after first N findings.
- Flag missing tech-skill load as Error when triggered files are in scope.
- Flag new dependency added without user approval as Error per `tech-solution.md`.

## Stage 5 — Output

Use the templates below for all findings output.

### Shared Block (every finding)

Field order: `What` → `Why` → `How` → `[Context]` → `[Where]` → `Verify` → `[If it fails]`.
Always require `What`, `Why`, `How`, `Verify`.
Omit `Context` when no non-obvious constraint exists. Omit `Where` when no file is touched.
Require `If it fails` for schema, state, or external-system risks.
Make `How` a standalone fix recipe: APIs, validation, error paths, thread-safety/performance/security constraints, prerequisite state.
Put `Where` as path, approximate line numbers, and searchable symbol anchor.
Put `Verify` as exact command (`-c Release` when applicable) and expected result.
Use bullets in `How` where possible; avoid redundant prose.

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

### Findings Overview Table

List every finding in one row: ID, bucket prefix (`E`/`C`/`R`/`P`), title, one-sentence description with location. Place before Scope in file mode, before bucket sections in chat-only mode.

```markdown
| ID | Bucket | Title | Summary |
|----|--------|-------|---------|
| E1 | E | {title} | {one sentence with location} |
```

### Output Modes

**File mode** (default under `/complex-task`): write to `reviews/review_<slug>_<iteration>.md`. Put Findings Overview at top. Every finding as full Shared Block under its bucket section. In chat: output compact summary only — bucket counts, release verdict, artifact path, prioritized action list. Do not repost Shared Block contents in chat.

**Chat-only mode** (no review file): output Findings Overview table and every finding as full Shared Block in chat. Overview before bucket sections; Priority Action List after all findings.

### Review File Sections

1. Findings Overview (top)
2. Summary
3. Scope
4. Errors
5. Cosmetic Issues
6. Refactoring Opportunities
7. Performance and Allocations
8. Closing Assessment
9. Priority Action List

## Finding Buckets

- Error
- Cosmetic
- Refactoring Opportunity
- Performance

Assign exactly one bucket per finding.

## Category Rules

Bucket-specific `How` / `Why` / `Context` requirements:

- **Error:** include Severity (High/Medium/Low) in status line; include before/after in `How`; name OWASP category for security; specify missing boundary and guard for validation gaps.
- **Cosmetic:** reference exact style rule in `How`; include before/after when non-obvious.
- **Refactoring:** state unchanged behavior in `Why`; name extract/move/split in `How`.
- **Performance:** reference Section 4.4 in `How`; include frequency, allocation pressure, cache behavior, and exception-throwing paths in `Context`.

## Closing Assessment

- Include architecture quality, dominant error themes, thread-safety posture, allocation profile.
- State explicit release verdict: `Ready for public release` or prioritized blocker IDs.
- List top 3 priority actions by finding ID.

## Completion

- Report counts by bucket and prioritized action list.

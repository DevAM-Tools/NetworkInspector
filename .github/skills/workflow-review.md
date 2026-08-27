# Review Workflow

Load on `/review`. Apply `copilot-instructions.md` Sections 2–4.

**Purpose:** Critically examine the existing solution. Find defects in the parts and in how the parts interact. Skeptic stance: assume it cannot work; hunt the hair in the soup.

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
- Load Sweep Mode from `workflow-council.md`.

## Stage 3 — Gather Context

- Enumerate in-scope files.
- If `reviews/brief_<slug>*.md` exists for this scope, read it first. Follow Seq. Expect is a claim, not proof.
- Read in-scope files, related tests, direct dependencies, and call sites.
- Map composition: callers, callees, shared state, sequencing, and error paths that only appear when pieces combine.
- Read definition and docs for involved types and items.
- Build coverage checklist: file × criterion.
- Build test-coverage matrix; list gaps explicitly.
- Load matching skills per Tech Load Protocol, including the test/coverage skill when tests or production APIs are in scope.
- Apply the coverage gate and build command from loaded tech skills.

## Stage 4 — Review

- **Consistency first:** cross-check plan, request, code, tests, docs, and comments for mismatches (e.g. documented behavior ≠ implementation, `Verify` ≠ reality, API contract ≠ call sites).
- Fix cross-file drift in finding `How` when source-of-truth is clear; cite `C{n}` when plan already chose. Undocumented mismatch → Error.
- Review exhaustively and adversarially. Do not trust tests, docs, or a green path.
- **Skeptic pass (required):** Assume the in-scope solution cannot work. Do not stop after the first flaw. Cite `Skeptic` in finding `Context`.
  - **Parts:** every in-scope unit — wrong default, off-by-one, silent swallow, copy-paste, tests that cannot fail, missing guard. Dumbest caller/operator error AND worst abuse + STRIDE at trust boundaries.
  - **Whole:** composition — call-graph, shared state, ordering, contracts vs callers, `Verify` that does not prove the claim, R{n} that holds per file but fails end-to-end, defects that exist only in the interplay of otherwise-correct pieces.
  - Hair in the soup counts. Isolated nits that violate §4 or become fatal in combination = Error. `none` only after both hunts ran and found nothing (say so in Sweep).
- Evaluate all in-scope files against `copilot-instructions.md` Section 4 criteria plus loaded tech-skill rules.
- Compare requested target vs observed result.
- When a plan is in scope: check the built result against every R{n} from the user view. Unmet R{n} = Error.
- Evaluate test coverage for behaviors, errors, boundaries.
- Require 100% exit-path coverage per Section 4.5 and the loaded tech skill’s gate (if any).
- Flag missing misuse/abuse analysis in plan as Error when new public APIs are introduced.
- Evaluate interface vs hot-path concrete decisions.
- For hot paths, evaluate allocation strategy per Section 4.4 and the loaded tech skill.
- Never stop after first N findings.
- Flag missing tech-skill load as Error when triggered files are in scope.
- Flag new dependency added without user approval as Error per the loaded tech skill New Dependency Protocol.
- Sweep per `workflow-council.md`. Skip none. View defect → finding; cite view in `Context`. Skeptic Sweep row must cover parts and whole and point at finding IDs (or documented `none`).
- Competing goals without recorded preference → Error; user decision before release verdict. High-stakes fork: recommend `/council` in `How`. Do not auto-run Full unless asked.

## Stage 5 — Output

Use the templates below for all findings output.

### Shared Block (every finding)

Field order: `What` → `Why` → `How` → `[Context]` → `[Where]` → `Verify` → `[If it fails]`.
Always require `What`, `Why`, `How`, `Verify`.
Omit `Context` only when neither constraints nor sources exist. Omit `Where` when no file is touched.
Require `If it fails` for schema, state, or external-system risks.
❗Specify the concrete fix. Intent-only, outline-only, or slogan-only `How` is incomplete.
❗Write `How` so another agent can implement the fix without inventing types, items, signatures, algorithms, control flow, or file structure.
Make `How` a standalone, exhaustive fix recipe: types, items, visibility, signatures, parameters, return values, call-site edits, validation, error paths, control flow, data flow, thread-safety/performance/security constraints, prerequisite state, decision rationale, and important edge cases.
❗Include fenced **Problem** and **Fix** code in every finding `How` — current code, then target code with real signatures and key bodies; anchor with path/symbol. Do not substitute stubs, pseudocode-only, or comments-as-code for the solution.
❗Cite a concrete source in every finding `Context` when an external reference exists: URL, official doc title + section, API reference, RFC. Else cite skill/ADR/path/symbol.
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

### Findings Overview Table

List every finding in one row: ID, bucket prefix (`E`/`C`/`R`/`P`), title, one-sentence description with location. Place before Scope in file mode, before bucket sections in chat-only mode.

```markdown
| ID | Bucket | Title | Summary |
|----|--------|-------|---------|
| E1 | E | {title} | {one sentence with location} |
```

### Output Modes

**File mode** (default under `/complex-task`): write to `reviews/review_<slug>_<iteration>.md`. Put Findings Overview at top. Every finding as full Shared Block under its bucket section. In chat: output compact summary only — bucket counts, release verdict, artifact path, prioritized action list. Do not repost Shared Block contents in chat.

**Chat-only mode** (no review file): output Findings Overview table, Perspective Sweep table, and every finding as full Shared Block in chat. Overview, then Sweep, then bucket sections; Priority Action List after all findings.

### Review File Sections

1. Findings Overview (top)
2. Summary
3. Scope
4. Perspective Sweep (table; every view filled; finding IDs or `none`)
5. Errors
6. Cosmetic Issues
7. Refactoring Opportunities
8. Performance and Allocations
9. Closing Assessment
10. Priority Action List

## Finding Buckets

- Error
- Cosmetic
- Refactoring Opportunity
- Performance

Assign exactly one bucket per finding.

## Category Rules

Bucket-specific `How` / `Why` / `Context` requirements:

- **Error:** include Severity (High/Medium/Low) in status line; show Problem/Fix code in `How`; name OWASP category for security; specify missing boundary and guard for validation gaps.
- **Cosmetic:** reference exact style rule in `How`; show Problem/Fix code in `How`.
- **Refactoring:** state unchanged behavior in `Why`; name extract/move/split in `How`; show Problem/Fix code in `How`.
- **Performance:** reference Section 4.4 in `How`; show Problem/Fix code in `How`; include frequency, allocation pressure, cache behavior, and throw/panic / error-return paths in `Context`.

## Closing Assessment

- Include architecture quality, composition (parts vs whole), dominant error themes, thread-safety posture, allocation profile.
- Confirm Sweep covered all five views; unused view = `none`.
- Confirm Skeptic pass ran on parts and on composition; cite finding IDs or `none`.
- State explicit release verdict: `Ready for public release` or prioritized blocker IDs.
- List top 3 priority actions by finding ID.

## Completion

- Report counts by bucket and prioritized action list.

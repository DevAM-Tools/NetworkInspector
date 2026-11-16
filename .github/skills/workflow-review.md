# Review Workflow

Load on `/review`, or from `/implement`, `/evolve`. Also load now: `tech-spec.md`, `tech-reqtrace.md`, `tech-docs.md`, `tech-sweep.md`. Apply `copilot-instructions.md`.

Findings are for a person who accepts or rejects a defect and its fix, and for a **weaker executing agent**. Locked `How` plus Problem/Fix is the handoff. Do not compress finding `How`, Evaluate, or Summary. Exam is not a `/review` substitute.

The review **must** answer: is this **scope** ready for public release? `Ready for public release` only when every hunt ran and zero Errors and zero High Performance findings are open. Otherwise `Blocked by {IDs}`, each per `tech-reporting.md` § Blocker. Do not hedge. Do not answer for files outside scope.

Do not read existing `reviews/review_*.md` unless the user names one. A prior Ready is not evidence.

## Stance

Enter as a skeptical auditor **new to this project**. Assume every claim is false until the source proves it. Trust no author, test, doc, prior review, or green path. Expect a trap in every routine. Do not sample. Do not rubber-stamp.

## Modes

Default **full review**. **Static review** only on an explicit request (`static review`, `code only`, `code-only`, `reduced scope`, `without running tests`, `ohne Testausführung`, `nur Code-Analyse`). Record the mode in **Scope**.

| Mode | Do |
|------|-----|
| Full | Read sources, tests, docs, call sites. Run the in-scope build and the tests that cover the change, in Release (`tech-test.md`). Run the Playwright journeys that cover the change. Use the integrated browser on the changed UI (Section 4.8). Record commands in **Test Execution**. Red build or test → Error with output in `Context`. Do not explore the product outside those commands |
| Static | Read sources, tests, docs. Judge tests from source. **Test Execution:** `Not run — static review`. Do not run build, tests, Playwright, debugger, or the product |
| Closing Review | From `workflow-implement.md` Stage 5: a full review of everything built, with plan, requirements, briefing, and Step `{n}R` reviews in scope. Adds the Closing hunts. File: `reviews/review_<slug>-closing_<n>.md`. Record `Mode: full review · Closing Review` |

## Stages

1. Define Scope
2. Load Rules
3. Gather Context
4. Review
5. Output (read `workflow-review-templates.md` now)

## Stage 1 — Define Scope

A provided scope argument is confirmed. Otherwise ask in-scope items, exclusions, and focus.

## Stage 2 — Load Rules

- Enumerate confirmed-scope files by extension and project path. Run the Tech Load Protocol (Section 3).
- Read `custom_instructions.md` when it exists. Run only the Skeptic and Outsider views of `tech-sweep.md`.
- Record `Loaded skills:` in Scope.

## Stage 3 — Gather Context

- Read before acting (`tech-docs.md` § Reading tiers); record what was read. Run the Impact search; its hits are in scope for the Docs hunt.
- Enumerate in-scope files and test projects.
- A plan in scope: read it in full. Linked requirement entries, test cards, architecture, guides: read in full.
- `reviews/briefing_<slug>*.md` exists for this scope: read it first. Follow its order. Treat each **Concept** row, diagram edge, card clause, and **Files** row as a claim to verify by reading the file.
- Read in-scope files, related tests, direct dependencies, call sites, and definitions of involved types.
- Map composition: callers, callees, shared state, sequencing, error paths that exist only when pieces combine.
- Name in-scope **hot paths** before Stage 4.
- Full review: run build and scoped tests. Consider a collision (Section 4.14) before calling a failure a defect.

## Stage 4 — Review

Run **every** hunt. Never stop after the first N findings. Cite the hunt in `Context`.

### Authority

Leading sources: `custom_instructions.md`, every loaded skill, Section 4. Walk their properties, ❗ and non-❗, against in-scope files. The hunts are reminders and are not complete. A miss against a leading source is an Error even if this skill omits it, unless its fix changes no content (§ Category Rules: Cosmetic).

A **property** is a rule the artifact must satisfy. A **procedure** is how an agent runs a workflow: stage order, Tech Load, git, subagents, chat language, output templates. Do not file procedure misses against the product. A trigger that matches but whose skill was not loaded = Error.

### Property hunts

Record finding IDs or `none` on the Summary **Checks** row.

| Hunt | Walk |
|------|------|
| Security | OWASP Top 10; caller parameters, URLs, bindings, or payloads used without validation; injection; secrets, tokens, or PII in logs; STRIDE mitigations; each security decision commented where enforced (threat, mitigation, rejected alternative, residual risk) |
| Limits | `tech-limits.md`: one complete declaration per limit; no bare bound at a trust boundary (Error); standard hit sentence; never silent truncation; limit tests; register lists the limit; a `FACT`, `TUNE`, or `PRESENT` constant has its reason. A review whose scope names the audit classifies every bound and writes the register in a tracked document |
| Races | Section 4.3: unsynchronized shared mutable state; race, TOCTOU, async interleaving, lock inversion, partial state; a primitive more expensive than needed; chosen primitive and its reason documented; concurrent tests for thread-safety claims |
| Correctness | Section 4.1: warnings or suppressions without the template; silent failure; throw or panic on an expected failure; parameter and trust-boundary validation; invalid state after errors; unchecked return values; `TODO`; tracking IDs in code; off-by-one; overflow |
| API | Invalid states unrepresentable; try or result at expected-failure boundaries; misuse analysis; public snippet matches the plan |
| Agent access | `tech-agent-access.md`: surface or waiver exists; parity; contract properties; security |
| Orphans | Unused types, APIs, files; dead code; stale docs; deprecated patterns; leaked internals; least visibility |
| Repo | Section 4.7: Windows, Linux, macOS on x64 and ARM64; UTF-8 console set once at start; Debug = Release; line length; no dates in code; copyright header; mandatory lint ran clean; no unapproved dependency, vendored file, or script; license MIT, Apache-2.0, or BSD-like |
| UI | `tech-web.md`: phone-first, breakpoints, tables, overflow, icons and tooltips, dark mode, a11y; Playwright journeys. Full: run them, failures = Error. Static: specs exist and can fail. Illustrations: only `workflow-illustrate.md` |
| Debug | Smallest native command, filterable tests, observable errors, traces; core logic needs no human-only debugger |

### Trace

Scope touches `spec/` or implements requirements: run `reqtrace check`, with .NET tests `reqtrace check --results artifacts/trace-results` on the TRX of this review's test run (`tech-reqtrace.md` § Test evidence). Then by search the integrity checks of `tech-spec.md` it does not cover. Each tool Error and each miss = Error finding; its Fix follows `tech-reqtrace.md` § Findings. Exit `2` is a check that did not run, never a pass. L4 or L6 on an entry a later plan step builds is no finding. A review never runs `accept`.

**Meaning:** for each card listed under `Not confirmed yet` in `reqtrace report`, read the old text, the new text, and the test. Do this in a session that did not write the change. It still proves the requirement: `reqtrace confirm <TEST-ID> --note "<what was checked>"` (`tech-reqtrace.md` § Confirm). It does not: Error. A machine check never replaces this hunt: a marker on the wrong test and a test without an assertion pass every tool check.

### Tests

Judge **content**, not file names. Is what the suite proves complete for the implemented behavior? Also judge speed.

- Classes that apply: happy, error, boundary, collection 0/1/2, absence, concurrency, trust-boundary. Happy-only coverage of a behavior with other classes is a gap.
- Important scenarios, edges, constellations, contradictions, gaps.
- A test that cannot fail is a gap, also when it passes.
- A plan in scope: walk every requirement and card **Content** against code and tests. Unmet on the page = Error. Missing Content = gap. An applicable class with no card, no code test, and no Out = gap. A code test without a card is not a gap.
- **Code tests** (`tech-test.md` § Code tests): walk each new or changed member yourself; never trust the ledger line. Branch, error path, parameter guard, loop 0/1/2, overflow guard, state transition, limit, thread-safety or allocation claim: one with no test, no covering card test, and no `n/a` reason = gap. A ledger line that names a missing test, or a code test without an exact assertion = Error.
- Full: run executable Done-when checks. Static: judge from source.

### Design decisions

Hunt encoded choices without a why: algorithm, data layout, lock or atomic, error policy, limit, security trade-off, fallback, compatibility shim, omitted validation. Undocumented = Error. “Obvious” is not documentation.

### Docs

Run the Sync check over the scope plus Impact hits. Walk `tech-docs.md` § Review severity. Do not sample.

- **Docs code:** each type and member has a contract doc comment with Related by meaning or dependency. Each non-trivial body passes the comment-only read. Optimizations, invariants, security decisions, traps, and couplings carry their tag. No history, commented-out code, stale parameter, or repeating file header.
- **Docs project:** each claim in README, architecture, concepts, domain, guides, decision records, and package docs matches the code. The code keeps every documented boundary, dependency, flow, and invariant, or the architecture and a decision record changed with it; a silent break is an Error even when the code works. Concepts and domain facts the change relies on are in their homes. New or moved components are in the Overview; a new non-compiler coupling of more than two parties is on the component’s **Easy to miss** line. Non-temporary documents are English (Section 4.6). Each fact sits in its home. Each non-compiler coupling has `Sync:` at both ends. Links, paths, symbols, and commands resolve.

File a defect once and cite both hunts when it is also a Consistency finding.

### Closing (Closing Review only)

- **Requirements end to end:** walk every requirement as its actor would; run each executable Done-when. Holds per file but fails end to end = Error.
- **Verify proves:** per step `Verify` and test ID, name the command that must fail if the claim did not hold. One that cannot fail = Error.
- **Leftovers:** scaffolding, dead code, stale docs, or a temporary shape from an earlier step = Error.
- **Briefing claims:** each **Concept** row, diagram edge, card clause, and **Files** row holds in its file. A missing Concept row for a new type, a missing card for a Judge or Sample file, or a Judge file marked Routine = Error.

### Performance

Required pass. Walk Section 4.4 and the loaded skill. Inspect named hot paths intensively (any allocation is a finding). Inspect cold and setup paths for dominant waste (unbounded buffer, slurped file, accidental N², needless copies, LINQ chains, boxing).

For each hot path, record on **Checks** (IDs or `none` per axis):

| Axis | Check |
|------|-------|
| Complexity | Time and space vs input; hidden nested loops; repeated scans |
| Allocations | Per-item heap; intermediate collections; boxing; closure capture; materialization; copy where a view would do |
| Latency | Blocking I/O on the request path; sync-over-async; await in a tight loop; lock across I/O |
| Throughput | Per-item vs batch; lock contention; serialized independent work |
| Memory | Retained graphs; unbounded caches; gen-1 or LOH survival when the skill names it |
| CPU | Redundant work; virtual dispatch; reflection; exceptions on the expected path; forbidden abstractions |

Source-visible cost is enough. No profiler unless asked or the finding claims a measured delta. Correctness or security wins per §4.13.

### Consistency

- Compare request vs source. Cross-check plan, requirements, code, tests, docs, comments, briefings, and skills. Hunt these pairs: documented behavior ≠ implementation; claimed `Verify` ≠ observed test result; API contract ≠ call sites; README or guide ≠ current commands and options.
- Do not assume either side is right. Source of truth clear: `How` names which side changes (`C{n}` when the plan already chose). Both could be intended: record **both** as two Fix options; do not pick. Undocumented mismatch → Error.
- Missing misuse analysis for a new public API, API drift from the plan snippet, or an unapproved dependency or script → Error.

### Skeptic (required)

Assume the solution cannot work. Do not stop at the first flaw.

- **Parts:** every in-scope unit: wrong default, off-by-one, silent swallow, copy-paste, tests that cannot fail, missing guard, a limit too tight for a capable host or too loose for its exposure. Dumbest caller error AND worst abuse + STRIDE.
- **Whole:** call graph, shared state, ordering, contracts vs callers, tests that do not prove the claim, defects that exist only in the interplay.
- Isolated nits that violate §4 or become fatal in combination = Error. `none` only after both hunts ran.

### Outsider (required)

First-time caller or reader. Flag jargon, opaque names, missing first-caller steps, docs that need insider context. `none` only after this hunt ran.

A high-stakes fork with ≥2 valid options: recommend `/council` in `How`. Competing goals without a recorded preference → Error; user decision before the verdict.

## Stage 5 — Output

Read `workflow-review-templates.md`. Write Summary last; place it after Findings Overview. The verdict lives in Summary. No second closer.

## Category Rules

Exactly one bucket. IDs: `E{n}` Error · `S{n}` Cosmetic · `R{n}` Refactoring · `P{n}` Performance. Never `C{n}`.

Ask in this order. The first yes names the bucket:

| Question | Bucket | Example |
|----------|--------|---------|
| Does the fix change content: behavior, a contract, whether a requirement holds, a fact a doc or comment states, or what the tests prove? | Error | A doc comment names a parameter that is gone |
| Are the results correct, but time, allocation, or memory cost is avoidable? | Performance | A LINQ chain allocates per row in the parse loop |
| Does the fix change form only (spelling of a private name, order, layout, wording), so that behavior, contracts, and stated facts stay exactly the same? | Cosmetic | A comment restates the line below it |
| Does the fix change structure only (extract, move, split), with unchanged behavior? | Refactoring | Two methods share twenty identical lines |

Cosmetic means no change in content. Which rule was missed, and whether it carries ❗, does not decide the bucket.

| Severity | Error | Performance |
|----------|-------|-------------|
| High | Security; data loss or corruption; a wrong result on a normal path; a requirement not met | A planned budget or a ❗ rule violated on a named hot path |
| Medium | A wrong result on an edge or error path; behavior without a test; a doc that states something false | Cost that grows with the input outside a named hot path |
| Low | No observable effect today: an undocumented decision, dead code, a stale comment | Constant cost on a cold path |

Every open Error blocks release, whatever its severity. Severity orders **Priority**.

| Bucket | Requires |
|--------|----------|
| Error | Severity; Problem/Fix. OWASP category for security; missing guard for validation. Covers skill misses, undocumented design decisions, `tech-docs.md` severity list, unproven behavior, race, unused code |
| Cosmetic | Exact style rule; Problem/Fix |
| Refactoring | Unchanged behavior in `Why`; extract, move, or split; Problem/Fix |
| Performance | Severity; Section 4.4 plus skill; Problem/Fix; complexity, frequency, allocation pressure, latency, throughput, memory, CPU, error paths in `Context`. **High** = violates a planned budget or a ❗ on a hot path; blocks release like an Error |

## Completion

**Release** answers the question for this scope. **Findings** lists every finding with Status. **Checks** has every hunt. Chat: bucket counts, verdict, path, prioritized actions.

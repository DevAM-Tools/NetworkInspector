# Copilot Instructions

## 1) Scope and Precedence

- Apply these instructions to every change, technology, and workflow phase.
- If `custom_instructions.md` exists at the repository root, read and apply it. Missing is not an error. Do not create it. It cannot weaken Section 4.
- Natural-language equivalents of `/evolve`, `/requirements`, `/plan`, `/implement`, `/review`, `/dream`, `/council`, `/commit-message`, `/illustrate`, `/docs-repair`, `/port-dotnet-rust`, `/port-dotnet-rust-loop` are the same workflow trigger.
- ❗ Edit only files inside the current workspace. Paths outside need an explicit user request.
- ❗ When the user attaches or names a file, read it in full.
- Out-of-scope work discovered: record it in the run report and continue in scope. Stop only when the scope depends on it.
- Read definitions of involved types and items before use or change.

## 2) Language

- Chat follows the user. Skills, instructions, and non-temporary documents are English (Section 4.6).
- Write complete sentences in chat. Keep technical terms, API names, paths, commands, and error text exact. Never abbreviate symbols or CLI flags.
- Never compress security warnings, destructive operations, or a sequence where shortening changes meaning or order.

## 3) Tech Load Protocol

- ❗ Before planning, editing, or reviewing: enumerate in-scope files by extension and project path, then read every matching skill from `.github/skills/`.
- ❗ Before the first edit of a run, print one line: `Loaded: <skills>`. Record it in the plan Context Anchor or review Scope.
- ❗ A workflow stage that names a skill: read it at that stage. A skill you cannot quote (context compacted, new session): read it again. Never work from memory of a skill.
- Scope matches a trigger but the skill is not loaded → stop and load it. Uncertain → load it. Unloaded skills do not apply.
- On workflow start, load the matching workflow skill (Section 6).
- Always with code, comments, or docs in scope: `tech-docs.md`. With tests or production APIs: `tech-test.md`. On `/requirements`, `/plan`, `/implement`, `/review`, `/evolve`, `/dream`: `tech-spec.md`, `tech-reqtrace.md`.
- `tech-web.md` and `tech-playwright.md` load when planning or changing a **product** web UI, even before files exist. Never `tech-web.md` for `illustrations/**`.
- Profiling skills load only when a measurement is needed: profiling requested, hot-path optimization, a `Verify` or `Debug` that needs numbers, a finding that claims a measured delta.
- Reviewing C# production APIs: also `tech-tunit.md` and `tech-test.md`.

| Trigger | Load skill |
|---------|------------|
| `*.cs` | `tech-csharp.md` |
| `*Tests.cs`, `{Project}.Tests/**`, `{App}.UiTest`, C# test projects | `tech-test.md`, `tech-tunit.md` |
| `GlobalUsings.cs` | `tech-csharp.md`, `tech-solution.md` |
| `.editorconfig`, `Directory.*.props`, `Directory.Build.targets`, `*.csproj`, `*.props`, `*.targets` | `tech-solution.md` |
| NuGet restore reports NU1900–NU1904; packing or releasing a NuGet package | `tech-solution.md` |
| `global.json` | `tech-tunit.md` |
| `*.razor`, `*.razor.cs`, `*.razor.css` | `tech-web.md`, `tech-blazor.md`, `tech-playwright.md`, `tech-test.md`, `tech-tunit.md`, `tech-csharp.md` |
| Web UI / HTML / CSS in an application | `tech-web.md`, `tech-playwright.md`, `tech-test.md` |
| `*Generator*.cs`, `IIncrementalGenerator` | `tech-sourcegen.md`, `tech-csharp.md` |
| `*.rs`, `Cargo.toml`, `Cargo.lock` | `tech-rust.md` |
| Rust tests (`#[cfg(test)]`, `tests/`) | `tech-test.md`, `tech-rust-test.md` |
| .NET → Rust port, `port/**` | `workflow-port-dotnet-rust.md`, `workflow-port-dotnet-rust-loop.md`, `workflow-port-dotnet-rust-books.md`, `tech-dotnet-rust.md`, `tech-csharp.md`, `tech-rust.md`, `tech-test.md`, `tech-rust-test.md`; `tech-tunit.md` when the source tests use TUnit |
| `*.py`, `pyproject.toml`, `*.pyi` | `tech-python.md`, `tech-test.md` |
| `test_*.py`, `*_test.py`, `pytest` | `tech-python.md`, `tech-test.md`, `tech-pytest.md` |
| `*.spec.ts`, Playwright, `illustrations/**` | `tech-playwright.md` (plus `tech-test.md` for specs) |
| `README.md`, `docs/**`, `CHANGELOG.md`, doc comments | `tech-docs.md` |
| New or changed project doc | `tech-docs-templates.md` |
| `spec/**`, `docs/domain/**` | `tech-spec.md` |
| `spec/trace.lock`, `spec/trace.ignore`, `.config/dotnet-tools.json`, a `reqtrace` call | `tech-reqtrace.md` |
| UI features, CLI, REST, MCP, or library API of an application | `tech-agent-access.md` |
| `.github/**` instructions, `.cursor/**`, `.claude/skills/**`, `AGENTS.md`, `CLAUDE.md`, `custom_instructions.md` | `tech-instructions.md` |
| A trust-boundary limit | `tech-limits.md` |
| Blocker, decision card, Spec delta, briefing, progress note, run report | `tech-reporting.md` |
| Measurement in C# / Rust / Python | `tech-csharp-profiling.md` / `tech-rust-profiling.md` / `tech-python-profiling.md` |

## 4) Always-On Quality Contract

Language mechanisms live in loaded tech skills. This section is intent.

### 4.1 Correctness

- ❗ Treat warnings as errors; fix root causes. A suppression needs user approval and the tech skill’s template (rule id, reason, scope).
- ❗ Never fail silently. Expected failures return result types, error codes, or try-APIs, always on public boundaries and hot paths. Throw or panic only for bugs and broken invariants.
- ❗ Validate external input at trust boundaries: content, structure, type, range, encoding, size.
- ❗ Validate function parameters at entry. Document omitted validation with reason and caller guarantees.
- List every affected file before any edit, with the Impact search (`tech-docs.md`). Keep cross-file changes consistent.
- Never leave invalid state after errors: atomic update, rollback, or compensation.
- Evaluate every return value that may indicate failure.
- Preserve preconditions, postconditions, and interface consistency. Guard against off-by-one errors, invalid transitions, and logic regressions.
- Never ship incomplete work. A `// TODO:` with a reason is allowed only between plan steps of an active `/implement`. Any `TODO` in a reviewed scope is an Error.
- ❗ Never put plan IDs, issue IDs, `REQ-`, `TEST-`, `DRIFT-`, `Q-` IDs, or tracking IDs in code or comments. One exception: a test may carry the one marker of its test card (`tech-spec.md` § Trace).
- ❗ Assess integer operations for overflow; guard when wrap-around would break correctness or security.

### 4.2 Security

- ❗ Do not violate OWASP Top 10.
- ❗ Never trust caller parameters, URLs, bindings, or payloads without validation.
- ❗ Never log secrets, credentials, tokens, or PII.
- Enumerate STRIDE threats when planning external input. After implementation, verify boundary validation and mitigations. Check injection paths and secret exposure.
- ❗ Comment each security decision where the code enforces it: threat, mitigation, rejected alternative, residual risk.
- ❗ Every bound on untrusted input is a limit: tied to a threat, agreed with the user, finite by default, never tighter than the host handles, named in the error when hit. Never truncate silently. Load `tech-limits.md`.

### 4.3 Thread Safety

- ❗ Shared mutable state across threads needs explicit synchronization; no data races.
- ❗ Identify race, TOCTOU, async interleaving, lock inversion, and partial-state risks in design. Verify after shared-state changes.
- ❗ Choose the cheapest correct primitive, in this order: (1) no shared mutation (ownership transfer, per-task state, immutable snapshot); (2) atomics for one word (counter, flag, pointer swap); (3) one-time initialization; (4) a lock with a short critical section: no I/O, allocation, await, or callback while held; (5) blocking hand-off (channel, queue, semaphore) only where threads must wait by design.
- Document the chosen primitive and why, and the thread safety of non-immutable shared types.
- Prove thread-safety claims with concurrent tests.

### 4.4 Performance

Performance is a feature. Design for it from the first plan step.

- ❗ Minimize allocations on every path.
- ❗ Hot paths (per-item or per-byte loops after setup, named in the plan or measured) allocate nothing.
- ❗ Read and write files with stream or incremental APIs. Never load an unbounded file into one string or byte array. Keep a bounded buffer.
- Prefer stack and reuse over heap, locality over chasing pointers, abstractions at boundaries and concrete, inlinable work in the loop.

### 4.5 Testing

- ❗ Every public or internal API has tests before release.
- Cover happy path, errors, boundaries, concurrency, security: behavior classes, not every value.
- Each unit test finishes in ≤ 100 ms. Slower tests carry the slow or integration marker. No sleeps. No network in unit tests.
- ❗ Minimize test run time. When a full run is not required, run only the tests that cover the change. .NET: `dotnet test`.
- ❗ Never delete or weaken a test to get green.
- ❗ Cards (`tech-spec.md`) hold the minimum from the requirements; each step adds code tests for what it wrote (`tech-test.md` § Two layers).

### 4.6 Documentation

Docs and code say the same thing for the life of the project. Doc set, checks, examples: `tech-docs.md`.

- ❗ One home per fact; elsewhere link to it. Requirements and test content live in `spec/`, domain knowledge in `docs/domain/` (`tech-spec.md`).
- ❗ Non-temporary documents are English: plans, requirements, `spec/`, reviews, briefings, councils, README, guides, architecture and domain docs, illustrations kept in the repo, public API docs. Git-ignored workflow folders count too. Only chat follows the user.
- ❗ Before planning, editing, or reviewing, read `README.md` and the architecture Overview; then the architecture sections in scope; then only the matching concept, domain, and decision entries. Record what was read.
- ❗ The architecture is a contract. Breaking a documented boundary, dependency, flow, or invariant needs user agreement, an architecture update, and a decision record in the same change.
- ❗ Docs move with code. A change to behavior, contract, structure, commands, or configuration updates every affected doc and comment in the same change.
- ❗ Knowledge learned in a run lands in its home in the same run. Workflow folders are git-ignored history.
- ❗ Before editing: Impact search. Before ✅: Sync check (`tech-docs.md`).
- ❗ Before adding to shared knowledge, read its section (`tech-spec.md` § Editing shared knowledge).
- ❗ Document every type and member, private included, in the language’s doc form: what it does in caller terms, parameters, returns, errors, side effects, thread safety, cost where not obvious.
- ❗ Related references link only items bound in meaning or dependency, never usages. A coupling the compiler cannot check gets `Sync:` at both ends.
- ❗ Comments state why and the flow: intent, design decisions, optimizations, invariants, security trade-offs. Split non-trivial bodies into blocks led by a comment. Never restate syntax.
- ❗ Comments and docs describe the present: no history, no commented-out code, no file header that describes the file.
- Markdown tables: 3–4 narrow columns. Diagrams: Mermaid `TD`, no wide `LR`, no ASCII art.
- ❗ File references are clickable Markdown links relative to the containing file, with forward slashes. Fenced commands may keep raw paths. In chat, link from the repo root.

### 4.7 Repository

- Support Windows/Linux/macOS on x64/ARM64. Keep Debug and Release behavior identical.
- ❗ Console executables set UTF-8 once at process start.
- Line length ≤ 130. No dates in code; copyright year is allowed. Add per-file copyright from `COPYRIGHT` to new source files.
- ❗ Run the language’s mandatory style or lint tool.
- ❗ **New Dependency Protocol.** Never add a package, crate, npm module, vendored file, browser, or tool without user approval. Ask in Grill Me, or before the edit when no plan exists; under `/evolve`, in the Spec delta. Present: id and exact version, what it does, why, license (MIT, Apache-2.0, or BSD-like only), alternatives including none.
- ❗ Use native toolchain commands. Do not write script files or multi-statement shell programs. Allowed: single commands, environment prefixes, plain pipes into a filter. A vendor-shipped install tool named in a tech skill counts as toolchain. Anything else: ask first.

### 4.8 UI

Applies to product web UIs, not `/illustrate` HTML. Rules: `tech-web.md`.

- ❗ Phone-first and responsive from a 320 px floor, no page-level horizontal scroll, unless the user waives it.
- Every interactive control has an accessible name and state.
- One visual language. Dark mode ships; other themes are out of scope unless asked.
- Plan Playwright journeys from the first plan.
- ❗ Use the integrated browser on the running UI while implementing, verifying, and debugging. Per-tool mapping: `tech-playwright.md`. No browser tool: say so once and use headed Playwright or a trace.

### 4.9 Structure

- Least-required visibility. Default private. Do not expose internals to other projects.
- Keep files cohesive. Remove dead code, stale docs, and deprecated patterns in active paths.
- Keep naming and patterns consistent across touched files.

### 4.10 Release Verdict

Every review answers whether its scope is ready for public release, in Summary: `Ready for public release` or prioritized blockers.

### 4.11 Git

- ❗ Git is read-only. Allow only `git status`, `git diff`, `git log`, `git show`, `git blame`, `git ls-files`, `git rev-parse`, `git branch` (list), `git remote -v`.
- Never run `add`, `commit`, `push`, `pull`, `fetch`, `checkout`, `switch`, `merge`, `rebase`, `reset`, `stash`, `tag`, `cherry-pick`, `revert`, `clean`, `rm`, `mv`, or any other command that writes to the repo, index, refs, or working-tree git state.

### 4.12 API Misuse Prevention

- Make incorrect use compile-time impossible or obviously wrong at the call site. Prefer types that encode invariants over flags.
- Enumerate misuse and abuse vectors in planning.
- Agree a public API as a short snippet plus usage in planning. Changing it after approval needs a new agreement.

### 4.13 Rule Priority

Security > Correctness > API contract > Performance > Style.

### 4.14 Concurrent agents, tools, loops

- Other agents may work in this repo. Build or test failures can be file locks or a dirty `bin`/`obj`. Re-run up to 3 times without sleeping. Still failing: blocker. Never rewrite production code to “fix” a collision.
- Unexpected diff outside the current step: stop and ask.
- The same tool call with the same result twice: change strategy or report blocked. Never re-read a file hoping it changed.
- ❗ Never use a subagent for council, advisors, peer review, or Exam.

### 4.15 Agentic debugging

- ❗ Design software so an agent can debug it: smallest native command, filterable tests, observable errors, traces. Core logic never needs a human-only debugger.
- `Verify` fails: reproduce, debug, then change code. Rule out a collision first. Do not redesign on the first red run.
- Two failed remedies of the same root cause: stop that item with a blocker. Continue other work.
- ❗ Profile and debug without administrator rights or system setting changes. Use the no-rights method of the loaded skill.

### 4.16 Decide, ask, report

❗ Never stop on a detail. Never ask what a skill, the spec, the docs, or the code answers. Sort every open point:

| Class | Examples | Do |
|-------|----------|----|
| Covered | Named by a skill, spec, doc, or neighboring code | Follow it |
| Detail | Name, file location, order, private shape, test layout, wording | Decide. Match neighbors. Say nothing |
| Assumption | A gap with a reversible, in-scope default | Take the default. Log it in the Spec delta or run report |
| Decision | Changes a requirement, public API, persisted format, security posture, limit, dependency, or architecture boundary; cannot be undone | Ask. Continue everything that does not depend on it |

```text
Wrong: "Should I name it ExportService or ExportManager?"
Wrong: "There are 3 warnings. Do you want me to fix them?"
Wrong: "I can't do this. Should I stop?"
Right: Pick the name that matches the neighbors. Fix the warnings. Try another remedy, then file a blocker.
```

❗ Every question to a person stands alone. The reader did not watch the run and knows no term you coined. Write it as to a colleague back from vacation, in the reader’s language:

```markdown
**What this is about:** {the subject in plain words}
**Today:** {what happens now; one concrete example; what you checked}
**Why it matters:** {what goes wrong or changes, and for whom}
**Question:** {one decision, in plain words}
**Options:** A) {what the reader gets} — {gain} · {cost or risk}. B) … Or answer in your own words.
**Recommendation:** {one option and why}
```

❗ Before sending, check:

1. Each name, ID, and abbreviation from this run is explained where it first appears, or removed.
2. A concrete example shows the problem: a real file, value, or scenario.
3. Each option says what the reader gets, not what you would do.
4. The reader can answer without asking back. If not, rewrite.

```text
Wrong: Should I build SpecCheck? I1–I11 run by search. A) Tool B) Keep search.
Right:
What this is about: spec/ links requirements to tests and code by IDs and paths. A link breaks when a test is renamed.
Today: The agent finds broken links by text search. A weak model can miss one; a requirement then counts as tested although its test is gone.
Why it matters: At the release gate you trust "all requirements tested", and that can be false.
Question: Should a small command-line tool check these links, so the result no longer depends on the model?
Options: A) Build it — a broken link fails the build · one more NuGet tool to maintain. B) Keep searching — nothing to maintain · misses stay possible. Or answer in your own words.
Recommendation: A, because a release decision rests on it.
```

Options never close the answer space. A choice tool without free text: ask in chat.

❗ Blockers, cards, Spec delta, briefing, and run report follow `tech-reporting.md`. Every editing workflow ends with a run report.

### 4.17 Context budget

Some sessions run in 100k tokens.

- ❗ Read the smallest unit that answers: search, then the heading, then the file. “Read in full” applies only where a rule says so.
- ❗ Never paste whole files or long logs into chat or plans. Filter build and test output to errors.
- ❗ Keep state in files, so a fresh session can resume. Before a context limit, save the state.
- Load a skill at the stage that uses it, not earlier (Section 3).

### 4.18 Agent access

❗ Software is built to be usable by agents: a UI is one client. Every application has an agent surface unless a requirement waives it with a reason. Load `tech-agent-access.md`.

## 5) Status Legend

`✅` Complete · `❌` Failed · `🚧` In progress · `⚠️` Blocked (a blocker stops it) · `⬜` Not started.

Tick every status surface of an item together. Plan: Step Overview, Shared Block, Task Checklist. Review finding: Summary **Findings** cell and finding `Status` line.

## 5.1 Terms

| Term | Meaning |
|------|---------|
| Grill Me | One round of Decision-class questions (Section 4.16) |
| `C{n}` | A recorded choice between options that could not all hold |
| Shared Block | The field template for a plan step or review finding |
| Step `{n}R` | The `/review` of Step `{n}`, scoped to that step’s change |
| Closing Review | `/implement` Stage 5: one full-scope `/review` of the built result |
| `REQ-…` / `TEST-…` / `DRIFT-…` / `Q-…` | Requirement, test card, doc-drift card, decision card. Format: `tech-spec.md`, `tech-reporting.md` |
| Done-when | A runnable check on a requirement: actor, boundary action, observable |
| MTP | Microsoft.Testing.Platform |
| Finding IDs | `E{n}` Error · `S{n}` Cosmetic · `R{n}` Refactoring · `P{n}` Performance. Never `C{n}` |
| Context Anchor | The plan’s summary block: loaded skills, Sweep, topics, coverage tables, decision count |
| Sweep | Short multi-view pass inside `/plan` or `/review` (`tech-sweep.md`) |
| Exam | Council teardown of a built result, on request. **Holds** or **Does not hold** |
| Kill shot | A flaw that fails a requirement or a ❗ rule; forces **Does not hold** |
| Briefing | `reviews/briefing_<slug>.md`: the human packet of what shipped |
| Spec delta | The human packet of requirement changes (`reviews/delta_<slug>.md`) |
| Decision card | A saved question for a person (`spec/open.md`) |
| Slice | The smallest set of requirements a person can try |
| Gate A / E / B | Human stops of `/evolve`: Spec · Escalation · Release |
| Lead | The `/evolve` session: plans, writes cards, checks, reviews, holds the gates |
| Builder | A subagent on a cheaper model that builds one plan step under `/evolve` (`workflow-evolve.md` § Builder) |
| Card test · Code test | Proves a test card · derived from the written code; no card |
| Hot path | A per-item or per-byte loop after setup |
| Limit definition | The one declaration of a trust-boundary limit (`tech-limits.md`) |
| Impact search | Before an edit: find every file, doc, and coupling it touches |
| Sync check | Before ✅: prove touched docs and comments state the new truth |
| Architecture Overview | First section of `docs/architecture.md`: component table |
| Easy to miss | Architecture line naming couplings the compiler does not check |
| Decision record | `docs/decisions/<slug>.md`: one decision, its reasons, rejected options |
| Related | Doc-comment field linking items bound in meaning or dependency |
| Run report | The last chat message of an editing workflow run |
| Progress note | Chat status after each step of `/implement` and `/evolve`: done now, items, next, waiting on you |
| Blocker | A stop written per `tech-reporting.md` |

## 6) Workflow Entry

Load this file, then `custom_instructions.md` when present, then the matching skill. `/implement` runs only after explicit plan approval or equivalent intent, or inside `/evolve` after Gate A.

| Trigger | Skill |
|---------|-------|
| `/evolve` | `workflow-evolve.md` |
| `/requirements` | `workflow-requirements.md` |
| `/plan` | `workflow-plan.md` |
| `/implement` | `workflow-implement.md` |
| `/review` | `workflow-review.md` |
| `/dream` | `workflow-dream.md` |
| `/council` | `workflow-council.md` |
| `/commit-message` | `workflow-commit-message.md` |
| `/illustrate` | `workflow-illustrate.md` |
| `/docs-repair` | `workflow-docs-repair.md` |
| `/port-dotnet-rust` | `workflow-port-dotnet-rust.md` |
| `/port-dotnet-rust-loop` | `workflow-port-dotnet-rust-loop.md` |

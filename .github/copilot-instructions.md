# Copilot Instructions

## 1) Scope and Precedence

- Apply these instructions to every change, technology, and workflow phase.
- Optional: if `custom_instructions.md` exists at the repository root, read and apply it. Missing is not an error. Do not create it. It extends these instructions; it cannot weaken Section 4.
- Treat natural-language equivalents of `/plan`, `/implement`, `/review`, `/review-loop`, `/complex-task`, `/council`, `/commit-message`, `/requirements`, `/illustrate`, `/docs-repair`, `/port-dotnet-rust`, `/port-dotnet-rust-loop` as the same workflow trigger.
- ❗ Edit only files inside the current workspace. Paths outside it need an explicit user request.
- ❗ When the user attaches or names files, **read that file in full**. User-given docs are context, not optional background.
- Stop and ask when out-of-scope work is discovered.
- Read definitions of involved types and items before use or change.

## 2) Language

Chat follows the user. Skills, instructions, and non-temporary documents are English (Section 4.6).

- Write complete sentences in chat. Do not compress chat.
- Keep technical terms, API names, types, paths, commands, and error text exact. Never abbreviate symbols or CLI flags.
- Write code and build/project files in normal complete form per loaded tech rules.
- Do not compress security warnings, destructive operations, or any sequence where shortening would change meaning or order.

## 3) Tech Load Protocol

- Before planning, implementation, or review edits: enumerate in-scope files by extension and project path, then load every matching skill from `.github/skills/` with the file-read tool.
- Record `Loaded skills: <list>` in the plan Context Anchor (review: Scope).
- Scope matches a trigger but the skill is not loaded → abort with a blocker. Uncertain → load it. Unloaded skills do not apply.
- Load `tech-docs.md` whenever code, comments, or docs are in scope: every `/plan`, `/implement`, `/review`, `/review-loop`, `/complex-task`, `/docs-repair`. Add `tech-docs-templates.md` when creating or restructuring a project doc.
- Load `tech-test.md` whenever tests or production APIs are in scope.
- Load `tech-web.md` when planning or changing a **product** web UI — even before HTML files exist. Do not load it for `illustrations/**`.
- Load `tech-playwright.md` when planning or changing a web UI — even before spec files exist — and when verifying HTML illustrations at create time or in a full `/review`. The shipped illustration zip does not include Playwright.
- Load the language’s profiling skill (`tech-csharp-profiling.md`, `tech-rust-profiling.md`, `tech-python-profiling.md`) with its language skill only when a measurement is needed: profiling is requested, a step optimizes a hot path, a `Verify` or `Debug` needs a measurement, or a finding claims a measured delta.

| Trigger | Load skill |
|---------|------------|
| `*.cs` | `tech-csharp.md` |
| `*Tests.cs`, `{Project}.Tests/**`, C# test projects, `{App}.UiTest` / C# Playwright UI tests | `tech-test.md`, `tech-tunit.md` |
| `GlobalUsings.cs` | `tech-csharp.md`, `tech-solution.md` |
| `.editorconfig`, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props` | `tech-solution.md` |
| `global.json` | `tech-tunit.md` |
| `*.razor`, `*.razor.cs`, `*.razor.css` | `tech-web.md`, `tech-blazor.md`, `tech-playwright.md`, `tech-test.md`, `tech-tunit.md`, `tech-csharp.md` |
| Web UI / HTML / CSS in an application | `tech-web.md`, `tech-playwright.md`, `tech-test.md` |
| `*Generator*.cs`, `IIncrementalGenerator` | `tech-sourcegen.md`, `tech-csharp.md` |
| `*.csproj`, `*.props`, `*.targets` | `tech-solution.md` |
| `*.rs`, `Cargo.toml`, `Cargo.lock` | `tech-rust.md` |
| Rust tests (`#[cfg(test)]`, `tests/`) | `tech-test.md`, `tech-rust-test.md` |
| .NET → Rust port, `port/**` | `workflow-port-dotnet-rust.md`, `workflow-port-dotnet-rust-loop.md`, `workflow-port-dotnet-rust-books.md`, `tech-dotnet-rust.md`, `tech-csharp.md`, `tech-rust.md`, `tech-test.md`, `tech-rust-test.md`; `tech-tunit.md` when the source tests use TUnit |
| `*.py`, `pyproject.toml`, `*.pyi` | `tech-python.md`, `tech-test.md` |
| `test_*.py`, `*_test.py`, `pytest` | `tech-python.md`, `tech-test.md`, `tech-pytest.md` |
| `*.spec.ts`, Playwright | `tech-playwright.md`, `tech-test.md` |
| `illustrations/**`, illustration HTML | `tech-playwright.md` |
| `README.md`, `docs/**`, `CHANGELOG.md`, doc comments | `tech-docs.md` |
| `.github/copilot-instructions.md`, `.github/skills/**`, `.github/prompts/**`, `.cursor/**`, `.claude/skills/**`, `AGENTS.md`, `CLAUDE.md`, `custom_instructions.md` | `tech-instructions.md` |
| Measurement needed in C# / Rust / Python (see bullet above) | `tech-csharp-profiling.md` / `tech-rust-profiling.md` / `tech-python-profiling.md` |

- When reviewing C# production APIs, also load `tech-tunit.md` and `tech-test.md`.
- On workflow start, load the matching workflow skill from `.github/skills/`.

## 4) Always-On Quality Contract

Language-specific mechanisms live in loaded tech skills. This section is intent.

### 4.1 Correctness

- ❗ Treat warnings as errors; fix root causes. Do not hide a warning without user approval. A suppression uses the loaded tech skill’s **suppression template** (rule id, reason, scope); without a reason it is incomplete.
- ❗ Never fail silently. Expected failures return result types, error codes, or language-idiomatic try-APIs — at public boundaries and always on hot paths. Throw or panic only for bugs and broken invariants.
- ❗ Validate external input at trust boundaries: content, structure, type, range, encoding, size. Limits: Section 4.2.
- ❗ Validate function parameters at entry. Document omitted validation in the API docs with reason and caller guarantees.
- List every affected file before any edit — implementation, call sites, tests, config, docs — with the Impact search (`tech-docs.md`). Keep cross-file changes consistent.
- Never leave invalid or undefined state after errors; use atomic update, rollback, or compensation.
- Evaluate return values that may indicate failure. Never omit return values.
- Preserve preconditions, postconditions, and interface consistency. Guard against off-by-one errors, invalid transitions, and logic regressions.
- Never ship incomplete implementations. A `// TODO:` with a concrete reason is allowed only between plan steps of an active `/implement` run. Any `TODO` in a reviewed scope is an Error in `/review` and blocks release.
- Never put plan IDs, issue IDs, `REQ-<AREA>-<TOPIC>`, `TEST-<AREA>-<TOPIC>`, `DRIFT-<AREA>-<TOPIC>`, or tracking IDs in code or comments.
- ❗ Assess integer ops for overflow/underflow; guard when wrap-around would break correctness, security, or invariants. Mechanism: loaded tech skill.

### 4.2 Security

- ❗ Do not violate OWASP Top 10.
- ❗ Never trust caller parameters, URLs, bindings, or payloads without validation.
- ❗ Never log secrets, credentials, tokens, or PII.
- Enumerate STRIDE threats in planning for every external-input feature. After implementation, verify boundary validation and the STRIDE mitigations.
- Check injection paths and secret exposure actively.
- ❗ Comment each security decision where the code enforces it: the threat, the chosen mitigation, the rejected alternative, and the accepted residual risk.

**Trust-boundary limits** (size, count, length, depth, range, time, rate). A limit protects; it must not cripple the host.

- ❗ Agree every limit with the user. Do not invent silent defaults.
- ❗ Tie each limit to the threat it stops (exhaustion, amplification, abuse). The host is unknown: do not cap below what it may handle just to pick a number.
- Prefer a bound that scales over a small fixed number: stream instead of buffer (Section 4.4), or derive the default from the host (memory, cores).
- **Constant** only for facts of a format, protocol, or type. A value that depends on host, data, deployment, or risk appetite is **configurable**.
- ❗ Configurable: record where it lives, who may change it (code, operator, end user), the default, and the allowed range. Validate the configured value as external input. A ceiling that protects other users or the system stays out of end-user reach.
- ❗ Label the **posture** per limit: **secure by default** (tight default; loosening is opt-in) or **open by default** (loose default; tightening is opt-in). Neither wins by rule. Choose by exposure (untrusted, remote, or multi-user input → secure by default) and record why. Mark an open default in the docs and next to the setting.
- ❗ End-user setting: show what it limits, why it exists, the unit, the allowed range, the default, and the risk of raising or lowering it. Reject an out-of-range value with a message that names the range.
- ❗ Exceeding a limit is an expected failure (Section 4.1): name the limit in the error. Never truncate or drop silently.
- ❗ Record per limit, in the plan and in a comment at its definition: value or range · threat · constant or configurable (where, who) · posture and why.

### 4.3 Thread Safety

- ❗ Shared mutable state across threads requires explicit synchronization; no data races.
- ❗ Identify race, TOCTOU, async interleaving, lock inversion, and partial-state risks in design. After shared-state changes, verify no new concurrency defects.
- ❗ Choose the cheapest primitive that is correct. Context switches and kernel waits are expensive; design so threads rarely block. Order of preference:
  1. No shared mutation: ownership transfer, per-thread or per-task state, immutable snapshots.
  2. Atomics for a single word (counter, flag, pointer swap).
  3. One-time initialization primitives for lazily built shared state.
  4. A lock with a short critical section: no I/O, no allocation, no await, no callback while held. Prefer a reader-writer lock only when reads dominate and the section is long enough to pay for it.
  5. Blocking hand-off (channels, queues, semaphores) only where threads must wait by design.
- Document the chosen primitive and why. Document thread safety of non-immutable shared types. Mechanism, types, and form: loaded tech skill.
- Prove thread-safety claims with concurrent tests.

### 4.4 Performance

Performance is a feature of every application and library, not optional polish. Design for it from the first plan step. Language APIs, GC, SIMD, and pools: **loaded tech skill**.

- ❗ Minimize allocations on every path. Each allocation is garbage the GC or allocator must reclaim, and any of them can trigger a collection that stalls the process. Allocate only what the result needs.
- ❗ Hot paths (per-item / per-byte loops after setup, named in the plan or measured) allocate nothing.
- ❗ Read and write files with stream, reader, or incremental APIs. Do not load an unbounded file into a single string or byte array. Keep a bounded buffer. Mechanism: loaded tech skill.
- Prefer stack and reuse over heap; prefer locality and a stable layout over chasing pointers.
- Prefer abstractions at boundaries and concrete, inlinable work in the loop.

### 4.5 Testing

- ❗ Require tests before release for every public or internal API.
- Cover happy path, errors, boundaries, concurrency, and security. Exhaustive means those **behavior classes**, not iterating every integer value.
- Keep tests fast: each unit test finishes in ≤ 100 ms. Slower tests carry the stack’s slow or integration marker. No sleeps. No network in unit tests.
- ❗ Minimize test execution time on every stack. When a full run is not required, run only the tests that cover the change. .NET tests use `dotnet test`. Mechanism: `tech-test.md`.
- Test **strategy**: `tech-test.md`. Test **stack**: loaded tech skill.
- In planning / Grill Me, lock test **content** as `TEST-<AREA>-<TOPIC>` (important scenarios, edges, constellations, contradictions, gaps). That content is the minimum to prove; extra tests at implement time are allowed. Schema: `workflow-plan.md`.

### 4.6 Documentation

Goal: docs and code say the same thing for the whole life of the project. Details, checks, and examples: `tech-docs.md`. Templates: `tech-docs-templates.md`.

**Doc set.** One home per fact; everywhere else links to it. Minimum: `README.md` and `docs/architecture.md`. Create the others with their first fact.

| Doc | Holds | Belongs here when |
|-----|-------|-------------------|
| `docs/architecture.md` | Overview (components, folders, entries, allowed dependencies); per component: flows, invariants, easy-to-miss couplings | Which part does what, who may call whom |
| `docs/concepts.md` | Ideas of the solution that span components | How our solution works inside |
| `docs/domain.md` | Glossary, domain rules with source, external facts | True even if the code were rewritten |
| `docs/decisions/<slug>.md` | Decision record: context, decision, rejected options | Why A and not B |

- ❗ Non-temporary documents are **English**: plans, requirements, briefings, reviews, councils, README, guides, architecture docs, illustrations that stay in the repo, public API docs. Chat follows the user’s language.
- ❗ Read before acting: before planning, editing, or reviewing, read `README.md` and the architecture Overview; then the architecture sections of the components in scope; then only the concept, domain, and decision entries for the task’s terms (grep the heading). Record what was read. Do not read whole docs the task does not touch.
- ❗ The architecture is a contract. A change that breaks a documented boundary, dependency, flow, or invariant needs the user’s agreement, an architecture update, and a decision record in the same change; otherwise it is a defect.
- ❗ Docs move with code. A change to behavior, contract, structure, commands, or configuration updates every affected doc and comment in the same change. Knowledge learned in a run (user answer, spec fact, review finding) lands in its home in the same run; workflow folders are git-ignored history.
- ❗ Before editing, find what the change touches (Impact search): grep each changed symbol, path, option, key, and message across code, docs, and config; follow the **Related** and `Sync:` references of the touched items. Before ✅, re-read every touched comment and doc statement against the new code (Sync check).
- ❗ Document every type and member, private included, in the language’s doc form: what it does in caller terms (never the name reworded), parameters, returns, errors, side effects, thread safety, and cost where not obvious. Form: loaded tech skill.
- ❗ References in doc comments (**Related**) link only items bound in meaning, items this one depends on, and items easy to forget on change. Never list call sites or usages. A coupling the compiler cannot check gets a `Sync:` line at both ends that names the other end.
- ❗ Inline comments explain why and the flow: purpose, motivation, background, design decisions (algorithm, data layout), optimizations, invariants, security trade-offs (Section 4.2). Never restate obvious syntax. Split non-trivial bodies into semantic blocks separated by one blank line (two allowed); lead each with a comment that states intent and what to watch. Signatures, doc comments, and block comments alone must tell the flow.
- ❗ Comments and docs describe the present: no change history, no commented-out code. No file header that describes the file; the type or module doc is that description. Large files: named section markers in flow order.
- Markdown tables: narrow (about 3–4 columns, short cells). Diagrams: Mermaid `TD`, tall layout, short labels — no wide `LR` graphs, no ASCII art. Assume the reader has several panes open.
- ❗ File references are **clickable Markdown links**. The target is relative to the file that contains the link, with forward slashes, so the link opens. Form from `plans/` or `reviews/`: [`src/Foo.cs`](../src/Foo.cs). Bare backticks, absolute `E:\` / `/Users/` paths, and unlinked names are not enough. Especially plans, briefings, and reviews; also requirements, councils, and README. Fenced commands may keep a raw path so it stays copy-pasteable. In chat, link workspace files from repo root: [`src/Foo.cs`](src/Foo.cs).

### 4.7 Repository

- Support Windows/Linux/macOS on x64/ARM64.
- ❗ Executables that write or read the console: set the console to UTF-8 **once** at process start. Required on Windows (legacy code page). Mechanism: loaded tech skill.
- Keep Debug and Release behavior identical.
- Limit line length to 160.
- Do not put dates in code; copyright year is allowed.
- Add per-file copyright from `COPYRIGHT` when creating source files. Syntax: loaded tech skill.
- ❗ Require the language’s mandatory style/lint tool per loaded tech skill.
- ❗ **New Dependency Protocol.** Never add a package, crate, npm module, vendored file, browser, or tool without user approval. Ask in Grill Me (or before the edit when no plan exists). Present: id and exact version, what it does, why it is needed, license (MIT, Apache-2.0, or BSD-like only), alternatives including “no dependency”. Stack mechanics (where the version lives, install command): loaded tech skill.
- ❗ Use native toolchain commands from the loaded tech skill. Do not write new script files (shell, PowerShell, batch, Python) and do not run multi-statement shell programs. Allowed: single commands, environment-variable prefixes, and plain pipes into a filter (`grep`, `Select-String`). A vendor-shipped install tool named in a tech skill (for example Playwright’s `playwright.ps1`) is a toolchain command, not a repo script. Last resort for anything else: ask first; say what the script does.

### 4.8 UI

These bullets apply to **product** web UIs (app pages, Blazor, HTML/CSS in a product), not to `/illustrate` HTML. Illustrations are not required to be responsive; Playwright may verify them at create time (`workflow-illustrate.md`), and the shipped zip does not depend on it.

- Every interactive control has an accessible name and state. Add `aria-*` only when visible text does not already provide them (`tech-web.md`).
- ❗ Product web UIs must be responsive (phone-first, 320px floor, no page-level horizontal scroll) unless the user explicitly asks otherwise. Locked breakpoints, CSS layers, and JS vs CSS: `tech-web.md`.
- Keep one visual language across product pages. Shared CSS for app/layout look; isolate only component-specific rules. Weigh each new rule: app-wide, layout/page, or this component. Mechanism: `tech-web.md` (stack extras: `tech-blazor.md`).
- Ship **dark mode** on product UIs. Other themes are allowed but **out of scope** unless the user asks.
- Plan Playwright journeys for product web UIs from the first plan, for tests **and** debugging.
- ❗ Use the integrated browser on a running product web UI while implementing, verifying, and debugging it: open the page, navigate, click, type, read what rendered, check the console. **Cursor:** the agent Browser. **VS Code (Copilot):** Integrated Browser. **Claude Code:** the desktop app browser pane; in the CLI and the VS Code extension, the Claude in Chrome browser tools when connected. When the session exposes no browser tool, say so once and use headed Playwright or a trace. It never replaces Playwright journeys. Mechanism: `tech-playwright.md`.

### 4.9 Structure

- Use least-required visibility; default private.
- Keep files cohesive; do not mix unrelated types or modules.
- Remove dead code, stale docs, deprecated patterns in active paths.
- Keep naming and patterns consistent across touched files.
- Do not expose internals to other projects.

### 4.10 Release Verdict

- Every review must answer whether the reviewed **scope** is ready for public release. Write that in Summary: `Ready for public release` or prioritized blockers, each written per Section 4.16. Do not add a separate closing section.

### 4.11 Git

- ❗ Git is read-only. Never change the repository, index, refs, or working-tree git state.
- Allow only non-mutating inspection: `git status`, `git diff`, `git log`, `git show`, `git blame`, `git ls-files`, `git rev-parse`, `git branch` (list), `git remote -v`.
- Never run `git add`, `git commit`, `git push`, `git pull`, `git fetch`, `git checkout`, `git switch`, `git merge`, `git rebase`, `git reset`, `git stash`, `git tag`, `git cherry-pick`, `git revert`, `git clean`, `git rm`, `git mv`, or any other command that writes to the repo.
- Never create, amend, or rewrite commits.

### 4.12 API Misuse Prevention

- Design APIs so incorrect use is compile-time impossible or obviously wrong at call sites.
- Prefer types and states that encode invariants instead of primitive flags or ambiguous combinations. Prefer making invalid states unrepresentable over runtime validation alone when cost is reasonable.
- Enumerate misuse and abuse vectors in planning (how can the solution be used wrongly or exploited).
- For library (and other public-surface) work, agree the public API in planning as a **short snippet plus usage**. Changing that snippet after plan approval needs a new Grill Me.

### 4.13 Rule Priority

- On conflict apply: Security > Correctness > API contract > Performance > Style.

### 4.14 Concurrent agents, tools, loops

- Assume other agents may work in the same repo. Build and test failures can be file locks, PDB locks, parallel test runners, or a dirty `bin`/`obj` — not a logic bug. Re-run the same command up to 3 times; do not sleep in a loop. Still failing: report a blocker (Section 4.16). Do not rewrite production code to “fix” an environmental collision.
- Unexpected diffs outside the current step’s `Where` → stop and ask.
- If the same tool name and equivalent arguments return the same result twice, stop. Change strategy or report blocked. Do not re-read the same file hoping it changed.
- ❗ Never use a subagent or delegation tool for council, advisors, peer review, or Exam. Same agent, sequential.

### 4.15 Agentic debugging

- ❗ Design apps and libraries so an agent can debug them: smallest native toolchain command, filterable tests, observable errors, traces. Do not require a human-only debugger for core logic.
- When `Verify` fails: reproduce with that command, then debug, then change code. Do not redesign on the first red run. Rule out a concurrent-agent collision first (Section 4.14).
- After two failed remediations of the same root cause: stop and report a blocker (Section 4.16).
- ❗ Profile, trace, and debug without administrator, root, or sudo rights, and without changing kernel or system settings (no UAC prompt, no `perf_event_paranoid` change, no `wsl -u root`). Tools that need them are out: ETW kernel sessions (xperf, WPR, samply on Windows), Linux `perf` or samply when `perf_event_paranoid` > 1. Use the no-rights method in the loaded tech skill.

### 4.16 Run reports, blockers, and questions

The reader may not have seen earlier output. Every report item, blocker, and question stands alone: a person can judge it without asking back.

**Run report.** ❗ Every run of a workflow that edits files (`/implement`, `/review-loop`, `/complex-task`, `/docs-repair`, `/port-dotnet-rust-loop`) ends with one run report as its last chat message, on every exit: complete, partial, blocked, failed, paused, aborted. A parent workflow reports once, not per child. Sections in this order; an empty section says `none`, never omitted:

1. **Outcome:** workflow, status (`✅` done · `🚧` partial or paused · `⚠️` stopped by a blocker · `❌` failed), scope, `{done} of {total}` items, artifact paths.
2. **Not done:** every scoped item that is not `✅`, with its link and reason (not started · stopped at · deferred by the user · skipped because). `none` only when everything in scope is `✅`.
3. **Needs you:** each open decision, in the question form below.
4. **Problems:** each blocker, in the blocker form below.
5. **Deviations:** each place where the result differs from the plan or finding: planned vs built, why, approved by whom or `needs approval` (then also under **Needs you**).

❗ Never report a run as done while **Not done** has an entry. Never mention unfinished work only in passing.

**Blocker.** A blocker stops a plan, step, finding, review verdict, loop, or port unit: plan Open Questions, review **Release** `Blocked by`, run report **Problems**, Exam **Does not hold**, port `BL-*` records. It carries:

- **What blocks:** the concrete defect, missing fact, or failed check; its ID and a clickable location (Section 4.6).
- **Evidence:** command and output, file and symbol, or quoted source.
- **Impact:** what fails, for whom, and what ships wrong if it is ignored or worked around (users, data, security, performance, release).
- **Scope:** what is blocked and what can still continue.
- **To unblock:** the decision, fact, or fix needed; the options with their consequences; who must act.

Add per kind:

| Kind | Also states |
|------|-------------|
| Requirement not met | Requirement link and Done-when; what ran; what was observed; the gap |
| Conflict | Both sides quoted with location; which side the work followed and why; what the other choice would change |
| Failed check | Exact command; the key output lines; whether a concurrent-agent collision was ruled out (Section 4.14) |

❗ **Question.** Every question to the user (Grill Me, decision card, Open Question, a stop that needs a choice) carries:

- **Context:** the task and where the run stands, in one line; then what was found, quoted or linked; what was already checked.
- **Why it matters:** what changes depending on the answer.
- **Question:** one part.
- **Options:** each with its consequence (what changes, for whom, what it risks).
- **Recommendation:** one option and why.

- ❗ Options are suggestions, never the whole answer space. The user can always answer in their own words. A choice tool must offer a free-text answer; when it cannot, ask in chat instead.

A blocker that only names an ID or says “blocked”, a question without context or consequences, and a run report without **Not done** are incomplete.

## 5) Status Legend

- `✅` Complete / Fixed
- `❌` Error / Failed
- `🚧` Started / In progress (work began, not finished, nothing blocks it)
- `⚠️` At risk / Blocked (a blocker per Section 4.16 stops it)
- `⬜` Not started / Open
- Tick every plan status surface together: Step Overview, Shared Block, Task Checklist. Never leave the overview stale.
- Tick every review finding status surface together: the Summary **Findings** Status column and the finding Shared Block `Status` line. Never leave the summary table stale.

## 5.1 Terms

| Term | Meaning |
|------|---------|
| Grill Me | One round of user-fact questions (Section 4.16 question form). Not a council. |
| `C{n}` | A recorded choice between options that could not all hold. |
| Shared Block | The field template for a plan step or review finding. |
| Step `{n}R` | The `/review` of Step `{n}`, scoped to that step’s change. Same as “Step NR” with N = `{n}`. |
| Closing Review | `/implement` Stage 5: one full-scope `/review` of the built result with the plan in scope. |
| `REQ-<AREA>-<TOPIC>` | One atomic requirement. The ID names the subject. Stays out of product code. |
| `TEST-<AREA>-<TOPIC>` | Plan test content. The ID names the case. Not a file or method name. Stays out of product code. |
| `DRIFT-<AREA>-<TOPIC>` | A `/docs-repair` decision card: doc and code disagree. Stays out of product code. |
| MTP | Microsoft.Testing.Platform (`global.json` `test.runner`). |
| Finding IDs | `E{n}` Error · `S{n}` Cosmetic (style) · `R{n}` Refactoring · `P{n}` Performance. Never `C{n}`. |
| Done-when | A runnable check on a requirement: actor, boundary action, observable. |
| Context Anchor | The plan’s summary block: loaded skills, Perspective Sweep, coverage tables, decision count. |
| Sweep | Short multi-view pass inside `/plan` (five views) or `/review` (Skeptic and Outsider). No file. |
| Exam | Council teardown of a built result, on user request after the Closing Review. Verdict **Holds** or **Does not hold** (loop status `exam-fail`). |
| Kill shot | A flaw that makes the result fail a requirement or a ❗ rule. Forces **Does not hold**. |
| Briefing | `reviews/briefing_<slug>.md`: the human packet of what `/implement` shipped. |
| Hot path | A per-item or per-byte loop after setup (Section 4.4). |
| Impact search | Before an edit: find every file, doc, and coupling the change touches (Section 4.6; steps in `tech-docs.md`). |
| Sync check | Before ✅: prove touched docs and comments state the new truth (Section 4.6; steps in `tech-docs.md`). |
| Architecture Overview | The first section of `docs/architecture.md`: component table with folder, entry, responsibility, allowed dependencies. Read before acting. |
| Easy to miss | The line in an architecture component section that names couplings the compiler does not check. |
| Decision record | `docs/decisions/<slug>.md`: one decision, its reasons, the rejected options (some teams say ADR). |
| Related | The doc-comment field that links items bound to this one in meaning or dependency; never usages. |
| Run report | The last chat message of an editing workflow run, on every exit (Section 4.16). |
| Blocker | A stop written per Section 4.16. |

## 6) Workflow Entry

Load this file, then `custom_instructions.md` when present, then the matching skill. `/implement` only after explicit plan approval (or equivalent intent). Stages live in the skill.

| Trigger | Skill |
|---------|-------|
| `/requirements` | `workflow-requirements.md` |
| `/plan` | `workflow-plan.md` |
| `/implement` | `workflow-implement.md` |
| `/review` | `workflow-review.md` |
| `/review-loop` | `workflow-review-loop.md` |
| `/complex-task` | `workflow-complex-task.md` |
| `/council` | `workflow-council.md` |
| `/commit-message` | `workflow-commit-message.md` |
| `/illustrate` | `workflow-illustrate.md` |
| `/docs-repair` | `workflow-docs-repair.md` |
| `/port-dotnet-rust` | `workflow-port-dotnet-rust.md` |
| `/port-dotnet-rust-loop` | `workflow-port-dotnet-rust-loop.md` |

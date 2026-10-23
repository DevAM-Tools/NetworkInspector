# Copilot Instructions

## 1) Scope and Precedence

- Apply these instructions to every change, technology, and workflow phase.
- Optional: if `custom_instructions.md` exists at the repository root, read and apply it. Missing is not an error. Do not create it. It extends these instructions; it does not replace them. It cannot weaken Section 4.
- Treat natural-language equivalents of `/plan`, `/implement`, `/review`, `/review-loop`, `/complex-task`, `/council`, `/commit-message`, `/requirements`, `/illustrate`, `/port-dotnet-rust`, `/port-dotnet-rust-loop` as the same workflow trigger.
- ❗ Edit only files inside the current workspace. Paths outside it need an explicit user request.
- ❗ When the user attaches or names files, **read that file in full**. User-given docs are context, not optional background.
- Stop and ask when out-of-scope work is discovered.
- Read definitions of involved types and items before use or change.
- Keep README, guides, architecture docs, package docs, and skill pointers consistent with **every** change. If behavior, API, commands, or workflow entry points move, update those docs in the same work.

## 2) Language

Chat follows the user. Skills, instructions, and non-temporary documents are English (Section 4.6).

- Write complete sentences in chat. Do not compress chat.
- Keep technical terms, API names, types, paths, commands, and error text exact. Never abbreviate symbols or CLI flags.
- Write code and build/project files in normal complete form per loaded tech rules.
- Do not compress security warnings, destructive operations, or any sequence where shortening would change meaning or order.

## 3) Tech Load Protocol

- Run Tech Load Check before planning, implementation, or review edits.
- Enumerate in-scope files by extension and project path first.
- Load every matching skill from `.github/skills/` with the file-read tool before any edit.
- Record loaded skills in plan Context Anchor: `Loaded skills: <list>`.
- Abort with blocker if scope matches a trigger but skill was not loaded.
- When uncertain whether a skill applies, load it.
- Apply only loaded tech skills. Unloaded skills do not apply.
- Load `tech-test.md` whenever tests or production APIs are in scope.
- Load `tech-web.md` when planning or changing a **product** web UI — even before HTML files exist. Do not load it for `illustrations/**`.
- Load `tech-playwright.md` when planning or changing a web UI — even before spec
  files exist — and when verifying HTML illustrations at create time or in a
  full `/review`. The shipped illustration zip does not include Playwright.
- Load the language’s profiling skill (`tech-csharp-profiling.md`,
  `tech-rust-profiling.md`, `tech-python-profiling.md`) with its language
  skill only when a measurement is needed: profiling is requested, a step
  optimizes a hot path, a `Verify` or `Debug` needs a measurement, or a
  finding claims a measured delta.

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
| Measurement needed in C# / Rust / Python (see bullet above) | `tech-csharp-profiling.md` / `tech-rust-profiling.md` / `tech-python-profiling.md` |

- When reviewing C# production APIs, also load `tech-tunit.md` and `tech-test.md`.
- On workflow start, load the matching workflow skill from `.github/skills/`.

## 4) Always-On Quality Contract

Language-specific mechanisms live in loaded tech skills. This section is intent.

### 4.1 Correctness

- ❗ Treat warnings as errors; fix root causes. Do not hide a warning without user approval. When a warning is suppressed, always use the loaded tech skill’s **suppression template** (rule id, reason, scope). A suppression without a reason is incomplete.
- ❗ Never fail silently; return a meaningful error (throw or panic only for bugs and broken invariants).
- ❗ Validate external input at trust boundaries: content, structure, type, range, encoding, size.
- ❗ Agree and record trust-boundary **limits** per Section 4.2.
- ❗ Validate function parameters at entry.
- ❗ Expected failures use result types, error codes, or language-idiomatic try-APIs — at public boundaries and always on hot paths. Throw or panic only for bugs and broken invariants.
- List every affected file before any edit: implementation, call sites, tests, config, docs.
- Keep cross-file changes consistent.
- Never leave invalid or undefined state after errors; use atomic update, rollback, or compensation.
- Evaluate return values that may indicate failure. Never omit return values.
- Document omitted parameter validation in the public API docs with reason and caller guarantees.
- Preserve preconditions, postconditions, and interface consistency.
- Never ship incomplete implementations. A `// TODO:` with a concrete reason is allowed only between plan steps of an active `/implement` run. Any `TODO` in a reviewed scope is an Error in `/review` and blocks release.
- Never put plan IDs, issue IDs, `REQ-<AREA>-<TOPIC>`, `TEST-<AREA>-<TOPIC>`, or tracking IDs in code or comments.
- Guard against off-by-one errors, invalid transitions, and logic regressions.
- ❗ Assess integer ops for overflow/underflow; guard when wrap-around would break correctness, security, or invariants. Mechanism: loaded tech skill.

### 4.2 Security

- ❗ Do not violate OWASP Top 10.
- ❗ Never trust caller parameters, URLs, bindings, or payloads without validation.
- ❗ Never log secrets, credentials, tokens, or PII.
- Enumerate STRIDE threats in planning for every external-input feature.
- After external-input implementation, verify boundary validation and STRIDE mitigations.
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
- ❗ Identify race, TOCTOU, async interleaving, lock inversion, and partial-state risks in design.
- After shared-state changes, verify no new concurrency defects.
- ❗ Choose the cheapest primitive that is correct. Context switches and kernel waits are expensive; design so threads rarely block. Order of preference:
  1. No shared mutation: ownership transfer, per-thread or per-task state, immutable snapshots.
  2. Atomics for a single word (counter, flag, pointer swap).
  3. One-time initialization primitives for lazily built shared state.
  4. A lock with a short critical section: no I/O, no allocation, no await, no callback while held. Prefer a reader-writer lock only when reads dominate and the section is long enough to pay for it.
  5. Blocking hand-off (channels, queues, semaphores) only where threads must wait by design.
  Mechanism and concrete types: loaded tech skill.
- Document chosen lock or atomic primitive and rationale when synchronization is required.
- Document thread-safety for non-immutable shared types. Form: loaded tech skill.
- Prove thread-safety claims with concurrent tests.
- Synchronization APIs: loaded tech skill.

### 4.4 Performance

Performance is a feature of every application and library, not optional polish. Design for it from the first plan step. Language APIs, GC, SIMD, and pools: **loaded tech skill**.

- ❗ Minimize allocations on every path. Each allocation is garbage the GC or allocator must reclaim, and any of them can trigger a collection that stalls the process. Allocate only what the result needs.
- ❗ Hot paths (per-item / per-byte loops after setup, named in the plan or measured) allocate nothing.
- ❗ Read and write files with stream, reader, or incremental APIs. Do not load an unbounded file into a single string or byte array. Keep a bounded buffer. Mechanism: loaded tech skill.
- Hot-path expected failures: Section 4.1 (result types / try-APIs, not throw/panic).
- Prefer stack and reuse over heap; prefer locality and a stable layout over chasing pointers.
- Prefer abstractions at boundaries and concrete, inlinable work in the loop.

### 4.5 Testing

- ❗ Require tests before release for every public or internal API.
- Cover happy path, errors, boundaries, concurrency, and security. Exhaustive means those **behavior classes**, not iterating every integer value.
- Keep tests fast: each unit test finishes in ≤ 100 ms. Slower tests carry the stack’s slow or integration marker. No sleeps. No network in unit tests.
- ❗ Minimize test execution time on every stack. When a full run is not required, run only the tests that cover the change. .NET tests use `dotnet test`. Mechanism: `tech-test.md`.
- Test **strategy**: `tech-test.md`. Test **stack**: loaded tech skill.
- In planning / Grill Me, lock test **content** as `TEST-<AREA>-<TOPIC>`
  (important scenarios, edges, constellations, contradictions, gaps). The ID
  names the case the way `REQ-<AREA>-<TOPIC>` names a requirement. That
  content is the minimum to prove. Extra tests at implement time are allowed.
  Schema: `workflow-plan.md`. Strategy: `tech-test.md`.

### 4.6 Documentation

- ❗ Non-temporary documents are **English**: plans, requirements, briefings, reviews, councils, README, guides, architecture docs, illustrations that stay in the repo, public API docs. Chat shall follow the user’s language.
- ❗ Use inline comments for purpose, motivation, caveats, design decisions, and security trade-offs (Section 4.2). State why; never restate obvious syntax.
- Split non-trivial function bodies into semantic blocks. Separate blocks with one blank line (two allowed). Lead each block with a comment that states intent and what to watch for.
- Keep comments, XML/rustdoc/docstrings, README, guides, and architecture docs synchronized with code.
- Document every public API item in the language’s canonical doc form. Form: loaded tech skill.
- Document key algorithm and data-structure decisions.
- Markdown tables: keep them narrow (about 3–4 columns, short cells). Mermaid: `TD`, tall layout, short labels — not wide `LR` graphs. Assume the reader has several panes open.
- ❗ File references are **clickable Markdown links**. The target is relative to the
  file that contains the link, with forward slashes, so the link opens. Form from
  `plans/` or `reviews/`: [`src/Foo.cs`](../src/Foo.cs). Bare backticks, absolute
  `E:\` / `/Users/` paths, and unlinked names are not enough. Especially plans,
  briefings, and reviews; also requirements, councils, and README. Fenced commands
  may keep a raw path so it stays copy-pasteable. In chat, link workspace files
  from repo root: [`src/Foo.cs`](src/Foo.cs).

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
- Use Mermaid (`TD`, tall layout) instead of ASCII art.

### 4.8 UI

These bullets apply to **product** web UIs (app pages, Blazor, HTML/CSS in a
product). They do **not** apply to `/illustrate` HTML. Do not load `tech-web.md`
for `illustrations/**`.

- Every interactive control has an accessible name and state. Add `aria-*` only when visible text does not already provide them (`tech-web.md`).
- ❗ Product web UIs must be responsive (phone-first, 320px floor, no page-level
  horizontal scroll) unless the user explicitly asks otherwise. Locked
  breakpoints, CSS layers, and JS vs CSS: `tech-web.md`.
- Keep one visual language across product pages. Shared CSS for app/layout look;
  isolate only component-specific rules. Weigh each new rule: app-wide,
  layout/page, or this component. Mechanism: `tech-web.md` (stack extras:
  `tech-blazor.md`).
- Ship **dark mode** on product UIs. Other themes are allowed but **out of
  scope** unless the user asks.
- Plan Playwright journeys for product web UIs from the first plan, for tests
  **and** debugging. Playwright may verify HTML illustrations at create time
  (`workflow-illustrate.md`). The shipped zip does not depend on Playwright.
  Illustrations are not required to be responsive.
- ❗ Use the integrated browser on a running product web UI while implementing,
  verifying, and debugging it: open the page, navigate, click, type, read what
  rendered, check the console. **Cursor:** the agent Browser. **VS Code
  (Copilot):** Integrated Browser. **Claude Code:** the desktop app browser
  pane; in the CLI and the VS Code extension, the Claude in Chrome browser tools
  when connected. When the session exposes no browser tool, say so once and use
  headed Playwright or a trace. It never replaces Playwright journeys.
  Mechanism: `tech-playwright.md`.

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
- Prefer types and states that encode invariants instead of primitive flags or ambiguous combinations.
- Expected failures at public boundaries: Section 4.1 (result types / try-APIs).
- Enumerate misuse and abuse vectors in planning (how can the solution be used wrongly or exploited).
- Prefer making invalid states unrepresentable over runtime validation alone when cost is reasonable.
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
- When `Verify` fails: reproduce with that command, then debug, then change code. Do not redesign on the first red run.
- For web UI, use the integrated browser (Section 4.8) alongside a Playwright trace, screenshot, or headed debug.
- Consider concurrent-agent collision before treating a failure as a product defect.
- After two failed remediations of the same root cause: stop and report a blocker (Section 4.16).
- ❗ Profile, trace, and debug without administrator, root, or sudo rights, and without changing kernel or system settings (no UAC prompt, no `perf_event_paranoid` change, no `wsl -u root`). Tools that need them are out: ETW kernel sessions (xperf, WPR, samply on Windows), Linux `perf` or samply when `perf_event_paranoid` > 1. Use the no-rights method in the loaded tech skill.

### 4.16 Blocker reports

A blocker stops a plan, step, finding, review verdict, loop, or port unit. Write
every blocker — plan Open Questions, review **Release** `Blocked by`, implement
and loop stops, Exam **Does not hold**, port `BL-*` records — so a person can
decide without reconstructing it:

- **What blocks:** the concrete defect, missing fact, or failed check; its ID and
  a clickable location (Section 4.6).
- **Evidence:** command and output, file and symbol, or quoted source.
- **Impact:** what fails, for whom, and what ships wrong if it is ignored or
  worked around (users, data, security, performance, release).
- **Scope:** what is blocked and what can still continue.
- **To unblock:** the decision, fact, or fix needed; the options with their
  consequences; who must act.

A blocker that only names an ID or says “blocked” is incomplete.

## 5) Status Legend

- `✅` Complete / Fixed
- `❌` Error / Failed
- `⚠️` At risk / Blocked
- `⬜` Not started / Open
- Tick every plan status surface together: Step Overview, Shared Block, Task Checklist. Never leave the overview stale.
- Tick every review finding status surface together: the Summary **Findings**
  Status column and the finding Shared Block `Status` line. Never leave the
  summary table stale.

## 5.1 Terms

| Term | Meaning |
|------|---------|
| Grill Me | One round of user-fact questions. Not a council. |
| `C{n}` | A recorded choice between options that could not all hold. |
| Shared Block | The field template for a plan step or review finding. |
| Step `{n}R` | The `/review` of Step `{n}`. Same as “Step NR” with N = `{n}`. |
| `REQ-<AREA>-<TOPIC>` | One atomic requirement. The ID names the subject. Stays out of product code. |
| `TEST-<AREA>-<TOPIC>` | Plan test content. The ID names the case. Not a file or method name. Stays out of product code. |
| MTP | Microsoft.Testing.Platform (`global.json` `test.runner`). |
| Finding IDs | `E{n}` Error · `S{n}` Cosmetic (style) · `R{n}` Refactoring · `P{n}` Performance. Never `C{n}`. |
| Done-when | A runnable check on a requirement: actor, boundary action, observable. |
| Context Anchor | The plan’s summary block: loaded skills, Perspective Sweep, coverage tables, decision count. |
| Sweep | Short multi-view pass inside `/plan` (five views) or `/review` (Skeptic and Outsider). No file. |
| Exam | Council teardown of a built result after `/implement`. Verdict **Holds** or **Does not hold** (loop status `exam-fail`). |
| Kill shot | A flaw that makes the result fail a requirement or a ❗ rule. Forces **Does not hold**. |
| Briefing | `reviews/briefing_<slug>.md`: the human packet of what `/implement` shipped. |
| Hot path | A per-item or per-byte loop after setup (Section 4.4). |
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
| `/port-dotnet-rust` | `workflow-port-dotnet-rust.md` |
| `/port-dotnet-rust-loop` | `workflow-port-dotnet-rust-loop.md` |

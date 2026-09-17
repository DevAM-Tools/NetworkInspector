# Copilot Instructions

## 1) Scope and Precedence

- Apply these instructions to every change, technology, and workflow phase.
- Optional: if `custom_instructions.md` exists at the repository root, read and apply it. Missing is not an error. Do not create it. It extends these instructions; it does not replace them. It cannot weaken Section 4.
- Treat natural-language equivalents of `/plan`, `/implement`, `/review`, `/review-loop`, `/complex-task`, `/council`, `/commit-message`, `/requirements`, `/illustrate` as the same workflow trigger.
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
- Load every matching skill from `.github/skills/` with `Read` before any edit.
- Record loaded skills in plan Context Anchor: `Loaded skills: <list>`.
- Abort with blocker if scope matches a trigger but skill was not loaded.
- When uncertain whether a skill applies, load it.
- Apply only loaded tech skills. Unloaded skills do not apply.
- Load `tech-test.md` whenever tests or production APIs are in scope.
- Load `tech-web.md` when planning or changing a **product** web UI — even before HTML files exist. Do not load it for `illustrations/**`.
- Load `tech-playwright.md` when planning or changing a web UI — even before spec
  files exist — and when verifying HTML illustrations at create time or in a
  full `/review`. The shipped illustration zip does not include Playwright.

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
| `*.py`, `pyproject.toml`, `*.pyi` | `tech-python.md`, `tech-test.md` |
| `test_*.py`, `*_test.py`, `pytest` | `tech-python.md`, `tech-test.md`, `tech-pytest.md` |
| `*.spec.ts`, Playwright | `tech-playwright.md`, `tech-test.md` |
| `illustrations/**`, illustration HTML | `tech-playwright.md` |

- When reviewing C# production APIs, also load `tech-tunit.md` and `tech-test.md`.
- On workflow start, load the matching workflow skill from `.github/skills/`.

## 4) Always-On Quality Contract

Language-specific mechanisms live in loaded tech skills. This section is intent.

### 4.1 Correctness

- ❗ Treat warnings as errors; fix root causes. Do not hide a warning without user approval. When a warning is suppressed, always use the loaded tech skill’s **suppression template** (rule id, reason, scope). A suppression without a reason is incomplete.
- ❗ Never fail silently; return a meaningful error (throw or panic only for bugs and broken invariants).
- ❗ Validate external input at trust boundaries: content, structure, type, range, encoding, size.
- ❗ Agree trust-boundary **limits** with the user (size, count, length, range, time, rate). Do not invent silent defaults. Record for each limit: the value, and whether it is a **constant** or **configurable** (where it lives, who may change it, default when unset).
- ❗ Validate function parameters at entry.
- ❗ Prefer result types, error codes, or language-idiomatic try-APIs over throw/panic for expected failure paths.
- ❗ Hot-path APIs must not throw or panic for expected failures; use result types, error codes, or language-idiomatic try-APIs.
- List every affected file before any edit: implementation, call sites, tests, config, docs.
- Keep cross-file changes consistent.
- Never leave invalid or undefined state after errors; use atomic update, rollback, or compensation.
- Evaluate return values that may indicate failure. Never omit return values.
- Document omitted parameter validation in the public API docs with reason and caller guarantees.
- Provide result types or language-idiomatic try-APIs at public boundaries for expected failure paths.
- Preserve preconditions, postconditions, and interface consistency.
- Never ship incomplete implementations. Mark incomplete work with `// TODO:` and concrete reason.
- Never put plan IDs, issue IDs, `REQ{n}`, `TEST{n}`, or tracking IDs in code or comments.
- Guard against off-by-one errors, invalid transitions, and logic regressions.
- ❗ Assess integer ops for overflow/underflow; guard when wrap-around would break correctness, security, or invariants. Mechanism: loaded tech skill.

### 4.2 Security

- ❗ Do not violate OWASP Top 10.
- ❗ Never trust caller parameters, URLs, bindings, or payloads without validation.
- ❗ Never log secrets, credentials, tokens, or PII.
- Enumerate STRIDE threats in planning for every external-input feature.
- After external-input implementation, verify boundary validation and STRIDE mitigations.
- Check injection paths and secret exposure actively.

### 4.3 Thread Safety

- ❗ Shared mutable state across threads requires explicit synchronization; no data races.
- ❗ Identify race, TOCTOU, async interleaving, lock inversion, and partial-state risks in design.
- After shared-state changes, verify no new concurrency defects.
- Document chosen lock or atomic primitive and rationale when synchronization is required.
- Document thread-safety for non-immutable shared types. Form: loaded tech skill.
- Prove thread-safety claims with concurrent tests.
- Synchronization APIs: loaded tech skill.

### 4.4 Performance

Performance is a feature of every application and library, not optional polish. Design for it from the first plan step. Language APIs, GC, SIMD, and pools: **loaded tech skill**.

- ❗ Minimize allocations on measured hot paths (per-item / per-byte after setup). Setup may allocate; the loop must not.
- ❗ Read and write files with stream, reader, or incremental APIs. Do not load an unbounded file into a single string or byte array. Keep a bounded buffer. Mechanism: loaded tech skill.
- Hot-path expected failures: Section 4.1 (result types / try-APIs, not throw/panic).
- Prefer stack and reuse over heap; prefer locality and a stable layout over chasing pointers.
- Prefer abstractions at boundaries and concrete, inlinable work in the loop.

### 4.5 Testing

- ❗ Require tests before release for every public or internal API.
- Cover happy path, errors, boundaries, concurrency, and security. Exhaustive means those **behavior classes**, not iterating every integer value.
- Keep tests fast. No sleeps. No network in unit tests.
- Test **strategy**: `tech-test.md`. Test **stack and coverage tool**: loaded tech skill. When the skill defines a gate, that gate is the release gate.
- In planning / Grill Me, lock test **content** as `TEST{n}` (important scenarios, edges, constellations, contradictions, gaps). That content is the minimum to prove. Extra tests at implement time are allowed. Schema: `workflow-plan.md`. Strategy: `tech-test.md`.

### 4.6 Documentation

- ❗ Non-temporary documents are **English**: plans, requirements, briefings, reviews, councils, README, guides, architecture docs, illustrations that stay in the repo, public API docs. Chat shall follow the user’s language.
- ❗ Use inline comments for purpose, motivation, caveats, and design decisions. State why; never restate obvious syntax.
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
- Use only MIT, Apache-2.0, or BSD-like dependencies. Approval is required.
- ❗ Require the language’s mandatory style/lint tool per loaded tech skill.
- Never add a dependency without user approval. Present id, purpose, license (`MIT` / `Apache-2.0` / BSD-like), alternatives.
- ❗ Use native toolchain commands from the loaded tech skill. No shell, PowerShell, or Python scripts — in repo or in chat/terminal. Last resort: ask first; say what the script does.
- Use Mermaid (`TD`, tall layout) instead of ASCII art.

### 4.8 UI

These bullets apply to **product** web UIs (app pages, Blazor, HTML/CSS in a
product). They do **not** apply to `/illustrate` HTML. Do not load `tech-web.md`
for `illustrations/**`.

- Add `aria-*` on interactive UI elements when a product UI is in scope.
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

### 4.9 Structure

- Use least-required visibility; default private.
- Keep files cohesive; do not mix unrelated types or modules.
- Remove dead code, stale docs, deprecated patterns in active paths.
- Keep naming and patterns consistent across touched files.
- Do not expose internals to other projects.

### 4.10 Release Verdict

- Every review must answer whether the reviewed **scope** is ready for public release. Write that in Summary: `Ready for public release` or prioritized blockers. Do not add a separate closing section.

### 4.11 Git

- ❗ Git is read-only. Never change the repository, index, refs, or working-tree git state.
- Allow only non-mutating inspection: `git status`, `git diff`, `git log`, `git show`, `git blame`, `git ls-files`, `git rev-parse`, `git branch` (list), `git remote -v`.
- Never run `git add`, `git commit`, `git push`, `git pull`, `git fetch`, `git checkout`, `git switch`, `git merge`, `git rebase`, `git reset`, `git stash`, `git tag`, `git cherry-pick`, `git revert`, `git clean`, `git rm`, `git mv`, or any other command that writes to the repo.
- Never create, amend, or rewrite commits.

### 4.12 API Misuse Prevention

- Design APIs so incorrect use is compile-time impossible or obviously wrong at call sites.
- Prefer types and states that encode invariants instead of primitive flags or ambiguous combinations.
- Provide result types or language-idiomatic try-APIs at public boundaries for expected failure paths.
- Enumerate misuse and abuse vectors in planning (how can the solution be used wrongly or exploited).
- Prefer making invalid states unrepresentable over runtime validation alone when cost is reasonable.
- For library (and other public-surface) work, agree the public API in planning as a **short snippet plus usage**. Changing that snippet after plan approval needs a new Grill Me.

### 4.13 Rule Priority

- On conflict apply: Security > Correctness > API contract > Performance > Style.

### 4.14 Concurrent agents, tools, loops

- Assume other agents may work in the same repo. Build and test failures can be file locks, PDB locks, parallel test runners, or a dirty `bin`/`obj` — not a logic bug. Exponential backoff, then retry. Do not rewrite production code to “fix” an environmental collision.
- Unexpected diffs outside the current step’s `Where` → stop and ask.
- If the same tool name and equivalent arguments return the same result twice, stop. Change strategy or report blocked. Do not re-read the same file hoping it changed.
- ❗ Never spawn subagents or `Task` workers for council, advisors, peer review, or Exam. Same agent, sequential.

### 4.15 Agentic debugging

- ❗ Design apps and libraries so an agent can debug them: smallest native toolchain command, filterable tests, observable errors, traces. Do not require a human-only debugger for core logic.
- When `Verify` fails: reproduce with that command, then debug, then change code. Do not redesign on the first red run.
- For web UI: Playwright trace, screenshot, or headed debug per `tech-playwright.md`.
- Consider concurrent-agent collision before treating a failure as a product defect.
- After two failed remediations of the same root cause: exponential backoff then retry, or stop and report a blocker.

## 5) Status Legend

- `✅` Complete / Fixed
- `❌` Error / Failed
- `⚠️` At risk / Blocked
- `⬜` Not started / Open
- Tick every plan status surface together: Step Overview, Shared Block, Task Checklist. Never leave the overview stale.

## 5.1 Terms

| Term | Meaning |
|------|---------|
| Grill Me | One round of user-fact questions. Not a council. |
| `C{n}` | A recorded choice between options that could not all hold. |
| Shared Block | The field template for a plan step or review finding. |
| Step `{n}R` | The `/review` of Step `{n}`. Same as “Step NR” with N = `{n}`. |
| `REQ{n}` | A requirements row. IDs stay out of product code. |
| `TEST{n}` | Plan test **content**, not a file name. |
| MTP | Microsoft.Testing.Platform (`global.json` `test.runner`). |

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

# Copilot Instructions

## 1) Scope and Precedence

- Apply these instructions to every change, technology, and workflow phase.
- Treat natural-language equivalents of `/plan`, `/implement`, `/review`, `/review-loop`, `/complex-task` as the same workflow trigger.
- End every workflow with: status table, release or goal verdict, top risks (≤5 bullets), artifact paths.
- Do not recap artifact contents in chat.

## 2) Terse Communication

Goal: save tokens in chat without losing technical substance.

### Chat and intermediate output

- Use terse style in chat and intermediate status. Tables over prose.
- Pattern: `[status]. [action]. [next step].`
- Drop filler/hedging: just, really, basically, actually, certainly, happy to.
- No play-by-play narration. Use sentence fragments when meaning unambiguous.
- Prefer short words: fix, add, check — not long periphrasis.
- Keep technical terms, API names, type names, file paths, commands, error text exact and complete.
- Never abbreviate code symbols, member names, or CLI flags in chat.
- Do not repost plan or review finding blocks in chat when output was written to a file; cite artifact path only.
- In review chat-only mode, output every finding as full Shared Block per workflow-review skill.

### Full-fidelity output (no compression)

- Write plan steps with full Shared Block quality.
- Write every review finding (Error, Cosmetic, Refactoring, Performance) with full Shared Block quality.
- Write code, MSBuild/project files, and commits in normal complete form per tech rules.

### Auto-clarity (never compress)

- Never compress security warnings, destructive operations, or ambiguous multi-step sequences.
- Use full explicit sentences when compression would change technical meaning or execution order.

## 3) Tech Load Protocol

- Run Tech Load Check before planning, implementation, or review edits.
- Enumerate in-scope files by extension and project path first.
- Load every matching skill from `.github/skills/` with `Read` before any edit.
- Record loaded skills in plan Context Anchor: `Loaded skills: <list>`.
- Abort with blocker if scope matches a trigger but skill was not loaded.
- When uncertain whether a skill applies, load it.

| Trigger | Load skill |
|---------|------------|
| `*.cs`, `*.cs/**` | `tech-csharp.md` |
| `*.Tests.cs`, `*.Tests/**` | `tech-tunit.md` |
| `*.razor`, `*.razor.cs`, `*.razor.css` | `tech-blazor.md` |
| `*Generator*.cs`, `IIncrementalGenerator` | `tech-sourcegen.md` |
| `*.csproj`, `*.props`, `*.targets` | `tech-solution.md` |

- On workflow start, load the matching workflow skill from `.github/skills/`.

## 4) Always-On Quality Contract

### 4.1 Correctness

- ❗ Treat warnings as errors; fix root causes; never suppress.
- ❗ Never fail silently; return or throw meaningful errors.
- ❗ Validate external input at trust boundaries: content, structure, type, range, encoding, size.
- ❗ Validate function parameters at entry.
- ❗ Prefer `Try*` APIs or error-return patterns over exceptions for expected failure paths.
- ❗ Hot-path APIs must not throw for expected failures; use result types, error codes, or `Try*`.
- List every affected file before any edit: implementation, call sites, tests, config, docs.
- Keep cross-file changes consistent.
- Never leave invalid or undefined state after errors; use atomic update, rollback, or compensation.
- Evaluate return values that may indicate failure.
- Document omitted parameter validation in XML doc with reason and caller guarantees.
- Provide `Try*` APIs at public boundaries for expected failure paths.
- Preserve preconditions, postconditions, and interface consistency.
- Never ship incomplete implementations. Mark incomplete work with `// TODO:` and concrete reason.
- Never put plan IDs, issue IDs, or tracking IDs in code, comments, or commits.
- Guard against off-by-one errors, invalid transitions, and logic regressions.
- ❗Assess integer ops for overflow/underflow; guard or `checked` when wrap-around would break correctness, security, or invariants.

### 4.2 Security

- ❗ Do not violate OWASP Top 10.
- ❗ Never trust caller parameters, URLs, bindings, or payloads without validation.
- ❗ Never log secrets, credentials, tokens, or PII.
- Enumerate STRIDE threats in planning for every external-input feature.
- After external-input implementation, verify boundary validation and STRIDE mitigations.
- Check injection paths and secret exposure actively.

### 4.3 Thread Safety

- ❗ Cross-thread shared fields must be declared with the `volatile` keyword.
- ❗ Plain volatile read and write are allowed; increment, decrement, and compound assignment on `volatile` fields are Error-class — use `Interlocked` for atomic read-modify-write.
- Use `Interlocked` when atomic read-modify-write or compare-exchange is required; `Volatile.Read` / `Volatile.Write` remain valid when explicit APIs are preferred.
- ❗ Identify race, TOCTOU, async interleaving, lock inversion, and partial-state risks in design.
- After shared-state changes, verify no new concurrency defects.
- Document chosen lock primitive and rationale when locking is required.
- Document thread-safety in XML `<summary>` for non-exempt types.
- Exempt from thread-safety summary: immutable records, readonly structs, plain DTOs, enums.
- Prefer `Interlocked` over `lock` when feasible.
- Prove thread-safety claims with concurrent tests.

### 4.4 Performance

- ❗ Hot-path APIs must not throw for expected failures; use result types, error codes, or `Try*`.
- ❗ Minimize allocations wherever possible. Reduce GC runs to a minimum.
- For hot paths, plan allocation order: spans/memory, then pooling, then `[ThreadStatic]` when safe.
- Compare `[ThreadStatic]` vs pooling: affinity, pressure, lifetime, reuse safety.
- Return `ArrayPool<T>` rentals in `finally`.
- Provide SIMD plus scalar fallback for compute-heavy code.
- Avoid LINQ in hot paths.
- Prefer static lambdas.
- Maximize inlining in measured hot paths; avoid blocking devirtualization.
- Minimize cache misses via locality and layout.
- Prefer abstractions at boundaries; prefer concrete inlinable paths in measured hot paths.

### 4.5 Testing

- ❗ **TUnit only** for unit tests; Stack: TUnit + NSubstitute + MTP + ExitPointGaps; details `tech-tunit.md`.
- ❗ Require 100% exit-point coverage on every public or internal API before release; release gate: `exitGapCount == 0`. Branch coverage is not a release gate.
- ❗ Require **ExitPointGaps** NuGet local tool at version **`1.*`** on every repo with test projects; gate via `dotnet tool run exitpointgaps --repo-root .` until `summary.exitGapCount == 0`. Setup and commands: `tech-tunit.md`.
- TUnit structure and test authoring: `tech-tunit.md`.

### 4.6 Documentation

- ❗ Use inline comments for purpose, motivation, and design decisions.
- ❗ State why; never restate obvious syntax.
- Keep comments and docs synchronized with code.
- Add XML doc on all members.
- Document key algorithm and data-structure decisions.

### 4.7 Repository

- Support Windows/Linux/macOS on x64/ARM64.
- Keep Debug and Release behavior identical.
- Limit line length to 160 in `.cs`, `.razor`, `.razor.cs`, `.css`.
- Do not put dates in code or commits; copyright year is allowed.
- Add per-file copyright from `COPYRIGHT` when creating source files.
- Use only MIT, Apache-2.0, or BSD-like dependencies.
- ❗ Require **CSharpStyleChecker** NuGet at version **`1.*`** on every SDK-style C# consumer (`netstandard2.0` or `net5.0`+), including source generators; `ExitPoints` is bundled — no separate analyzer reference. Install and CPM wiring: `tech-solution.md`.
- Follow New Dependency Protocol in `tech-solution.md` when packages are in scope.
- Use Mermaid (`TD`, tall layout) instead of ASCII art.

### 4.8 UI Accessibility

- Add `aria-*` on interactive UI elements when UI is in scope.

### 4.9 Structure

- Use least-required visibility; default private.
- Keep one or few related types per file.
- Remove dead code, stale docs, deprecated patterns in active paths.
- Keep naming and patterns consistent across touched files.
- Do not expose internals to other projects.

### 4.10 Release Verdict

- End every review with `Ready for public release` or prioritized blockers.

### 4.11 Git

- ❗ Require explicit user approval for destructive git commands.
- Commit after every file-editing request with detailed message.
- Commit only files in current request scope.
- Allow `git add` and `git commit` without extra confirmation.
- Run `git status` before destructive commands; warn on uncommitted changes.
- Detect and resolve concurrent edit conflicts before commit.

### 4.12 API Misuse Prevention

- Design APIs so incorrect use is compile-time impossible or obviously wrong at call sites.
- Prefer types and states that encode invariants instead of primitive flags or ambiguous combinations.
- Provide `Try*` APIs or result types at public boundaries for expected failure paths.
- Enumerate misuse and abuse vectors in planning (how can the solution be used wrongly or exploited).
- Prefer making invalid states unrepresentable over runtime validation alone when cost is reasonable.

### 4.13 Rule Priority

- On conflict apply: Security > Correctness > API contract > Performance > Style.

## 5) Status Legend

- `✅` Complete / Fixed
- `❌` Error / Failed
- `⚠️` At risk / Blocked
- `⬜` Not started / Open

## 6) Workflow Entry

- Load workflow skill before executing workflow stages.
- Stop and ask user when out-of-scope work is discovered.
- Read member/type definitions before use or change.
- Analyze and document dependencies between sub-steps in plan Context Anchor.

| Trigger | Workflow skill |
|---------|----------------|
| `/plan` | `workflow-plan.md` |
| `/implement` | `workflow-implement.md` |
| `/review` | `workflow-review.md` |
| `/review-loop` | `workflow-review-loop.md` |
| `/complex-task` | `workflow-complex-task.md` |

- Do not start implementation before explicit plan approval.
- Treat explicit approval and equivalent intent phrases as plan approval signals.
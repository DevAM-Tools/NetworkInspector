# Copilot Instructions

## 1) Scope and Precedence

- Apply these instructions to every change, technology, and workflow phase.
- Optional: if `custom_instructions.md` exists at the repository root, read and apply it. Missing is not an error. Do not create it. It extends these instructions; it does not replace them. It cannot weaken Section 4.
- Treat natural-language equivalents of `/plan`, `/implement`, `/review`, `/review-loop`, `/complex-task`, `/council`, `/commit-message` as the same workflow trigger.
- End every workflow with: status table, release or goal verdict, top risks (≤5 bullets), artifact paths.
- Do not recap artifact contents in chat. Exception: council verdict sections.

## 2) Terse Communication

Goal: save tokens in chat without losing technical substance.

### Chat and intermediate output

- Use terse style in chat and intermediate status. Tables over prose.
- Pattern: `[status]. [action]. [next step].`
- Drop filler/hedging: just, really, basically, actually, certainly, happy to.
- No play-by-play narration. Use sentence fragments when meaning unambiguous.
- Prefer short words: fix, add, check — not long periphrasis.
- Keep technical terms, API names, type names, file paths, commands, error text exact and complete.
- Never abbreviate code symbols, item names, or CLI flags in chat.
- Do not repost plan or review finding blocks in chat when output was written to a file; cite artifact path only.
- In review chat-only mode, output every finding as full Shared Block per workflow-review skill.

### Full-fidelity output (no compression)

- Never apply chat terse style to plan steps, plan Requirements, or review findings.
- Write plan steps and review findings with full Shared Block quality.
- Write plan Requirements as user-observable outcomes with a Done-when check. Not slogans. Not implementation tasks.
- ❗ Specify the concrete implementation in every plan-step and review-finding `How`. Intent-only, outline-only, or slogan-only `How` is incomplete — expand before emitting the artifact.
- ❗ Write `How` so another agent can implement without inventing types, items, signatures, algorithms, control flow, or file structure.
- ❗ Name types, items, visibility, signatures, parameters, return values, call-site edits, validation, error paths, and state changes.
- ❗ Describe the chosen solution in full, not only the goal. Expand until one implementation is uniquely specified.
- Write council verdict sections at full fidelity in chat and artifact. Advisor essays stay in the artifact.
- Put fenced code examples in every plan-step and review-finding `How`. Use Before/After for steps; Problem/Fix for findings. Anchor with path/symbol. Show real signatures and key bodies — not stubs, pseudocode-only, or comments-as-code. Exception: Requirements-fit may skip Before/After unless a gap needs a code fix.
- Name non-trivial test coverage in every plan step: behaviors, error paths, boundaries, concurrency, security. Do not list only test file names.
- Cite a concrete source in every plan-step and review-finding `Context` when an external reference exists: URL, official doc title + section, API reference, RFC. Else cite skill/ADR/path/symbol.
- Write code and build/project files in normal complete form per loaded tech rules.

### Auto-clarity (never compress)

- Never compress security warnings, destructive operations, or ambiguous multi-step sequences.
- Never compress plan-step or review-finding `How`.
- Never compress plan Requirements or the Requirements-fit step.
- Use full explicit sentences when compression would change technical meaning or execution order.

## 3) Tech Load Protocol

- Run Tech Load Check before planning, implementation, or review edits.
- Enumerate in-scope files by extension and project path first.
- Load every matching skill from `.github/skills/` with `Read` before any edit.
- Record loaded skills in plan Context Anchor: `Loaded skills: <list>`.
- Abort with blocker if scope matches a trigger but skill was not loaded.
- When uncertain whether a skill applies, load it.
- Apply only loaded tech skills. Unloaded skills do not apply.

| Trigger | Load skill |
|---------|------------|
| `*.cs`, `*.cs/**` | `tech-csharp.md` |
| `*.Tests.cs`, `*.Tests/**` | `tech-tunit.md` |
| `*.razor`, `*.razor.cs`, `*.razor.css` | `tech-blazor.md` |
| `*Generator*.cs`, `IIncrementalGenerator` | `tech-sourcegen.md` |
| `*.csproj`, `*.props`, `*.targets` | `tech-solution.md` |
| `*.rs`, `Cargo.toml`, `Cargo.lock` | `tech-rust.md` |

- When reviewing C# production APIs, also load `tech-tunit.md`.
- On workflow start, load the matching workflow skill from `.github/skills/`.

## 4) Always-On Quality Contract

Language-specific mechanisms live in loaded tech skills. This section is intent.

### 4.1 Correctness

- ❗ Treat warnings as errors; fix root causes; never suppress.
- ❗ Never fail silently; return a meaningful error (throw or panic only for bugs and broken invariants).
- ❗ Validate external input at trust boundaries: content, structure, type, range, encoding, size.
- ❗ Validate function parameters at entry.
- ❗ Prefer result types, error codes, or language-idiomatic try-APIs over throw/panic for expected failure paths.
- ❗ Hot-path APIs must not throw or panic for expected failures; use result types, error codes, or language-idiomatic try-APIs.
- List every affected file before any edit: implementation, call sites, tests, config, docs.
- Keep cross-file changes consistent.
- Never leave invalid or undefined state after errors; use atomic update, rollback, or compensation.
- Evaluate return values that may indicate failure.
- Document omitted parameter validation in the public API docs with reason and caller guarantees.
- Provide result types or language-idiomatic try-APIs at public boundaries for expected failure paths.
- Preserve preconditions, postconditions, and interface consistency.
- Never ship incomplete implementations. Mark incomplete work with `// TODO:` and concrete reason.
- Never put plan IDs, issue IDs, or tracking IDs in code or comments.
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

- ❗ Hot-path APIs must not throw or panic for expected failures; use result types, error codes, or language-idiomatic try-APIs.
- ❗ Minimize allocations wherever possible.
- For hot paths, plan allocation order per loaded tech skill.
- Provide SIMD plus scalar fallback for compute-heavy code.
- Avoid intermediate allocations and heap-capturing closures on hot paths. Mechanism: loaded tech skill.
- Maximize inlining in measured hot paths.
- Minimize cache misses via locality and layout.
- Prefer abstractions at boundaries; prefer concrete inlinable paths in measured hot paths.

### 4.5 Testing

- ❗ Require tests before release for every public or internal API.
- Cover happy path, errors, boundaries, concurrency, and security.
- ❗ Require 100% exit-path coverage on every public or internal API before release. Branch coverage is not a release gate.
- Test stack and coverage **tool** (if any): loaded tech skill only. When the skill defines a gate, that gate is the release gate. When it does not, every exit path must still have a test.

### 4.6 Documentation

- ❗ Use inline comments for purpose, motivation, and design decisions.
- ❗ State why; never restate obvious syntax.
- Keep comments and docs synchronized with code.
- Document every public API item in the language’s canonical doc form. Form: loaded tech skill.
- Document key algorithm and data-structure decisions.

### 4.7 Repository

- Support Windows/Linux/macOS on x64/ARM64.
- Keep Debug and Release behavior identical.
- Limit line length to 160.
- Do not put dates in code; copyright year is allowed.
- Add per-file copyright from `COPYRIGHT` when creating source files. Syntax: loaded tech skill.
- Use only MIT, Apache-2.0, or BSD-like dependencies.
- ❗ Require the language’s mandatory style/lint tool per loaded tech skill.
- Never add a dependency without user approval. Present id, purpose, license (`MIT` / `Apache-2.0` / BSD-like), alternatives. Stack steps: loaded tech skill New Dependency Protocol.
- Use Mermaid (`TD`, tall layout) instead of ASCII art.

### 4.8 UI Accessibility

- Add `aria-*` on interactive UI elements when UI is in scope.

### 4.9 Structure

- Use least-required visibility; default private.
- Keep files cohesive; do not mix unrelated types or modules.
- Remove dead code, stale docs, deprecated patterns in active paths.
- Keep naming and patterns consistent across touched files.
- Do not expose internals to other projects.

### 4.10 Release Verdict

- End every review with `Ready for public release` or prioritized blockers.

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

### 4.13 Rule Priority

- On conflict apply: Security > Correctness > API contract > Performance > Style.

## 5) Status Legend

- `✅` Complete / Fixed
- `❌` Error / Failed
- `⚠️` At risk / Blocked
- `⬜` Not started / Open
- Tick every plan status surface together: Step Overview, Shared Block, Task Checklist. Never leave the overview stale.

## 6) Workflow Entry

- Load this file, then `custom_instructions.md` when present, then the matching workflow skill, before executing workflow stages.
- Stop and ask user when out-of-scope work is discovered.
- Read definitions of involved types and items before use or change.
- Analyze and document dependencies between sub-steps in plan Context Anchor.

| Trigger | Workflow skill |
|---------|----------------|
| `/plan` | `workflow-plan.md` |
| `/implement` | `workflow-implement.md` |
| `/review` | `workflow-review.md` |
| `/review-loop` | `workflow-review-loop.md` |
| `/complex-task` | `workflow-complex-task.md` |
| `/council` | `workflow-council.md` |
| `/commit-message` | `workflow-commit-message.md` |

- `/council`, `council this`, `run the council`, `pressure-test this`, `stress-test this` → `workflow-council.md`. Not casual "should I". Sweep on every plan/review. Full council on `/council` or a blocking Decision Loop fork (Lite default in plan Decision Loop). Exam council after `/implement` Stage 5 and after complex-task loop success. Exam is not a `/review` substitute.
- `/commit-message`, `/commit_message`, `write a commit message`, `commit message for …` → `workflow-commit-message.md`. Scope required. Write `commit_message.md`.
- Council subagents use the parent model. Cursor `Task`: set `model: inherit`. Never omit `model`. Never pick a cheaper or faster slug.
- Do not start implementation before explicit plan approval.
- Treat explicit approval and equivalent intent phrases as plan approval signals.

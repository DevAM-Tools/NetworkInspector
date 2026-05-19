<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# Copilot Instructions

## Scope

The rules in this file are **mandatory for every change**, regardless of technology or file type.
Supplemental guides in the Coding Style Guides section are equally mandatory for their technology; they extend, not replace, these rules.
If `CUSTOM_INSTRUCTIONS.md` exists at the repository root, read it first; its rules are mandatory and override this file on conflict.

## Global Rules

- Add a copyright notice to every file using the exact text from `COPYRIGHT` at the repository root. Source files (`.cs`, `.razor`, `.razor.cs`, `.css`): `//` comment. Markup/docs (`.md`, `.html`, `.razor`): HTML comment.
- Use only dependencies licensed under MIT, Apache-2.0, or BSD-like licenses.
- Treat warnings as errors; fix the root cause — never suppress.
- Never fail silently; surface errors with meaningful messages.
- Validate all external input (user data, file content, network data) at the system boundary — structure, type, range, encoding, and size. Never trust unvalidated data inside the application.
- Use prechecks to prevent predictable failures from becoming exceptions. Offer `Try*` APIs (`TryParse`, `TryGet`) at public boundaries for expected failure paths; reserve exceptions for truly unrecoverable conditions.
- Mark every incomplete section with `// TODO:` explaining what is missing and why.
- No dates in code or commit messages. Exception: the copyright year in licence notices is permitted.
- Release and debug builds must behave identically; no `#if DEBUG` or `Debug.Assert()` behavioral differences.
- Support Windows, Linux, macOS on x64 and ARM64; no platform-specific behavior without a cross-platform fallback.
- Line length: 160 characters max in `.cs`, `.razor`, `.razor.cs`, `.css` (enforced by `.editorconfig`); exempt in docs and policy files.
- Use Mermaid instead of ASCII art. Layouts: top-down (`TD`), tall rather than wide.
- **Find every affected file before acting.** Before any edit, search the codebase to identify every call site, test, configuration entry, and documentation section that references the symbol or pattern being changed. List all affected files explicitly before making the first edit — nothing should slip through undetected.
- Keep cross-file changes consistent across all affected files.
- Keep docs and comments current with every code change.
- **Comment intent, not mechanics.** Every non-trivial type, method, and algorithm must carry a comment explaining *why* it exists, *what problem* it solves, and *what invariants or constraints* apply — protocol quirks, performance requirements, thread-safety assumptions, or algorithm rationale. For complex logic, add inline *why* comments at non-obvious decision points. Never paraphrase what the code already expresses clearly (`i++; // increment i`); such comments add noise and go stale the moment the code is refactored. Anchor comments to intent and invariants, not to specific variable names, step counts, or current line structure.
- One or a few closely related types per file.
- Use these status indicators in plans, reviews, and task lists: ✅ Complete / Fixed · ❌ Error / Failed · ⚠️ At risk / Blocked · ⬜ Not started / Open.

## Testing

- Every public API requires 100% test coverage before release.
- Test all error paths, edge cases, boundary values, and corner cases — not only the happy path. Corner cases include empty inputs, null inputs, maximum/minimum values, off-by-one conditions, type limits, concurrent access, and any value that straddles a branch boundary.
- Enumerate corner cases explicitly in `[InlineData]`/`[MemberData]` — don't rely on one representative value.
- Tests must be independent, repeatable, and deterministic — no shared mutable state or execution-order dependencies.
- Tests must pass on all supported platforms (Windows, Linux, macOS — x64 and ARM64).
- Language-specific testing conventions are in the applicable style guide.

## Git

- Commit after every file-editing request on `dev`; include all edited files with a detailed message.
- Only `git add` and `git commit` are permitted without confirmation.
- History-rewriting commands (`git reset`, `git rebase`, `git commit --amend`, `git push --force`, `git push --force-with-lease`, `git cherry-pick`, `git revert`, `git clean`, `git stash drop`, `git tag -d`) require explicit user approval.
- Before any destructive command, run `git status` and warn if uncommitted changes exist.
- Be aware that other agents may edit the same files concurrently; check for and resolve conflicts before committing.

## Workflow Invocation

Phase rules apply regardless of how a request is phrased — natural-language equivalents of `/plan`, `/review`, and implement-phase invocations trigger identical workflows.

Every workflow must close with a comprehensive summary: what was done, key decisions and rationale, trade-offs, remaining risks, and next steps. One-liners and checklists are not acceptable.

---

## Phases

### Plan

- Never start implementing before a plan is approved by the user.
- Use the `/plan` prompt.
- Resolve every ambiguity with the user before planning — make no assumptions.
- Favour vertical slicing: each slice must be independently functional and testable.
- **Architectural foundation first**: for multi-slice solutions, define the layer structure (e.g., Presentation, Application, Domain, Infrastructure), shared infrastructure, and cross-cutting patterns as the first plan step. These boundaries are inviolable.
- All slices share the same layer structure; no slice bypasses a layer or references another slice's internals directly. Slices communicate via shared interfaces, events, or mediator only.
- Cross-cutting concerns (logging, auth, validation, error handling, caching) must be designed as shared infrastructure once — never ad hoc per slice.
- Extract any pattern that appears in a second slice to shared infrastructure before duplicating it. Cross-slice duplication is a plan-level defect.
- Write plans so an agent can execute them autonomously without further clarification:
  - Order steps in strict **topological order** — no step may appear before all its declared dependencies are complete.
  - Every step declares its **dependencies**, **output artifact** (files or tests), and **verification gate** (command or observable confirming completion).
  - High-risk steps include an explicit **recovery path** (rollback or repair steps if the step fails).
  - Include a flat **task checklist** at the end; the agent marks items complete as it goes.
  - Include a **context anchor** (brief summary of task, scope, and key constraints) that the agent re-reads at the start of each step.
  - If implementation reveals scope not covered in the approved plan, stop and confirm before continuing.
- Justify non-obvious algorithm and data structure choices.
- For compute-heavy paths, consider SIMD; always include a non-SIMD scalar fallback.
- For hot paths, plan allocation strategy explicitly: prefer `Span<T>`/`Memory<T>`, then pooling (`ArrayPool<T>`, object pools), then `[ThreadStatic]` scratch buffers where thread affinity is safe — document reasoning and buffer lifetime/reuse safety.
- Identify race conditions, TOCTOU, async-interleaving, and partial-state publication risks before proposing any shared-state design.
- For every feature that handles external input or crosses a trust boundary, enumerate STRIDE threats (Spoofing, Tampering, Repudiation, Information Disclosure, Denial of Service, Elevation of Privilege) at those boundaries and document the required mitigations as an explicit plan step before any implementation step for that boundary.

### Review

- Use the `/review` prompt.
- Approach the review critically — assume nothing is correct until verified. Actively challenge design decisions, contracts, naming, and test coverage; do not accept code at face value.
- Define the review scope explicitly before starting.
- Classify every finding in exactly one bucket:
  - **Error** — must be fixed (bug, security vulnerability, broken contract, missing test coverage, silent failure, race condition).
  - **Cosmetic** — optional; improves readability or style without changing behavior (naming, formatting, minor clarity).
  - **Refactoring Opportunity** — no behavior change, but meaningfully improves structure, readability, or maintainability (extract method, decompose class, simplify algorithm).
  - **Performance** — no behavior change, but reduces allocations, improves throughput, or enables optimization in hot paths (e.g., `Span<T>`, pooling, SIMD, avoiding redundant copies).
- Open with total finding counts per category and a verdict; then the detailed findings.
- For hot paths, evaluate `[ThreadStatic]` scratch buffers vs. pooling (`ArrayPool<T>`, object pools); document trade-offs: thread affinity, pool pressure, buffer lifetime, reuse safety.
- Conclude with an explicit **public-release verdict**: either "Ready for public release" or a prioritised list of blockers that must be resolved first.

### Implement

- Before writing or modifying code, read the applicable style guide(s) from the Coding Style Guides section in full — do not rely on memory.
- Follow every rule in the applicable guide(s); they are as binding as the global rules above.
- Each vertical slice must compile, pass all tests, and be committed before the next slice begins.
- After each step, build and run tests before proceeding.
- After every step that handles external input, explicitly verify that all inputs are validated at the boundary (structure, type, range, encoding, size) and that the STRIDE mitigations from the plan are implemented before moving to the next step.
- After every step that touches shared state or concurrent execution paths, explicitly verify that no new data races, TOCTOU windows, or lock-inversion risks have been introduced before moving to the next step.
- If implementation reveals out-of-plan scope, stop and confirm with the user.
- Never embed plan step IDs, task references, issue numbers, or tracking identifiers in code, comments, or commits.

## Coding Style Guides

Each guide is **mandatory** for its technology; all applicable guides apply simultaneously. Read each in full before writing, modifying, or reviewing code in that technology.

| Technology | Guide | Apply when… |
|---|---|---|
| **C#** | [CsharpCodingStyle.md](styles/CsharpCodingStyle.md) | Any `.cs` file is created or modified. |
| **Blazor / Razor** | [BlazorRazorCodingStyle.md](styles/BlazorRazorCodingStyle.md) | Any `.razor` or `.razor.cs` file is created or modified. |

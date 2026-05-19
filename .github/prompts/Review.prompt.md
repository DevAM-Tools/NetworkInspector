---
name: review
description: Review code for errors, quality, and refactoring opportunities
argument-hint: Describe what to review — files, feature area, or PR
agent: agent
---

<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

You are performing a thorough code review. Work through the following stages **in order**.

---

## Stage 1 — Define Scope

Ask the user:

- What is **in scope** for this review (files, features, PR, or area)?
- What is **explicitly excluded**?
- Is there a specific concern or focus area (e.g., security, performance, thread safety)?

If scope was provided as an argument when this prompt was invoked, treat it as confirmed and proceed directly to Stage 2 without asking.

Do not start the review until scope is confirmed.

---

## Stage 2 — Load Style Guides

Identify every technology present in the in-scope files and read the corresponding guide in full before proceeding. Do not rely on memory of a previous session.

| Technology present | Guide to read |
|---|---|
| `.cs` files | `.github/styles/CsharpCodingStyle.md` |
| `.razor` or `.razor.cs` files | `.github/styles/BlazorRazorCodingStyle.md` |

Apply every rule from the loaded guides during Stage 4 with the same weight as the criteria listed there.

---

## Stage 3 — Gather Context

Read all in-scope files, their tests, and any directly related files outside scope needed to evaluate contracts, integrations, and dependencies. Understand the intended behaviour before looking for issues.

---

## Stage 4 — Review

In addition to the criteria below, verify compliance with every rule in the style guides loaded in Stage 2. Violations of style-guide rules are classified using the same Error / Cosmetic / Refactoring / Performance buckets.

Examine the code thoroughly against **all** of the following criteria:

- **Correctness** — logic errors, off-by-one, incorrect state transitions
- **Security** — OWASP Top 10, input validation, untrusted data, injection, exposed secrets
- **Thread safety** — data races, TOCTOU, volatile-field access discipline, lock-free correctness, async-interleaving risks, partial-state publication.
  Specifically: **for every field documented as volatile (XML doc or `// volatile` comment), verify that ALL access sites — both reads and writes — use exclusively `Volatile.Read`, `Volatile.Write`, or `Interlocked`. Any plain field access anywhere is an Error-class defect.**
- **Performance and allocations** — hot-path allocations, missing pooling or `Span<T>`, SIMD opportunities, unnecessary copies
- **Test coverage** — 100 % coverage of all public APIs; test quality and meaningful assertions
- **Documentation accuracy** — comments and XML docs reflect actual behaviour; no stale or misleading text
- **API design and contracts** — preconditions, postconditions, interface consistency
- **Error handling** — no silent failures, no discarded return values, meaningful error messages
- **Cross-platform compatibility** — Windows / Linux / macOS, x64 / ARM64
- **Accessibility** — `aria-*` attributes on interactive UI elements
- **Dead code, legacy, and outdated content** — unused members, unreachable branches, unnecessary complexity; deprecated APIs still in active use; legacy patterns that should be modernised; stale comments, XML docs, or documentation that no longer reflects current behaviour
- **Consistency** — naming, structure, and patterns consistent across all affected files
- **Stubs and TODOs** — every incomplete section must be marked with a `// TODO:` comment; any incomplete code without a `// TODO:` marker is an Error; any `// TODO:` that lacks an explanation of what is missing and why is also an Error
- **Visibility** — least-required accessibility on all types and members
- **No internal identifiers** — code, comments, identifiers, and commit messages must not contain internal plan step IDs, task references, issue numbers, or tracking identifiers of any kind
- **Hot-path memory strategy** — for every hot path, evaluate whether `[ThreadStatic]` scratch buffers or pooling (`ArrayPool<T>`, object pools) would reduce allocations; document trade-offs: thread affinity, pool pressure, buffer lifetime, and security of buffer reuse
- **Release readiness** — explicitly assess whether the code is ready for release; if not, list the exact blockers with remediation order

---

## Stage 5 — Output

Each finding is **self-contained**: an implementer copies a single finding and executes it as a standalone fix prompt — without re-reading other findings or searching the codebase. Write every field with that usage in mind.

Use the structure below. Emit only the sections that have findings; omit empty sections. After all findings are written, produce a **Findings Overview Table** (format and placement described at the end of this stage template).

---

# Summary

**Scope**
<one sentence stating what was reviewed>

**Verdict**
<comprehensive assessment: overall code quality, dominant error themes, explicit public-release verdict ("Ready for public release" or a numbered blockers list), and top 3 priority actions>

**Findings** — Errors: N | Cosmetic: N | Refactoring: N | Performance: N

---

# Scope

*State the confirmed review scope.*

---

# Errors

> Must be fixed. Includes bugs, security vulnerabilities, broken API contracts, missing test coverage, silent failures, and race conditions.

---

## [E1] {Short Title}

Status: ⬜ Open

_(⬜ → ✅ fixed · ❌ rejected · ⚠️ partially addressed)_

### What

Precise, unambiguous description of what is wrong. Name the exact code construct, its current behaviour, and why it is incorrect.

### Where

Exact file path, type, method, and line. Example: `src/Foo/Bar.cs` · `BarService.ProcessAsync` · line 42. Include the current (incorrect) code state with enough surrounding context for the implementer to understand the problem.

### Why

Impact if left unfixed: user-visible consequences, data corruption, security exposure, or test failures.

### Context

Additional considerations: related code paths affected, concurrency assumptions, interaction with other findings, or constraints that are not obvious from the code alone.

### How

Exact fix to apply. Include a minimal code snippet showing the corrected state. Name the pattern, API, or transformation to use. State all constraints the fix must respect (naming conventions, thread safety, performance, etc.).

### Verify

Exact command with build config (`-c Release`) and xUnit filter string. State the expected outcome: exit code, passing test count, or specific observable state.

---

# Cosmetic Issues

> Optional improvements that do not change behaviour — naming, formatting, minor clarity.

---

## [C1] {Short Title}

Status: ⬜ Open

_(⬜ → ✅ applied · ⬜ deferred)_

### What

Description of the style or clarity problem. Name the specific construct and what is non-ideal about it.

### Where

Exact file path and line. Include the current code state with enough surrounding context for the implementer to locate and understand the issue.

### Why

What improves if fixed: readability, consistency, or adherence to style conventions.

### Context

Additional considerations: related occurrences elsewhere, team conventions, or interaction with other cosmetic findings.

### How

Exact change to apply. Show before/after where helpful. Name the relevant style convention.

### Verify

Exact command with build config confirming the change is correctly applied and the build remains clean.

---

# Refactoring Opportunities

> No behaviour change, but meaningfully improves structure, readability, or maintainability.

---

## [R1] {Short Title}

Status: ⬜ Open

_(⬜ → ✅ applied · ⬜ deferred)_

### What

Description of the structural improvement. Name the specific code smell or structural issue.

### Where

Exact file path(s), type(s), method(s), and line(s). Describe the current structure concisely with enough code context for the implementer to act independently.

### Why

What improves: readability, maintainability, testability, or structure. Confirm behaviour must not change.

### Context

Additional considerations: dependencies on this code from other modules, test coverage that must remain green, or constraints on the refactoring approach.

### How

Exact refactoring to perform. Name the extract/move/split operations and their targets. Describe expected end state.

### Verify

Exact test command confirming behaviour is unchanged. State passing test count or specific assertions.

---

# Performance and Allocations

> No behaviour change, but reduces allocations, improves throughput, or enables optimization in hot paths.

---

## [P1] {Short Title}

Status: ⬜ Open

_(⬜ → ✅ applied · ⬜ deferred)_

### What

Description of the performance issue. E.g., unnecessary allocation, missing `Span<T>`, absent pooling, SIMD candidate, redundant copy. Name the exact construct.

### Where

Exact file path, hot-path method, and line. Include current allocation or performance behaviour with relevant code context.

### Why

Expected improvement: allocation reduction, throughput, or latency. Quantify where possible.

### Context

Additional considerations: call frequency, allocation pressure from surrounding code, platform-specific behaviour (x64 vs. ARM64), or interaction with other performance findings.

### How

Exact optimization to apply. Name types, APIs, or patterns to use (e.g., `ArrayPool<T>`, `Span<T>`, SIMD intrinsics). State constraints: scalar fallback requirement for SIMD, ArrayPool return-in-finally rule. Describe expected end state.

### Verify

Benchmark, profiler measurement, or test confirming the improvement and no behaviour change. State the measurable expected outcome.

---

# Closing Assessment

Write a comprehensive assessment (at minimum 4–6 substantive points) covering:
- Overall code quality and architectural soundness
- Dominant error themes and their underlying root-cause pattern
- Thread-safety and concurrency posture across the reviewed scope
- Performance and allocation profile in hot paths, including pooling / SIMD findings
- Explicit public-release verdict: "Ready for public release" or a prioritised list of blockers with remediation order
- Top 3 highest-priority actions, referencing finding IDs

---

# Priority Action List

Ordered list of the highest-impact items to address first, referencing finding IDs (e.g., E1, E3, R2).

---

# Findings Overview

> Produce this table after all findings are written. It lists every finding in a single line for at-a-glance orientation.
>
> **Placement rule**: When writing the review to a **file**, move this table to the very top of the document — before the Scope section. When delivering the review in **chat**, leave this table here at the end — after the Priority Action List.

| Finding | Description |
|---------|-------------|
| E1 — {title} | {one sentence describing the problem and its location} |
| C1 — {title} | {one sentence describing the issue and where it occurs} |
| R1 — {title} | {one sentence describing the improvement and its scope} |
| P1 — {title} | {one sentence describing the bottleneck and its impact} |

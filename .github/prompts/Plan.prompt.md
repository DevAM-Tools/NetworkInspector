---
name: plan
description: Create a detailed implementation plan for a feature or task
argument-hint: Briefly describe the feature or change to plan
agent: agent
---

<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

You are creating an implementation plan. Work through the following stages **in order**. Do not skip ahead.

---

## Stage 1 — Gather Context

Read the relevant areas of the codebase: existing implementations, tests, documentation, interfaces, and build configuration. Identify all affected files, dependencies, and integration points — search call sites, tests, configuration, and docs broadly; list every affected file explicitly before moving on. Be aware that other agents may be editing the same files concurrently; check for and resolve any conflicts before proceeding. Identify which technologies the plan will touch and read every applicable style guide from `.github/styles/` in full — do not rely on memory of a previous session. Summarise what you find before moving on.

---

## Stage 2 — Define Scope

Ask the user to confirm in one message:

- What is **in scope**?
- What is **explicitly out of scope**?
- Are there deadlines, performance targets, or compatibility constraints?

Do not proceed to Stage 3 until scope is confirmed.

---

## Stage 3 — Grill Me

Ask **every** question needed to fully understand the requirements before designing a solution. Cover:

- Functional requirements and acceptance criteria
- Edge cases and error conditions
- Performance, scalability, and memory constraints
- Security, threat modeling, and access-control implications — identify trust boundaries, untrusted inputs, and STRIDE threats (Spoofing, Tampering, Repudiation, Information Disclosure, Denial of Service, Elevation of Privilege); document required mitigations
- Concurrency and thread-safety requirements — which resources are accessed concurrently? What synchronisation strategy (locks, channels, immutability, etc.) is required? Are there TOCTOU windows or async-interleaving risks?
- Compatibility, migration, and legacy code — which existing APIs, data formats, or integrations must remain unchanged? Are there breaking changes; if so, what is the deprecation, versioning, or migration strategy? Is there legacy code that must be preserved, wrapped, or replaced, and what is the boundary between old and new?
- Testing strategy and definition of done
- Architectural boundaries: which layers exist (Presentation, Application, Domain, Infrastructure, or equivalent), what belongs in shared infrastructure, and which cross-cutting concerns (logging, auth, validation, error handling, caching, thread safety) need a solution-wide pattern
- Any other open ambiguity

List **all questions at once** in a single message. Wait for complete answers. If any answers raise new questions, ask those too. Do **not** proceed to Stage 4 until every question is answered and zero ambiguities remain.

---

## Stage 4 — Write the Plan

Produce the plan using the structure below. Write it so an agent can execute it **autonomously** from start to finish without further clarification. After all sections are complete, produce a **Step Overview Table** (format and placement described at the end of this stage template).

---

# Summary / Context Anchor

> The plan's executive summary and the agent's working context. Write 3–5 sentences covering: what is being built, why, the agreed scope boundaries, the top 2–3 constraints the agent must respect throughout execution, and the established architectural layers and shared-infrastructure boundaries. **Re-read this before starting each step.**

---

# Phases (optional — for large plans only)

When a plan exceeds ~10 steps or spans multiple independent areas of the codebase, divide it into **phases**. Each phase groups related slices that can be implemented, reviewed, and committed as a cohesive unit before the next phase begins. Phases are delivered sequentially; a phase must be fully complete (all slices passing, all reviews clean) before the next phase starts.

## Phase 1 — {Name}

**Goal** — {what this phase achieves as a whole}
**Slices** — {slice IDs included in this phase}
**Gate** — {condition that must be met before Phase 2 begins — e.g., "all tests pass, zero warnings, review clean"}

## Phase 2 — {Name}

...

For small-to-medium plans (≤10 steps, single area), phases are unnecessary — use slices directly.

---

# Vertical Slices

List slices in delivery order. Each slice must be independently functional and testable when complete.

For solutions with more than one feature slice, the **first slice must always be the architectural foundation**: it establishes layer boundaries, shared infrastructure, and cross-cutting concern patterns before any feature work begins. Feature slices build on this foundation; they must not redefine or bypass it.

## Slice 1 — {Name}

**Delivers** — {observable, testable outcome}
**Steps** — {step IDs included in this slice}

## Slice 2 — {Name}

...

---

# Steps

Order steps in strict **topological order**: no step may appear before all of its declared dependencies are complete. Steps with no mutual dependencies within the same slice may be listed in any order, but must never be placed after a step that depends on them.

Each step is **self-contained**: an implementer copies the step and executes it as a standalone prompt — without re-reading the rest of the plan or searching the codebase. Write every field with that usage in mind.

Do not embed plan step IDs, task references, or tracking identifiers in code, comments, or commits.

---

## Step N — {Title}

Status: ⬜ Not started · Depends on: {step IDs or "none"} · Output: {files created or modified; tests added}

_(⬜ → ✅ complete · ❌ failed · ⚠️ blocked)_

### What

What must be done, precisely and unambiguously. Name the exact change: add, modify, remove, rename. State the expected end state after this step is complete.

### Where

Exact file paths (relative to repo root) that will be created or modified. For modifications: name the specific types, methods, or sections affected and describe their current state (key signatures, class hierarchies, existing logic). Include related test files. List everything the implementer must open.

### Why

Rationale: what breaks or is missing without this step. How this enables subsequent steps. What invariants this step establishes or preserves.

### Context

Additional considerations the implementer must be aware of: related decisions from earlier steps, constraints imposed by other parts of the system, concurrency or ordering assumptions, known pitfalls, or anything else that affects how this step must be executed but does not fit neatly into the other fields.

### How

Complete implementation approach — the core of the prompt. Must contain:

- Key types, methods, algorithms, data structures to use or create
- Pseudo-code or method signature sketches for non-trivial logic
- Existing codebase patterns and conventions to follow (reference specific files)
- Interfaces to implement or extend (with their current signatures)
- Edge cases and error conditions to handle
- Coding conventions, thread-safety constraints, and security constraints that apply
- Any prerequisite state from earlier steps that the implementer must know

### Verify

Exact build/test command (including `-c Release` and xUnit filter string where applicable). State the expected outcome: exit code, passing test count, or specific observable state.

### If it fails

_(Include only for steps touching shared state, external systems, or schema migrations.)_

Recovery path: rollback or repair steps if the step fails.

---

## Example — filled-in step

> The following shows what a completed step looks like when rendered.

---

## Step 3 — Add retry policy to OrderService

Status: ⬜ Not started · Depends on: Step 2 · Output: `src/Orders/OrderService.cs`, `tests/Orders/OrderServiceTests.cs`

### What

Add an exponential-backoff retry policy to `OrderService.SubmitAsync` for transient HTTP failures. After this step, transient 5xx responses from the payment gateway are retried up to 3 times before surfacing an error.

### Where

- `src/Orders/OrderService.cs` — `OrderService` class, `SubmitAsync` method (currently calls `_httpClient.PostAsync` without retry logic).
- `tests/Orders/OrderServiceTests.cs` — new test methods for retry scenarios.

### Why

Transient gateway failures cause order loss in production (~0.3 % of requests). Retry eliminates this without user-visible latency for successful retries.

### Context

`OrderService` is registered as a scoped service. The `HttpClient` comes from `IHttpClientFactory` (configured in Step 2). Follow the Polly pattern already used in `src/Shipping/ShippingClient.cs`.

### How

1. Add a `Polly` `AsyncRetryPolicy<HttpResponseMessage>` field initialised in the constructor:
   ```csharp
   private static readonly AsyncRetryPolicy<HttpResponseMessage> RetryPolicy =
       Policy.HandleResult<HttpResponseMessage>(r => (int)r.StatusCode >= 500)
             .WaitAndRetryAsync(3, attempt => TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)));
   ```
2. Wrap the `PostAsync` call: `var response = await RetryPolicy.ExecuteAsync(() => _httpClient.PostAsync(uri, content));`
3. Add tests: success on first try, success on second retry, failure after exhausting retries.

### Verify

```
dotnet test tests/Orders -c Release --filter "OrderServiceTests"
```

Expected: 5 tests pass, 0 warnings.

---

# Edge Cases and Risks

List every known edge case and risk, each with a proposed mitigation.

---

# Open Questions

List any questions that could not be resolved in Stage 3 and may surface during implementation, together with a suggested default if the agent must proceed without asking.

---

# Closing Summary

Write a comprehensive summary covering at minimum:
- What is being built and the key architectural decisions with their rationale
- The main technical risks identified and how the plan mitigates each
- Hot-path allocation strategy (Span / pooling / ThreadStatic), SIMD considerations, and thread-safety design
- Trade-offs accepted and why
- The precise, observable definition of “done” for the entire plan — exact test commands, coverage targets, and release criteria

---

# Task Checklist

A flat, ordered list of every step with an alternating review gate after each one. The agent marks items complete as it goes.

- ⬜ Step 1 — {title}
- ⬜ Step 1R — Review Step 1 output; fix all Error findings; repeat until zero Errors remain
- ⬜ Step 2 — {title}
- ⬜ Step 2R — Review Step 2 output; fix all Error findings; repeat until zero Errors remain
- ⬜ Step 3 — {title}
- ⬜ Step 3R — Review Step 3 output; fix all Error findings; repeat until zero Errors remain

**Agent execution rules**:
1. Mark each item ✅ immediately after completing and verifying it; use ❌ if a step fails and ⚠️ if a step is blocked or at risk.
2. A review gate (`NR`) must reach **zero Error findings** before the next implementation step begins. Keep iterating the review–fix cycle until the gate is clean.
3. If a step uncovers scope not described in this plan, **stop and ask the user** before continuing.
4. Never embed internal plan step IDs, task references, or tracking identifiers in code, comments, or commit messages.

---

# Step Overview

> Produce this table after all steps are written. It lists every implementation step and review gate with a concise one-sentence description of what each delivers or verifies.
>
> **Placement rule**: When writing the plan to a **file**, move this table to the very top of the document — before the Summary / Context Anchor. When delivering the plan in **chat**, leave this table here at the end — after the Task Checklist.

| Step | Delivers |
|------|---------|
| Step 1 — {title} | {one sentence describing what this step produces or changes} |
| Step 1R — Review Step 1 | Verify zero Error findings in Step 1 output; iterate until clean |
| Step 2 — {title} | {one sentence} |
| Step 2R — Review Step 2 | Verify zero Error findings in Step 2 output; iterate until clean |
| ... | ... |

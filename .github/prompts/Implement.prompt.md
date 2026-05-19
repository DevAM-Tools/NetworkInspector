---
name: implement
description: Execute an approved implementation plan step by step
argument-hint: Reference the approved plan or paste the step(s) to execute
agent: agent
---

<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

You are executing an approved implementation plan. Work through the following stages **in order**. Do not skip ahead.

---

## Stage 1 — Prepare

1. **Verify plan approval**: Confirm that an approved plan exists in this conversation. Approval is satisfied by any of the following: an explicit sign-off ("approved", "looks good", "go ahead"), an instruction that implies feature implementation ("implement the plan", "start implementation", or equivalent), or an instruction that implies fixing review findings ("fix the findings", "implement the fixes", "address the errors", "fix all errors", or equivalent). If no plan or review findings and no such approval signal exists, stop immediately and direct the user to run `/plan` or `/review` first. Do not write a single line of code without an approved plan or an accepted set of review findings.
2. Be aware that other agents may be editing the same files concurrently; check for and resolve any conflicts before proceeding.
3. Identify every technology involved in this implementation task and read the corresponding guide in full. Do not rely on memory of a previous session.

   | Technology present | Guide to read |
   |---|---|
   | `.cs` files | `.github/styles/CsharpCodingStyle.md` |
   | `.razor` or `.razor.cs` files | `.github/styles/BlazorRazorCodingStyle.md` |

4. Re-read the approved plan's **Summary / Context Anchor** before beginning any step.
5. **Confirm legacy and compatibility strategy**: Verify that the approved plan explicitly addresses how legacy code, backward compatibility, and breaking changes are handled. If the plan is silent on any of these points and the implementation touches existing public APIs, data contracts, or integrations, stop and ask the user before writing any code.

Do not write a single line of code until all applicable style guides are loaded.

---

## Stage 2 — Execute Steps

For each plan step, in strict topological order:

1. **Mark the step in-progress** in the plan's Task Checklist before starting any work on it. This immediately captures current state — if execution is interrupted, the checklist accurately shows what is in progress and what is complete, enabling seamless resumption.
2. **Implement** exactly what the step specifies — no more, no less. Follow every rule from `.github/copilot-instructions.md` and every rule in the style guides loaded in Stage 1. Do not add features, refactor, or make improvements beyond what the step requires.
3. **Build and test**: run the exact `Verify` command from the plan step. The build must be clean (zero warnings — warnings are errors) and all tests must pass.
4. **Review**: run `/review` scoped to the files listed in the step's `Output`. Fix every **Error** finding and re-run until zero Error findings remain. Cosmetic, Refactoring, and Performance findings may be deferred.
5. **Commit**: stage and commit all changed files with a descriptive commit message. Do not include plan step IDs, task references, issue numbers, or tracking identifiers in code, comments, identifiers, or commit messages.
6. **Mark the step complete** (`✅`) in the Task Checklist **immediately** after verification and commit succeed — before starting the next step. Never batch or delay completions; the checklist must always reflect the true current state.

If any step uncovers scope not described in the approved plan, **stop and ask the user** before continuing.

---

## Stage 3 — Final Verification

After all steps are complete:

1. **Run the full build and all tests** in Release mode (`-c Release`) to confirm the complete change set is clean and all tests pass.

2. **Completeness audit — item by item**: Go through every item in the plan's Task Checklist (or every finding in the accepted review) in order. For each item:
   - Re-read the original specification — the step's **What**, **Where**, and **How** fields, or the finding's description and fix instructions.
   - Verify the implementation matches what was specified: check every named file, type, method, and line explicitly.
   - Search for additional affected locations beyond those explicitly listed — call sites, tests, configuration entries, and documentation that may have been missed.
   - Run the step's or finding's **Verify** command and confirm it passes.
   - Mark the item `✅` only after all of the above are confirmed. If any gap is found, address it immediately before continuing the audit.

3. **Confirm goal achievement**: State explicitly whether the overall objective from the plan or review was fully met. For plans with a defined “done” criterion (test coverage targets, specific observable states, release criteria), verify each criterion by name.

4. **Output Implementation Status Table** at the end of the chat response:

   | Step / Finding | Status |
   |----------------|--------|
   | Step 1 — {title} | ✅ Complete |
   | Step 1R — Review Step 1 | ✅ Clean — 0 Errors |
   | Step 2 — {title} | ✅ Complete |
   | E1 — {finding title} | ✅ Fixed |
   | E2 — {finding title} | ⚠️ Deferred — {reason} |

5. **Report deferred findings**: List all Cosmetic, Refactoring, and Performance findings that were deferred during per-step reviews, so the user can decide whether to address them.

# Test Strategy

Load whenever tests or production APIs are in scope. Content and case design live here. Runner, asserts, and coverage **tools** live in the technology test skill (`tech-tunit.md`, `tech-pytest.md`, `tech-rust-test.md`, `tech-playwright.md`).

Implements Section 4.5 in `copilot-instructions.md`.

## What exhaustive means

Exhaustive is **behavior classes**, not value enumeration.

Cover:

- happy path
- expected errors / `Err` / false
- boundaries: empty, min, max, off-by-one
- collections of size 0, 1, and 2
- `null` / absence when the type allows it
- concurrency when shared state exists
- trust-boundary / misuse cases for public APIs

```text
Wrong: [Arguments(0), (1), (2), … (int.MaxValue)] for Clamp
Right: empty, one element, two elements, min, max, min-1, overflow-relevant, null if possible
```

Do not iterate every integer, every string, or every permutation “to be sure”.

## Speed

- Finish unit tests in milliseconds. Do not `Sleep` / `time.sleep` / `thread::sleep`.
- Do not use network, real clock waits, or an extra process when a deterministic fake will do.
- Assert one behavior per test. Parametrize related inputs; do not copy-paste.

## Web UI (Playwright)

When a product web UI is in scope, **use Playwright intensively** to prove what the user can see and do. Do not treat a green unit/component suite as UI coverage.

When requirements or the plan name **concrete UI behavior** (`REQ{n}` / `TEST{n}`: visible text, validation, navigation, viewport, keyboard, enabled/disabled), each named behavior needs an observable Playwright assert. A generic “page loads” journey is not enough.

Cover the same **behavior classes** as above, in the browser: happy path, expected errors, empty/min/max, viewports in `tech-web.md`, and trust-boundary messages the user can read. Plan those cases as `TEST{n}` **Content**.

Do **not** skip journeys to keep the suite small. Do **not** assert every pixel, screenshot, or CSS rule. Assert observable UI: heading, row, error text, URL, focus, enabled/disabled, layout that would break the task (clipped primary action, required horizontal scroll).

Trace on failure. No `waitForTimeout` as sync. Mechanics: `tech-playwright.md`. Component logic stays in the component runner (`tech-blazor.md`), not Playwright. **C# project:** `{App}.UiTest`.

## Plan and Grill Me

Write test **content** as first-class `TEST{n}` cards in `workflow-plan.md` (schema there). Agree the **minimum** the plan must cover: important scenarios, edges, special constellations, contradictions, gaps, what is out, and the time budget. Class and Layer may be named. Test code in the plan is allowed; it does not replace Content.

Implement those cards as steps. Extra tests at implement time are allowed. Do not treat a green build as a test case.

## Design

- Use real deterministic implementations. Mock only external or non-deterministic dependencies.
- Write tests that can fail. A test that cannot fail is not coverage.
- Name tests `unit_scenario_expected` unless the tech test skill says otherwise. **C#:** PascalCase methods (`tech-tunit.md`).
- Separate Arrange, Act, Assert with a blank line.
- Cover Windows/Linux/macOS, x64/ARM64 unless the user scoped otherwise.

## Exit paths

When the loaded tech skill defines a coverage gate, that gate is the release gate. Humans review **whether the cases are the right ones**; the tool counts exits. C#: `tech-tunit.md` (ExitPointGaps).

## Debugging

On failure: run the smallest filter from the tech skill, then debug (Section 4.15). Treat file locks and parallel-agent runners as collision before rewriting product code (Section 4.14).

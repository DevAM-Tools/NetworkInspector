# Implement Workflow

Load on `/implement`. Apply `copilot-instructions.md` for all quality, tech, git, and communication rules.

**Purpose:** Implement approved plan steps or accepted review findings **exactly**. Complete every item in scope. Close with an **extensive briefing**, then a council Exam of the built result.

## Stage Order

1. Prepare
2. Execute Steps
3. Final Verification
4. Briefing
5. Closing Exam

## Stage 1 — Prepare

- Require approved plan or accepted review findings per `copilot-instructions.md` Section 6; stop otherwise.
- ❗ Read the **entire** plan (or finding file) **before the first edit**. Do not skim Step Overview and skip `How`. Read the requirements artifact and any linked architecture / guide / doc **in full**.
- Run Tech Load Protocol per Section 3.
- Resume at first `⚠️`, else first `⬜`.

## Stage 2 — Execute Steps

- **Checklist status:** `⚠️` before first edit · `✅` after Verify, alignment,
  requirement/test ticks, Step `{n}R` clean (review of step n).
- Tick **all three** plan surfaces together: Step Overview, Shared Block, Task Checklist.
- Process **every** checklist step, dedicated test step, and finding in scope; skip none.
- Follow topological order per plan dependencies.
- Implement **only** current step or finding scope — match `What` and `How` exactly, including plan Before/After or finding Problem/Fix.
- Do not substitute, simplify, or extend beyond scope without user approval.
- Implement named `TEST{n}` **content** listed on the step (add or run). Mark those rows `✅` when the Content exists as a test that can fail. Extra tests beyond `TEST{n}` are allowed when they still prove agreed behavior.
- Run `Verify` from the plan step or finding; require pass. On failure follow Section 4.15 and the step `Debug` block. Consider concurrent-agent collision (Section 4.14).
- **Alignment check:** plan/finding target vs actual; no silent deviation.
- **Requirements check (step close):** re-read the **linked** requirements file (not a copy in the plan). Run Done-when for `REQ{n}` still `⬜` that this step could have made true. Tick `Met` in that file only when the check **ran**. Do not tick because `How` alluded to an outcome. Do not write `REQ{n}` or `TEST{n}` into code or comments.
- Confirm step `Acceptance` checkboxes.
- Confirm misuse/abuse checklist from the plan when new public APIs are in scope.
- Confirm docs (README, guides, architecture, package docs) still match behavior and commands (Section 1).
- Run `/review` at each Step `{n}R` (review of step n); zero Errors before next
  step.
- Persist review file in complex-task mode per `workflow-complex-task.md`.
- After two failed remediations for the same Error root cause: exponential backoff then retry, or stop with blocker.

## Stage 3 — Final Verification

- Confirm **every** scoped step, `TEST{n}`, and finding is `✅`; leftover `⬜` / `⚠️` = blocker.
- Re-run alignment: every `REQ{n}` with Done-when Met ✅, every `TEST{n}` ✅, or all accepted findings resolved.
- Run full build and all tests in optimized/Release. Commands from the loaded tech skill.
- For web UI: run the planned Playwright journeys.
- Output Implementation Status Table (every step / finding / `TEST{n}` listed).
- Do not enter Stage 4 until this stage is green.

## Stage 4 — Briefing

Write the briefing before Stage 5. Exam skipped → still write it. No complete without it. Chat: path only.

Call this artifact a **briefing**, never a “review brief”. `/review` is a different workflow (adversarial review; default includes test execution).

The briefing is the human packet for what shipped. It must be **extensive**: a reader who did not watch implementation should know what shipped, why it had to, how to try it, what to inspect in each file, which acceptance checks now hold, and what is out of scope. English (Section 4.6). ❗ Incomplete without contribution, how to try the result, and what to inspect. Do not compress **How it serves** / **Why needed** / header **Why**.

- Path: `reviews/briefing_<slug>.md`. Single item: `reviews/briefing_<slug>_<item>.md` (`step{n}` / finding ID). Before Exam: full-scope file.
- List every created, edited, or deleted path. Omit none. Rewrite on remediation.
- Built result. Name symbols, behavior, contracts. Include small illustrative snippets where they help (signatures, usage). Not a raw diff dump.
- Header fields below are required. Expand until a reader can act without reconstructing the argument.
- `REQ{n}` / `TEST{n}` / `E{n}` links belong in the briefing (reader aid). Do not copy those IDs into product code. `REQ{n}` links use the path from the plan **Requirements** link (template default: `requirements/req_{slug}.md`).
- Existing heading: clickable relative Markdown link (Section 4.6), label is the repo-root path in backticks. Deleted: same link + `(deleted)`. Forward slashes. Sibling files may share one card. Walkthrough steps that name a file use that link form too.
- Field order per card: **Changed** → **Why needed** → **How it serves** → **Look at** → **Depends on** → **Serves**.
- **How it serves:** per linked `REQ{n}` / `TEST{n}` / `E{n}`, full sentences: which symbols, what exists or is gone, what a caller can or cannot do, which Done-when this file owns. Unique to this file. Ban slogans, ID-only, “as planned”, empty purpose.
- **Look at:** where in the file a reader should spend time (type, method, invariant).
- Order for reading: contracts/types → implementations → cutover → tests → docs.

```markdown
# Briefing — {scope}

**Expect:** {outcome}
**Done when:** {checks the reader can run}
**Why:** {problem without this work; what stays wrong if it does not ship}
**Out:** {exclusions}

**Try this:** {exact commands, Playwright spec, or usage snippet; expected observable}

**Acceptance evidence:**
- REQ12 ({short outcome}): {command/UI/API and what was observed} · [REQ12](../requirements/req_{slug}.md)
- TEST1 ({case}): {test filter or spec and result}

**Public API:** {short snippet or "unchanged"}

**Performance:** {hot-path allocation notes, or "not in hot path"}

**Requirements:** [REQ12](../requirements/req_{slug}.md) {short outcome} · [REQ13](../requirements/req_{slug}.md) {short property}

**Test cases:** [TEST1](../plans/plans_{slug}.md#test-cases) {short name} · [TEST2](../plans/plans_{slug}.md#test-cases) {short name}

## Walkthrough

1. {Open this file; confirm this symbol / AC}
2. {Then this}

## Files

1. [`{path}`](../{path})
   - **Changed:** {symbols/behavior}
   - **Why needed:** {what fails without this file — several sentences if needed}
   - **How it serves:** [REQ12](../requirements/req_{slug}.md): {causal contribution unique to this file}. [TEST1](../plans/plans_{slug}.md#test-cases): {how this file makes the TEST{n} Content possible}
   - **Look at:** {symbol and what to verify}
   - **Depends on:** —
   - **Serves:** [REQ12](../requirements/req_{slug}.md), [TEST1](../plans/plans_{slug}.md#test-cases)

2. `{path}` (deleted)
   - **Changed:** {what went away}
   - **Why needed:** {what the deleted type blocked or enabled wrongly}
   - **How it serves:** {type is gone; callers must use …; Done-when this deletion closes}
   - **Look at:** callers listed in Depends on / Serves
   - **Depends on:** 1
   - **Serves:** [REQ12](../requirements/req_{slug}.md)
```

## Stage 5 — Closing Exam

- Load `workflow-council.md`. Run **Exam** on the built result (plan `REQ{n}`,
  `TEST{n}`, briefing, touched files, tests, latest Step `{n}R` reviews). Same
  agent; no subagents.
- Skip Exam when parent is `workflow-complex-task.md` Stage 3 per-item or `workflow-review-loop.md` Stage 2. Still run Stage 4.
- User `quick`/`lite` → Lite Exam (Skeptic addendum still required). Else Full Exam.
- Chairman **Holds** → implement complete. Cite the briefing and the exam artifact.
- Chairman **Does not hold** → remediate kill shots and §4 violations (Verify, alignment, two-attempt cap). Re-run Stage 3 for touched scope, rewrite Stage 4, then re-Exam `_<n>`.
- Grill Me follow-ups → ask user; do not mark implement complete.
- Do not treat Exam as `/review`. Do not skip Step `{n}R` because Exam will run.

## Implementation Status Table

```markdown
| Step / Finding | Status |
|----------------|--------|
| Step 1 - {title} | ✅ Complete |
| Step 1R - Review Step 1 | ✅ Clean - 0 Errors |
| TEST1 - {case} | ✅ |
| Step {N} - Requirements fit | ✅ Every REQ{n} with Done-when Met |
| REQ12 - {outcome} | ✅ Met |
| E1 - {title} | ✅ Fixed |
| Briefing | ✅ reviews/briefing_<slug>.md |
| Closing Exam | ✅ Holds · councils/council_<slug>-exam.md |
```

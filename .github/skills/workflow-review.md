# Review Workflow

Load on `/review`. Apply `copilot-instructions.md` Sections 2–4.

Findings are for **human acceptance** of the defect and the proposed fix, and for a **weaker executing agent**. Locked `How` plus Problem/Fix is the handoff — the same bar as plan step `How`. Write each finding so a weaker agent has **no interpretation room** on the fix, and so a person can accept or reject the defect and the fix without reconstructing the argument. Fenced **Problem** (current) and **Fix** (after) code is the most useful part of a finding; do not replace it with a prose description of the edit. Extra illustrative snippets (failing call site, a test that would catch it, bad vs good usage) are welcome in the review output. Do not compress finding `How`, Evaluate, or Summary. Exam is not a `/review` substitute.

The review **must** answer: is this **scope** ready for public release? Write that as Summary **Release**. `Ready for public release` only when every hunt ran and zero Errors are open. Otherwise `Blocked by {IDs}`. Do not hedge. Do not answer for files outside scope.

Do not read existing `reviews/review_*.md` unless the user names one. Do not copy findings, verdicts, or style from them. A prior Ready is not evidence.

Adversarial review of code, associated tests, composition, performance, documented design, and whether leading-source **properties** hold in scope. Required views: **Skeptic** and **Outsider** only — not a Full council.

## Stance

Enter as a skeptical auditor **new to this project**. A single defect can cause severe damage. Assume every claim is false until the source proves it. Trust no author, test, doc, prior review, or green path. Expect a trap in every routine. Hunt the hair in the soup. Check excessively and with absolute precision. Do not sample. Do not rubber-stamp.

## Modes

Default **full review**. Enter **static review** only on an explicit reduced-scope request (`static review`, `code only`, `code-only`, `reduced scope`, `without running tests`, `ohne Testausführung`, `nur Code-Analyse`). Record the mode in **Scope**. Do not review before scope and mode are confirmed.

**Full:** Read sources, tests, docs, call sites. Judge test **content**. Run in-scope build, paired tests, coverage gate, and Playwright in optimized/Release. Record commands in **Test Execution**. Red build/test/gate → **Error** with output in `Context`. Do not explore the product outside those commands.

**Static:** Read sources, tests, docs. Judge tests and exit-path coverage from source. Write **Test Execution:** `Not run — static review`. Do not run build, tests, gate, Playwright, debugger, or the product.

## Stage Order

1. Define Scope
2. Load Rules
3. Gather Context
4. Review
5. Output

## Stage 1 — Define Scope

- Treat a provided scope argument as confirmed. Otherwise ask in-scope items, exclusions, and focus.

## Stage 2 — Load Rules

- Enumerate confirmed-scope files by extension and project path.
- Run Tech Load Protocol (`copilot-instructions.md` Section 3). Load every skill the Section 3 table maps to those files. Also apply the extra Section 3 bullets (C# production APIs → `tech-tunit.md` + `tech-test.md`; product web UI; illustrations).
- Do not skip a matching trigger. Do not load a skill whose trigger is not in scope. When uncertain, load it. Unloaded skills do not apply.
- Read `custom_instructions.md` when it exists at repo root.
- Load **Skeptic** and **Outsider** from `workflow-council.md` (Views table). Do **not** run Problem-First, Upside, Builder, Lite, Full, or Exam as part of `/review`.
- Record `Loaded skills:` in Scope.

## Stage 3 — Gather Context

- Enumerate in-scope files and associated test projects / spec files. In the
  artifact, each path is a clickable relative Markdown link (Section 4.6).
- Do not open `reviews/review_*.md` unless the user named that file.
- If a plan is in scope, **read it in full**. If requirements, architecture, guides, or briefings are linked or attached, **read those in full**.
- If `reviews/briefing_<slug>*.md` exists for this scope, read it first. Also accept legacy `reviews/brief_<slug>*.md`. Follow card order. Open each file link. Treat **How it serves** as claims to verify **in the file by reading**.
- Read in-scope files, related tests, direct dependencies, and call sites.
- Map composition: callers, callees, shared state, sequencing, and error paths that exist only when pieces combine.
- Read definition and docs for involved types and items.
- Name in-scope **hot paths** (per-item / per-byte after setup) before Stage 4.

**Full review:** after reading, run build and scoped tests per Modes. On failure, capture output. Consider concurrent-agent collision (Section 4.14) before treating a failure as a product defect.

**Static review:** do not run build, test, or coverage commands.

## Stage 4 — Review

Run **every** hunt. Do not sample. Never stop after first N findings. Apply Stance. Cite the hunt (and view, when applicable) in `Context`.

### Authority

Leading sources: `custom_instructions.md` when present, every **loaded** skill (tech and workflow), and `copilot-instructions.md` Section 4. Read those files. Walk their **properties** against in-scope files. `custom_instructions.md` extends Section 4; it cannot weaken it. Skills supply mechanisms. Hunts and the distillate below are reminders. They do **not** replace the leading sources and are not complete. A miss against a leading source is an Error even if this skill omitted it.

A **property** is a rule the artifact must satisfy. Walk ❗ and non-❗. A **procedure** is how an agent runs a workflow (stage order, Tech Load, git, subagents, chat language, output templates). Do not file procedure misses against the product.

Cite source path and the broken rule in `Context`. Miss = Error. A trigger that matches but whose skill was not loaded = Error. Unloaded skills do not apply.

### Distillate (always-on properties)

Reminder of Section 4. Still walk the leading sources in full. Record finding IDs or `none` in Summary.

- **Security:** OWASP Top 10; never trust caller parameters, URLs, bindings, payloads; injection; no secrets, credentials, tokens, or PII in logs; STRIDE mitigations for external input.
- **Races:** no unsynchronized shared mutable state; race, TOCTOU, async interleaving, lock inversion, partial-state; document lock/atomic; concurrent tests for thread-safety claims.
- **Docs:** English non-temp docs; why-comments; public API docs; comments, XML/docs, README, guides in sync with code.
- **Orphans:** unused types, objects, APIs, files; dead code; stale docs; deprecated patterns in active paths; internals leaked; least visibility.
- **Correctness:** warnings as errors; no silent failure; validate trust-boundary input and parameters; agreed limits (constant vs configurable); no invalid state after errors; check returns; no tracking IDs in code; off-by-one; overflow; result/try APIs on expected and hot-path failures.
- **API:** invalid states unrepresentable; try/result at public expected-failure boundaries; misuse/abuse analysis; public snippet matches the plan.
- **Repo:** Windows/Linux/macOS on x64/ARM64; UTF-8 console once at process start; Debug = Release; line length 160; copyright; mandatory lint; no unapproved dependency or script; MIT / Apache-2.0 / BSD-like only.
- **UI:** product web UI: `aria-*`; phone-first responsive (320px, locked
  breakpoints); one visual language; dark mode; Playwright journeys. Illustrations:
  `workflow-illustrate.md` only — not `tech-web.md`, not §4.8 responsive.
- **Debug:** smallest native command, filterable tests, traces; core logic must not require a human-only debugger.
- **Performance / Tests / Design:** hunts below. Interface at boundaries, concrete work in the loop.

### Tests

Judge **content**, not file names (`tech-test.md`). Ask: is what the suite actually proves complete and sufficient for the implemented behavior, or are there gaps? Also judge speed (no sleeps, no network in unit tests).

Walk associated tests against implemented behavior:

- behavior classes that apply: happy, error, boundary, collection 0/1/2, absence, concurrency, trust-boundary
- important scenarios, edges, special constellations, contradictions, gaps
- a test that cannot fail is a gap. **Full review:** a test that passes but cannot fail is still a gap

Happy-only coverage of a behavior that has other applicable classes is a gap. Existing tests that miss an important edge, constellation, contradiction, or gap are insufficient even when filenames look complete.

When a plan is in scope: walk every `REQ{n}` Done-when from the **linked** requirements file and every `TEST{n}` **Content** against code and tests. Unmet on the page = Error. Extra tests beyond `TEST{n}` are not a gap. Missing Content on a card is a gap. An applicable `tech-test.md` class with no `TEST{n}`, no extra test, and no Out = gap.

**Full review:** run executable Done-when checks; run the loaded coverage gate when one exists (failing gate = Error). **Static review:** judge from source only; if `tech-tunit.md` is loaded, missing exit-path tests = Error; do not run the gate. Other stacks: do not invent an exit-path gate.

### Design decisions

Hunt encoded choices without a why: algorithm, data layout, lock/atomic, error policy, trust-boundary limit, fallback, compatibility shim, omitted validation. Section 4.6 (also 4.1, 4.3) requires comments/docs for those. Undocumented choice = Error. "Obvious" is not documentation.

### Performance

Required pass. Walk Section 4.4 and every performance **property** in the loaded tech skill. Performance is a feature. Evaluate interface-at-boundary vs concrete hot-path work.

Inspect named hot paths **intensively**. Inspect cold/setup paths only for dominant waste (unbounded buffer, slurped file, accidental N²).

For **each** named hot path, check and record in Summary (finding IDs or `none` per axis):

- **Complexity:** time and extra space vs input size; hidden nested loops; repeated full scans; worse-than-necessary polynomial work
- **Allocations:** per-item heap; intermediate collections; boxing; closure capture; string/byte materialization; copy where a view, stack buffer, or reuse would do
- **Latency:** blocking I/O on the request path; sync-over-async; await in a tight loop; lock held across I/O or per-item work
- **Throughput:** per-item vs batch; lock contention; serialization of independent work
- **Memory:** retained object graphs; unbounded caches/buffers; gen-1 / LOH survival when the loaded skill names it
- **CPU:** redundant work; virtual dispatch; reflection; exceptions on the expected path; abstractions the loaded tech skill forbids on the hot path

Source-visible cost is enough. Do not require a profiler unless the user asked or the finding claims a measured delta. Bucket **Performance** when the defect is cost. Correctness or security wins per §4.13.

### Consistency

- Compare requested target vs observed source. Cross-check plan, requirements, request, code, tests, docs, and comments (documented behavior ≠ implementation, claimed `Verify` ≠ observed test result, API contract ≠ call sites, README/guide stale).
- Hunt mismatches with README, guides, XML/docs, comments, plans, briefings, and skills. Do not assume code is right or docs are right. If the source of truth is clear, `How` names which side to change (`C{n}` when the plan already chose). If both variants could be intended, record **both** in `What`/`How` (two Fix options); do not pick a winner. Undocumented mismatch → Error.
- Missing misuse/abuse analysis for new public APIs, public API drift from the plan snippet, or a dependency/script added without user approval → Error.
- Product web UI: markup/CSS follow `tech-web.md` (phone-first, locked
  breakpoints) unless the user waived responsiveness. **Full review:** run planned
  Playwright journeys; failures = Error. **Static review:** specs must exist and
  look able to fail; do not launch the browser.
- Illustrations: do not load or apply `tech-web.md`. Do not require responsive
  layout. Asserts in `workflow-illustrate.md` only.

### Skeptic (required)

This is the required stance, not a soft overlay. Assume the in-scope solution cannot work. Do not stop after the first flaw. Cite `Skeptic` in `Context`.

- **Parts:** every in-scope unit — wrong default, off-by-one, silent swallow, copy-paste, tests that cannot fail, missing guard. Dumbest caller/operator error AND worst abuse + STRIDE at trust boundaries.
- **Whole:** composition — call-graph, shared state, ordering, contracts vs callers, tests that pass but do not prove the claim, requirements that hold per file but fail end-to-end, defects that exist only in the interplay of otherwise-correct pieces.
- Hair in the soup counts. Isolated nits that violate §4 or become fatal in combination = Error. `none` only after both hunts ran and found nothing (say so in Sweep).

### Outsider (required)

First-time caller or reader. Flag jargon, opaque names, missing first-caller steps, and docs that only make sense with insider context. Cite `Outsider` in `Context`. `none` only after this hunt ran.

High-stakes fork with ≥2 valid options: recommend `/council` in `How`. Do not auto-run Full unless asked. Competing goals without recorded preference → Error; user decision before the release verdict.

## Stage 5 — Output

Use the templates below. Write Summary **last**; place it after Findings Overview. Put the release verdict **in Summary**. Do **not** add a Closing Assessment or any second closer. Extra illustration snippets are welcome in findings; they do not replace Problem/Fix.

### Shared Block (every finding)

Field order: `What` → `Why` → `Evaluate` → `How` → `[Context]` → `[Where]` → `Verify`.
Always require `What`, `Why`, `Evaluate`, `How`, `Verify`.
Omit `Context` only when neither constraints nor sources exist. Omit `Where` when no file is touched.

❗`Why` must cover all three: what is wrong; **why it matters**; **what follows if it stays** (caller, security, data, ops, or release). Do not add a separate `If it fails` field.

❗Write `What`, `Why`, and `Evaluate` so a person who did not write the code can accept or reject the finding **and the proposed fix** without reconstructing the argument. Name the defect in plain language. Name what the fix changes and what it must not change.

❗Specify the concrete fix. Intent-only `How` is incomplete.
❗Write `How` so another agent can implement the fix without inventing types, items, signatures, algorithms, control flow, or file structure.
❗Write `How` exhaustively: types, items, visibility, signatures, parameters, return values, call-site edits, validation, error paths, control flow, data flow, thread-safety / performance / security constraints, prerequisite state, decision rationale, and important edge cases.
❗Include fenced **Problem** and **Fix** code in every finding `How` — current code, then target code with real signatures and key bodies; anchor with path/symbol. Not stubs, not comments-as-code, not an intermediate shape.
That before/after pair is what a person and a weaker agent use; extra illustration snippets are welcome and do not replace it.
Reject a `How` that allows more than one implementation, **except** when Consistency recorded two Fix options for an unresolved source-of-truth conflict (user decides; do not pick a winner).

❗Cite a concrete source in every finding `Context` when an external reference exists. File cites are clickable relative Markdown links (Section 4.6).
`Where`: clickable relative Markdown link (Section 4.6), approximate line numbers, searchable symbol.
`Verify`: exact command a later implementer must run (optimized/Release per loaded tech skill) and the expected result. **Full review:** you may have run it already — still record it here for post-fix confirmation. **Static review:** do not run `Verify` during `/review`.

```markdown
## {ID} - {Title}
Status: ⬜ {Initial} · {Depends on / Severity}
### What
### Why
{what is wrong; why it matters; what follows if it stays}
### Evaluate
Look at: {clickable relative link / symbol or command}
Accept when: {observable a person can check}
Must not change: {behavior that must stay}
### How
Fenced **Problem** (current) / **Fix** (after) — required. Extra illustration snippets welcome.
### Context
### Where
### Verify
```

### Findings Overview Table

```markdown
| ID | Bucket | Title | Summary |
|----|--------|-------|---------|
| E1 | E | {title} | {one sentence with location} |
```

```markdown
## Perspective Sweep
| View | Caught |
|------|--------|
| Skeptic | {finding IDs or `none`; parts and whole} |
| Outsider | {finding IDs or `none`} |
```

### Summary

Release verdict lives here. Architecture, composition, themes, security, races, orphans, performance axes, test sufficiency, undocumented design, and the property walk live here. Do not add a second closer.

```markdown
## Summary

**Release:** Ready for public release | Blocked by {IDs}

**Execution:** {build / test / gate outcome, or `Not run — static review`}

**Sweep:** Skeptic {IDs or `none`; parts and whole} · Outsider {IDs or `none`}

**Architecture / composition:** {1–3 sentences}

**Dominant themes:** {or `none`}

**Security:** {IDs or `none`}

**Thread-safety / races:** {posture; IDs or `none`}

**Orphans / structure:** {IDs or `none`}

**Performance:** hot paths {names}; complexity {IDs or `none`} · allocations {IDs or `none`} · latency {IDs or `none`} · throughput {IDs or `none`} · memory {IDs or `none`} · CPU {IDs or `none`}

**Tests:** {content sufficient | gaps {IDs}; `TEST{n}` match when a plan exists}

**Docs / design decisions:** {consistency; undocumented choices {IDs or `none`}}

**Skills / instructions:** leading = custom_instructions {yes|none} · {loaded skills} · Section 4; misses {IDs or `none`}; unloaded trigger {none | Error}

**Priority:** {top 3 finding IDs, one line each}
```

Patch Output before Completion if any Stage 4 hunt is missing from Summary.

### Output Modes

**File mode** (default for `/review`, `/review-loop`, and `/complex-task`): write
to `reviews/review_<slug>_<iteration>.md`. Iteration is `1` when no prior file
exists for the slug. Findings Overview at top, then Summary. Every finding as
full Shared Block under its bucket section. In chat: bucket counts, release
verdict, artifact path, prioritized action list. Do not repost Shared Block or
Summary body in chat.

**Chat-only mode** only when the user says `chat only` / `ohne Review-Datei`.
Then: Findings Overview, Summary, Perspective Sweep, every Shared Block,
Priority Action List in chat. Do not write `reviews/review_*.md`.

### Review File Sections

1. Findings Overview (top)
2. Summary (release verdict lives here)
3. Scope (**Mode:** full review | static review; **Loaded skills:** {list}; in-scope files as Section 4.6 links)
4. Test Execution (**full review:** commands run, pass/fail, gate summary; **static review:** `Not run — static review`)
5. Perspective Sweep (Skeptic and Outsider; finding IDs or `none`)
6. Errors
7. Cosmetic Issues
8. Refactoring Opportunities
9. Performance and Allocations
10. Priority Action List

## Category Rules

Assign exactly one bucket: Error · Cosmetic · Refactoring Opportunity · Performance.

- **Error:** Severity (High/Medium/Low) in the status line; Problem/Fix in `How`; OWASP category for security; missing boundary/guard for validation gaps; skill/instruction property miss; undocumented design decision; test-content gap that leaves behavior unproven; race/TOCTOU; unused type or dead code in scope.
- **Cosmetic:** exact style rule in `How`; Problem/Fix in `How`.
- **Refactoring:** unchanged behavior in `Why`; extract/move/split in `How`; Problem/Fix in `How`.
- **Performance:** Section 4.4 + tech skill in `How`; Problem/Fix in `How`; complexity, frequency, allocation pressure, latency, throughput, memory, CPU, and throw/panic / error-return paths in `Context`.

## Completion

- Summary **Release** answers the public-release question for this scope.
- Summary holds every Stage 4 hunt. No second closer.
- Report counts by bucket and prioritized action list.

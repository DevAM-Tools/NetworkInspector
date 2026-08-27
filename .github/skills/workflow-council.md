# Council Workflow

Load on `/council`. Apply `copilot-instructions.md` Sections 2–4. Do not implement. Do not edit code.

Five named views, not job titles. Independent advisors → anonymous peer review (Full/Exam) → chairman. Rule Priority §4.13. Same model as the parent for every subagent.

## When

**Run:** architecture, public API, security vs performance, irreversible choice, genuine multi-option fork. Closing Exam after `/implement` Stage 5 (standalone) or complex-task loop success.

**Skip:** lookups, post-choice how-to, `/review` (Sweep still runs inside review; Exam is not a review substitute), casual should-I, write-X. Explicit trigger on a validation-only ask: still run; do not rubber-stamp.

Vague question → one clarifier, then proceed.

## Modes

| Mode | When | How | Artifact |
|------|------|-----|----------|
| **Sweep** | `/plan` Stage 2, `/review` Stage 4 | Same agent. No subagents. | none |
| **Lite** | Plan Decision Loop default; user says `quick`/`lite` | 5 advisors + chairman | `councils/council_<slug>[_<n>].md` |
| **Full** | `/council` default; security, public API, irreversible | 5 advisors + 5 peer reviews + chairman | same |
| **Exam** | `/implement` Stage 5; complex-task after Error-clean loop; user says `exam` | Full mechanics unless user says `quick`/`lite`. Skeptic uses Exam addendum. | `councils/council_<slug>-exam[_<n>].md` |

## Views

Skip none. Use these names only.

| View | Ask | Do |
|------|-----|-----|
| Skeptic | What kills this? | Assume it cannot work. Find the fatal flaw. Dig if it looks solid. Dumbest user/operator error AND worst abuse + STRIDE at trust boundaries. Do not trust tests, docs, or a green path. |
| Problem-First | Right problem? | Strip the proposed solution. Rebuild the problem. Say when the question is wrong. |
| Upside | What extra value sits in scope? | Hunt in-scope gain only. No new product, dependency, or scope. New packages still need New Dependency Protocol. |
| Outsider | Would a first-time user get this? | Drop insider context. Flag jargon, opaque names, missing first-caller steps. Unknown term → treat as unknown. |
| Builder | What is the first concrete step? | Prove it can ship. Name the first command, plan step, or `Verify`. No first step → say so. |

**Constraint Block** (all except Outsider): §4.13. Loaded test stack and coverage gate (cite skill). New Dependency Protocol (cite loaded skill). MIT/Apache-2.0/BSD-like. No scope expand. No edits. Cite path/symbol for code claims.

Outsider gets the framed question only — no Constraint Block, no tech-skill dump.

## Exam

Target is the **built result**, not a decision fork. Frame: "Does this implementation hold against the plan R{n} and in-scope code?" Include plan path, every R{n}, review brief path, Implementation Status Table, in-scope paths, latest Step NR review paths. No steering toward Holds. Do not rubber-stamp.

Follow Lite/Full stages with these overlays. Advisors still do not edit.

**Skeptic Exam addendum** (mandatory; overrides schema length for Skeptic only):

- Default hypothesis: this cannot work.
- Read shipped code, tests, and `Verify` commands. Cite path/symbol for every claim.
- Take the implementation apart. Question every invariant, happy path, error path, and "obvious" guard.
- Find the hair in the soup: wrong default, off-by-one, silent swallow, copy-paste, tests that cannot fail, `Verify` that does not prove the claim.
- Both axes: the dumbest caller/operator mistake and the most malicious misuse.
- Do not stop at the first flaw. Kill shots first, then nits that become fatal in combination.
- Schema (Skeptic in Exam): 400–700 words, no preamble: Position · Argument · Fatal risks (ordered) · Nits · Evidence (path/symbol) · Do this.

Other views keep 150–250 words. Exam Ask:

| View | Exam Ask |
|------|----------|
| Skeptic | Why can the shipped result not work? Apply the Skeptic Exam addendum. |
| Problem-First | Did we solve the stated R{n}, or a different problem? |
| Upside | What in-scope gap remains in what shipped? Defects only. No gold-plating. |
| Outsider | Would a first-time user or caller of the built API get this? |
| Builder | Does `Verify` prove the claim? Name the first command that must fail if it does not hold. |

**Peer review extra (Exam):** Flag any letter that did not cite path/symbol, that accepted a green path without trying to break it, or that treated Skeptic as optional.

**Chairman:** **Holds** · **Does not hold**. Kill shots and §4 violations → Does not hold. Isolated nits that are not fatal and do not violate §4 → risks; still Holds. No "it depends" without Grill Me. Do not emit plan `C{n}`.

Loop status: `done` (Holds) · `exam-fail` (Does not hold) · `grill-me` · `re-council`. Parent implement remediates `exam-fail`, then re-runs Exam as `_<n>`. Cap 2 reruns on the same root cause → blocker.

## Sweep

From plan and review. No `councils/` file. No peer review.

Per view: 2–5 bullets. Cite path/symbol for code facts. Empty row → re-run that view.

Classify each bullet as one of: **Grill Me** (user fact) · **Council candidate** (≥2 valid options, high cost of error) · **Act** (plan constraint or review finding).

**Review overlays:** view defect → finding; cite view in `Context`. Skeptic: parts **and** composition per `workflow-review.md` Skeptic pass; hair in the soup; `none` only after both hunts ran. Upside: in-scope defects only, not gold-plating. Problem-First mismatch vs request/plan → Error. Builder: `Verify` cannot prove the claim → Error.

```markdown
## Perspective Sweep
| View | Caught |
|------|--------|
| Skeptic | |
| Problem-First | |
| Upside | |
| Outsider | |
| Builder | |

**Grill Me candidates:** …
**Council candidates:** … | none
```

Emit: plan chat + Context Anchor; review Perspective Sweep section.

## Grill Me ↔ Council

Grill Me = user facts. Council = pressure-test a choice. Loop until blocking ambiguity is gone. Exam is not a choice council — do not skip it because the plan already chose.

| From → To | When |
|-----------|------|
| Sweep/Council → Grill Me | Needs a user fact. Tag `Source: Sweep` or `Source: Council`. |
| Grill Me → Council | ≥2 valid options, high cost of error, not taste. |
| Council → Plan `C{n}` | Chairman recommendation. |
| Grill Me only | User-only fact; no technical fork. Do not council. |

Do not council a how-to after the choice. Do not Grill Me a rubber-stamp.

**Stop:** no blocking ambiguity · same question would repeat · 3 Decision Loop iterations → Open Questions; user chooses or accepts risk.

## Lite / Full stages

1. **Context** — User text, attachments, plan/review/ADR, in-scope code. Tech Load when code is in scope; pass **constraints**, not full skill text. Reuse prior `councils/council_<slug>*` on the same fork unless new evidence. Outsider context stays thin. Exam: built files, tests, `Verify`, plan R{n}, latest reviews.
2. **Frame** — One neutral prompt: decision, user context, repo constraints/paths, stakes, Constraint Block (omit for Outsider). No steering. Save in artifact. Exam: use the Holds question in **Exam**.
3. **Advisors** — Spawn all five **in parallel**. Sequential contaminates.
   - Each: view + framed question + addendum + schema. Lean into the angle. Do not hedge. Exam: attach Skeptic Exam addendum to Skeptic only.
   - Cursor: `Task` `generalPurpose`, `run_in_background: true`, `model: inherit`, self-contained prompt, no edits/git.
   - Same model as the parent. Never omit `model`. Never pick a cheaper, faster, or smaller slug.
   - Copilot / no subagents: sequential; before each: "You have not seen the other advisors." Mark `degraded`. Prefer Cursor for Full and Exam.
   - Schema (150–250 words, no preamble): Position · Argument · Fatal risk / Extra value · Evidence (path/symbol) · Do this. Exam Skeptic: 400–700 words per Exam addendum.
4. **Peer review (Full and Exam, not Lite)** — Shuffle A–E; do not default Skeptic=A. Mapping secret until chairman. Five parallel reviewers; framed question + A–E only; no advisor names. Same `Task` settings: `model: inherit`. Each answers (<200 words): strongest letter and why · biggest blind spot · what all five missed · least-evidenced claim. Exam: also the peer-review extra in **Exam**.
5. **Chairman** — Parent agent. De-anonymize. §4.13. May dissent from the majority; explain. No "it depends" without a recorded user fact — emit Grill Me instead.
   - Verdict: Agrees · Clashes (do not smooth) · Blind spots (peer-review only, or all missed) · Recommendation (one call) · One thing to do first · Grill Me Follow-ups (`Source: Council`; omit if none) · `C{n}` mapping (omit if not in a plan loop). Exam: Holds / Does not hold per **Exam**; omit `C{n}`.
6. **Ambiguity loop** — Follow-ups → one Grill Me round (`workflow-plan.md` template). Answers change the fork → re-frame, re-run, `_<n>`. User-only facts still missing → stay in Grill Me. Clear recommendation → `C{n}`, stop. Enforce stop rules above. From plan Decision Loop: return to Reconcile. Exam: `exam-fail` → parent remediates and re-Exams; do not open a plan Decision Loop.
7. **Output** — `councils/council_<slug>.md`; Exam: `councils/council_<slug>-exam.md`; reruns `_<n>` starting at 2. Slug: lowercase, punctuation/whitespace → `-`. Artifact: framed question, mode, paths/skills, advisors, letter mapping (Full/Exam), peer reviews (Full/Exam), verdict, loop status (`done` / `grill-me` / `re-council` / `exam-fail`). Chat: verdict sections only. No advisor essays. No HTML.

## Completion

Status table: mode, path, loop status, `C{n}` (omit `C{n}` on Exam). Goal verdict = Recommendation one-liner (Exam: Holds / Does not hold). Risks ≤5 from Skeptic + clashes. Do not start `/implement` from a decision verdict. Exam `exam-fail` continues the current implement run.

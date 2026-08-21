# Council Workflow

Load on `/council`. Apply `copilot-instructions.md` Sections 2–4. Do not implement. Do not edit code.

Five thinking styles, not job titles. Independent advisors → anonymous peer review (Full) → chairman. Rule Priority §4.13.

## When

**Run:** architecture, public API, security vs performance, irreversible choice, genuine multi-option fork.

**Skip:** lookups, post-choice how-to, `/review`, casual should-I, write-X. Explicit trigger on a validation-only ask: still run; do not rubber-stamp.

Vague question → one clarifier, then proceed.

## Modes

| Mode | When | How | Artifact |
|------|------|-----|----------|
| **Sweep** | `/plan` Stage 2, `/review` Stage 4 | Same agent. No subagents. | none |
| **Lite** | Plan Decision Loop default; user says `quick`/`lite` | 5 advisors + chairman | `councils/council_<slug>[_<n>].md` |
| **Full** | `/council` default; security, public API, irreversible | 5 advisors + 5 peer reviews + chairman | same |

## Lenses

Skip none. Do not rename.

| Lens | Job |
|------|-----|
| Contrarian | Find the fatal flaw. Dig if it looks solid. Misuse/abuse + STRIDE at trust boundaries. |
| First Principles | Strip the proposed solution. Rebuild the problem. Say when the question is wrong. |
| Expansionist | In-scope upside only. No new product, dependency, or scope. New packages still need New Dependency Protocol. |
| Outsider | No insider context. Flag jargon, opaque names, missing first-caller steps. Unknown term → treat as unknown. |
| Executor | Can this be done? Name the first command, plan step, or `Verify`. No first step → say so. |

**Constraint Block** (all except Outsider): §4.13. Loaded test stack and coverage gate (cite skill). New Dependency Protocol (cite loaded skill). MIT/Apache-2.0/BSD-like. No scope expand. No edits. Cite path/symbol for code claims.

Outsider gets the framed question only — no Constraint Block, no tech-skill dump.

## Sweep

From plan and review. No `councils/` file. No peer review.

Per lens: 2–5 bullets. Cite path/symbol for code facts. Empty row → re-run that lens.

Classify each bullet as one of: **Grill Me** (user fact) · **Council candidate** (≥2 valid options, high cost of error) · **Act** (plan constraint or review finding).

**Review overlays:** lens defect → finding; cite lens in `Context`. Expansionist: in-scope defects only, not gold-plating. First Principles mismatch vs request/plan → Error. Executor: `Verify` cannot prove the claim → Error.

```markdown
## Perspective Sweep
| Lens | Caught |
|------|--------|
| Contrarian | |
| First Principles | |
| Expansionist | |
| Outsider | |
| Executor | |

**Grill Me candidates:** …
**Council candidates:** … | none
```

Emit: plan chat + Context Anchor; review Perspective Sweep section.

## Grill Me ↔ Council

Grill Me = user facts. Council = pressure-test a choice. Loop until blocking ambiguity is gone.

| From → To | When |
|-----------|------|
| Sweep/Council → Grill Me | Needs a user fact. Tag `Source: Sweep` or `Source: Council`. |
| Grill Me → Council | ≥2 valid options, high cost of error, not taste. |
| Council → Plan `C{n}` | Chairman recommendation. |
| Grill Me only | User-only fact; no technical fork. Do not council. |

Do not council a how-to after the choice. Do not Grill Me a rubber-stamp.

**Stop:** no blocking ambiguity · same question would repeat · 3 Decision Loop iterations → Open Questions; user chooses or accepts risk.

## Lite / Full stages

1. **Context** — User text, attachments, plan/review/ADR, in-scope code. Tech Load when code is in scope; pass **constraints**, not full skill text. Reuse prior `councils/council_<slug>*` on the same fork unless new evidence. Outsider context stays thin.
2. **Frame** — One neutral prompt: decision, user context, repo constraints/paths, stakes, Constraint Block (omit for Outsider). No steering. Save in artifact.
3. **Advisors** — Spawn all five **in parallel**. Sequential contaminates.
   - Each: lens + framed question + addendum + schema. Lean into the angle. Do not hedge.
   - Cursor: `Task` `generalPurpose`, `run_in_background: true`, self-contained prompt, no edits/git.
   - Copilot / no subagents: sequential; before each: "You have not seen the other advisors." Mark `degraded`. Prefer Cursor for Full.
   - Schema (150–250 words, no preamble): Position · Argument · Fatal risk / Upside · Evidence (path/symbol) · Do this.
4. **Peer review (Full only)** — Shuffle A–E; do not default Contrarian=A. Mapping secret until chairman. Five parallel reviewers; framed question + A–E only; no advisor names. Each answers (<200 words): strongest letter and why · biggest blind spot · what all five missed · least-evidenced claim.
5. **Chairman** — Parent agent. De-anonymize. §4.13. May dissent from the majority; explain. No "it depends" without a recorded user fact — emit Grill Me instead.
   - Verdict: Agrees · Clashes (do not smooth) · Blind spots (peer-review only, or all missed) · Recommendation (one call) · One thing to do first · Grill Me Follow-ups (`Source: Council`; omit if none) · `C{n}` mapping (omit if not in a plan loop).
6. **Ambiguity loop** — Follow-ups → one Grill Me round (`workflow-plan.md` template). Answers change the fork → re-frame, re-run, `_<n>`. User-only facts still missing → stay in Grill Me. Clear recommendation → `C{n}`, stop. Enforce stop rules above. From plan Decision Loop: return to Reconcile.
7. **Output** — `councils/council_<slug>.md`; reruns `_<n>` starting at 2. Slug: lowercase, punctuation/whitespace → `-`. Artifact: framed question, mode, paths/skills, advisors, letter mapping (Full), peer reviews (Full), verdict, loop status (`done` / `grill-me` / `re-council`). Chat: verdict sections only. No advisor essays. No HTML.

## Completion

Status table: mode, path, loop status, `C{n}`. Goal verdict = Recommendation one-liner. Risks ≤5 from Contrarian + clashes. Do not start `/implement` from a verdict.

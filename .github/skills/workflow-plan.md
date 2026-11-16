# Plan Workflow

Load on `/plan`, or from `/evolve`. Also load now: `tech-spec.md`, `tech-reqtrace.md`, `tech-docs.md`, `tech-sweep.md`. Apply `copilot-instructions.md`.

A plan is the handoff to a **weaker executing agent**. Locked `How`, Before/After, and test cards leave no choice. A person reads Step Overview and Expected Experience. Prefer fenced snippets over prose for types, APIs, algorithms, control flow, and file shape; prose carries why and constraints. Never compress step `How`. Never copy requirements or test cards into the plan: link. Do not treat files under `plans/` as style examples unless the user names one.

## Modes

| Mode | When | Approval | Open points |
|------|------|----------|-------------|
| Interactive | `/plan` | The person approves the plan | Grill Me in chat |
| Autonomous | Inside `/evolve` | Gate A already covers it; start implementing | Decision class → card in `spec/open.md`; the steps that do not depend on it continue |

## Stages

0. Requirements
1. Gather Context
2. Perspective Sweep
3. Grill Me
4. Reconcile
5. Decision Loop
6. Write Plan (read `workflow-plan-templates.md` now)
7. Coverage Check

## Stage 0 — Requirements

- A requirements source exists (`spec/`, user path, attached doc): read the entries in scope in full. Do not rewrite clear outcomes. Do not turn them into a task list.
- Else execute `workflow-requirements.md`, then continue.
- The plan links the area files. Map every requirement in scope in Target Solution and Coverage. Never cite requirement IDs inside a step `How`.

## Stage 1 — Gather Context

- Run the Tech Load Protocol (Section 3). Read before acting (`tech-docs.md` § Reading tiers). Record what was read and which docs are missing on the Context Anchor `Read:` line.
- Read relevant code, tests, docs, interfaces, build config. Read user-attached docs in full.
- Run the Impact search (`tech-docs.md` § Impact search). Enumerate all affected files, docs included. Each later lands in a step `Where` or `Docs` field, or in Out with a reason.
- ❗ A design that breaks a documented boundary, dependency, flow, or invariant is Decision class, never a silent choice.
- ❗ Check every existing member the plan names against the code by name. A snippet that calls a missing member is a gap.
- Identify interface candidates, hot paths, public API surface, and web UI journeys.

## Stage 2 — Perspective Sweep

Sweep per `tech-sweep.md` with all five views. **Ask** → Stage 3. **Council candidate** → Stage 5. **Act** → plan constraint.

## Stage 3 — Grill Me

Walk every topic of the table. Classify each open point (Section 4.16): Covered, Detail, or Assumption → decide and continue. ❗ Ask only Decision class. Ask all in one round. Never re-ask; follow up only on new ambiguity and cite the prior answer.

❗ Record one line per topic in the Context Anchor **Topics**: what was settled, and its class with the source. A topic that does not apply says `n/a — {why}`. A topic without a line was not walked.

```text
Right: Performance: hot path = the row loop in `InvoiceReader.Read`; no allocation per row (Assumption: no budget stated)
Right: Limits: import size 1 GB, set by the end user, secure by default (Decision: Q2 answered)
Right: Concurrency: n/a — single-threaded CLI, no shared state
Wrong: Performance: considered.
Wrong: Security: OK.
```

| Topic | What to settle |
|-------|----------------|
| Outcomes | Concrete acceptance, not “it builds” |
| Test content | The card minimum: scenarios, edges, constellations, contradictions, gaps, what is out, time budget (`tech-test.md` § Card tests). Cards live in `spec/tests/`. Code tests follow from the written code at implement time (`tech-test.md` § Code tests); never settle them here |
| Public API | Snippet plus usage; interfaces, traits, or contract types the steps implement |
| Agent surface | Which surface an agent uses (CLI, REST, MCP, library) and its parity with the UI, or the waiver (`tech-agent-access.md`) |
| Web UI | Dark mode, responsive, tables, journeys, debug story (`tech-web.md`, `tech-playwright.md`, `tech-test.md`) |
| Performance | Named hot paths, time and memory budgets, allocations per item |
| Errors | Each expected failure and its result type or error code; empty, missing, malformed, and oversized input |
| Security | Each trust boundary; per external input the STRIDE threats and their mitigations |
| Limits | Threat, default or range, constant vs configurable, posture and why, register and policy slots (`tech-limits.md`) |
| Concurrency | Shared mutable state, TOCTOU, async interleaving; the chosen primitive (Section 4.3) |
| Compatibility | What breaks for existing users in persisted formats, public API, and config keys; the migration |
| Dependencies | Each new package, vendored file, or tool: id, exact version, purpose, license, alternatives including none (Section 4.7) |
| Architecture | The component that owns the change; its allowed dependencies (Overview **May depend on**); a break is Decision class |
| Debug | Smallest command that reproduces, filterable tests, traces |
| Misuse | How a caller can use the API wrongly, how an attacker can abuse it, and what makes each impossible or visible |
| Scale | Same edit on more than ten call sites: approved script or codemod |
| Docs | Which docs change; mismatches found in Stage 1 (which side is right); gaps beyond touched members (Out with `/docs-repair`, or in scope) |

Form: Section 4.16, with `**Source:** Sweep | Council | Plan | Requirements`.

## Stage 4 — Reconcile

- Cross-check request, requirements, answers, Sweep, council verdicts, code, docs, decision records, and Section 4.
- Record a preference when both sides cannot hold (`C{n}`; form: `workflow-plan-templates.md` § Closing sections). Apply Section 4.13, then an explicit user choice, then a scope split.
- Align docs, code, and tests when the source of truth is unclear; ask only when Decision class.
- Write Plan when no undecided preference blocks scope. Else Stage 5.

## Stage 5 — Decision Loop

Grill Me ↔ Council (`workflow-council.md`) until no blocking fork. Lite default; Full for `/council`, security, public API, or irreversible choices. Verdict → `C{n}`. Cap 3 iterations. Do not write the plan while one is open, except in Autonomous mode: card it and plan around it.

## Stage 6 — Write Plan

Read `workflow-plan-templates.md`.

- Path: user path, else `plans/plans_<slug>.md`. English. Slug: lowercase, punctuation and whitespace → `-`, collapse, trim, fallback `task`.
- Write new or changed test cards to `spec/tests/<area>.md` first (`tech-spec.md` § Test card). Per behavior, each applicable class of `tech-test.md` (happy, error, boundary, collection 0/1/2, absence, concurrency, trust-boundary) has a card, or an `Out` with a reason. So does each edge, constellation, and contradiction named in Stage 3. **Content** states setup, action, and observable, so an executing agent proves the card without inventing the scenario.
- New cards are `proposed` in Interactive mode and `active` in Autonomous mode (Gate A covers them). Before changing or retiring an existing card: `reqtrace show <TEST-ID>`; each test it lists gets a step.
- After writing the cards: `reqtrace check`. L4 and L6 on entries this plan builds are expected (`tech-reqtrace.md` § Findings); fix every other Error now.
- Write every section of `workflow-plan-templates.md` § Plan Structure, in that order, from its template.
- Every file named in the plan is a clickable relative link (Section 4.6).
- Do not present the plan. Run Stage 7.

## Step Rules

Soft budgets; justify when exceeded; do not split an atomic cutover just to hit a number:

- ≤ 12 production files and ≤ ~3000 production lines per step. Tests do not count.
- Prefer additive edits when the file shape can stay.
- Do not list one file as `primary` in many steps unless each increment is a complete Experience.
- Leave each step as something a person can see or try.

Required:

- Freeze file shape in Target Solution. `After` is that shape, not an intermediate.
- Write new types complete (signature, body, errors, tests) before consumers call them.
- Keep rename or move separate from behavior change.
- Ship tests with the behavior they prove. Dedicated test steps are welcome when cases are large; do not dump all tests at the end.
- Size a step so its Before/After can be accepted.
- Order topologically. State depends-on.
- ❗ Reject a `How` that allows more than one implementation, or whose `After` is not Target Solution for primary files.
- ❗ Reject a step `After` whose public signature or interface member differs from **API and interfaces**.
- Close with Requirements fit.

**Slices:** group steps by what becomes tryable. Cut a real thin slice; do not fake end-to-end. Ban scaffolding later steps delete. Build each cross-cutting concern in target shape. Extract a repeated pattern before a second slice copies it. Preserve layer boundaries.

## Stage 7 — Coverage Check

Run after the plan is written. ❗ Walk twice. Patch until clean. After every patch, run both walks and the three checks again.

1. **Requirements walk:** re-read the linked entries in full; never treat the plan as a copy of them. Every requirement in scope lands in Target Solution and in a step `Acceptance`; an unmapped one = gap. Every test ID lands in a step `Tests` field and has a card with **Content**. An applicable `tech-test.md` class with no card and no Out = gap. IDs never land in `How`.
2. **Conversation walk:** re-read the conversation, answers, Sweep, council, and attached docs in full, then the plan. Every relevant ask, constraint, non-goal, named type or path or command, accepted proposal, rejected option with leftover constraint, and named edge lands on an ID, a card **Content**, Out, or `C{n}`. “Implied” without a citation = gap.

Then:

- **Link check:** every `#` link matches a heading slug.
- **Experience check:** each Expected Experience claim traces to a step `Experience`. Each `Not yet` is covered by a later block or Out.
- **Doc check:** each Impact-search hit and Doc delta line lands in a step `Docs` field or in Out with a reason.

## Checklist Rules

- Flat ordered list: Step N, dedicated test steps, Step `{n}R`, Requirements fit. Link each entry.
- Tick status on all three surfaces together. Zero Error findings at each review gate before the next step.

## Completion

Both walks green. Chat: the plan path only. Interactive: wait for explicit approval (Section 6). Autonomous: continue.

Interactive approval, before `/implement` starts:

1. Set every `proposed` requirement and card the plan links to `active`.
2. Run `reqtrace accept --allow-gap "tests follow in /implement of plans/plans_<slug>.md"` (`tech-reqtrace.md` § Accept). Refused for anything but L4 or L6 → fix, accept again.

A question or a change request is no approval: patch the plan, run Stage 7, wait again.

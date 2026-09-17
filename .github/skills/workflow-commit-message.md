# Commit-Message Workflow

Load on `/commit-message`, `/commit_message`, or a request to write a commit message for a named scope. Apply `copilot-instructions.md` Section 2 (chat language). This skill is the contract for a **complete** message. Git commit, push, and other repo writes after the file are **out of scope**.

**Purpose:** Write one English, paste-ready commit message for the given scope. The message must be **specific enough to use without opening the diff**: named outcomes, before/after behavior, and at least one example when there is a usage surface. When the scope is a **release** (or the user asks), also update `CHANGELOG.md`. Type prefix is the scan header. Voice is the person who lives with the result. This workflow **ends** when that file (and CHANGELOG when Stage 5 ran) is written.

**Hard rules:**

- Do not invent a scope. No scope in the prompt → Stage 3, then stop until answered.
- Require exactly one type from the closed set. Prefix is not a substitute for the subject or body.
- Never dump a file list, hunk list, or diff stats. Name the **thing the person uses** (command, type, flag, workflow, contract), not the tree.
- Never mention test counts, coverage, CI, build logs, or “all tests passed”.
- Never write an implementation diary, ticket/plan/review IDs, or Conventional-Commit area scopes (`feat(cli):`).
- ❗ A slogan is incomplete. If a later reader cannot tell what to type, call, or expect, rewrite before emitting.
- Skip Tech Load. Do not edit production code. Overwrite `commit_message.md` unless the user names another path. **Exception:** `CHANGELOG.md` when Stage 5 runs.
- Do not `git add`, `git commit`, `git push`, or otherwise record the message in git as part of this workflow. That step is out of scope.

## Stage Order

1. Resolve Scope
2. Gather Evidence
3. Grill Me
4. Write Message
5. Changelog (when in scope)

## Stage 1 — Resolve Scope

Take the bound from the user prompt. Confirmed when named. Examples, not a closed set: staged changes, a plan, a review, a release, any other bound the user names.

No scope, two scopes, or an ambiguous bound → Stage 3. Do not pick a default.

One message per named bound. Unrelated concerns in the same bound → Stage 3 (one message vs split). Do not merge them silently.

## Stage 2 — Gather Evidence

Inspect only the named scope. Choose how: whatever actually contains that work (artifacts, tree, conversation, or a VCS view if the scope is VCS). Do not assume a VCS is in play. Read a named plan or requirements file **in full**.

Extract a **detail list** before writing. Do not summarize to a theme label and stop.

From the evidence, record:

- who is affected (end user, operator, caller, contributor)
- what they can do now that they could not (or no longer get wrong)
- **names**: commands, flags, types, error codes, workflow triggers, config keys, public contracts
- **before → after** of observable behavior
- one **example** per usage surface (invocation, snippet, scenario)
- breaking edges, if any

Read for behavior, intent, type, and those names. Follow this skill’s shape; do not mimic other messages in the repo (they may be the short style this skill forbids).

Empty evidence → Stage 3. Do not write a placeholder.

Plan scope: delivered outcomes only. Ignore remaining `⬜` work.
Review scope: resulting effect of applied remediations, not finding IDs.
Release scope: user-facing and contributor-facing effects since the previous changelog heading.

## Stage 3 — Grill Me

Run for unresolved ambiguity. Ask every open question in one round. Do not re-ask answered questions. Do not proceed on a guess.

Mandatory when:

- scope is missing or ambiguous
- evidence is empty
- purpose cannot be inferred
- type cannot be inferred; never default to `chore`
- `feat` and `fix` (or other types) both apply → one message vs split
- two or more unrelated themes might be separate messages (one message vs subset)
- it is unclear who the change is for (end user vs contributor) and the evidence does not settle it
- a breaking change may exist and is not obvious
- it is unclear whether `CHANGELOG.md` should be updated (release vs ordinary work)

Skip when scope is confirmed and purpose and type are obvious. Skip changelog questions when the user already said “release” or “no changelog”.

Do **not** Grill Me to shorten a message. If evidence is rich, the message must carry that richness.

```markdown
## Q{n} — {topic}
**Source:** Commit-message
**Context:** {1-3 sentences}
**Question:** {single-part question}
**Options:** 1) {option} · 2) {option} · 3) {option} · or free-text
```

## Stage 4 — Write Message

Overwrite `commit_message.md` at repo root (gitignored). User path overrides. The file **is** the commit message: no wrapper title, no YAML, no “Commit message:” label. Trailing newline.

Language: English. Wrap body near 72 characters, except URLs and fenced examples.

### Type (required)

Closed set. Exactly one. Lowercase. Then optional `!` if breaking, then `: ` then the subject.

| Type | When |
|------|------|
| `feat` | New capability for an end user or contributor |
| `fix` | Wrong behavior becomes correct |
| `perf` | Same behavior, faster or fewer allocations |
| `docs` | Documentation only |
| `refactor` | Internal shape only, same behavior |
| `test` | Test capability — not “N tests passed” |
| `chore` | Last resort: tooling/workflow with no user-facing outcome |

Do not invent types. Do not use `style`, `ci`, or `build`. Do not put an area in parentheses. Area belongs in the subject if it matters.

### Specificity (required)

Incomplete unless all of these hold:

- A reader who did not see the diff can act: run the new command, call the new API, follow the new rule, or avoid the old footgun.
- The body names the commands, types, flags, workflows, or contracts that changed — not “the workflow” or “the stack skill”.
- Before and after of the observable result are stated in prose or by example.
- Each usage surface has a short example (command, snippet, or scenario). Typo-only and comment-only changes may skip the example.
- Theme labels (“Workflows”, “Stack work”) are not a substitute for those names and examples.

Wrong: three sentences that could apply to any refactor.
Right: named trigger, named rule, example of what you type and what you get.

### Shape

```
{type}: {subject}

{purpose}

{block…}

{breaking footer, if any}
```

1. **Subject line** — `{type}: ` plus imperative present-tense outcome. No trailing period. Not the mechanism. Soft 50 characters for the description after `: `. Hard 72 for the whole first line. `feat: updates` / `fix: bug` is invalid.
2. **Purpose** — After a blank line. **3–6 sentences**: who it is for, what is now true, what is no longer true, and a pointer at the first example. Not one slogan sentence.
3. **Blocks** — Default on. Skip blocks only when the scope is a **single** behavior and the purpose already contains the name, before/after, and example.
   - One block per theme (capability, audience, or purpose). Not per file, project, or hunk.
   - Block title: markdown `## {Theme}`. Never `#` (reserved; line 1 stays `{type}: {subject}`).
   - Blank line before and after the heading.
   - Then enough prose or `- ` bullets that each named change is specific: what to type or call, what happens, what stopped happening. Include a fenced example when the theme has a usage surface.
   - Product/end-user blocks first; contributor/API/workflow blocks after.
4. **Examples in the body** — Prefer a fenced command or a short usage snippet over a metaphor. Keep it the **person’s** view (`dotnet-trace collect --profile cpu-sampling …`, `TryPair(..., out PairError error)`, `/requirements` then `/plan`). Do not paste production patches or whole files.
5. **Breaking** — Put `!` before `:` on the type line. After the body, a blank line, then `BREAKING CHANGE: ` plus what breaks, an example of the old call, and what to use instead. Do not hide a break in the purpose paragraph. Footer is only for this; no issue trailers.

### Voice

Write from the affected person’s seat: end user, operator, or contributor — whichever the change actually touches. Say what they can do, no longer must do, or no longer get wrong. Write “you” or the role (“callers”, “operators”). Never “this commit”, “changes include”, “updated X”, “refactored Y”, “addressed review comments”, `WIP`, or `misc`.

Concrete over abstract. Internals belong when the reader is a contributor and the internals **are** the product (API, skill, workflow, contract). Then name them.

### Ban list (message body and subject)

- file inventories, hunk lists, “N files changed”, path dumps
- tests run / passed / failed, coverage %, CI green
- step IDs, finding IDs, issue IDs, plan IDs, `Closes #…`, `Co-authored-by`
- `feat(area):` and any parenthetical Conventional-Commit scope
- `WIP`, `misc`, “addressed review”
- slogans with no names (`improve the workflow`, `follow intent then the skill`)
- secrets, tokens, PII

Allowed: a public type, command, flag, workflow trigger, or config key; a short fenced example of using it.

### Examples

Small (one behavior — purpose still has a name, before/after, and example):

```
feat: Let operators gate coverage with one local tool

You can list remaining exit-point gaps with one local tool and treat a
zero gap count as the release gate. You no longer install or version a
separate analysis library, and you no longer infer coverage from a
generic percentage.

After restore, run:

    dotnet tool run exitpointgaps --repo-root .

A passing run means summary.exitGapCount is 0. A failing run lists each
remaining exit (file, line, kind) so you can add a test and re-gate.
```

Large (blocks — each theme names the surface and shows how to use it):

```
feat: Let you capture requirements and illustrate before code

You get two first-class stages besides plan/implement/review. You can
freeze observable outcomes before a plan exists, and you can ship a
zippable HTML demo of one idea without touching product code. Chat
follows your language; plans and other lasting artifacts stay English.

## Requirements

Say `/requirements` (or capture the user-view system description before `/plan` when none exist).
You get `REQ{n}` rows (behavior, properties, shall-not) and a Done-when check you can
observe — not “it builds”. If you already attached a list, the plan
reads it instead of rewriting it.

Example: “Export must fail closed when the path is empty” becomes
`REQ10` with Done-when: the operator sees a path-empty error and no file.

## Illustrate

Say `/illustrate` for one concept. You get an HTML zip you can open
without the repo. The agent asks before vendoring libraries (download
into `vendor/`; no CDN in the file). Slideshows include an overview
plus next/prev. Playwright verifies at create time only.

## Review and council

`/review` analyzes and runs associated tests unless you explicitly ask
for static-only (`static review`, `code only`). `/council` runs five
views in the same session; do not spawn subagent advisors.
```

Breaking:

```
feat!: Switch the public CLI to a single run command

You invoke one command to gate a repo. Split verbs (plan / run /
report) are gone, so scripts and docs that called them will fail.

Old:

    exitpointgaps plan --repo-root .
    exitpointgaps run --repo-root .

New:

    exitpointgaps --repo-root .

BREAKING CHANGE: The previous command names no longer exist. Use the
single run command with the same --repo-root argument. Drop `plan` and
`run` subcommands from scripts.
```

## Stage 5 — Changelog

Run when the named scope is a **release**, the user asked to update the changelog, or Grill Me concluded that a release entry is needed. Otherwise skip.

- Edit `CHANGELOG.md` (repo root) unless the user names another path.
- Keep existing style: newest heading first, grouped bullets, narrow tables.
- Add or extend `## Unreleased` when no version heading exists yet; use `## {version}` when the user named a version.
- Write user-visible effect, not a file list. Match the commit-message voice (specific names, not slogans).
- Breaking changes get their own short subsection.
- Do not invent versions. Do not delete historical headings.

Example addition:

```markdown
## Unreleased

### Agent workflow

- `/requirements` formalizes high-level `REQ{n}` user-view requirements before `/plan` when none exist
- Council runs in the same agent; no subagent advisors
```

## Completion

Chat: subject line, artifact path, changelog path if Stage 5 ran. Do not paste the body.

| Item | Status |
|------|--------|
| Scope | {confirmed scope} |
| Type | {type}{! if breaking} |
| Artifact | `commit_message.md` |
| Changelog | `CHANGELOG.md` · skipped |
| Goal | Message complete / Blocked (Grill Me) |

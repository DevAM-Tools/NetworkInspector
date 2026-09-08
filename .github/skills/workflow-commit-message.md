# Commit-Message Workflow

Load on `/commit-message`, `/commit_message`, or a request to write a commit message for a named scope. Apply `copilot-instructions.md` Section 2.

**Purpose:** Write one English, paste-ready commit message for the given scope. Using this skill is the style. Type prefix is the scan header. Body describes purpose and effect from the person who lives with the result.

**Hard rules:**

- Do not invent a scope. No scope in the prompt → Stage 3, then stop until answered.
- Require exactly one type from the closed set. Prefix is not a substitute for the subject or body.
- Never list files, paths, hunks, or diff stats in the message.
- Never mention test counts, coverage, CI, build logs, or “all tests passed”.
- Never write an implementation diary, ticket/plan/review IDs, or Conventional-Commit area scopes (`feat(cli):`).
- Skip Tech Load. Do not edit production code. Overwrite only `commit_message.md` unless the user names another path.

## Stage Order

1. Resolve Scope
2. Gather Evidence
3. Grill Me
4. Write Message

## Stage 1 — Resolve Scope

Take the bound from the user prompt. Confirmed when named. Examples, not a closed set: staged changes, a plan, a review, any other bound the user names.

No scope, two scopes, or an ambiguous bound → Stage 3. Do not pick a default.

One message per named bound. Unrelated concerns in the same bound → Stage 3 (one message vs split). Do not merge them silently.

## Stage 2 — Gather Evidence

Inspect only the named scope. Choose how: whatever actually contains that work (artifacts, tree, conversation, or a VCS view if the scope is VCS). Do not assume a VCS is in play.

Read for behavior, intent, and type. Follow this skill’s shape; do not mimic other messages.

Empty evidence → Stage 3. Do not write a placeholder.

Plan scope: delivered outcomes only. Ignore remaining `⬜` work.
Review scope: resulting effect of applied remediations, not finding IDs.

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

Skip when scope is confirmed and purpose and type are obvious.

```markdown
## Q{n} — {topic}
**Source:** Commit-message
**Context:** {one sentence}
**Question:** {single-part question}
**Options:** 1) {option} · 2) {option} · 3) {option} · or free-text
```

## Stage 4 — Write Message

Overwrite `commit_message.md` at repo root (gitignored). User path overrides. The file **is** the commit message: no wrapper title, no YAML, no “Commit message:” label. Trailing newline.

Language: English. Wrap body near 72 characters, except URLs.

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

### Shape

```
{type}: {subject}

{purpose}

{block…}

{breaking footer, if any}
```

1. **Subject line** — `{type}: ` plus imperative present-tense outcome. No trailing period. Not the mechanism. Soft 50 characters for the description after `: `. Hard 72 for the whole first line. `feat: updates` / `fix: bug` is invalid.
2. **Purpose** — After a blank line. One short paragraph: what this change is for and what is now true that was not true before.
3. **Blocks** — Use when the change is large or has two or more themes. One block per theme (capability, audience, or purpose). Not per file, project, or hunk.
   - Block title: markdown `## {Theme}` so the paste file and forges that render CommonMark separate blocks. Never `#` (reserved; line 1 stays `{type}: {subject}`).
   - Blank line before and after the heading. Then 2–5 sentences or `- ` bullets of effects.
   - Product/end-user blocks first; contributor/API/workflow blocks after.
4. **Small change** — Type line + purpose only. No blocks.
5. **Breaking** — Put `!` before `:` on the type line. After the body, a blank line, then `BREAKING CHANGE: ` plus what breaks and what the affected person should do instead. Do not hide a break in the purpose paragraph. Footer is only for this; no issue trailers.

### Voice

Write from the affected person’s seat: end user, operator, or contributor — whichever the change actually touches. Say what they can do, no longer must do, or no longer get wrong. Prefer “you” or the role (“callers”, “operators”). Never “this commit”, “changes include”, “updated X”, “refactored Y”, “addressed review comments”, `WIP`, or `misc`.

Abstract: purpose and effect. Concrete enough to be true. Not slogans. Not internals unless the reader is a contributor and the internals are the product (API, skill, workflow, contract).

### Ban list (message body and subject)

- file names, paths, symbols-as-inventory, “N files changed”
- tests run / passed / failed, coverage %, CI green
- step IDs, finding IDs, issue IDs, plan IDs, `Closes #…`, `Co-authored-by`
- `feat(area):` and any parenthetical Conventional-Commit scope
- `WIP`, `misc`, “addressed review”
- secrets, tokens, PII

### Examples

Small:

```
feat: Let operators gate coverage with one local tool

You can run a single tool to list remaining exit-point gaps and treat a
zero gap count as the release gate. You no longer install or version a
separate analysis library.
```

Large (blocks):

```
feat: Give contributors one paste-ready commit-message shape

This change makes commit text describe purpose and effect from the
person who lives with the result, so history stays readable instead of
turning into a file list or a test report.

## Operators

You name a scope — staged work, a plan, or a review — and get an English
message you can paste.

## Contributors

You get one Grill-Me-then-write path. The message stays free of paths,
test counts, and implementation diary. Large work lands as theme blocks,
not as a dump of hunks.
```

Breaking:

```
feat!: Switch the public CLI to a single run command

You invoke one command to gate a repo. The old split entry points are
gone.

BREAKING CHANGE: The previous command names no longer exist. Use the
single run command with the same repo-root argument.
```

## Completion

Chat: subject line, artifact path, status table, goal verdict, top risks (≤5). Do not paste the body.

| Item | Status |
|------|--------|
| Scope | {confirmed scope} |
| Type | {type}{! if breaking} |
| Artifact | `commit_message.md` |
| Goal | Ready to paste / Blocked (Grill Me) |

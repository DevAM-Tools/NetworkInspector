# Plan Templates

Load at `workflow-plan.md` Stage 6. Rules live there.

## Plan Structure

1. Step Overview (linked rows)
2. Expected Experience
3. Requirements — links to area files
4. Test cases — index only; cards live in `spec/tests/`
5. Summary / Context Anchor
6. Target Solution
7. Slices
8. API and interfaces — immediately before the steps
9. Steps (Shared Block; last = Requirements fit + `{n}R`)
10. Edge Cases and Risks
11. Decisions & Trade-offs (`C{n}`; omit when none)
12. Open Questions (each blocker per `tech-reporting.md`)
13. Closing Summary
14. Task Checklist

## Step Overview

Narrow table. ❗ Link every Step cell to its block (`#` + GitHub slug of the heading: lowercase, punctuation other than `-` dropped, spaces → `-`). Link `Step {n}R` to the block of Step `{n}`.

```markdown
| Step | Status | Delivers |
|------|--------|----------|
| [Step 1 — Create export](#step-1---create-export) | ⬜ | {one sentence} |
| [Step 1R — Review Step 1](#step-1---create-export) | ⬜ | Zero Error findings |
| [Step 2 — Requirements fit](#step-2---requirements-fit) | ⬜ | Every requirement Met |
| [Step 2R — Review Step 2](#step-2---requirements-fit) | ⬜ | Zero Error findings |
```

## Expected Experience

Directly after Step Overview. Aggregates step `Experience`; adds no new claim.

- No phases: one block `### Result`. Phases (>10 steps or several areas): one block `### Phase {P} — {title}` per phase, in step order; each step in exactly one phase.
- Describe the cumulative state. Mark what is new with `New:`.
- `How to try` is a runnable command, Playwright spec, or usage snippet.
- Each `Not yet` is covered by a later phase or Out. The last block’s is `nothing` or Out.

```markdown
## Expected Experience

### Phase 1 — Create and list exports
Steps: [Step 1](#step-1---create-export) · [Step 2](#step-2---list-exports)
After this phase a person can: create an export and see it listed.
They should see: one row per export; a duplicate name shows an error.
How to try: `dotnet run --project src/App -c Release -- export create Report`
Not yet: deleting an export.
```

## Requirements

```markdown
## Requirements

Source: [spec/requirements/export.md](../spec/requirements/export.md) — REQ-EXPORT-CREATE, REQ-EXPORT-NO-DUPLICATE
```

Reject the plan when a requirement in scope cannot be observed by a user.

## Test cases

```markdown
| ID | Case | Step | Status |
|----|------|------|--------|
| [TEST-EXPORT-NO-DUPLICATE](../spec/tests/export.md#test-export-no-duplicate) | Duplicate name rejected | [Step 2](#step-2---list-exports) | ⬜ |
```

Status mirrors the card’s `Status`. Test code in a plan is welcome; it never replaces **Content**.

## Summary / Context Anchor

Every line is required. An empty one says `none`.

```markdown
## Summary / Context Anchor

**Goal:** {one sentence: what exists after the last step}
**Loaded skills:** tech-csharp.md, tech-test.md, tech-tunit.md, tech-docs.md, tech-spec.md, tech-sweep.md
**Read:** architecture Overview, § Export · domain § Export names · missing: docs/concepts.md
**Sweep:** {the table of `tech-sweep.md`}
**Councils:** {links, or `none`} · **Decision Loop:** {n} iterations
**Topics:**
- Outcomes: {what was settled} ({Covered: source | Assumption | Decision: Q{n} or C{n} | n/a — why})
- {one line per Stage 3 topic, in table order}
**Assumptions:** {the default taken — what changes if it is wrong}
**Step dependencies:** Step 2 → Step 1 · Step 3 → Step 1, Step 2
**Coverage:** {both tables of § Coverage tables}
```

## Target Solution

- The concrete end state: types, files, APIs, data flow, invariants, algorithms, shown in snippets. Not a slogan. No step order.
- Map every requirement to a design element. Do not paste Done-when. Extra design with no requirement: justify or cut.
- SSOT for final file layout, data flow, invariants, algorithms. **API and interfaces** is the SSOT for signatures. A primary-file `After` that differs from either is an incomplete plan.
- **Limits:** one row per limit. A limit without threat or posture is incomplete. Each limit gets the limit cases of `tech-test.md` as cards.
- **Doc delta:** Overview rows and sections, concept and domain entries, README sections, guides, and decision records the end state changes, one line each. A new decision with rejected options, and every agreed architecture change, gets a decision record. Domain facts land in `docs/domain/`.

```markdown
| Limit | Default · range | Kind · who | Posture · why |
|-------|-----------------|------------|---------------|
| Import size (memory exhaustion) | 1 GB · 1 MB–host RAM | config · end user | secure: files from strangers |
```

## Slices

One line per slice: the steps, and what a person can try after them. Rules: `workflow-plan.md` § Step Rules.

```markdown
## Slices

1. Create and list — Steps 1–2 — a person creates an export and sees it listed.
2. Reject duplicates — Step 3 — a second create with the same name fails with a visible error.
```

## API and interfaces

The schematic contract the steps implement, immediately before them.

- Signatures and how types connect. Not bodies. One heading per group; above each fence, a link to the file.
- **Public API:** signatures a caller uses, then a usage snippet. Section 4.12 locks it.
- **Interfaces:** the type, its members, which type implements it, which calls it. Use the plan’s language (interface, trait, protocol).
- Neither exists: keep the heading, write one sentence.

````markdown
## API and interfaces

### Public API

[`src/Export.cs`](../src/Export.cs)

```csharp
public static class Export
{
    public static Result<ExportId> Create(string name);
}
```

Usage:

```csharp
Result<ExportId> created = Export.Create("Report");
```

### Interfaces

[`src/IExportStore.cs`](../src/IExportStore.cs)

```csharp
public interface IExportStore
{
    Result<ExportId> Add(string name);
    bool Contains(string name);
}
```

`FileExportStore` implements `IExportStore`. `Export.Create` calls `Add`.
````

## Shared Block

Field order: `What` → `Why` → `How` → `Experience` → `Acceptance` → `Tests` → `[Public API]` → `[Size]` → `[Context]` → `[Where]` → `Docs` → `Verify` → `Debug` → `[If it fails]`.

| Field | Required |
|-------|----------|
| `What`, `Why`, `How`, `Experience`, `Acceptance`, `Docs`, `Verify`, `Debug` | Always |
| `Tests` | Step ships behavior (cite card IDs) |
| `Public API` | Step ships or changes a public surface; matches **API and interfaces** |
| `Size` | Step may exceed the soft budget |
| `Context` | Omit only when no constraint or source exists |
| `Where` | Omit when no file is touched |
| `If it fails` | Schema, state, or external-system risk |

- ❗ `How` names the concrete implementation so another agent invents nothing: types, items, visibility, signatures, parameters, returns, call-site edits, validation, error paths, control flow, data flow, thread-safety, performance and security constraints, prerequisite state, rationale, edge cases.
- ❗ Every `How` has fenced **Before** and **After**: current code, then the Target Solution shape (real signatures and key bodies), anchored by path and symbol. Not stubs or an intermediate shape. New file: After only. Requirements fit: skip unless a gap needs a code fix; then stop and file the gap as a blocker.
- ❗ Cite a concrete source in `Context` when an external reference exists.
- Extra illustrative snippets are welcome. They never replace Before/After or card **Content**.
- `Where`: link, approximate lines, symbol; mark `primary`, `call-site`, or `additive`.
- `Docs`: every doc from the Impact search, as link plus section (Overview row, architecture section, concept or domain entry, README section, guide, decision record), and the comment work in code: contract doc comments with Related on new and changed members, block comments, tags, sections, `Sync:` pairs. Or `none — {why no doc states what this step changes}`. `After` shows doc comments of new members.
- `Verify`: exact Release command per the tech skill, plus expected result.

```markdown
## {ID} - {Title}
Status: ⬜ {Initial} · {Depends on}
### What
### Why
### How
Fenced **Before** / **After**.
### Experience
After this step a person can: …
They should see: …
How to try: {command | Playwright spec | usage snippet}
Not yet: …
### Acceptance
- [ ] {observable check}
### Tests
- TEST-EXPORT-NO-DUPLICATE — {add | run}
### Public API
### Size
Prod files: {n} · ~LOC: {n} · over budget because: {or n/a}
### Context
### Where
### Docs
- [docs/architecture.md](../docs/architecture.md) § {component} — {what changed}
- Code: doc comments on {members}; `Sync:` pair {file ↔ file}
### Verify
### Debug
Reproduce: …
First probe if red: …
UI: {Playwright trace / headed / n/a}
Collision: other agents locking build or test?
### If it fails
```

### Worked example

Copy the depth, not the content. An executing agent builds this step without one decision of its own.

````markdown
## Step 3 - Reject a duplicate export name
Status: ⬜ · Depends on: Step 1
### What
`Export.Create` returns `ExportError.DuplicateName` when the store already holds the name.
### Why
Two exports with one name make the older one unreachable by name.
### How
`Create` asks the store before it adds. No new type. Names compare ordinal and case-sensitive, as `IExportStore.Contains` does today. Add the enum member `DuplicateName` after `EmptyName` in `ExportError`.

Before ([`src/Export.cs`](../src/Export.cs) `Create`):
```csharp
public static Result<ExportId> Create(string name)
{
    return _Store.Add(name);
}
```
After:
```csharp
/// <summary>Creates an export under a name that is unique in the store.</summary>
/// <returns>The new id; <see cref="ExportError.DuplicateName"/> when the name exists.</returns>
public static Result<ExportId> Create(string name)
{
    // Names are unique per store; reject before the add so no partial row exists.
    if (_Store.Contains(name))
    {
        return Result<ExportId>.Fail(ExportError.DuplicateName);
    }

    return _Store.Add(name);
}
```
### Experience
After this step a person can: submit a name twice and get an error the second time.
They should see: `An export named "Report" already exists.` and still one row in the list.
How to try: run `dotnet run --project src/App -c Release -- export create Report` twice
Not yet: names that differ only in case count as different.
### Acceptance
- [ ] The second create prints the duplicate-name message.
- [ ] `export list` prints one row titled Report.
### Tests
- TEST-EXPORT-NO-DUPLICATE — add
### Public API
Signature unchanged. New error value `ExportError.DuplicateName`.
### Where
- [`src/Export.cs`](../src/Export.cs) ~L20 `Export.Create` (primary)
- [`src/ExportError.cs`](../src/ExportError.cs) `ExportError` (additive)
### Docs
- [README.md](../README.md) § Errors — new row `DuplicateName`
- Code: doc comment on `Create` (returns) and on `DuplicateName`
### Verify
`dotnet test src/Export.Tests/Export.Tests.csproj -c Release -- --treenode-filter "/*/*/ExportTests/*"` → all pass, including `CreateTwiceWithSameNameReturnsDuplicateName`.
### Debug
Reproduce: the `Verify` command with the one test name in the filter.
First probe if red: do both calls use the same store instance?
UI: n/a
Collision: rerun once alone before changing code.
````

```text
Wrong How: "Add duplicate detection to the export logic and handle the error appropriately."
           (which type, which check, which error, which message: the executing agent must invent all four)
```

## Requirements fit (last step)

```markdown
## Step {N} - Requirements fit
Status: ⬜ Depends on all prior steps
### What
Walk the built solution as a user. Check every requirement and test ID in scope.
### Why
A green build can still miss the user outcome.
### How
- Re-read the linked entries and cards. Ignore implementer intent.
- Per requirement: run Done-when. Cite evidence. Tick Met in the area file. Fill `Built in`.
- Per card: confirm the Content exists as a test that can fail. Add its marker and set `Status`.
- Mark ✅ only when the check holds with no caveats.
- Any ❌ or leftover ⬜ = blocker (`tech-reporting.md`).
### Experience
A person can execute every Done-when without reading the source.
### Acceptance
- [ ] Every requirement in scope Met ✅
- [ ] Every test ID ✅
### Verify
Zero leftover ⬜.
### Debug
Re-run the failing Done-when alone. Check for a collision before redesign.
```

## Coverage tables

Include every requirement, test ID, and answer as a row. Record dropped items with a reason. Write both into the Context Anchor.

```markdown
**Coverage (requirements → plan):**

| ID | Lands in |
|----|----------|
| REQ-EXPORT-CREATE | Target Solution · Step 2 Acceptance |
| TEST-EXPORT-NO-DUPLICATE | Step 2 Tests |

**Coverage (conversation → plan):**

| Item | Source | Lands in |
|------|--------|----------|
| {one-line item} | User · Q{n} · Sweep · Council · Doc | REQ-… · TEST-… · Step {n} · C{n} · Out ({reason}) |
```

## Closing sections

Each row of **Edge Cases and Risks** lands on a step, a card, a `C{n}`, or Out with a reason. A `C{n}` names both options, who or what chose, and what the choice gives up. **Closing Summary** is three sentences: what exists after the last step, what stays Out, the first command to run. **Task Checklist** repeats the Step Overview rows in order, with the same links and marks.

```markdown
## Edge Cases and Risks

| Case or risk | Handled by |
|--------------|------------|
| Empty name | Step 1 guard · TEST-EXPORT-EMPTY-NAME |
| Two parallel creates | Out — single-threaded CLI (C1) |

## Decisions & Trade-offs

- **C1 — Check, then add, without a lock.** Options: atomic `TryAdd` on the store · check then add in `Create`. Chosen by: Q3 answer (the CLI is single-threaded). Gives up: safety under parallel callers.

## Task Checklist

1. ⬜ [Step 1 — Create export](#step-1---create-export)
2. ⬜ [Step 1R — Review Step 1](#step-1---create-export)
```

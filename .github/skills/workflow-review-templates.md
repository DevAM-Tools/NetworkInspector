# Review Templates

Load at `workflow-review.md` Stage 5, and from `workflow-dream.md` and `workflow-docs-repair.md` when writing a finding. Rules live there.

## Shared Block (every finding)

Field order: `What` → `Why` → `Evaluate` → `How` → `[Context]` → `[Where]` → `Verify`. `Context` omitted only when no constraint or source exists. `Where` omitted when no file is touched.

- ❗ `Why` covers: what is wrong; why it matters; what follows if it stays (caller, security, data, ops, release).
- ❗ A person who did not write the code can accept or reject the finding **and the fix** from `What`, `Why`, `Evaluate`.
- ❗ `How` names the concrete fix so another agent invents nothing: types, items, visibility, signatures, parameters, returns, call-site edits, validation, error paths, control flow, data flow, thread-safety, performance and security constraints, prerequisite state, rationale, edge cases.
- ❗ Every `How` has fenced **Problem** and **Fix** code: real signatures and key bodies, anchored by path and symbol. Not stubs, not comments-as-code, not an intermediate shape.
- Reject a `How` that allows more than one implementation, **except** when Consistency recorded two Fix options.
- ❗ Cite a concrete source in `Context` when an external reference exists. File cites are clickable relative links (Section 4.6).
- `Where`: link, approximate lines, symbol.
- `Verify`: exact Release command and expected result. Record it also when already run. Static: do not run it.
- Write `⬜` on the `Status` line and in the Summary **Findings** cell together. `/implement` advances both. A later review file starts every Status at `⬜` again.
- `Severity` is required for Error and Performance, omitted otherwise.

```markdown
## {ID} - {Title}
Status: ⬜ · Severity: {High | Medium | Low} · Depends on: {IDs or —}
### What
### Why
{what is wrong; why it matters; what follows if it stays}
### Evaluate
Look at: {link / symbol or command}
Accept when: {observable a person can check}
Must not change: {behavior that must stay}
### How
Fenced **Problem** / **Fix**. Extra illustration snippets welcome.
### Context
### Where
### Verify
```

### Worked example

Copy the depth, not the content.

````markdown
## E1 - Export accepts a duplicate name
Status: ⬜ · Severity: High · Depends on: —
### What
`Export.Create` adds a second export when the name already exists. The store check `Contains` is never called on this path.
### Why
Two rows titled Report exist after the second submit. REQ-EXPORT-NO-DUPLICATE forbids it and lookups by name return the first row only. If it stays, users believe their newer report was saved.
### Evaluate
Look at: [`src/Export.cs`](../src/Export.cs) `Create`
Accept when: a second `Create("Report")` returns `ExportError.DuplicateName` and the store holds one row.
Must not change: the first `Create("Report")` succeeds and returns its id.
### How
Problem ([`src/Export.cs`](../src/Export.cs) ~L20):
```csharp
public static Result<ExportId> Create(string name)
{
    return _Store.Add(name);
}
```
Fix:
```csharp
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
Test that must fail before the fix:
```csharp
[Test]
public async Task CreateTwiceWithSameNameReturnsDuplicateName()
{
    Export.Create("Report");

    Result<ExportId> second = Export.Create("Report");

    await Assert.That(second.Error).IsEqualTo(ExportError.DuplicateName);
}
```
### Context
Skeptic (parts). REQ-EXPORT-NO-DUPLICATE in [`spec/requirements/export.md`](../spec/requirements/export.md).
### Where
[`src/Export.cs`](../src/Export.cs) ~L20 `Export.Create` (primary)
### Verify
`dotnet test src/Export.Tests/Export.Tests.csproj -c Release -- --treenode-filter "/*/*/ExportTests/*"` → all pass, including `CreateTwiceWithSameNameReturnsDuplicateName`.
````

## Findings Overview

❗ Link every finding ID in Overview, **Findings**, and **Priority** to its block (GitHub slug: lowercase, punctuation other than `-` dropped, spaces → `-`).

```markdown
| ID | Bucket | Title | Summary |
|----|--------|-------|---------|
| [E1](#e1---export-accepts-a-duplicate-name) | E | {title} | {one sentence with location} |
```

## Summary

Verdict, every finding in one table, every hunt in one table. No paragraphs instead of tables.

- **Release:** one line when Ready. When Blocked, one block per blocker (`tech-reporting.md`).
- **Findings:** one row per finding. Status starts `⬜`. Keep the row after a fix; change Status. **Release** and **Counts** stay the verdict of the review that wrote the file.
- **Checks:** one row per hunt. `none` means it ran and found nothing. `n/a` needs a reason. A missing row is an incomplete Summary.
- **Priority:** fix order, one line each, or `none`.

```markdown
## Summary

**Release:** Ready for public release | Blocked by {IDs}

**Execution:** {build and test outcome, or `Not run — static review`}

**Counts:** Error {n} · Cosmetic {n} · Refactoring {n} · Performance {n}

**Hot paths:** {names, or `none`}

### Findings

| ID | Status | Bucket | Result |
|----|--------|--------|--------|
| [E1](#e1---export-accepts-a-duplicate-name) | ⬜ | Error · High | {defect and where, one sentence} |

### Checks

| Check | Result |
|-------|--------|
| Security | |
| Limits | |
| Races | |
| Correctness | |
| API | |
| Agent access | |
| Trace | |
| Docs code | |
| Docs project | |
| Design decisions | |
| Orphans | |
| Tests | |
| Performance complexity | |
| Performance allocations | |
| Performance latency | |
| Performance throughput | |
| Performance memory | |
| Performance CPU | |
| Repo | |
| UI | |
| Debug | |
| Consistency | |
| Skeptic parts | |
| Skeptic whole | |
| Outsider | |
| Closing requirements | {or `n/a — not a Closing Review`} |
| Closing verify | {or `n/a — not a Closing Review`} |
| Custom instructions | {`present` or `none`} |
| Skills | {loaded skill names} |
| Unloaded trigger | {`none` or Error ID} |

### Priority

1. [E1](#e1---export-accepts-a-duplicate-name) — {what the fix changes}
```

Nothing found: Findings is one row `| — | — | — | none |`, Priority is `none`.

Patch before Completion if a Checks row is missing, a finding is absent from Findings, a row has no Status, or a link does not match a heading.

## Output Modes

**File mode** (default): `reviews/review_<slug>_<iteration>.md`, iteration from 1. In chat: counts, verdict, path, prioritized actions. Do not repost blocks.

**Chat-only** only on `chat only` or `ohne Review-Datei`: all sections in chat; no file.

## Review File Sections

1. Findings Overview
2. Summary
3. Scope
4. Test Execution
5. Perspective Sweep (the table of `tech-sweep.md`, Skeptic and Outsider rows)
6. Errors
7. Cosmetic Issues
8. Refactoring Opportunities
9. Performance and Allocations
10. Priority Action List

A bucket section without findings says `none`; never omit it.

```markdown
## Scope

**Mode:** full review
**Loaded skills:** tech-csharp.md, tech-test.md, tech-tunit.md, tech-docs.md, tech-spec.md, tech-sweep.md
**Read:** architecture Overview, § Export · domain § Export names
**In scope:** [`src/Export.cs`](../src/Export.cs) · [`src/Export.Tests/ExportTests.cs`](../src/Export.Tests/ExportTests.cs)
**Excluded:** {paths and why, or `none`}

## Test Execution

| Command | Result |
|---------|--------|
| `dotnet build src/Export/Export.csproj -c Release` | 0 warnings, 0 errors |
| `dotnet test src/Export.Tests/Export.Tests.csproj -c Release -- --treenode-filter "/*/*/ExportTests/*"` | 12 passed, 0 failed |
```

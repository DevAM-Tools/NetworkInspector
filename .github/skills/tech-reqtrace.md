# ReqTrace — the Trace Check

Load with `tech-spec.md`: on `/requirements`, `/plan`, `/implement`, `/review`, `/evolve`, `/dream`, and when `spec/trace.lock`, `spec/trace.ignore`, or `.config/dotnet-tools.json` is in scope. `tech-spec.md` is the spec format; this skill is the tool that checks it.

`reqtrace` reads `spec/requirements/`, `spec/tests/`, the card markers in C#, Python, and Rust tests, the accepted state in `spec/trace.lock`, and optional TRX test results. It reads files only; it builds and runs nothing. Only entries that are new or changed since the accepted state fail; a gap in an unchanged entry is an **open gap**: counted, listed, never failing.

Sync: [`docs/agents.md`](https://github.com/DevAM/CopilotAIWorkflow/blob/main/docs/agents.md) of the kit repository, the product reference of the same commands, rules, and output.

## Call form

Below, `reqtrace <command>` means the call of the first matching row. Run it from any folder inside the repository; the tool finds the root by `spec/index.md`.

| Repository | Call |
|------------|------|
| Holds `src/ReqTrace/ReqTrace.csproj` (the kit's own repository) | `dotnet run --project src/ReqTrace -c Release -- <command>` |
| `.config/dotnet-tools.json` lists `reqtrace` | `dotnet tool run reqtrace <command>` |
| Neither | § Setup, then the row above |

- Never add `--no-build` to the first row: after a pack or a clean it fails, and after a source change it runs an old build.
- `dotnet tool run` says `Run "dotnet tool restore"` → run `dotnet tool restore` once (fresh clone, new machine), then call again.
- `syntax` needs ReqTrace 0.2.0 or later and runs anywhere, also without `spec/`. `Unknown command: syntax` → use `tech-spec.md` § Requirement entry and § Test card instead, and name the upgrade in the run report.

## Setup

Once per repository. Each step leaves the trace named after `→`.

1. `dotnet --version` → `10.0` or later. Lower or missing: never install an SDK; go to § Without the tool.
2. `spec/index.md` exists → else run `/requirements` first; without it every call exits `2` (`No spec/index.md found here or in any parent folder.`).
3. ❗ Ask before installing: the tool is a new dependency (Section 4.7). Under `/evolve`, put the request in the Spec delta; else ask in chat. Fields: `ReqTrace`, the exact version (look it up: `dotnet package search ReqTrace --exact-match`), "checks that every requirement reaches a passing test", MIT, alternative "checks by search; a weak model can miss a broken link". Not approved → § Without the tool.
4. `.config/dotnet-tools.json` missing → `dotnet new tool-manifest` → the file exists.
5. `dotnet tool install ReqTrace --version <approved version>` → the manifest lists `reqtrace` with that version.
6. `reqtrace check` → exit `0` or `1` and the line `No accepted state yet. Run accept to record the starting point.` Exit `2`: § Exit codes.
7. First `reqtrace accept` at the next approval (§ When to run what) → `spec/trace.lock` exists; the output says `This is the starting point`. Gaps present then stay open gaps.
8. Run report, **Needs you:** "Commit `.config/dotnet-tools.json`, `spec/trace.lock`, and `spec/trace.ignore` (when present) with the change." Git is read-only for the agent (Section 4.11).

The search prints `No results found`, or a version not on nuget.org yet is wanted (testing a local pack): ask for the folder that holds the `.nupkg` and add `--add-source <folder>` to step 5, in this repository `--add-source artifacts/release`. Every later `dotnet tool restore` then needs that folder too; say so in the run report.

## When to run what

| Moment | Run | Then |
|--------|-----|------|
| Before changing, retiring, or merging an existing `REQ-` or `TEST-` entry (`/requirements` Delta, `/plan`, `/dream`) | `show <ID>` | Each card and test it lists lands in the change, or the change stops |
| After each edit to `spec/` or to a test, in every workflow | `check` | Fix every Error (§ Findings) |
| A form finding (F1–F5), or before the first entry of a fresh spec | `syntax <kind>` (`requirement`, `card`, `marker`, `ignore`) | Copy the printed form; rewrite the line in it |
| `/implement` step 9 and Stage 3; `/review` Tests hunt, .NET tests | Tests with TRX (§ Test evidence), then `check --results artifacts/trace-results` | E1–E4 are Errors |
| Spec delta (Gate A, Delta mode), briefing (Gate B, `/implement` Stage 4) | `report` | Paste unedited under `## Generated report` (`tech-reporting.md`) |
| `/review` Trace hunt | `report` | Each line under `Not confirmed yet` → § Confirm |
| A person approved the spec: Gate A, a Spec delta, a plan | Set the approved entries `active`, then `accept` | § Accept |
| `/evolve` Gate B: the person accepts the batch; after `/implement`: the person replies `accept` to the run report | `accept` | § Accept; it records the new markers, so C2 guards them |

```text
Wrong: The plan wrote three cards; the agent skips `check` because "the tests come later".
Right: Run `check`. The three new cards show L6 (no marked test): expected until the step that writes their tests. Any other Error is fixed now.
```

## Exit codes and output

| Exit | Means | Do |
|------|-------|----|
| `0` | No Error; open gaps may exist | Continue |
| `1` | At least one Error, each with file, line, rule, and `Fix:` | § Findings |
| `2` | Wrong call or unreadable input: unknown command or option, no `spec/index.md`, a `--results` path that does not exist, a file that is not valid XML | Fix the call or the file. ❗ Never count exit `2` as a passed check |

```text
> reqtrace check
spec/tests/export.md:14  L6  TEST-EXPORT-NO-DUPLICATE has no marked test.
  Fix: Add the marker to the test that proves it.
Read: 4 requirements · 5 test cards · 4 markers · 0 ignored paths
Evidence not checked: no --results given.
Result: 1 error · 2 open gaps
```

`check --json` prints the same findings as one object (`schemaVersion`, `counts`, `findings` with `rule`, `severity`, `file`, `line`, `message`, `fix`), for an agent that parses them.

## Findings

The `Fix:` line says what to do; this table says what the fix may not be.

| Rule | Means | Do | Never |
|------|-------|----|-------|
| F1–F5 | A spec line, ID, field, or value breaks `tech-spec.md`, or a file holds no entry | Copy the line or value the `Fix:` quotes; for the whole entry run `syntax <kind>` and rewrite the line in that form | Delete the text to silence it |
| L1 | One ID heads two entries | Keep the older; the newer gets a new ID | Rename an ID that tests or code already cite |
| L2 | A card proves a missing or retired requirement | Point `Proves` at the active successor (`Replaced by`), or retire the card | Revive a retired ID |
| L3 | A marker names a missing or retired card | Fix the typo (`Did you mean …`), or write the card | Delete the marker |
| L4 | An active requirement has no active card | Write the card (`/plan`) | Set `Proof` to `demo`, `review`, or `waived` without the person's approval |
| L5 | A marker has no test method within 8 lines | Move the marker directly above the test method | — |
| L6 | An active card has no marked test | Write the test from the card's **Content**, with the marker | Put the marker on a test that does not prove the **Content** |
| L7 | A `spec/trace.ignore` line is invalid | Write `path/inside/repo — reason` | An entry without a reason |
| C1 | An accepted entry vanished | Restore it, or move it to `## Retired` with a reason | Delete an entry |
| C2 | A card lost its last marked test | Restore the test | Retire the card to get green |
| C3 | A card proves a requirement whose text changed | § Confirm | Confirm without reading |
| E1 | A `Met ✅` requirement's test is not in the results | Run the test, pass fresh results | Untick `Met` while its Done-when still holds |
| E2 | A `Met ✅` requirement's test failed | Fix the code; the test changes only through its card | Weaken the test |
| E3 | The result file is older than a marked test file | Run the tests again, then `check --results` | — |
| E4 | One method name is marked in two files | Rename one method | — |

- **Expected while building:** L4 and L6 on an entry whose card or test a later plan step or a queued slice writes. Record nothing. ❗ The step that writes it ends with none; the Closing Review sees none for the built scope.
- ❗ Never edit or delete `spec/trace.lock` by hand. Only `accept` and `confirm` write it.
- ❗ Never delete or weaken a test, move a marker, or add one to clear a finding (Section 4.5). A marker on the wrong test passes every rule; the review **Meaning** hunt (`workflow-review.md` § Trace) catches it.
- Open gaps never block. Fix one only when its entry is in scope.
- Two failed attempts on one finding: decision card (`tech-reporting.md`).

## Accept

`accept` records the current state as the one a person approved. New and changed entries are compared with it from then on.

- ❗ Run it only right after a person's explicit approval in this session: Gate A, Gate B, a Spec delta, a plan, or `accept` in reply to an `/implement` run report. A question, a comment on one entry, or silence is no approval.
- It refuses while any Error exists (`Not accepted. Resolve these first:`). Fix the listed findings, then run it again.
- Approved entries whose proof follows later (L4, L6) pass only with `--allow-gap "<where the proof follows>"`. Name the slice, plan, or workflow; never a bare "gap".
- Markers added after the last `accept` are not protected by C2 yet: a deleted test of such a card is only an open gap. The next `accept` records them.

```text
> reqtrace accept
Not accepted. Resolve these first:
spec/tests/export.md:14  L6  TEST-EXPORT-NO-DUPLICATE has no marked test.
  Fix: Add the marker to the test that proves it.
Read: 4 requirements · 5 test cards · 4 markers · 0 ignored paths
Result: 1 error · 0 open gaps
> reqtrace accept --allow-gap "tests follow in the create-and-list slice"
Accepted 9 entries.
Wrong: reqtrace accept --allow-gap "gap"
```

## Confirm

A card that proves a changed requirement shows under `Not confirmed yet` in `report` (C3 in `check`).

1. Read the old and the new wording: `report` § `Changed` lists removed and added words; `Possibly weakened` flags a lost strong word, a new weak word, or a changed number.
2. Read the card **Content** and the marked test (`show <TEST-ID>` names its file and line).
3. The test still proves the new wording → `confirm <TEST-ID> --note "<what you checked>"`. It does not → change the card **Content** and then the test, never the reverse; `check` again.

```text
Right: reqtrace confirm TEST-EXPORT-NO-DUPLICATE --note "new wording adds the message; the test asserts one row and the message text"
Wrong: reqtrace confirm TEST-EXPORT-NO-DUPLICATE --note "ok"
```

❗ In a review, confirm only in a session that did not write the change (`workflow-review.md` § Trace).

## Test evidence

`--results` reads `.trx` files: .NET tests only. A folder means every `.trx` below it; the path is relative to the repository root. E1 and E2 concern only requirements ticked `Met ✅`.

```bash
dotnet test --solution <Solution>.slnx -c Release --report-trx --results-directory artifacts/trace-results
reqtrace check --results artifacts/trace-results
```

- Run the tests right before the check; an older result file is E3.
- Pass `--report-trx --results-directory` without a `--` before them. With `--`, the .NET 10 SDK reads the folder as a project path (`Die Angabe eines Verzeichnisses … sollte über „--project“ oder „--solution“ erfolgen` / `Specifying a directory … should be done via --project or --solution`), writes no result file, and the check then reads an old one.
- Python and Rust: no TRX. Run `check` without `--results`. Check I7 by search: each `Met ✅` requirement has a card whose marked test passed in this run's test output. Say so in the run report.

## Ignoring a path

`spec/trace.ignore`, one line per path: `tests/fixtures/markers — sample files with marker text, not tests`. The scan skips files at or below each path; `report` lists them under `Ignored paths`. Use it only for files that hold marker text but are not tests. A path outside the repository is L7.

## Without the tool

No .NET 10 SDK, or the install was not approved: run every integrity check of `tech-spec.md` by search. Never write a script (Section 4.7). Run report, **Not done:** "reqtrace not available: integrity checks ran by search."

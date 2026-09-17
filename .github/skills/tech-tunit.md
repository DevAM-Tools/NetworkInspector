# TUnit Testing Rules

Load when `*Tests.cs`, `{Project}.Tests` projects, `{App}.UiTest`, or C# Playwright UI tests are in scope. Content and case design: `tech-test.md`. Browser journeys: `tech-playwright.md`.

## Scope

- Unit tests: `{Project}.Tests`.
- Razor/Blazor component logic: bUnit in that `.Tests` project (`tech-blazor.md`).
- C# browser journeys: dedicated `{App}.UiTest` with **TUnit** + `TUnit.Playwright` (`tech-playwright.md`). Do not put journeys in `{Project}.Tests`. ExitPointGaps pairs `.Tests`, not `.UiTest`.

## Framework

❗ **TUnit only** — unit, bUnit, and C# UI tests. Do not add or migrate to xUnit, NUnit, MSTest, FluentAssertions, `Microsoft.Playwright.NUnit`, `.MSTest`, or `.Xunit`.

**Minimal stack (nothing else required for unit tests):**

| Piece | Role |
|-------|------|
| **TUnit** | Test framework (`[Test]`, `await Assert.That(...)`, `[Arguments]`, `[Before]`) |
| **MTP** | Test runner (`global.json` → `Microsoft.Testing.Platform`) |
| **ExitPointGaps** | Exit-point coverage gate (local dotnet tool) |

C# UI tests add `TUnit.Playwright` (`JourneyTest` / `PageTest` in `{App}.UiTest`) — `tech-playwright.md`. Default browser: system `Channel`; see that skill.

Common agent mistakes — **do not** port xUnit/NUnit habits:

| Wrong (xUnit/NUnit) | Use instead (TUnit) |
|---------------------|---------------------|
| `[Fact]` / `[TestMethod]` | `[Test]` on `async Task` method |
| `Assert.Equal(...)` | `await Assert.That(actual).IsEqualTo(...)` |
| `[Theory]` + `[InlineData]` | `[Test]` + `[Arguments(...)]` or `[MethodDataSource]` |
| `IClassFixture<T>` / `[SetUp]` | `[Before(Class)]` / `[Before(Test)]` |
| `coverlet.collector` | MTP coverage via ExitPointGaps (no extra NuGet) |
| `PackageReference` xunit/nunit/`Microsoft.Playwright.NUnit` | **Remove** — unit `.csproj`: only `TUnit`. `{App}.UiTest` `.csproj`: `TUnit` + `TUnit.Playwright` |

**`global.json` (repo root):**

- SDK `10.0.100`
- `"test": { "runner": "Microsoft.Testing.Platform" }`

## ExitPointGaps (agent contract)

❗ Local dotnet tool **`ExitPointGaps` `1.*`** on every repo with C# test projects.

❗ 100% exit-path coverage on every public or internal API before release. Gate: `summary.exitGapCount == 0`. Branch gaps are informational only.

| Rule | Value |
|------|-------|
| `run` scope | class libraries (`OutputType` `Exe` excluded) |
| Test pairing | `{Project}.Tests` sibling or reference scan |

### Agent workflow

1. **Once per repo**
   - `dotnet new tool-manifest`
   - `dotnet tool install ExitPointGaps --version 1.*`
   - Fresh clone: `dotnet tool restore`
2. **Gate:** `dotnet tool run exitpointgaps --repo-root .`
   - Auto-discovers `.slnx`/`.sln`, pairs tests, runs Cobertura + gap report
3. **Read result**
   - Exit `0` pass · `1` gap/failure · `2` usage
   - Confirm `summary.exitGapCount == 0`
4. **Fix gaps:** every `exitGaps[]` item (`file`, `line`, `exitPointId`, `kind`) — re-gate until zero
5. **No tests yet:** `plan` → add tests → gate

**Multi-project output:**

- `summary.json` schema v3
- `projects[].reportFile` → per-project v1 JSON with `exitGaps[]`

**Scoped commands:**

| Intent | Command |
|--------|---------|
| Gate repo | `dotnet tool run exitpointgaps --repo-root .` |
| Gate solution | `dotnet tool run exitpointgaps run solution path/File.slnx --repo-root . --configuration Release` |
| Gate project | `dotnet tool run exitpointgaps run project path/Proj.csproj --repo-root .` |
| Plan exits (no tests) | `dotnet tool run exitpointgaps plan project path/Proj.csproj -o exits.json --repo-root .` |

**CLI details (flags, formats, paths):** run help — do not duplicate here.

```bash
dotnet tool run exitpointgaps -- run --help
# This repo (contributors):
dotnet run --project src/ExitPointGaps -c Release -- run --help
```

## Structure

- Test project: `<ProductionProjectName>.Tests` — mirror prod namespace and folders.
- One file per class: `<ClassName>Tests.cs`.
- Section 3 file glob is `*Tests.cs` in `{Project}.Tests/` (not `*.Tests.cs`).
- Shared helpers: `Helpers/`.
- ❗ Test method names are **PascalCase without underscores**. Not snake_case, not `Method_Scenario_Expected`, not `_PascalCase`.
- Data source names: PascalCase without underscores (`MethodScenarioData`).

```csharp
[Test]
public async Task PairEmptyRepoReturnsNone()
```

## Authoring

Mechanics only. What to cover, speed, doubles, AAA, exit paths: `tech-test.md`.

- `await Assert.That(actual).Is...`
- Always await async operations.
- Pass `CancellationToken` to cancellation-aware APIs.
- Assert exceptions with `Throws<T>`:

```csharp
await Assert.That(async () => await sut.PairAsync(path)).Throws<IOException>();
```

- Drive data with `[Arguments]` or `[MethodDataSource]`.

## Fixtures and parallelism

- `[Before(Test)]` / `[After(Test)]` for per-test setup.
- `[Before(Class)]` / `[After(Class)]` for class resources.
- `IAsyncDisposable` on test classes holding resources.
- Parallel-safe by default; no shared mutable statics.
- `[NotInParallel]` only when required. Document the reason in XML.

## Coverage

- Use `[ExcludeFromCodeCoverage]` only with an XML reason. Excluded exits skip the gate.

```csharp
/// <summary>Native interop stub. Reason: no managed exit to cover.</summary>
[ExcludeFromCodeCoverage]
```

## Commands

```bash
dotnet test path/Proj.Tests.csproj -c Release
dotnet test path/Proj.Tests.csproj -c Release -- --treenode-filter "/*ClassName/*"
dotnet tool run exitpointgaps --repo-root .
```

MTP is the runner (`global.json`). Do not pass xUnit-style `--filter FullyName`. Do not add a wrapper script without approval.

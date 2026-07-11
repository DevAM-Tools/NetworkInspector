# TUnit Testing Rules

Load when test files or test projects are in scope. Implements Section 4.5 in `copilot-instructions.md`.

## Scope

- Unit tests in `.Tests` projects.
- bUnit for Razor/Blazor → `tech-blazor.md`.

## Framework

❗ **This repo uses TUnit only.** Do not add or migrate to xUnit, NUnit, MSTest, or FluentAssertions — incompatible with MTP + ExitPointGaps.

**Minimal stack (nothing else required for unit tests):**

| Piece | Role |
|-------|------|
| **TUnit** | Test framework (`[Test]`, `await Assert.That(...)`, `[Arguments]`, `[Before]`) |
| **NSubstitute** | Mocks/stubs when needed |
| **MTP** | Test runner (`global.json` → `Microsoft.Testing.Platform`) |
| **ExitPointGaps** | Exit-point coverage gate (local dotnet tool) |

Common agent mistakes — **do not** port xUnit/NUnit habits:

| Wrong (xUnit/NUnit) | Use instead (TUnit) |
|---------------------|---------------------|
| `[Fact]` / `[TestMethod]` | `[Test]` on `async Task` method |
| `Assert.Equal(...)` | `await Assert.That(actual).IsEqualTo(...)` |
| `[Theory]` + `[InlineData]` | `[Test]` + `[Arguments(...)]` or `[MethodDataSource]` |
| `IClassFixture<T>` / `[SetUp]` | `[Before(Class)]` / `[Before(Test)]` |
| `coverlet.collector` | MTP coverage via ExitPointGaps (no extra NuGet) |
| `PackageReference` xunit/nunit | **Remove** — only `TUnit` in test `.csproj` |

**`global.json` (repo root):**

- SDK `10.0.100`
- `"test": { "runner": "Microsoft.Testing.Platform" }`

## ExitPointGaps (agent contract)

❗ Local dotnet tool **`ExitPointGaps` `1.*`** on every repo with test projects.

| Rule | Value |
|------|-------|
| Release gate | `summary.exitGapCount == 0` |
| Branch gaps | informational only |
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

## Test quality

- Cover happy path, errors, and edges.
- Edge cases: `null`, empty, min/max, off-by-one, collections 0/1/2, concurrency.
- Data-driven: `[Arguments]`, `[MethodDataSource]`.
- Deterministic; no `Thread.Sleep`.
- One logical assertion per test.
- Windows/Linux/macOS, x64/ARM64.

## Structure

- Test project: `<ProductionProjectName>.Tests` — mirror prod namespace and folders.
- One file per class: `<ClassName>Tests.cs`.
- Shared helpers: `Helpers/`.
- Test names: `<Method>_<Scenario>_<ExpectedResult>`.
- Data source names: `<Method>_<Scenario>_Data`.

## Authoring

- Separate Arrange, Act, Assert with blank lines.
- `await Assert.That(actual).Is...`
- Always await async operations.
- Pass `CancellationToken` to cancellation-aware APIs.
- Exceptions: `await Assert.That(async () => await sut.M()).Throws<T>()`.

## Fixtures and parallelism

- `[Before(Test)]` / `[After(Test)]` for per-test setup.
- `[Before(Class)]` / `[After(Class)]` for class resources.
- `IAsyncDisposable` on test classes holding resources.
- Parallel-safe by default; no shared mutable statics.
- `[NotInParallel]` only when required — document reason in XML.

## Doubles and coverage

- Prefer real deterministic implementations.
- NSubstitute only for external or non-deterministic dependencies.
- `[ExcludeFromCodeCoverage]` only with XML reason — excluded exits skip the gate.

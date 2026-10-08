# TUnit Testing Rules

Load when `*Tests.cs`, `{Project}.Tests` projects, `{App}.UiTest`, or C# Playwright UI tests are in scope. Content and case design: `tech-test.md`. Browser journeys: `tech-playwright.md`.

## Scope

- Unit tests: `{Project}.Tests`.
- Razor/Blazor component logic: bUnit in that `.Tests` project (`tech-blazor.md`).
- C# browser journeys: dedicated `{App}.UiTest` with **TUnit** + `TUnit.Playwright` (`tech-playwright.md`). Do not put journeys in `{Project}.Tests`.

## Framework

❗ **TUnit only** — unit, bUnit, and C# UI tests. Do not add or migrate to xUnit, NUnit, MSTest, FluentAssertions, `Microsoft.Playwright.NUnit`, `.MSTest`, or `.Xunit`.

**Minimal stack (nothing else required for unit tests):**

| Piece | Role |
|-------|------|
| **TUnit** | Test framework (`[Test]`, `await Assert.That(...)`, `[Arguments]`, `[Before]`) |
| **MTP** | Test runner (`global.json` → `Microsoft.Testing.Platform`) |

C# UI tests add `TUnit.Playwright` (`JourneyTest` / `PageTest` in `{App}.UiTest`) — `tech-playwright.md`. Default browser: system `Channel`; see that skill.

Common agent mistakes — **do not** port xUnit/NUnit habits:

| Wrong (xUnit/NUnit) | Use instead (TUnit) |
|---------------------|---------------------|
| `[Fact]` / `[TestMethod]` | `[Test]` on `async Task` method |
| `Assert.Equal(...)` | `await Assert.That(actual).IsEqualTo(...)` |
| `[Theory]` + `[InlineData]` | `[Test]` + `[Arguments(...)]` or `[MethodDataSource]` |
| `IClassFixture<T>` / `[SetUp]` | `[Before(Class)]` / `[Before(Test)]` |
| `PackageReference` xunit/nunit/`Microsoft.Playwright.NUnit` | **Remove** — unit `.csproj`: only `TUnit`. `{App}.UiTest` `.csproj`: `TUnit` + `TUnit.Playwright` |

**`global.json` (repo root):**

- SDK `10.0.100`
- `"test": { "runner": "Microsoft.Testing.Platform" }`

## Structure

- Test project: `<ProductionProjectName>.Tests` — mirror prod namespace and folders.
- One file per class: `<ClassName>Tests.cs`.
- Section 3 file glob is `*Tests.cs` in `{Project}.Tests/` (not `*.Tests.cs`).
- Shared helpers: `Helpers/`.
- Test method names are **PascalCase without underscores**. Not snake_case, not `Method_Scenario_Expected`, not `_PascalCase`.
- Data source names: PascalCase without underscores (`MethodScenarioData`).

```csharp
[Test]
public async Task ParseEmptyInputReturnsNone()
```

## Authoring

Mechanics only. What to cover, speed, doubles, AAA: `tech-test.md`.

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

- Use `[ExcludeFromCodeCoverage]` only with an XML reason.

```csharp
/// <summary>Native interop stub. Reason: not exercised from managed tests.</summary>
[ExcludeFromCodeCoverage]
```

## Commands

Run tests with `dotnet test`. When the full project is not required, pass a treenode filter for the class or test that covers the change. Run the project with no filter when the change can break tests you cannot name (`tech-test.md`).

```bash
dotnet test path/Proj.Tests.csproj -c Release -- --treenode-filter "/*ClassName/*"
dotnet test path/Proj.Tests.csproj -c Release
```

MTP is the runner (`global.json`). Do not pass xUnit-style `--filter FullyName`. Do not add a wrapper script without approval.

# Solution and Build Configuration

Load when `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `GlobalUsings.cs`, or `*.csproj` / `*.props` / `*.targets` are in scope, or when restore reports NU1900–NU1904. Extends Section 4.7 in `copilot-instructions.md`. SSOT for MSBuild properties, CPM, CSharpStyleChecker, and the .NET mechanics of the New Dependency Protocol (Section 4.7).

## File Layout

| File | Location |
|------|----------|
| `Directory.Build.props` | repository root |
| `Directory.Packages.props` | repository root |
| `Directory.Build.targets` | repository root (only when needed) |
| `.editorconfig` | repository root (`root = true`) |
| `GlobalUsings.cs` | project root, no namespace |

## GlobalUsings.cs

- ❗Group `global using` directives by category; separate groups with a comment header.
- Order groups: `System.*` → `Microsoft.*` → third-party → internal.

```csharp
// System
global using System;
global using System.Threading;

// Microsoft
global using Microsoft.Extensions.Logging;
```

## Directory.Build.props

### Target Framework and Language

| Property | Value |
|----------|-------|
| `TargetFramework` | `net10.0` |
| `TargetFrameworks` | multi-target only; must include `net10.0` |
| `LangVersion` | `14` |
| `Nullable` | `enable` |
| `ImplicitUsings` | `enable` |

Generator projects only:

| Property | Value |
|----------|-------|
| `TargetFramework` | `netstandard2.0` |

### Analysis and Warnings

| Property | Value |
|----------|-------|
| `TreatWarningsAsErrors` | `true` |
| `EnableNETAnalyzers` | `true` |
| `EnforceCodeStyleInBuild` | `true` |
| `AnalysisLevel` | `10-recommended` |
| `GenerateDocumentationFile` | `true` |
| `NoWarn` | omit globally; user approval only — template below |
| `WarningsAsErrors` | optional; specific warning codes only |
| `WarningsNotAsErrors` | user approval only — same template |

Library projects (no host, consumed by other code) enable CA2007 (`ConfigureAwait`), which is off by default. Put a `.editorconfig` in the library project folder; enabling a rule is not a suppression and needs no approval:

```ini
# src/MyLibrary/.editorconfig — library code configures every await (tech-csharp.md Async)
[*.cs]
dotnet_diagnostic.CA2007.severity = error
```

CA1848 and CA2254 (logging through `[LoggerMessage]` with constant templates) are already errors under `10-recommended` with `TreatWarningsAsErrors`.

#### Raising a diagnostic to error

- Raise a diagnostic by ID in the root `.editorconfig` under `[*.cs]`, one line per ID. Raising is not a suppression and needs no approval.
- ❗ Never raise by group: no `dotnet_analyzer_diagnostic.severity`, no `dotnet_analyzer_diagnostic.category-*.severity`, no higher `AnalysisLevel` or `AnalysisMode`. They switch on rules nobody chose.
- ❗ Raise `IDE0005` (unnecessary using) to error. It needs `EnforceCodeStyleInBuild` and `GenerateDocumentationFile` from the table above.
- `IDE0005` misses some unused entries in `GlobalUsings.cs`, and the build does not check their order. `/review` checks both by hand.
- ❗ Raise the unused-code diagnostics to error in every project: `IDE0051` (private member never used), `IDE0052` (private member assigned, value never read), `IDE0059` (unnecessary assignment of a value), `IDE0060` (parameter never used). Set `dotnet_code_quality_unused_parameters = all` so public and protected methods count too.
- An unused local (CS0168, CS0219) and a field never used or never assigned (CS0169, CS0414, CS0649) are compiler warnings: `TreatWarningsAsErrors` already fails them.
- ❗ Fix the cause, never silence it: delete the member, parameter, or variable together with its call-site arguments, doc tags, and tests. Never add a `NoWarn`, a pragma, or `severity = none` for these IDs without user approval (template below).
- Use a discard for an ignored value: `out _`, `_ = Call();`. Name a parameter `_` only when a delegate or interface shape forces it, and document it as ignored.
- A field that only generated code or reflection writes is unused: remove it and register the value explicitly.

```ini
# IDE0005: an unused using fails the build
dotnet_diagnostic.IDE0005.severity = error
# Unused code fails the build
dotnet_diagnostic.IDE0051.severity = error
dotnet_diagnostic.IDE0052.severity = error
dotnet_diagnostic.IDE0059.severity = error
dotnet_diagnostic.IDE0060.severity = error
dotnet_code_quality_unused_parameters = all
```

When a warning is hidden in MSBuild, use the **global** suppression template in `tech-csharp.md` (Lints and suppressions). One XML comment **per id**: id, why the warning is inapplicable, **why global** (not method/file/pragma), user approved.

```xml
<!-- Id: CS1591. Why: {why this warning is inapplicable}. Why global: {why not method/file/pragma}. User approved. -->
<NoWarn>$(NoWarn);CS1591</NoWarn>
```

### IDE / code-style (`IDE1006` and other `IDE*` naming)

`IDE1006` (naming convention) is an **EditorConfig code-style** diagnostic. It shows in the IDE even when `dotnet build` is clean.

Tried both:

| Mechanism | `dotnet build` (with `EnforceCodeStyleInBuild`) | IDE squiggle |
|-----------|--------------------------------------------------|--------------|
| `<NoWarn>$(NoWarn);IDE1006</NoWarn>` | Suppresses | **Does not** clear |
| `.editorconfig` `dotnet_diagnostic.IDE1006.severity = none` | Suppresses | **Clears** |

❗ Do **not** use `NoWarn` for `IDE1006`. Put it in the root `.editorconfig` under `[*.cs]`. Same comment fields as a global suppress: **id**, **why**, **why global**, user approved. One comment per id.

```ini
# Id: IDE1006. Why: default IDE naming rejects '_' on private members; CSharpStyleChecker requires _PascalCase. Why global: every private member; NoWarn does not clear the IDE diagnostic; a pragma per member is not viable. User approved.
dotnet_diagnostic.IDE1006.severity = none
```

Do not “fix” the squiggle by dropping the `_` prefix. Private members stay `_PascalCase` (`tech-csharp.md`). This repo’s `.editorconfig` already sets `IDE1006` to `none` for that reason.

### Build Behavior

| Property | Value |
|----------|-------|
| `Deterministic` | `true` |
| `VersionPrefix` | § Versioning & Metadata |
| `ContinuousIntegrationBuild` | `true` on Release builds; `true` when `CI` is set |
| `DebugType` | `embedded` or `portable` (consistent) |

### Versioning & Metadata (on-request)

Packages that release together: one `VersionPrefix` in `Directory.Build.props`. Packages that release on their own: `VersionPrefix` in each packable project and none in `Directory.Build.props`, so no package inherits another's number.
When publishing or packaging is in scope, ask user for: `VersionSuffix`, `Company`, `Authors`, `Copyright`, `Description`, `PackageLicenseExpression`, `PackageProjectUrl`, `RepositoryUrl`.

### Release packages

Every package (library, analyzer, dotnet tool) has one release path:

```bash
dotnet pack src/{Project}/{Project}.csproj -c Release    # → artifacts/release/{PackageId}.{version}.nupkg
```

- ❗ A build never packs. Never set `GeneratePackageOnBuild`: each Release build would overwrite the released package of the current version, and a tool would pack before its publish files exist (MSB3030 in a clean tree).
- ❗ `PackageOutputPath` is `artifacts/release/` at the repository root for every package. Never pass `-o`: a second output folder holds a second, unchecked copy.
- ❗ Before a pack builds, each project empties its own `$(OutDir)`. `dotnet pack` sets the global property `_IsPacking=true`, which reaches project references too. Keep `obj/`: restore has already written it. `dotnet clean` is no substitute: it deletes only the files it listed itself, never a stale one.
- ❗ Empty `$(PublishDir)` before each publish of a tool. The SDK packs a tool from a glob over that folder, and publish never deletes old files there.
- ❗ Stop every other way into a package with an error that names the release command: a Debug pack, `--no-build`, and a pack without `_IsPacking` (an IDE, `dotnet msbuild -t:Pack`). Then delete this version’s old `.nupkg`, so a failed pack leaves no stale file.
- Before a push, list the package (a `.nupkg` is a zip) and check that it holds only this package's files. A version on nuget.org can never be replaced.
- A local check that restores the package from `artifacts/release/` sets `RestoreAdditionalProjectSources` to that folder and `RestorePackagesPath` to a private folder. Otherwise `~/.nuget/packages` serves an older build of the same version.

```xml
<!-- Directory.Build.targets -->
<PropertyGroup Condition="'$(PackageId)' != ''">
  <IsPackable>true</IsPackable>
  <PackageOutputPath>$(MSBuildThisFileDirectory)artifacts\release\</PackageOutputPath>
</PropertyGroup>
<Target Name="CleanToolPublishDir" BeforeTargets="PrepareForPublish" Condition="'$(PackAsTool)' == 'true'">
  <RemoveDir Directories="$(PublishDir)" />
</Target>
<Target Name="CleanOutputBeforePack" BeforeTargets="BeforeBuild" Condition="'$(_IsPacking)' == 'true' and '$(OutDir)' != ''">
  <RemoveDir Directories="$(OutDir)" />
</Target>
<Target Name="RequireReleasePackPath" BeforeTargets="GenerateNuspec" Condition="'$(PackageId)' != ''">
  <Error Condition="'$(Configuration)' != 'Release'" Text="$(PackageId): Release only. Run: dotnet pack $(MSBuildProjectFullPath) -c Release" />
  <Error Condition="'$(NoBuild)' == 'true'" Text="$(PackageId): --no-build would pack old files. Run: dotnet pack $(MSBuildProjectFullPath) -c Release" />
  <Error Condition="'$(_IsPacking)' != 'true'" Text="$(PackageId): pack only through the dotnet CLI. Run: dotnet pack $(MSBuildProjectFullPath) -c Release" />
  <Delete Files="$(PackageOutputPath)$(PackageId).$(PackageVersion).nupkg;$(PackageOutputPath)$(PackageId).$(PackageVersion).snupkg" />
</Target>
```

Hook the clean on `BeforeBuild`, never on `Build`: a `BeforeTargets="Build"` target runs after the compile, because it follows the target’s own dependencies.

```text
Wrong: dotnet build -c Release, then push the .nupkg the build left behind.
Wrong: dotnet pack --no-build -o out/        (packs what bin/ held; a second output folder)
Right: dotnet pack src/ExitPointGaps/ExitPointGaps.csproj -c Release → artifacts/release/ExitPointGaps.1.1.2.nupkg
```

## Directory.Packages.props

| Property / item | Value |
|---------------|-------|
| `ManagePackageVersionsCentrally` | `true` |
| `CentralPackageTransitivePinningEnabled` | `true` |
| `CentralPackageFloatingVersionsEnabled` | `true` |
| `PackageVersion` | `Include="{package-id}" Version="{version}"` |
| Project `PackageReference` | `Include="{package-id}"` — no `Version` |

## Project File (`.csproj`)

| Item | Value / rule |
|------|----------------|
| `PackageReference` | `Include` only; version from `Directory.Packages.props` |
| `ProjectReference` | relative path |
| `OutputType` | per project |
| `RootNamespace` | per project |
| `AssemblyName` | per project |
| Duplicate `Directory.Build.props` properties | omit |

## CSharpStyleChecker

❗ Mandatory NuGet **`2.*`** on every SDK-style consumer (`netstandard2.0` or `net5.0`+), including Roslyn source generators. Active on the referencing project only — not transitive to downstream libraries.

| Step | Action |
|------|--------|
| CPM | `Directory.Packages.props`: `<PackageVersion Include="CSharpStyleChecker" Version="2.*" />` |
| Project | `<PackageReference Include="CSharpStyleChecker" />` — omit `Version` when CPM enabled |
| No CPM | `<PackageReference Include="CSharpStyleChecker" Version="2.*" />` in `.csproj` |

- Analyzers load from `analyzers/dotnet/cs`; **`ExitPoints` bundled** — no second package, no `PrivateAssets` / `IncludeAssets`.
- Violations = compiler errors (CSC*). Rebuild after add.
- Set `ApplyCSharpStyleChecker=false` only to opt out.
- ❗ Build every touched project with the checker enabled and read all checker output for the touched files. A filter on guessed line numbers misses violations.

## InvariantStringHelper

❗ Mandatory NuGet **`1.*`** on every SDK-style project that targets `net6.0` or later and interpolates strings. Not for `netstandard2.0` projects (analyzers, generators): they cannot take a run-time dependency.

| Step | Action |
|------|--------|
| CPM | `Directory.Packages.props`: `<PackageVersion Include="InvariantStringHelper" Version="1.*" />` |
| Project | `<PackageReference Include="InvariantStringHelper" />` — omit `Version` when CPM enabled |
| No CPM | `<PackageReference Include="InvariantStringHelper" Version="1.*" />` in `.csproj` |
| Using | `GlobalUsings.cs`, third-party group: `global using static InvariantStringHelper.InvariantText;` |

- Requires CSharpStyleChecker `1.2.0` or later; `1.1.0` reports the call as CSC009.
- Usage rule: `tech-csharp.md` § Style.

## Dependencies (.NET mechanics)

New Dependency Protocol: Section 4.7 (approval, what to present). .NET mechanics:

- Never add `PackageReference`, `PackageVersion`, or `ProjectReference` before approval.
- After approval: add `PackageVersion` first, then `PackageReference` without `Version`.

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="TUnit" Version="1.*" />
<!-- Project .csproj -->
<PackageReference Include="TUnit" />
```

## Vulnerable packages

NuGet Audit reports known advisories as NU1901–NU1904 (low to critical). `TreatWarningsAsErrors` turns them into restore failures. Fix the package, not the warning.

1. List every hit: `dotnet list path/App.slnx package --vulnerable --include-transitive`.
2. Direct package: raise its `PackageVersion` to the lowest patched version.
3. Transitive package: find the parent with `dotnet nuget why path/App.slnx {package-id}`. Raise the parent when a release pulls the patched version. Otherwise pin the package with a `PackageVersion` and no `PackageReference` (`CentralPackageTransitivePinningEnabled`).
4. Comment each pin: which package pulls the vulnerable version, advisory id, NU code, why this version.
5. Verify: the list from step 1 is empty, then build and run the tests.

- A patched version of a package already in the graph needs no approval (it is not a new dependency).
- No patched version in the current major: Decision class (Section 4.16). Options: upgrade the major, remove the package, replace it (New Dependency Protocol), or suppress the advisory.
- ❗ Never silence audit: no `NuGetAudit=false`, no raised `NuGetAuditLevel`, no `NuGetAuditMode=direct`, no NU190x in `NoWarn` or `WarningsNotAsErrors`.
- Suppress only with user approval, per advisory, as a last resort. It hides the advisory for every package that shares it. Delete the entry when a patch ships.
- NU1900 means the audit data could not be fetched; no package is at fault. Re-run per Section 4.14. Still failing: blocker (`tech-reporting.md`).

```xml
<!-- Id: GHSA-xxxx-xxxx-xxxx (NU1903). Why: {why the vulnerable code is unreachable here}. No patched version as of {package version}. User approved. -->
<NuGetAuditSuppress Include="https://github.com/advisories/GHSA-xxxx-xxxx-xxxx" />
```

## Commands

```bash
dotnet build path/App.slnx -c Release
dotnet test path/App.slnx -c Release --no-build
dotnet pack path/Proj/Proj.csproj -c Release    # the only release path (§ Release packages)
```

`dotnet test` on the solution is the full run. A scoped project run uses `tech-tunit.md`. File-in-use during build: concurrent agent (Section 4.14).

## Source File Copyright Header

Exact text from `COPYRIGHT`, in the file type’s native comment.

| File type | Value |
|-----------|-------|
| `.cs`, `.razor.cs` | `// {copyright}` |
| `.css`, `.razor.css` | `/* {copyright} */` |
| `.razor` | `@* {copyright} *@` (a Razor comment is not rendered into the HTML) |
| `.md`, `.html` | `<!-- {copyright} -->` |

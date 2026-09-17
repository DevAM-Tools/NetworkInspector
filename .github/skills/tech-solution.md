# Solution and Build Configuration

Load when `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `GlobalUsings.cs`, or `*.csproj` / `*.props` / `*.targets` are in scope. Extends Section 4.7 in `copilot-instructions.md`. SSOT for MSBuild properties, CPM, CSharpStyleChecker, and New Dependency Protocol steps.

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

When a warning is hidden in MSBuild, use the **global** suppression template in `tech-csharp.md` (Diagnostics). One XML comment **per id**: id, why the warning is inapplicable, **why global** (not method/file/pragma), user approved.

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
| `VersionPrefix` | central in `Directory.Build.props` (e.g. `1.0.0`) |
| `ContinuousIntegrationBuild` | `true` on Release builds; `true` when `CI` is set |
| `DebugType` | `embedded` or `portable` (consistent) |

### Versioning & Metadata (on-request)

Central release version: `VersionPrefix` in `Directory.Build.props` (applies to all packable projects with `PackageId`). Per-project `VersionPrefix` overrides only when a package must diverge.
When publishing or packaging is in scope, ask user for: `VersionSuffix`, `Company`, `Authors`, `Copyright`, `Description`, `PackageLicenseExpression`, `PackageProjectUrl`, `RepositoryUrl`.

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

❗ Mandatory NuGet **`1.*`** on every SDK-style consumer (`netstandard2.0` or `net5.0`+), including Roslyn source generators. Active on the referencing project only — not transitive to downstream libraries.

| Step | Action |
|------|--------|
| CPM | `Directory.Packages.props`: `<PackageVersion Include="CSharpStyleChecker" Version="1.*" />` |
| Project | `<PackageReference Include="CSharpStyleChecker" />` — omit `Version` when CPM enabled |
| No CPM | `<PackageReference Include="CSharpStyleChecker" Version="1.*" />` in `.csproj` |

- Analyzers load from `analyzers/dotnet/cs`; **`ExitPoints` bundled** — no second package, no `PrivateAssets` / `IncludeAssets`.
- Violations = compiler errors (CSC*). Rebuild after add.
- Set `ApplyCSharpStyleChecker=false` only to opt out.

## New Dependency Protocol

Intent: Section 4.7. Never add a dependency without user approval.

- Never add `PackageReference`, `PackageVersion`, or `ProjectReference` without user approval.
- Ask in Grill-Me when plan may need new dependencies.
- Present: package id, **what it does**, **why it is needed**, license (`MIT` / `Apache-2.0` / BSD-like), alternatives.
- After approval: add `PackageVersion` first, then `PackageReference` without `Version`.

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="TUnit" Version="1.*" />
<!-- Project .csproj -->
<PackageReference Include="TUnit" />
```

## Commands

```bash
dotnet build CopilotAIWorkflow.slnx -c Release
dotnet test CopilotAIWorkflow.slnx -c Release --no-build
dotnet pack path/Proj.csproj -c Release
```

Do not add a wrapper script without approval. File-in-use during build: concurrent agent (Section 4.14).

## Source File Copyright Header

| File type | Value |
|-----------|-------|
| `.cs`, `.razor.cs`, `.css` | `// {copyright}` — exact text from `COPYRIGHT` |
| `.md`, `.html`, `.razor` | `<!-- {copyright} -->` — exact text from `COPYRIGHT` |

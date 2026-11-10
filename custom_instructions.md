<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# Custom instructions

## Trust-boundary limits

The mechanism is the packages `NetworkInspector.Limits` (declaration, value, live holder, unit parts, hit sentence, duplicate
check) and `NetworkInspector.Settings` (a limit as a setting). The rules below are the policies of NetworkInspector with their
values; the reasons and the rejected options are in [`docs/decisions/limits-policy.md`](docs/decisions/limits-policy.md).

- Declare every limit at a trust boundary as one `LimitDefinition` in the catalog class of its project
  (`CoreLimits`, `SourcesLimits`, …). Do not declare a limit as a bare `const`, `long` option, or settings attribute.
- Make every limit secure by default: active, finite, and generous for the reference machine of 8 logical processors and 16 GB
  of memory. A default is one fixed number chosen for that machine; whoever needs more changes the setting. Size per-item limits
  at least 4× the largest item of ordinary work, and table sizes and durations at least 10×.
- Never derive a default from the memory or the processors of the machine. Check each catalog in one test with
  `LimitCatalog.Check(All)` (duplicate keys).
- Give every limit a hard range and allow `off`, for every limit, including the protection of the stack: keep `allowOff` of the
  `LimitDefinition` at its default `true`. Fill all seven texts of `LimitTexts`: title, description, reason, benefit, smaller
  values with an example, larger values with an example, off effect.
- Pick the unit from `LimitUnits` (`Count`, `Bytes`, `Duration`). When none fits, compose a `TableUnit` from a `UnitWordSet` once,
  next to the limits that use it; never write a unit into a text or a key by hand.
- Name a limit `group.lowerCamelName` without a unit word; the unit belongs to the value (`maxEntrySize`, not `maxEntryBytes`).
- Expose every limit to the user: `RegisterLimit` in the settings, `RegisterLimitOption` for source and format options,
  `[LimitSetting]` for protocol fields. The user changes it with the settings file, `ni settings set`, and `--set`. Every
  front end may change every limit; the definition makes no distinction by origin.
- Read a limit in a loop through its live holder (`Limit.Ceiling`, from `manager.GetLiveLimit(key)`), once per decision.
- Report every hit with the `HitMessage` extension of `CoreLimitPolicy`, which adds the `ni` change hint to
  `LimitMessages.HitMessage`. Never truncate, drop, or clamp data silently.
- Keep a bound as a constant only when it is a format fact, internal tuning, or a display rule. Write that reason as a
  comment at the constant.
- Add the limit to its catalog `All`; the limits reference `docs/limits.md` is generated from the catalogs and lists it once it
  exists. Until then the catalog is the register. Add the tests at the limit, one above, `off`, and the default.
- Test projects use constants; they need no limits.

## Packing

- ❗ Produce packages only with `dotnet pack` (project or solution). A build never packs: do not set `GeneratePackageOnBuild`, because `dotnet pack` would then skip its own build and package stale binaries from `bin/<Configuration>`.
- ❗ Never use `dotnet pack --no-build`; a target in `Directory.Build.targets` rejects it (`NIPACK001`).
- Keep the clean-before-pack rules in `Directory.Build.targets` (per project: bin and obj of the configuration, own package) and `Directory.Solution.targets` (solution pack: the package folder of the current version).
- Hook a step that must run before the package is written through the SDK property `BeforePack`. A `BeforeTargets="Pack"` hook runs after the package was written, because MSBuild runs `DependsOnTargets` first.
- Never write `--` inside an XML comment in a props or targets file; MSBuild rejects the file.

## Invariant text

- Write every interpolated string that formats a number, a GUID, or another culture-dependent value as `Invariant($"…")` from `InvariantStringHelper` (`using static`). The package is a reference of every .NET 6+ project through `Directory.Build.targets`.
- In `NetworkInspector.Limits` use `string.Create(CultureInfo.InvariantCulture, $"…")`: that package lists no dependency.

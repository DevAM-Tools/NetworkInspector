<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# Architecture

This document holds the components that have an architecture section so far: the two libraries for limits and settings and the
part of Core that uses them. The other components follow with the next `/docs-repair` run.

## Overview

| Component | Folder | Entry | Responsibility | May depend on |
|-----------|--------|-------|----------------|---------------|
| Limits | `NetworkInspector.Limits/` | `LimitDefinition` | Declares a trust-boundary limit, holds its live value, parses and formats it, reports a hit | nothing |
| Settings | `NetworkInspector.Settings/` | `SettingsManager` | Typed settings, text and JSON forms, sparse files, safe saving, profiles; its own loader limits | Limits |
| Core | `NetworkInspector.Core/` | `Stack` | Parser engine; passes its layout and its change hint to the two libraries | Values, Settings, Limits |

[`AssemblyReferencesTests`](../NetworkInspector.Limits.Tests/AssemblyReferencesTests.cs) of both test projects enforce the first
two rows: Limits references nothing of NetworkInspector, Settings references only Limits.

## Limits

**Flows.** A definition is built once (the constructor checks key, range, default, and the seven texts), shown with `Format`,
parsed with `TryParse`, held in one `LimitWriter` per consumer, and reported with `LimitMessages.HitMessage`. A settings store
keeps the writer and changes the value through it; enforcing code receives only the read side, `Limit`, and reads
`Limit.Ceiling` once per decision.

**Invariants.** A default lies inside its range. `0` never means off: `LimitValue.Off` is a separate state, and `default(LimitValue)`
is `Of(0)`, which fails closed. `Limit.Ceiling` is `long.MaxValue` when the limit is off. The core never uses the `Utility`
namespace. No default is read from the machine.

**Easy to miss.** The grammar in [`TableUnit.cs`](../NetworkInspector.Limits/Utility/TableUnit.cs) is described in the
[Limits README](../NetworkInspector.Limits/README.md) § Value grammar (`Sync:`). The README and guide snippets are the bodies of
`GuideSnippetTests`. The policies with their numbers are decisions of the application:
[`limits-policy.md`](decisions/limits-policy.md).

## Settings

**Flows.** `Load` reads each `<group>.json` of the folder (bounded by `settings.maxFileSize`, `settings.maxJsonDepth`, and
`settings.maxGroupFiles`), applies valid values, and keeps unknown keys as orphans; a manager that called `RegisterOwnLimits`
reads `settings.json` first, so a stored limit bounds the other files. `Save` writes only values that differ from their default and
deletes the stale group files it owns, after it has read every file it would replace or delete. `SaveChanged` merges single keys.
All three writers take the folder lock for at most `settings.folderLockTimeout`.

**Invariants.** A persisting manager has a `SettingsStorageLayout`. A `Setting` has one owner manager for its life. No file I/O
and no JSON conversion runs under the instance lock, a plain lock for a few dictionary operations. Events are raised after every
lock is released. A group publishes immutable snapshots, so it needs no lock. Every loader limit is a `LimitDefinition` of
`SettingsLimits`; `SettingsLimitValues` carries the starting values, checked when the manager is created, and the manager
enforces through a live `Limit` per limit. `Save` never replaces or deletes a file that `Load` skipped. A key is applied only from
the file of its group.

**Easy to miss.** The lock file and manifest names are the format on disk: `CoreSettingsLayout` in Core and
`SettingsDiskCompatTests` in the Core tests keep them (`Sync:`). The default group is the file `default.json`:
`SettingNames.DefaultGroupStem` is used by the name rule, the save, and the load (`Sync:`). Temporary files carry a 32-digit id
(`SettingsFileAccess.NewTempPath` and `IsTempFileName`, `Sync:`), because a save deletes only files of that form. The name
grammar of `SettingNames` is copied by the protocol layer and proved by `NameGrammarSyncTests` in the Core tests.
Decision: [`settings-limits-libraries.md`](decisions/settings-limits-libraries.md).

## Core

**Flows.** Core registers its settings through `SettingsRegistrar` and turns a limit hit into the `ni` sentence with
`CoreLimitPolicy.HitMessage`, which supplies the change hint (`ni settings set` and `--set`) that the library leaves to the
application.

**Invariants.** Every front end passes `CoreSettingsLayout.Instance` to the manager, the factory, and the profile functions.

**Easy to miss.** The change hint in `CoreLimitPolicy` names commands of the CLI (`Sync:` with `custom_instructions.md` § Trust-boundary
limits).

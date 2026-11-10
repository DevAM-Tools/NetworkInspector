<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Settings

Typed application settings for any .NET application. It depends on nothing application-specific: the package references the .NET runtime,
the small `InvariantStringHelper` package, and `NetworkInspector.Limits` (its sibling package for trust-boundary limits, which
itself has no dependency), nothing else.

An application has values that users change and that must survive a restart: a greeting, a port, the largest file it reads. This
package keeps them in one place with a type, a default, a range, and a text a user can read, and writes only what a user changed.

| What you get | How |
|--------------|-----|
| Typed values | `Setting` with bool, string, 64-bit integers, float, bytes, enum, arrays, and limit values; each with default and range |
| One text form and one JSON form | `SettingValueText` for command lines and text boxes, `SettingValueJson` for files and remote clients, with the same refusals |
| Sparse files | Only values that differ from their default are written, so a later change of a default reaches every user who never changed it |
| Safe saving | Atomic file replacement and a cross-process lock; two processes that change different keys with `SaveChanged` never lose each other's changes |
| Profiles | `SettingsProfiles`: list, create, copy, rename, and delete named settings folders |
| Limits as settings | `SettingsRegistrar.RegisterLimit` registers a limit of `NetworkInspector.Limits` as a setting that accepts `256 MiB` or `off`, refuses `off` and every change where the definition says so, and keeps a live holder (`GetLiveLimit`, read side only: it cannot change the limit) for hot loops |

## Quickstart

Prerequisites: .NET 10 or later.

```text
dotnet add package NetworkInspector.Settings
```

```csharp
using System.Globalization;
using NetworkInspector.Limits;
using NetworkInspector.Limits.Utility;
using NetworkInspector.Settings;

TextWriter output = Console.Out;

// 1. Choose the names of your application once: the folder under the application data folder, the folders next to the
//    profiles that are no profiles, and the two bookkeeping files of a settings folder. Never change them between releases.
SettingsStorageLayout layout = SettingsStorageLayout.Create("Alpha", ["logs", "cache"], ".alpha.lock", ".alpha-owned");

// A limit is declared once (see the guide of NetworkInspector.Limits) and registered like any other setting.
LimitDefinition maxEntrySize = new(
    "app.maxEntrySize",
    LimitUnits.Bytes,
    defaultValue: 128L * 1024 * 1024,
    minimum: 1024,
    maximum: 1L << 40,
    new LimitTexts(
        Title: "Largest archive entry",
        Description: "The largest size of one entry that is read from an archive.",
        Reason: "A hostile archive could otherwise claim an entry that uses all memory.",
        Benefit: "A broken or hostile archive is refused with a clear message.",
        SmallerValues: "Entries above the value are refused: 1 MiB refuses a 2 MiB entry.",
        LargerValues: "More memory may be used per entry: 1 GiB lets a 900 MiB entry through.",
        OffEffect: "Every entry is accepted and a hostile archive can use all memory."));

// 2. Create a manager for one folder, register the settings, and load what an earlier run stored.
string folder = Path.Combine(Path.GetTempPath(), "settings-quickstart-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
using (SettingsManager manager = new(folder, layout))
{
    SettingsRegistrar registrar = new(manager);
    registrar.RegisterStringSetting("app.greeting", "Greeting", "app", "Hello");
    registrar.RegisterLimit(maxEntrySize);
    manager.Load();

    // 3. Change values: a pending value becomes the current value with ApplyChanges, and Save writes the changed values.
    Setting greeting = manager.GetSetting("app.greeting")!;
    Setting limit = manager.GetSetting("app.maxEntrySize")!;
    greeting.SetPendingValue(SettingValue.String("Hi"));
    if (SettingValueText.TryParse(limit, "256 MiB", out SettingValue chosen, out SettingProblem? problem))
    {
        limit.SetPendingValue(chosen);
    }
    else
    {
        output.WriteLine(problem);
    }

    manager.ApplyChanges();
    manager.Save();
}

// 4. A new manager, for example in the next run, reads the same values.
using (SettingsManager later = new(folder, layout))
{
    SettingsRegistrar registrar = new(later);
    registrar.RegisterStringSetting("app.greeting", "Greeting", "app", "Hello");
    registrar.RegisterLimit(maxEntrySize);
    later.Load();

    output.WriteLine(later.GetStringSetting("app.greeting"));
    output.WriteLine(maxEntrySize.Format(later.GetLimit("app.maxEntrySize")!.Value));

    // A hot loop keeps the live holder of the limit and reads its ceiling: no lookup, no lock, no allocation.
    Limit live = later.GetLiveLimit("app.maxEntrySize")!;
    output.WriteLine(live.Ceiling);
}

// 5. The folder holds one file per group and the two bookkeeping files of the layout.
foreach (string path in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
{
    output.WriteLine(Path.GetFileName(path));
}

Directory.Delete(folder, recursive: true);
```

<!-- Sync: NetworkInspector.Settings.Tests/GuideSnippetTests.cs TheQuickstartWritesTheDocumentedOutput; docs/guides/settings.md § Add settings to an application. -->

Output:

```text
Hi
256 MiB
268435456
.alpha-owned
.alpha.lock
app.json
```

## What is stored where

A settings folder holds one JSON file per group: the setting `app.greeting` of the group `app` lives in `app.json`, and the
settings without a group live in `default.json` (so the group name `default` is reserved). Next to them sit the two
bookkeeping files whose names the layout chooses:

| File | Name comes from | Purpose |
|------|-----------------|---------|
| `<group>.json` | the group of a setting | The values of that group that differ from their default |
| Lock file | `SettingsStorageLayout.LockFileName` | The cross-process lock a save holds; it stays in the folder |
| Ownership manifest | `SettingsStorageLayout.OwnedManifestFileName` | The group files this library wrote, so that cleanup never deletes an unrelated JSON file |

Both bookkeeping names are part of the format on disk. A manager that wrote other names than an earlier run would not find the
files that run owned and would leave them in the folder, so the layout is required whenever a manager persists.

## What your application decides

| Decision | Where it enters |
|----------|-----------------|
| The application folder name, the folders that are no profiles, the two file names | `SettingsStorageLayout.Create` (required by every manager that persists) |
| Which settings exist, their defaults and ranges | Your registration code (`SettingsRegistrar`) |
| Which front end may change which setting | Your code: the library stores and validates values, it does not know who asks |
| The limits of the loader: the largest file, the deepest nesting, the wait for the folder lock, the most group files, the longest value text | `SettingsLimitValues` (start from `SettingsLimitValues.Default`: 64 MiB, depth 64, 2 s, 1,024 files, 1,048,576 characters; each is a `LimitDefinition` of `SettingsLimits`; `off` removes a limit, `0` is refused) passed to the `SettingsManager` constructor or `SettingsManagerFactory.Create`; call `manager.RegisterOwnLimits()` to offer them as settings of the group `settings` that a user can change and store |

## Build and test

```text
dotnet build NetworkInspector.Settings/NetworkInspector.Settings.csproj -c Release
dotnet test NetworkInspector.Settings.Tests/NetworkInspector.Settings.Tests.csproj -c Release
```

## Documentation

[Guide: add settings to an application](../docs/guides/settings.md) · [Limits package](../NetworkInspector.Limits/README.md)

## License

MIT. See the license in the repository root.

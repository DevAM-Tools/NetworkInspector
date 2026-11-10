<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# Guide — Add settings to an application

**For:** developers who give a .NET application settings that users change · **Prerequisites:** .NET 10, the package `NetworkInspector.Settings` ([README](../../NetworkInspector.Settings/README.md)). Limits as settings use the package `NetworkInspector.Limits` ([guide](limits.md)).

This guide takes an application from no settings to values that survive a restart: choose the layout, register settings, change
and save them, handle what a load reports, and use profiles and several processes. The quickstart is the body of a passing test.

## Steps

### Choose a layout

The layout names what the library writes into the settings folder. Create it once for the application and keep it for the life
of the process; never change a name between two releases, because the names are part of the format on disk.

| Name | Rule | Example |
|------|------|---------|
| Application name | A plain file name of at most 120 characters: no separator, no `..`, no `:`, no wildcard, no device name (`CON`) | `Alpha` |
| Reserved folders | Folders under the settings base that are no profiles; each must be a valid profile name; `application` is always reserved | `logs`, `cache` |
| Lock file | Starts with a dot, a plain name, does not end in `.json` | `.alpha.lock` |
| Ownership manifest | The same rules; differs from the lock file | `.alpha-owned` |

`SettingsStorageLayout.Create` throws an `ArgumentException` that names the argument and the rule when a name breaks its rule:
that is a defect of the setup and shows at the first run. A manager that persists cannot be created without a layout.

### Register settings and load

1. Create a `SettingsManager` for a folder: `new SettingsManager(folder, layout)`, or `SettingsManagerFactory.Create(layout,
   settingsPath, profileName)` to resolve the folder (the application data folder plus the application name by default, a profile
   as a sub-folder).
2. Register groups and settings by name through `SettingsRegistrar`: `RegisterStringSetting`, `RegisterBoolSetting`, the integer
   and float forms with optional `min` and `max`, enum and array forms, and `RegisterLimit` for a limit. A name is a
   dot-separated ASCII identifier (`app.greeting`); the group is the part before the last dot. Group names are lower case, and
   `default` is reserved because the file `default.json` holds the settings without a group.
3. Call `Load()`. It returns the warnings of the load (below); a file that cannot be used never stops the load.
4. Change a value with `setting.SetPendingValue(value)`, make it current with `manager.ApplyChanges()`, and write with `Save()`.
   `Save` writes the changed values of all groups and removes group files this library wrote that no longer hold a value;
   `SaveChanged(names)` merges only the named keys into the files, so two managers or processes that change different keys never
   lose each other's changes.

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

<!-- Sync: NetworkInspector.Settings.Tests/GuideSnippetTests.cs TheQuickstartWritesTheDocumentedOutput; NetworkInspector.Settings/README.md § Quickstart. -->

### Read a limit in a hot loop

A limit setting owns a live holder, `Setting.LiveLimit` (a `Limit`). Ask the manager for it once with
`manager.GetLiveLimit(key)` and keep it: reading `limit.Ceiling` takes no lock, allocates nothing, and does not look the setting
up. The holder shows the current value, so it changes with `ApplyChanges()`, a `Load()`, and a reset, and not with a pending value.
Every manager has its own holder, also when two managers register the same definition. The holder never changes its identity.

A limit that its definition declares read-only accepts only its default: another value is refused as a pending value, in the text
form, and in a file (a warning, the default stays) with the problem kind `ReadOnly` and the text `the limit is read-only`. A
limit that cannot be switched off refuses `off` in the same three places.

### Handle what a load reports

`Load()` returns a list of `SettingsLoadWarning` (kind, group, setting, message). A warning never stops the load; the setting keeps
its default or its earlier value.

| Kind | What happened | What the application does |
|------|---------------|---------------------------|
| `InvalidGroupName` | A file name is not a valid group name; the file is skipped | Show the warning; the user renames or removes the file |
| `TypeMismatch`, `DeserializationError` | A stored value has the wrong type or cannot be decoded; it is ignored | Show the warning; the default applies |
| `OutOfRange` | A stored value is outside the range of the setting, a limit outside its hard range, or another value of a read-only limit; it is ignored | Show the warning with the range; the default applies |
| `InvalidGroupFileShape`, `InvalidGroupFileSyntax` | The file is not a JSON object, is not valid JSON, or repeats a property; the file is skipped | Show the warning; the user repairs the file |
| `FileTooLarge` | The file is larger than `settings.maxFileSize`; the message names the limit, its value, and its key; the file is skipped | Show the warning; raise the limit when the application lets the user, or repair the file |
| `JsonTooDeep` | The file nests deeper than `settings.maxJsonDepth`; the message names the limit, its value, and its key; the file is skipped | Show the warning; raise the limit when the application lets the user, or repair the file |
| `TooManyGroupFiles` | The folder holds more files than `settings.maxGroupFiles`; the message names the limit; the files after it are not read | Show the warning; raise the limit or remove the files |
| `WrongGroupFile` | The key of a registered setting stands in the file of another group; it is not applied and a save keeps it in that file | Show the warning; the user moves the key to the file of its group |
| `EnumNameMismatch` | A stored enum name differs from the canonical name; the canonical name is applied | None needed |
| `ExternalConfigUnavailable` | A configuration file that a setting refers to cannot be read | The component that owns the setting continues with its default |

Values of settings that are not registered in this run (for example the settings of a component that is not loaded) are kept as
`Orphans` and written back unchanged by `Save`, so a save never loses them.

### Choose the limits of the loader

The library bounds what it reads, waits for, and parses with five limits, each a `LimitDefinition` in `SettingsLimits`. You pass
the starting values as `SettingsLimitValues` to the `SettingsManager` constructor or to `SettingsManagerFactory.Create`; they
apply to the first file that is read. Start from the secure defaults and change one value with `with`:

| Limit (key) | Default | Range | What it bounds |
|-------------|---------|-------|----------------|
| `settings.maxFileSize` | 64 MiB | 1 KiB – 1 TiB | One settings file, the manifest, or a referenced configuration file |
| `settings.maxJsonDepth` | 64 | 4 – 1024 | The nesting of arrays and objects in those files |
| `settings.folderLockTimeout` | 2 s | 100 ms – 1 h | The wait for the lock of the settings folder |
| `settings.maxGroupFiles` | 1,024 | 1 – 1,000,000 | The number of group files that one `Load` reads |
| `settings.maxTextLength` | 1,048,576 | 1 – 2^30 | The characters of one value text for `SettingValueText.TryParse` |

```csharp
SettingsLimitValues limits = SettingsLimitValues.Default with { MaxFileSize = LimitValue.Of(8L << 20) };
using SettingsManager manager = new(folder, layout, limits);
```

`LimitValue.Off` removes a limit; `LimitValue.Of(0)` is refused when the manager is created, because zero never means off. A file
above the size limit is skipped with a `FileTooLarge` warning, a file that nests too deep with `JsonTooDeep`, a folder with too many
files stops the load with `TooManyGroupFiles`, a text above the length limit is refused with the problem kind `TextTooLong`, and
every one names the limit and its key. Pass `manager.Limits.MaxTextLength` to the overload of `SettingValueText.TryParse` that
takes it.

**The limits as settings.** Call `manager.RegisterOwnLimits()` once, before `Load`, and the five limits become settings of the
group `settings` (file `settings.json`): a user reads them with `GetLimit`, changes them like any other setting, and `Save`
stores a value that differs from the starting value. `Load` reads `settings.json` first, so a stored value bounds every other
file of the same load, and a value that is applied or loaded takes effect for the next file at once. The call is opt-in: a manager
that does not call it (for example the short-lived manager of one request) lists no extra setting and keeps the starting values.
`RegisterOwnLimits(defaults)` takes other default values for the settings than the starting values (a front end that enforces a
value for one run while it loads, such as the `--set` of `ni`, gives its normal defaults), and the call returns the warnings for
stored values that it refused when it runs after `Load`.
`manager.Limits` returns the values in effect now; hot code keeps `manager.GetLiveLimit("settings.maxFileSize")` and reads its
`Ceiling`. The read side has no way to change a limit; the manager and the setting hold the writer.

**Streams.** `JsonConfigStream.TryLoad` is bounded by default (64 MiB, depth 64). Pass `manager.Limits` as the last argument to
follow the limits of a manager.

### Use profiles

A profile is a named sub-folder of the settings base. `SettingsProfiles` lists, creates (empty or as a copy), renames, and deletes
them. Each member takes the layout, because the reserved folders are no profiles and the lock file is not copied. A rename or
delete that fails midway puts the folder back. A profile name is 1 to 256 ASCII letters, digits, spaces, dots, dashes, and
underscores without a leading or trailing space or dot, is not a device name, and is not a reserved folder in any letter case;
`ProfileNames.TryValidate(name, layout, out error)` checks it before the name reaches a path.

### Several processes

Two processes may use one folder. `Save` and `SaveChanged` run under the lock file of the layout and wait up to `settings.folderLockTimeout` (2 s by default) for it;
then they throw a `PersistenceSettingsException` that names the limit and its key. `SaveChangedAsync` waits without blocking a thread
and stops at its `CancellationToken`. Only a sharing violation counts as a holder: any other I/O error (a full disk, a bad path) is
reported at once. A crashed holder releases the lock with its process. Only files listed in the ownership manifest are ever removed.

`Save` reads every file that it would replace or delete before it writes the first one. `Load` only warns about a file that is too
large, too deep, or not valid JSON, and skips it; a `Save` that would overwrite such a file throws a `PersistenceSettingsException`
and leaves the folder as it was, so a typo in a hand-edited file cannot cost the values of its group.

## Verify

```text
dotnet test NetworkInspector.Settings.Tests/NetworkInspector.Settings.Tests.csproj -c Release -- --treenode-filter "/*/*/GuideSnippetTests/*"
```

The test passes, and the output block of the README is the output of the snippet.

## Troubleshoot

These are the refusal texts a user can see from `SettingValueText.TryParse` for the settings of the examples (`app.port` is an
integer setting with the range 1 to 65535, `app.verbose` a bool setting).

| Text | Cause | Fix |
|------|-------|-----|
| `app.port: 'abc' is not a whole number; example: 8080` | The text is not an integer | Write a whole number |
| `app.port: allowed 1 – 65535` | The number is outside the range | Choose a number inside the range |
| `app.verbose: 'maybe' is not true or false` | The text is not a bool | Write `true` or `false` |
| `app.maxEntrySize: 'MB' is a decimal unit and is not accepted; use 256 MiB` | A limit text with a decimal unit | Write the binary unit the text suggests |

| Symptom | Cause | Fix |
|---------|-------|-----|
| `ArgumentException` naming `lockFileName` or `ownedManifestFileName` | The layout names break a rule (no leading dot, ends in `.json`, equal names) | Choose names that follow the table of the layout |
| Old group files stay in the folder after a save | The layout names differ from those of the earlier release, so the manager does not find the manifest | Use the file names the earlier release wrote |
| `PersistenceSettingsException`: the folder is in use by another process | Another process holds the lock file for more than two seconds | Try again |

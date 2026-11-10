<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# Contract Reference

One entry per public type of [`NetworkInspector.Plugins`](../../NetworkInspector.Plugins/README.md), the core-tier
plugin contract. The XML documentation of each type is the normative text; this page groups the types and shows how
they are used together. How to build a plugin: [plugin guide](guide.md).

| Group | Types |
| --- | --- |
| Entry and metadata | [`IPlugin`](#iplugin), [`PluginInfo`](#plugininfo), [`PluginDependency`](#plugindependency) |
| Configuration | [`IPluginBuilder`](#ipluginbuilder) |
| Running plugin | [`IPluginContext`](#iplugincontext), [`IPluginLog`](#ipluginlog) |
| Sources | [`SourceRegistration`](#sourceregistration), [`SourceCreate`](#sourcecreate), [`SourceRequest`](#sourcerequest) |
| Exporters | [`ExporterRegistration<TListener>`](#exporterregistrationtlistener), [`ExporterCreate<TListener>`](#exportercreatetlistener), [`ExportRequest`](#exportrequest) |
| Expected failures | [`PluginError`](#pluginerror), [`PluginErrorCode`](#pluginerrorcode) |

The contract adds registrations and a lifecycle only. Sources and exporters are the Core types `IFrameSource`,
`IFrameListener`, and `IPacketListener`; options and settings are the types of the settings library (`SettingsRegistrar`,
`ReadOnlySettingsManagerView` in `NetworkInspector.Settings`) and `SettingScope` of `NetworkInspector.Core.Settings`.

## Entry and metadata

### IPlugin

The entry point of a plugin. The entry class is public, not abstract, not generic, has a public parameterless
constructor, and lists `IPlugin` in its own interface list; the pre-scan finds it by that interface without loading the
assembly.

| Member | Called | Bound |
| --- | --- | --- |
| `PluginInfo Info { get; }` | when the plugin is admitted | none |
| `void Configure(IPluginBuilder builder)` | once, in load order, before the application is composed | none |
| `ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)` | once, in load order, after composition; default does nothing | `plugins.startTimeoutSeconds` |
| `ValueTask StopAsync(CancellationToken cancellationToken)` | once, in reverse load order; default does nothing | `plugins.stopTimeoutSeconds` |

A throw from any member marks only this plugin as failed. The application calls the members sequentially; state that
stack contributions or event handlers use later must be thread-safe.

Usage:

```csharp
public sealed class MyPlugin : IPlugin
{
    public PluginInfo Info { get; } = new("My.Plugin", "1.0.0", "MIT", "My protocol on UDP 40000.", []);

    public void Configure(IPluginBuilder builder) =>
        builder.AddStackContribution(static stack => MyRegistration.Register(stack));
}
```

### PluginInfo

`sealed record PluginInfo(string Name, string Version, string License, string Description,
IReadOnlyList<PluginDependency> Dependencies)` — the identity of a plugin and what the application lists about it.
`Name` has 1 to 256 characters, is a dot-separated identifier (`My.Plugin`), compares ignoring case, and names the
plugin's data folder. The other texts may be empty, never `null`. The loader checks the record when the plugin is
configured.

Usage:

```csharp
new PluginInfo("Acme.CanTools", "2.1.0", "MIT", "CAN signal decoding for Acme ECUs.",
    [new PluginDependency("Acme.Dbc", "4.0.1", "Apache-2.0", "Acme Corp.", "https://example.com/acme-dbc", "")]);
```

### PluginDependency

`sealed record PluginDependency(string Name, string Version, string License, string CopyrightHolder, string Link,
string LicenseText)` — a library the plugin ships privately next to its assembly, with the information an application
shows on its license page. `LicenseText` may be empty when the license name suffices.

Usage: list every library of the plugin folder in `PluginInfo.Dependencies`; an empty list when there is none.

## Configuration

### IPluginBuilder

Collects the contributions of one plugin while `Configure` runs. Every rule violation throws inside `Configure`, so the
plugin fails and the application continues; using the builder after `Configure` returned throws
`InvalidOperationException`. Single-threaded.

| Member | Contributes |
| --- | --- |
| `string PluginName` | Name of the plugin being configured |
| `void AddStackContribution(Action<IStackBuilder> register)` | A callback that runs for every stack the application builds; must be stateless or thread-safe |
| `void AddSettings(SettingScope scope, Action<SettingsRegistrar> register)` | Application or profile settings; `SettingScope.Session` throws `ArgumentOutOfRangeException` |
| `void AddSource(SourceRegistration source)` | A source kind, unique ignoring case |
| `void AddFrameExporter(ExporterRegistration<IFrameListener> exporter)` | A frame format |
| `void AddPacketExporter(ExporterRegistration<IPacketListener> exporter)` | A packet format |

Format names are unique across frame and packet formats.

Usage:

```csharp
public void Configure(IPluginBuilder builder)
{
    builder.AddStackContribution(static stack => SampleRegistration.Register(stack));
    builder.AddSource(SampleSource.Registration);
    builder.AddPacketExporter(SampleTextExporter.Registration);
    builder.AddSettings(SettingScope.Application, static registrar =>
        registrar.RegisterStringSetting("sample.greeting", "Greeting", "sample", "Hello"));
}
```

## Running plugin

### IPluginContext

What a started plugin gets from the application. Members are thread-safe; events are raised on one application thread,
and a throwing handler becomes one error log entry.

| Member | Meaning |
| --- | --- |
| `string PluginName` | Name of the plugin |
| `IPluginLog Log` | The plugin's log |
| `string DataDirectory` | `<settings base>/plugins-data/<storage name>/`; throws `IOException` with the reason when the folder could not be created |
| `ReadOnlySettingsManagerView ApplicationSettings` | Application settings, including the plugin's own |
| `ReadOnlySettingsManagerView ProfileSettings` | Settings of the active profile (in `ni`: the `--profile` of the run) |
| `event EventHandler<SettingsAppliedEventArgs>? SettingsApplied` | Raised after application or profile settings were applied |
| `CancellationToken Stopping` | Cancelled when the shutdown starts, before `StopAsync` |

Usage:

```csharp
public ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)
{
    string greeting = context.ApplicationSettings.GetStringSetting("sample.greeting") ?? string.Empty;
    context.Log.Info("started; greeting=" + greeting);
    return ValueTask.CompletedTask;
}
```

### IPluginLog

The log of one plugin; thread-safe. Each entry is one line. In `ni` an entry goes to the error output and to
`logs/ni.log` as `<level> <plugin>: <text>`; control characters become spaces.

| Member | Use |
| --- | --- |
| `void Info(string text)` | Progress worth knowing |
| `void Warning(string text)` | Something is off; the plugin keeps working |
| `void Error(string text, Exception? exception = null)` | A failure; the exception adds its type and message, never its stack trace |

Usage: `context.Log.Warning("configuration file not found; using defaults")`. Never log secrets or personal data.

## Sources

### SourceRegistration

`sealed record SourceRegistration(string Kind, string UiName, string Description, IReadOnlyList<string> FileExtensions,
bool AllowsMultipleFiles, Action<SettingsRegistrar> RegisterOptions, SourceCreate Create)` — a source kind.

| Parameter | Rule |
| --- | --- |
| `Kind` | 1 to 256 characters matching `[A-Za-z][A-Za-z0-9_.-]*`; unique ignoring case; the word users type (`SampleGen:count=10`) |
| `UiName` | Not empty |
| `Description` | One sentence, never `null` |
| `FileExtensions` | Each starts with `.`; used to pick the kind for a bare path; empty for kinds without files |
| `AllowsMultipleFiles` | Several files may be selected; the application creates one source per file |
| `RegisterOptions` | Registers the option settings (group `""`) into a fresh manager per request |
| `Create` | The factory |

Usage:

```csharp
builder.AddSource(new SourceRegistration("AcmeLog", "Acme log", "Reads Acme ECU logs.", [".acl"], true,
    static options => options.RegisterBoolSetting("strict", "Strict", "", false, "Stop at the first damaged record."),
    AcmeLogSource.TryCreate));
```

### SourceCreate

`delegate bool SourceCreate(SourceRequest request, out IFrameSource? source, out PluginError? error)` — creates a Core
source that is not started yet. Returns `false` with an error for every expected failure (missing or unreadable file,
unsupported format, invalid option combination) and never throws for them; a throw signals a bug. May run concurrently.

Usage:

```csharp
public static bool TryCreate(SourceRequest request, [NotNullWhen(true)] out IFrameSource? source,
    [NotNullWhen(false)] out PluginError? error)
{
    source = null;
    if (!File.Exists(request.Paths[0]))
    {
        error = new PluginError(PluginErrorCode.FileNotFound, "File not found: " + request.Paths[0], "paths");
        return false;
    }

    source = new AcmeLogSource(request.Paths[0], request.Options.GetBoolSetting("strict") ?? false);
    error = null;
    return true;
}
```

### SourceRequest

`sealed record SourceRequest(IReadOnlyList<string> Paths, ReadOnlySettingsManagerView Options,
ReadOnlySettingsManagerView SessionSettings)` — one request: the file of this source (empty for kinds without files),
the applied options, and the settings of the receiving session (in `ni`: the profile settings).

Usage: `request.Options.GetI64Setting("count") ?? 100` reads an option that Core already validated.

## Exporters

### ExporterRegistration<TListener>

`sealed record ExporterRegistration<TListener>(string Format, string UiName, string Description, string FileExtension,
Action<SettingsRegistrar> RegisterOptions, ExporterCreate<TListener> Create) where TListener : class` — an export
format. `TListener` is `IFrameListener` (frame formats, `ni convert`) or `IPacketListener` (packet formats,
`ni export`). `Format` follows the rule of source kinds and is unique across all formats; `FileExtension` starts with
`.` or is empty for directory output.

Contract rule: a listener that is disposed without `OnFinish` leaves no partial output. Either it deletes its output
itself, or the application's export runner deletes the target afterwards.

Usage:

```csharp
builder.AddPacketExporter(new ExporterRegistration<IPacketListener>("SampleText", "Sample text",
    "One line per sample packet.", ".sampletxt",
    static options => options.RegisterBoolSetting("includeHeader", "Header line", "", true),
    SampleTextExporter.TryCreate));
```

### ExporterCreate<TListener>

`delegate bool ExporterCreate<TListener>(ExportRequest request, out TListener? listener, out PluginError? error) where
TListener : class` — creates the listener for one export. The application prepared the target (existence and overwrite
are decided before this call). Returns `false` with an error for every expected failure; a throw signals a bug.

Usage: return `PluginErrorCode.TargetExists` when the listener would have to replace a file the application did not
clear, and `PluginErrorCode.FileUnreadable` when the folder is not writable.

### ExportRequest

`sealed record ExportRequest(string TargetPath, ReadOnlySettingsManagerView Options, IStack Stack, long
ExpectedItemCount, CancellationToken CancellationToken)` — one export: the full output path, the applied options, the
stack that parsed the packets (resolve field ids by name here, once), an informational item count for progress, and
the cancellation of the export.

Usage: `FieldId? speed = request.Stack.GetFieldId("sample.speed");` once in `Create`, then read the field per packet.

## Expected failures

### PluginError

`readonly record struct PluginError(PluginErrorCode Code, string Message, string? Option = null)` — an expected failure
of a source or exporter factory, returned and never thrown. `Message` is one English line for the user; `Option` names
the option at fault, `paths` when the file list is at fault, or `null`.

Usage: `new PluginError(PluginErrorCode.InvalidOption, "count must be even.", "count")`.

### PluginErrorCode

| Value | Meaning |
| --- | --- |
| `InvalidOption` | An option or the file list has a value the registration cannot use |
| `FileNotFound` | A file or folder the request needs does not exist |
| `FileUnreadable` | A file or folder exists but cannot be read or written |
| `UnsupportedFormat` | The file is not valid for this source kind |
| `TargetExists` | The export target exists and the caller did not ask to replace it |
| `Failed` | Any other expected failure; the message says what happened |

Usage: callers map the code to exit codes and dialog texts; `ni` prints the message as one line.

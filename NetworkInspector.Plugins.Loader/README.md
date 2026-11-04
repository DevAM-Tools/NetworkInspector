<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Plugins.Loader

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Plugins.Loader?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NetworkInspector.Plugins.Loader)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

The plugin loader of NetworkInspector: it finds plugins in folders without running them, loads each plugin assembly in
its own load context, configures the plugins, publishes their protocols, sources, export formats, and settings, and
starts and stops them with time limits. The `ni` command-line tool loads its plugins with this package. Reference it
when your own application should load the same plugins.

You do **not** need this package to write a plugin; a plugin references only
[`NetworkInspector.Plugins`](../NetworkInspector.Plugins/README.md).

## Install

```bash
dotnet add package NetworkInspector.Plugins.Loader
dotnet add package NetworkInspector.BuiltInPlugins   # the standard protocols, sources, and formats as plugins
```

## Load, run, and stop plugins

```csharp
using NetworkInspector.BuiltInPlugins;
using NetworkInspector.Core;
using NetworkInspector.Core.Ids;
using NetworkInspector.Core.Interfaces;
using NetworkInspector.Core.Settings;
using NetworkInspector.Plugins;
using NetworkInspector.Plugins.Loader;

string appFolder = Path.Combine(Path.GetTempPath(), "my-analyzer");
string pluginFolder = args[0];

// 1. Application settings: the start and stop timeouts of the lifecycle.
using SettingsManager application = new();
PluginTimeoutSettings.Register(application);

// 2. Load the built-in plugins and the plugins of the folder. A failing plugin is a record, never an exception.
using PluginRegistry registry = new();
PluginLoader loader = new(new PluginLoaderOptions(Path.Combine(appFolder, "plugin-scan-cache.json"),
    NetworkInspectorBuiltIns.CreateAll()));
PluginLoadReport report = loader.Load(PluginTier.Core, [pluginFolder], registry, CancellationToken.None);
foreach (PluginRecord record in report.Plugins)
{
    Console.WriteLine($"{record.Name} {record.Version}: {record.State} {record.Reason}");
}

// 3. Plugin settings, then a stack with every protocol of every loaded plugin (the standard protocols included).
registry.ApplySettings(SettingScope.Application, application);
using SettingsManager profile = new();
registry.ApplySettings(SettingScope.Profile, profile);

FrameInterfaceRegistry frames = new();
StackBuilder builder = new(new SettingsManager(), frames);
registry.ApplyStackContributions(builder);
using Stack stack = builder.Build();

// 4. Start the plugins; each gets a context with its log, data folder, and settings.
Dictionary<string, PluginContext> contexts = new(StringComparer.OrdinalIgnoreCase);
IPluginContext ContextFor(PluginRecord record)
{
    if (!contexts.TryGetValue(record.Name, out PluginContext? context))
    {
        string data = Path.Combine(appFolder, "plugins-data", StorageNames.ToStorageName(record.Name));
        context = new PluginContext(record.Name, new ConsoleLog(record.Name), data, application, profile);
        contexts[record.Name] = context;
    }

    return context;
}

report = await PluginLifecycle.StartAsync(report, registry.Disable, ContextFor,
    PluginTimeoutSettings.ReadStart(application.ReadOnly), CancellationToken.None);

// 5. Open a plugin source with option texts and parse its frames with the stack.
if (registry.TryGetSource("SampleGen", out SourceRegistration? kind, out string? problem))
{
    using SettingsManager options = RegistrationOptions.CreateDefaults(kind.RegisterOptions);
    if (RegistrationOptions.TryApplyTexts(options, [new("count", "3")], out PluginError? optionError)
        && kind.Create(new SourceRequest([], options.ReadOnly, profile.ReadOnly), out IFrameSource? source, out PluginError? error))
    {
        using (source)
        {
            source.Start(frames.RegisterSource(source), frames);
            ParseOptions parse = new();
            int nextId = 0;
            while (source.NextFrame() is { } frame)
            {
                if (Packet.TryParse(nextId++, stack, frame, in parse, out Packet? packet, out _) && packet is not null)
                {
                    Console.WriteLine(packet.Info);
                }
            }
        }
    }
}
else
{
    Console.WriteLine(problem);
}

// 6. Stop in reverse order, then release the contexts.
await PluginLifecycle.StopAsync(report, ContextFor, PluginTimeoutSettings.ReadStop(application.ReadOnly));
foreach (PluginContext context in contexts.Values)
{
    context.Dispose();
}

sealed class ConsoleLog(string plugin) : IPluginLog
{
    public void Info(string text) => Console.WriteLine($"Info {plugin}: {text}");
    public void Warning(string text) => Console.WriteLine($"Warning {plugin}: {text}");
    public void Error(string text, Exception? exception = null) => Console.WriteLine($"Error {plugin}: {text} {exception?.Message}");
}
```

With the [sample plugin](../samples/Sample.Protocol/README.md) in the folder the program prints:

```text
NetworkInspector.StandardProtocols 0.20.0: Loaded
NetworkInspector.FileSources 0.20.0: Loaded
NetworkInspector.RandomSource 0.20.0: Loaded
NetworkInspector.Exporters 0.20.0: Loaded
Sample.Protocol 0.1.0: Loaded
Info Sample.Protocol: started; greeting=Hello from Sample.Protocol
Sample counter=0 gear=0 speed=254
Sample counter=1 gear=1 speed=1
Sample counter=2 gear=2 speed=2
Info Sample.Protocol: stopped
```

## What the package contains

| Type | Purpose |
| --- | --- |
| `PluginLoader`, `PluginLoaderOptions` | Pre-scan, scan cache, load contexts, configure, commit; one `Load` call per application start |
| `PluginLoadReport`, `PluginRecord`, `PluginLoadState`, `PrescanSummary` | What was found and loaded: per plugin name, version, tier, state (`Loaded`, `Failed`, `Skipped`), reason, path, contributions, time |
| `PluginRegistry` | The published contributions: `Sources`, `FrameExporters`, `PacketExporters`, `TryGetSource`, `TryGetFrameExporter`, `TryGetPacketExporter`, `ApplyStackContributions`, `ApplySettings`, `Disable` |
| `PluginLifecycle` | `StartAsync` in load order and `StopAsync` in reverse order, each call bounded by a timeout and isolated |
| `PluginContext` | The `IPluginContext` implementation a started plugin receives |
| `PluginPathsSetting`, `PluginTimeoutSettings` | The application settings `plugins.paths`, `plugins.startTimeoutSeconds`, and `plugins.stopTimeoutSeconds` |
| `RegistrationOptions` | Option managers of a source or format from defaults and `key=value` texts, with Core validation |
| `ExportTargets`, `TargetReplacement` | Target rules every export runner applies: full path, parent folder, refused overwrite, an existing output moved aside until the new one is complete, cleanup of a partial output |
| `PluginTier`, `PluginTierStep`, `IPluginBuilderFactory`, `ContributionKind`, `ContributionRecorder` | Tier handling and contribution bookkeeping |

## Guarantees

- **No foreign code to look.** The pre-scan reads metadata with `System.Reflection.Metadata`; it never loads or runs an
  assembly to find out whether it holds a plugin. Results are cached per file (path, length, last write time).
- **One identity for shared types.** An assembly that the application already has (the shared framework, the contracts,
  Core, and every library of the application) always comes from the application, matched by name; other dependencies
  resolve from the plugin folder. Two plugins can ship different versions of the same private library.
- **Clear version refusals.** A plugin built against a newer version of an application assembly (the contract, Core, or the
  framework) cannot bind its references; the loader detects that from metadata before any plugin code runs and fails the
  plugin with a reason that names the assembly and both versions. The same or an older version runs with the application's
  copy, as long as that assembly kept its binary contract. Before 1.0 a minor release may change it (0.20.0 changed public
  Core signatures), and the loader does not detect that: rebuild a plugin against the minor version it runs on, or it fails
  when the changed code runs.
- **One plugin never breaks another.** A plugin that fails to load, configure, register, or start becomes a `Failed`
  record with a reason; its contributions are not published or are disabled, and the others continue. `Load` throws only
  for invalid arguments and cancellation.
- **All or nothing per plugin.** Contributions are staged while a plugin is configured and published by its commit, which
  runs after every plugin was configured.
- **Bounded start and stop.** `StartAsync` and `StopAsync` of each plugin run on a thread-pool thread and are bounded by
  `plugins.startTimeoutSeconds` (default 120 s) and `plugins.stopTimeoutSeconds` (default 60 s).
- **Plugins stay loaded.** A loaded plugin cannot be unloaded while the process runs; an assembly without a loaded plugin
  is released at once.

## Thread safety

`PluginLoader` runs one `Load` at a time per instance. `PluginRegistry` publishes one consistent snapshot for readers and
is safe to read from any thread; `Disable` and the apply methods are meant for the application's composition thread.
`PluginLifecycle` calls the plugins sequentially. `PluginContext` members are thread-safe.

## Security

A plugin is trusted in-process code with the rights of the application. `plugins.paths` is empty by default, so
nothing foreign is loaded until a user adds a folder; relative entries are refused because they would mean a different
folder for every working directory. Load plugins only from folders that only trusted users can write.

More: [plugin guide](../docs/plugins/guide.md) · [contract reference](../docs/plugins/reference.md) ·
[built-in plugins](../NetworkInspector.BuiltInPlugins/README.md).

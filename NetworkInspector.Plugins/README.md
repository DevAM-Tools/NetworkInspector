<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Plugins

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Plugins?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NetworkInspector.Plugins)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

The core-tier plugin contract of NetworkInspector. A plugin is a class library with one entry class that implements
`IPlugin`. It adds protocols, heuristic parsers, post-parsers, sources, exporters, and settings to every application
that loads it, such as the `ni` command-line tool or your own application with
[`NetworkInspector.Plugins.Loader`](../NetworkInspector.Plugins.Loader/README.md). Sources and exporters are the Core
types (`IFrameSource`, `IFrameListener`, `IPacketListener`), and options and settings are Core settings, so there is no
second set of interfaces to learn.

## Install

```bash
dotnet add package NetworkInspector.Plugins
```

The application that loads your plugin already contains this package and `NetworkInspector.Core`, so reference them with
`Private="false"` in a project reference (or exclude their runtime assets in a package reference). That keeps both
assemblies out of your plugin folder; a second copy next to your plugin would be ignored by the loader anyway.

## Write a plugin

```csharp
public sealed class MyPlugin : IPlugin
{
    public PluginInfo Info { get; } = new("My.Plugin", "1.0.0", "MIT", "My protocol on UDP 40000.", []);

    public void Configure(IPluginBuilder builder)
    {
        // Runs for every stack the application builds: create fresh protocol objects in every call.
        builder.AddStackContribution(static stack =>
        {
            MyProtocol protocol = new();
            ProtocolId id = stack.RegisterProtocol(protocol);
            protocol.RegisterFields(stack, id);
            stack.WhenProtocolTableRegistered("udp.port", table => stack.RegisterParserInU64Table(table, 40000, id));
        });
        builder.AddSettings(SettingScope.Application, static registrar =>
        {
            registrar.RegisterGroup("my", "My plugin", "Settings of My.Plugin.");
            registrar.RegisterStringSetting("my.greeting", "Greeting", "my", "Hello");
        });
    }
}
```

The entry class must be public, non-abstract, non-generic, have a public parameterless constructor, and list `IPlugin`
in its own interface list (a class that only inherits the interface from a base class is not found). Build the plugin
against the same or an older NetworkInspector version than the application that loads it. Register the folder with
`ni settings set plugins.paths <folder> --scope application` and check the result with `ni plugins list`:

```text
My.Plugin  1.0.0  core  Loaded  -  /opt/ni-plugins/my/My.Plugin.dll  MIT  load 6.0 ms  Protocol 1, Setting 1
```

From then on every `ni` command decodes the protocol, filters on its fields, and lists the setting in `ni settings`.

## What the contract contains

| Type | Purpose |
|------|---------|
| `IPlugin`, `PluginInfo`, `PluginDependency` | entry point, name, version, license, private dependencies |
| `IPluginBuilder` | declares stack contributions, settings, sources, and exporters while the plugin is configured |
| `IPluginContext`, `IPluginLog` | what a started plugin gets: log, data folder, settings, stop signal |
| `SourceRegistration`, `SourceCreate`, `SourceRequest` | a source kind that creates a Core `IFrameSource` |
| `ExporterRegistration<TListener>`, `ExporterCreate<TListener>`, `ExportRequest` | an export format that creates a Core listener |
| `PluginError`, `PluginErrorCode` | expected failures of registrations, returned instead of thrown |

The sample plugin [`samples/Sample.Protocol`](../samples/Sample.Protocol/README.md) uses every part of the contract.
Read the [plugin guide](../docs/plugins/guide.md) for the steps from project setup to distribution, the
[protocol chapter](../docs/plugins/protocols.md) for writing protocols, and the
[contract reference](../docs/plugins/reference.md) for one entry per type.

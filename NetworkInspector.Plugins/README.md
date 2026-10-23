<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Plugins

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Plugins?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NetworkInspector.Plugins)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

The core-tier plugin contract of NetworkInspector. A plugin is a class library with one entry class that implements
`IPlugin`. It adds protocols, parsers, sources, exporters, and settings to every application that loads it: the `ni`
command-line tool today, the MCP host and the Network Inspector app later. Sources and exporters are the Core types
(`IFrameSource`, `IFrameListener`, `IPacketListener`), and options and settings are Core settings, so there is no second
set of interfaces to learn.

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
        builder.AddStackContribution(static stack =>
        {
            ProtocolId id = stack.RegisterProtocol(new MyProtocol());
            stack.RegisterParserInU64TableByName("udp.port", 40000, id);
        });
        builder.AddSettings(SettingScope.Application, static registrar =>
            registrar.RegisterStringSetting("my.greeting", "Greeting", "my", "Hello"));
    }
}
```

The entry class must be public, non-abstract, non-generic, have a public parameterless constructor, and list `IPlugin`
in its own interface list (a class that only inherits the interface from a base class is not found). Register the
folder with `ni settings set plugins.paths <folder>` and check the result with `ni plugins list`.

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

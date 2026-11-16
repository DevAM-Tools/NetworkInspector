<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.BuiltInPlugins

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.BuiltInPlugins?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NetworkInspector.BuiltInPlugins)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

The functionality of NetworkInspector as four core-tier plugins on the same contract that external plugins use
([`NetworkInspector.Plugins`](../NetworkInspector.Plugins/README.md)). The `ni` command-line tool gets its protocols,
sources, and export formats from them. An application that loads plugins with
[`NetworkInspector.Plugins.Loader`](../NetworkInspector.Plugins.Loader/README.md) passes them to the loader and gets the
same inputs, outputs, and option names as `ni`.

## Install

```bash
dotnet add package NetworkInspector.BuiltInPlugins
```

## Use

```csharp
using NetworkInspector.BuiltInPlugins;
using NetworkInspector.Plugins.Loader;

PluginLoader loader = new(new PluginLoaderOptions(scanCacheFile, NetworkInspectorBuiltIns.CreateAll()));
```

`NetworkInspectorBuiltIns.CreateAll()` returns new instances of the four plugins in their fixed load order on every call,
so two loaders never share plugin state. The built-ins are passed as instances and never found by a folder scan: a copy
of this assembly in a plugin folder is reported as a duplicate name instead of being loaded twice. The full sequence of
load, start, use, and stop is in the [loader README](../NetworkInspector.Plugins.Loader/README.md#load-run-and-stop-plugins).

## The four plugins

| Plugin | Contributes |
| --- | --- |
| `NetworkInspector.StandardProtocols` | Every protocol of [`NetworkInspector.Protocols`](../NetworkInspector.Protocols/README.md) with its heuristic parsers, stream reassembly, and protocol settings; registered first, so the tables of the standard protocols exist before any other plugin's stack contribution runs |
| `NetworkInspector.FileSources` | The source kinds `PCAPNG`, `PCAP` (legacy), `BLF`, and `ASC` from [`NetworkInspector.Sources`](../NetworkInspector.Sources/README.md) |
| `NetworkInspector.RandomSource` | The source kind `Random`: seeded synthetic traffic |
| `NetworkInspector.Exporters` | The frame formats `PCAPNG`, `BLF`, and `ASC` and the packet formats `CSV`, `JSON`, `Text`, `PBF`, `Parquet`, and `DuckDB` from [`NetworkInspector.Exporters`](../NetworkInspector.Exporters/README.md) and [`NetworkInspector.Exporters.DuckDb`](../NetworkInspector.Exporters.DuckDb/README.md) |

Every registration returns the Core source or exporter itself and declares its options as Core settings with the
defaults of the command line (for example `scanMode`, `errorTolerance`, `preloadBudgetBytes`, `frameSlabBytes`,
`timestampTimeZone`, `delimiter`, `columns`, `style`). The option names and values are listed in the
[CLI README](../NetworkInspector.CLI/README.md); `ni plugins list` shows the counts per plugin.

Every exporter registration also declares how its format receives the output: `ExporterCreation.ForStreams` (the host hands a
`StreamExportRequest` with an `IExportTarget` of named write-only streams; all formats except DuckDB) or
`ExporterCreation.ForFile` (a `FileExportRequest` with a prepared path; DuckDB). `ni` builds a file target for a file format
and a folder target for a format without a file extension (the Parquet dataset). The contract is in the
[plugin reference](../docs/plugins/reference.md#exporters).

## Thread safety

The stack contributions and the source and exporter factories keep no shared state and may run concurrently, as the
contract requires. Each created source or exporter follows the thread-safety rules of its Core type.

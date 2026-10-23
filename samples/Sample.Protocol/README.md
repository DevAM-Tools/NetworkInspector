<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# Sample.Protocol

The tier 1 sample plugin of NetworkInspector. It is small on purpose and uses every part of the core-tier plugin contract
([`NetworkInspector.Plugins`](../../NetworkInspector.Plugins/README.md)).

| Contribution | What it shows | File |
|--------------|---------------|------|
| Protocol `sample` | a hand-written `IProtocol` decoding `sample.counter`, `sample.gear`, `sample.speed`, registered on UDP port 40000 | [`SampleProtocol.cs`](SampleProtocol.cs) |
| Heuristic parser | the magic `NIS1` found in TCP payload on any port through the existing `tcp.heuristic` table | [`SampleHeuristicParser.cs`](SampleHeuristicParser.cs) |
| Post-parser | `sample.post.magic_offset` on every frame that carries the magic | [`SamplePostParser.cs`](SamplePostParser.cs) |
| Source `SampleGen` | a Core `IFrameSource` (with random access and statistics) whose options are Core settings | [`SampleSource.cs`](SampleSource.cs), [`SampleFrameSource.cs`](SampleFrameSource.cs) |
| Exporter `SampleText` | a Core `IPacketListener` with statistics and partial-output cleanup | [`SampleTextExporter.cs`](SampleTextExporter.cs), [`SampleTextListener.cs`](SampleTextListener.cs) |
| Setting `sample.greeting` | an application setting registered with the Core `SettingsRegistrar` | [`SampleProtocolPlugin.cs`](SampleProtocolPlugin.cs) |

The wire format is a 14-byte payload: magic `NIS1` (4 bytes), counter (8 bytes, big endian), gear (1 byte), speed (1 byte).
The generated speed pattern gives tests two special values: 254 (error) when the frame index is a multiple of 50, and 255
(signal not available) for the indices 70–76 of every hundred.

## Build and use

```bash
dotnet build samples/Sample.Protocol/Sample.Protocol.csproj -c Release
```

Copy `Sample.Protocol.dll` (and nothing from NetworkInspector: the application provides the contract and Core) to a folder
and register the folder with `ni`:

```bash
ni settings set plugins.paths <folder>
ni plugins list
ni export SampleGen:count=10 -o out.json
```

`ni plugins list` shows the plugin with its tier, state, and contribution counts; `ni settings get sample.greeting` shows
the plugin's setting.

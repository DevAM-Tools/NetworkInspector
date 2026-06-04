<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Sessions

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Sessions)](https://www.nuget.org/packages/NetworkInspector.Sessions)

Session orchestration library for NetworkInspector.

## What This Is

`NetworkInspector.Sessions` coordinates frame sources, the protocol stack, pull-based listeners, and background jobs. It provides thread-safe packet access, a packet store with re-parse fallback, and Roaring-bitmap indexing during parsing.

## Key Types

- `Session` — lifecycle orchestration (`TryAddFrameSource`, `TryAddListener`, `TryStart`, `WaitForCompletion`, `Shutdown`)
- `ISessionReader` — read-only session view for listeners
- `ISessionListener` — pull-based notification callbacks
- `PacketStore` — chunked store retaining all parsed packets until restart or shutdown (`Clear`)

## Dependencies

- `NetworkInspector.Core` — stack, parsing, `PacketIndex`
- `NetworkInspector.Sources` — `IFrameSource` implementations

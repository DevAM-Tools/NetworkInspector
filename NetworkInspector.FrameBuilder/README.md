<!-- Copyright © 2026 DevAM. All rights reserved. -->

# NetworkInspector.FrameBuilder

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.FrameBuilder)](https://www.nuget.org/packages/NetworkInspector.FrameBuilder)

Typed frame construction package for NetworkInspector.

## What This Is

`NetworkInspector.FrameBuilder` helps you construct valid protocol frame stacks in .NET code.

Use it when you need to:

- generate test traffic,
- build synthetic packet streams,
- create protocol-valid frames for tooling and simulation.

## Why It Stands Out

- Fluent, typed composition via `FrameStack.Start(...).Then(...).CreateWithFixedValues()`.
- Supports common link/network/transport/application stack combinations.
- Suitable for reusable build pipelines and high-frequency generation loops.

## Install

```bash
dotnet add package NetworkInspector.FrameBuilder
```

## Quick Start

```csharp
using NetworkInspector.FrameBuilder;

EthernetLayer eth = new(dstMac, srcMac);
IPv4Layer ip = new(srcIp, dstIp);
UdpLayer udp = new(srcPort: 12345, dstPort: 53);

var stack = FrameStack
    .Start(eth)
    .Then(ip)
    .Then(udp)
    .CreateWithFixedValues();

byte[] buffer = new byte[stack.HeaderSize + payload.Length];
var sequence = stack.Build(payload);

while (sequence.MoveNext(buffer, out int written))
{
    sender.Send(buffer.AsSpan(0, written));
}
```

## Common Tasks

### Build Single Frames

Use `CreateWithFixedValues()` and one `Build(payload)` sequence for deterministic frame emission.

### Build Fragmented Output

Use the returned frame sequence and iterate until completion when payload size exceeds one frame.

### Build Stateful Packet Streams

Open sessions when sequence numbers, IDs, or counters must advance across packets.

## Limits And Caller Responsibilities

- Follow protocol constraints for checksums and segmentation behavior in your stack design.
- Validate payload sizing assumptions for transport-specific workflows.
- Keep generation loops bounded and cancellation-aware in long-running jobs.

## Links

- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.FrameBuilder)
- [Source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.FrameBuilder)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)
- [FRAMEBUILDER_GUIDE.md](FRAMEBUILDER_GUIDE.md)

## License

[MIT License](../LICENSE)

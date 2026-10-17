<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.FrameBuilder

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.FrameBuilder?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NetworkInspector.FrameBuilder)
[![Downloads](https://img.shields.io/nuget/dt/NetworkInspector.FrameBuilder?logo=nuget&label=downloads)][nuget]
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

[nuget]: https://www.nuget.org/packages/NetworkInspector.FrameBuilder

Build valid wire frames from typed protocol layers: Ethernet → IPv4 → UDP → payload in one fluent expression,
with lengths, next-protocol fields, and checksums filled in for you. Use it for test traffic, simulators, fuzzing
seeds, and synthetic captures.

## Highlights

- **Typed, compile-time-checked stacks.** `FrameStack.Start(eth).Then(ip).Then(udp)`; layer order and
  pseudo-header requirements are checked by the compiler.
- **Correct by default.** IPv4 header, UDP/TCP pseudo-header, and ICMP checksums, length fields, EtherType, and IP
  protocol numbers are computed. Pin any of them with `Auto<T>` when you need a specific (or wrong) value.
- **Built for loops.** Layers are `readonly struct`s and frames are written into your buffer through a `ref struct`
  sequence: no per-frame heap allocation on the build path.
- **Fragmentation and segmentation.** IPv4 fragmentation and SOME/IP-TP segmentation come from the same
  `MoveNext` loop.
- **Stateful sessions.** Auto-incrementing IPv4 ids, TCP sequence numbers, and SOME/IP-TP counters across packets;
  `TcpConnection` emits handshakes, MSS-segmented data, and FIN/RST teardown.
- **Broad layer set:** link, CAN/LIN/FlexRay bus, IPv4/IPv6 with extension headers, ARP, ICMP/ICMPv6, TCP/UDP, DNS,
  DHCP, TLS/DTLS records, HTTP/2, WebSocket, SOME/IP, PDU Transport, signal messages.
- **Verified output.** Frames are cross-checked against `tshark` 4.6 in the test suite.

## Install

```bash
dotnet add package NetworkInspector.FrameBuilder
```

`NetworkInspector.Values` (`MacAddress`, `IPv4Address`, …) comes in as a dependency.

## Quick Start

```csharp
using NetworkInspector.FrameBuilder;
using NetworkInspector.Values;
using UdpStack = NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.UdpLayer,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.IPv4Layer,
        NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.EthernetLayer, NetworkInspector.FrameBuilder.StackEnd>>>;

EthernetLayer eth = new(
    dstMac: MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]),
    srcMac: MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]));
IPv4Layer ip = new(IPv4Address.FromBytes([192, 168, 0, 1]), IPv4Address.FromBytes([192, 168, 0, 2]));
UdpLayer udp = new(srcPort: 12345, dstPort: 9);

CreatedStack<UdpStack, NoTrailer, NoInterceptor> stack = FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues();

byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
byte[] buffer = new byte[stack.HeaderSize + payload.Length + stack.TrailerSize];

using FrameSequence<UdpStack, NoTrailer, NoInterceptor> sequence = stack.Build(payload);
while (sequence.MoveNext(buffer, out int written))
{
    Console.WriteLine(Convert.ToHexString(buffer, 0, written));
}

if (sequence.Status != BuildStatus.Success)
{
    throw new InvalidOperationException($"Build failed: {sequence.Status}");
}
```

`MoveNext` never throws. When it returns `false` before success, `Status` (`BuildStatus`) says why, for example
`BufferTooSmall` or `FragmentationRequired`. Build the stack once and call `Build` for every payload.

The nested generic type is the price of compile-time stack checking; a file-local `using` alias (`UdpStack` above)
keeps it readable. Declare every sequence with `using`: a fragmenting build holds a per-thread scratch buffer until
its last fragment or `Dispose`. The snippets below reuse `UdpStack`, `eth`, `ip`, `udp`, `payload` and `buffer`.

## Common Tasks

### Write A Test Capture

Combine FrameBuilder with `NetworkInspector.Core` and `NetworkInspector.Exporters` to produce a `.pcapng` that
Wireshark opens:

```csharp
using NetworkInspector.Core;
using NetworkInspector.Core.Ids;
using NetworkInspector.Exporters.Pcapng;

FrameInterfaceRegistry registry = new();
using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToFile("synthetic.pcapng").Build();

for (int i = 0; i < 100; i++)
{
    using FrameSequence<UdpStack, NoTrailer, NoInterceptor> frames = stack.Build(payload);
    while (frames.MoveNext(buffer, out int written))
    {
        Frame frame = Frame.Create(
            new FrameId(i), Timestamp.FromMillis(i), buffer.AsSpan(0, written).ToArray(),
            LinkType.Ethernet, FrameInterfaceId.Invalid, registry).Value;
        exporter.OnFrame(frame);
    }
}

exporter.OnFinish();
```

### Fragment Large Datagrams

Allow fragmentation on the IPv4 layer and size the buffer for one link-layer frame:

```csharp
IPv4Layer fragmentingIp = new(IPv4Address.FromBytes([192, 168, 0, 1]), IPv4Address.FromBytes([192, 168, 0, 2]), dontFragment: false);
CreatedStack<UdpStack, NoTrailer, NoInterceptor> fragmenting = FrameStack.Start(eth).Then(fragmentingIp).Then(udp).CreateWithFixedValues();

byte[] frameBuffer = new byte[fragmenting.MaxFrameSize]; // smallest MTU in the stack (Ethernet: 1518)
using FrameSequence<UdpStack, NoTrailer, NoInterceptor> fragments = fragmenting.Build(new byte[4000]);
while (fragments.MoveNext(frameBuffer, out int written))
{
    // one IPv4 fragment per iteration
}
```

### Stream TCP Segments With Automatic Sequence Numbers

```csharp
using NetworkInspector.FrameBuilder.Constants; // TcpFlags
using TcpStack = NetworkInspector.FrameBuilder.Stack<NetworkInspector.FrameBuilder.TcpLayerWithAutoSequence,
    NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.IPv4Layer,
        NetworkInspector.FrameBuilder.StatelessStack<NetworkInspector.FrameBuilder.EthernetLayer, NetworkInspector.FrameBuilder.StackEnd>>>;

TcpLayerWithAutoSequence tcp = new(
    srcPort: 49152, dstPort: 80, initialSequence: 1000, initialAck: 5000, flags: TcpFlags.Ack);
StatefulCreatedStack<TcpStack, NoTrailer, NoInterceptor> stateful =
    StatefulFrameStack.CreateForSession(FrameStack.Start(eth).Then(ip).Then(tcp));

byte[] segmentBuffer = new byte[stateful.HeaderSize + payload.Length];
using Session<TcpStack, NoTrailer, NoInterceptor> session = stateful.OpenSession();
for (int i = 0; i < 3; i++)
{
    // The sequence number advances by the payload length per packet.
    using StatefulFrameSequence<TcpStack, NoTrailer, NoInterceptor> segment = session.NextPacket(payload);
    segment.MoveNext(segmentBuffer, out int written);
}
```

For a full conversation (SYN/SYN-ACK/ACK, data in both directions, FIN), use `TcpConnection.Open`.

### Append An Ethernet FCS

```csharp
CreatedStack<UdpStack, EthernetFcs, NoInterceptor> withFcs =
    FrameStack.Start(eth).Then(ip).Then(udp).WithTrailer(EthernetFcs.Crc32).CreateWithFixedValues();
// withFcs.TrailerSize == 4; size buffers with HeaderSize + payload length + TrailerSize
```

## Layers

| Group | Layers |
| --- | --- |
| Link | `EthernetLayer`, `VlanLayer`, `LlcLayer`, `LinuxSllLayer`, `LinuxSll2Layer` |
| Bus | `SocketCanLayer`, `SocketCanFdLayer`, `SocketCanXlLayer`, `LinLayer`, `FlexRayLayer` |
| Network | `IPv4Layer`, `IPv4LayerWithOptions`, `IPv4LayerWithAutoIpId`, `IPv6Layer`, IPv6 extension headers, `ArpLayer` |
| Transport | `UdpLayer`, `TcpLayer`, `TcpLayerWithOptions`, `TcpLayerWithAutoSequence`, ICMPv4 and ICMPv6 layers |
| Application | `DnsLayer`, `DhcpV4Layer`, `DhcpV6Layer`, `TlsRecordLayer`, `DtlsRecordLayer`, `Http2Layer`, `WebSocketLayer` |
| Automotive | `SomeIpLayer`, `SomeIpTpLayer`, `SomeIpTpLayerWithAutoCounter`, `PduTransportLayer`, `PduTransportMultiLayer`, `SignalMessageLayer` |

Layers and stacks are in the `NetworkInspector.FrameBuilder` namespace; protocol constants such as `TcpFlags` are
in `NetworkInspector.FrameBuilder.Constants`. Layers named `…WithAuto…` keep state across
packets and need `StatefulFrameStack`.

## Limits And Caller Responsibilities

- A built stack is immutable and can be shared; a `Session` and a `TcpConnection` are single-threaded.
- `Build` returns a `ref struct`: consume it in the same method, do not store it, and declare it with `using`.
  Disposing a `Session` ends its open sequences: their next `MoveNext` returns `false` with `SessionDisposed`.
- Payloads that do not fit return `FragmentationRequired` unless a layer can fragment (IPv4 with
  `dontFragment: false`, SOME/IP-TP).
- Opening a `Session` or `TcpConnection` allocates once; the per-frame path does not.
- **Lengths:** a frame whose IPv4 TotalLength, IPv6 PayloadLength or UDP Length would exceed 65 535 is not emitted;
  `Status` is `InvalidLayerState`. IPv6 jumbograms are not supported. A failed packet does not advance session
  counters.
- **Memory:** `ReadOnlyMemory<byte>` constructor arguments (option blocks; payloads of CAN XL, FlexRay, DNS, TLS,
  WebSocket, …) are referenced, not copied. Keep that memory unchanged until the last frame built from the layer has
  been emitted.
- **Constructor limits:** CAN ids (11 or 29 bits), LIN checksum type (1 or 2), FlexRay payload (≤ 254 bytes) and type
  index (≤ 127), IPv6 Hop-by-Hop options (≤ 2046 bytes) and Routing data (≤ 2040 bytes) throw
  `ArgumentOutOfRangeException` when exceeded.
- **Stateful layers:** the session state has a closed slot set. Custom stateful layers are not supported yet, and two
  stateful layers of the same family (for example two auto-ID IPv4 layers) must not share a stack. Typed per-layer
  state that lifts both limits is planned for a later major version.
- **TCP helper:** `TcpConnection` is a happy-path façade, not a TCP stream model. One `Write*` call is one message;
  segments never carry bytes of two messages; there is no retransmission, reordering or window accounting; both
  directions use the same stateless carrier type. Only `WebSocketLayer` is an `IStreamProducer`; HTTP/1.x requests
  are written as raw bytes. Producer buffers above 64 KiB are released after each write instead of being kept per
  thread. A segment the carrier cannot build throws `InvalidOperationException`; none of its frames is delivered and
  the sequence numbers stay unchanged. A per-direction byte-stream model is planned for a later version.

## Learn More

- [FrameBuilder guide](FRAMEBUILDER_GUIDE.md): composition rules, `Auto<T>`, post-fix phases, fragmentation,
  interceptors, trailers, stateful sessions
- [Values](../NetworkInspector.Values/README.md) · [Exporters](../NetworkInspector.Exporters/README.md) ·
  [Root overview](../README.md)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector) ·
  [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)

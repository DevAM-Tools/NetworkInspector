<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# NetworkInspector.FrameBuilder

Zero-allocation, type-induced, JIT-inlining frame construction for .NET 10.

## At a glance

```csharp
EthernetLayer eth = new(dstMac, srcMac);
IPv4Layer     ip  = new(srcIp,  dstIp);
UdpLayer      udp = new(srcPort: 12345, dstPort: 53);

var stack = FrameStack
    .Start(eth)            // IRootLayer (frame root)
    .Then(ip)              // IProvidesNextProtocolValue<EtherTypeKind>
    .Then(udp)             // IProvidesNextProtocolValue<IpNextProtocolKind> + IRequiresPseudoHeader
    .CreateWithFixedValues();

byte[] buf = new byte[stack.HeaderSize + payload.Length];
FrameSequence<…> seq = stack.Build(payload);
while (seq.MoveNext(buf, out int written))
{
    sender.Send(buf.AsSpan(0, written));
}

if (seq.Status != BuildStatus.Success)
{
    // BufferTooSmall / FragmentationRequired / InvalidLayerState — never thrown.
}
```

The same call shape covers single-frame, fragmented and stateful builds.
`MoveNext` is throw-free; all expected runtime situations are surfaced via
`BuildStatus` (see §8 of the concept).

## Folder layout

| Folder | Purpose |
|---|---|
| `Constants/` | Wire constants (EtherTypes, IpProtocols, TcpFlags, SOME/IP message types). |
| `Headers/` | Fixed-size `[BinaryWritable]` POD header structs. |
| `Core/` | Checksum primitives (one's-complement, IPv4/IPv6 pseudo-header helpers). |
| `Capabilities/` | Marker interfaces (`IStackNode`, `IRootLayer`, `IPayloadLayer`, `IIPv6ExtensionLayer`, `IFragmentable`, `IProvides*<TKind>` / `IRequires*<TKind>`, `IStatelessLayer`, `IStatefulLayer<>`). |
| `Stack/` | Cons-list value types (`Stack<,>`, `StatelessStack<,>`, `StackEnd`), `FrameStack` builder, `CreatedStack<,,>`, `FrameLimits`. |
| `Composition/` | Fluent `Then(...)` extension methods, one per allowed transition. |
| `Build/` | `FrameSequence<,,>` ref-struct iterator, ThreadStatic scratch, `BuildStatus`, `FixPhase`, `PostFixContext`, `Auto<T>`, interceptor primitives. |
| `Trailers/` | `NoTrailer`, `EthernetFcs`. |
| `Layers/Link/` | Ethernet, VLAN. |
| `Layers/Network/` | IPv4 (+ Options), IPv6, ARP, IPv6 Hop-by-Hop / Routing / DestOpts / Fragment extension headers. |
| `Layers/Transport/` | TCP (+ Options), UDP, ICMPv4-Echo, ICMPv6-Echo. |
| `Layers/Application/` | SOME/IP, SOME/IP-TP. |
| `Layers/Bus/` | SocketCAN, CAN-FD, CAN-XL. |
| `Stateful/` | `Session<,,>` with pool, `IPv4LayerWithAutoIpId`, `IPv6FragmentExtensionLayerWithAutoId`, `TcpLayerWithAutoSequence`, `SomeIpTpLayerWithAutoCounter`. |

## Release scope and public-API decision

The FrameBuilder assembly has a mixed visibility surface: the core
composition types (`FrameStack`, `StackEnd`, `CreatedStack<,,>`,
`IProtocolLayer`, `IStatelessLayer`, `IStatefulLayer`, and the individual
layer structs) are `public`; session-pool and advanced stateful helpers
remain `internal` and are consumed only by `NetworkInspector.*` projects
through `InternalsVisibleTo`. The fluent `FrameStack` API (composition,
sessions, fragmentation) is feature-complete for the layers shipped in this
assembly (Ethernet, VLAN, IPv4 / IPv6 / IPv6 extension headers, ARP, TCP,
UDP, ICMPv4 / ICMPv6 echo, SOME/IP, SOME/IP-TP, SocketCAN / CAN-FD /
CAN-XL).

The follow-on work
(Phases A–E: generic ICMP / NDP, additional bus protocols, additional
application protocols, etc.) is **post-1.0** and is not a release blocker.
Promotion of the existing types to `public` is deferred to a 1.x minor
release once the API has been validated by in-tree consumers.

## Capability model (§4 of the concept)

| Marker | Provided by | Effect |
|---|---|---|
| `IRootLayer` | Ethernet, SocketCAN | Frame root - allowed argument to `FrameStack.Start(...)`. |
| `IConsumesNextProtocolValue<TKind>` | most layers | Outer layer publishes a slot in a typed namespace (`EtherTypeKind`, `IpNextProtocolKind`). The kind discriminator prevents invalid stackings (e.g. `Eth -> UDP`) at compile time. |
| `IProvidesNextProtocolValue<TKind>` | most layers | New layer declares its protocol type belongs to that namespace. |
| `IPayloadLayer` | SOME/IP, future DNS/HTTP/TLS | Pure payload carrier - no next-protocol coupling. |
| `IProvidesPseudoHeader` / `IRequiresPseudoHeader` | IPv4/IPv6 / TCP/UDP/ICMP | Pseudo-header for transport checksum. |
| `IProvidesMtu` / `IRequiresMtu` | Ethernet / IP | Link-MTU anchor for fragmentation. |
| `IFragmentable` | IPv4Layer (DF=false), IPv6FragmentExtensionLayer | Enables the multi-frame `FrameSequence` path. |
| `IStatelessLayer` / `IStatefulLayer<TState>` | every layer picks one | Compile-time gate between `Build(...)` and `OpenSession(...)`. |
| `ITrailerLayer` | `EthernetFcs` | After-payload bytes (FCS, MIC, padding). |

## Stateful sessions (§7.3)

```csharp
TcpLayerWithAutoSequence tcp = new(srcPort: 1234, dstPort: 80);
var stack = FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues();

using Session<…> session = stack.OpenSession();
foreach (ReadOnlySpan<byte> payload in messages)
{
    StatefulFrameSequence<…> seq = session.NextPacket(payload);
    while (seq.MoveNext(buf, out int written))
    {
        sender.Send(buf.AsSpan(0, written));
    }
}
```

The session owns the per-stateful-layer state slots (IP-ID counter, TCP
sequence number, …), the header cache and the scratch buffer. Stateless
layers in the same stack contribute no state.

## Fragmentation (§M5c/M5d) — two flavours

`IFragmentable.FragmentationKind` selects between two distinct strategies that
a single `FrameSequence`/`StatefulFrameSequence` iterator dispatches to at
build time. The choice is made by the **innermost** `IFragmentable` layer
that signals `CanFragment`.

### `FragmentationKind.NetworkLayer` (default)

Used by IPv4 (`IPv4Layer`, `IPv4LayerWithAutoIpId`) and the IPv6 Fragment
extension header (`IPv6FragmentExtensionLayer`,
`IPv6FragmentExtensionLayerWithAutoId`). The full payload is one logical
datagram; it is split at the IP layer and each fragment carries the same
inner pseudo-header / checksum.

* The headers + pseudo-header + transport (inner) checksum are computed
  **once** on a ThreadStatic scratch buffer.
* Each emitted fragment then only re-runs `ApplyPostFixUpTo(Length)` →
  `PatchFragmentable(activeKind, fragmentOffset, more)` →
  `ApplyPostFixUpTo(OuterChecksum)` → trailer.
* Fragment alignment defaults to **8 bytes** (IPv4 / IPv6 fragment-offset unit).
* `BuildStatus.FragmentationRequired` is returned for IPv4 if DF is set and
  the payload would not fit.

### `FragmentationKind.ApplicationSegmentation`

Used by SOME/IP-TP (`SomeIpTpLayer`, `SomeIpTpLayerWithAutoCounter`) and any
future application-level segmentation layer (e.g. ISO-TP). Each emitted
frame is a **complete, self-contained packet**: outer IP / UDP / Ethernet
fields are recomputed for the segment's actual length and the outer IP
layers are **never** patched as fragments — only the
application-segmentation layer itself updates its TP/segment header.

* The scratch buffer holds raw headers + the **full** payload; per-segment
  build runs the **whole** post-fix walk (`PublishPseudoHeader`,
  `InnerChecksum`, `Length`, `OuterChecksum`, `Trailer`) so e.g. IPv4 Total
  Length, UDP Length and the SOME/IP `length` field are correct for the
  segment, not for the unfragmented payload.
* `PatchFragmentable` is called **before** the post-fix walk so the
  application segmentation header (e.g. SOME/IP-TP offset/more-flag word)
  is in place when checksums and lengths are computed.
* The filter `f.FragmentationKind == activeKind` in
  `Stack<,>.PatchFragmentable` / `StatelessStack<,>.PatchFragmentable`
  ensures outer `IFragmentable` layers (IPv4/IPv6 frag) are inactive on the
  application-segmentation path.
* SOME/IP-TP segment alignment is **16 bytes** (AUTOSAR PRS §7.5.2.6:
  TP-offset is a 28-bit big-endian value with the low 4 bits implicitly
  zero, encoding 16-byte units).

### Stateful auto-counter for SOME/IP-TP

`SomeIpTpLayerWithAutoCounter` snapshots the next SOME/IP `Session ID` from
`SessionState.SomeIpNextSessionId` on every `WriteHeader` call and skips
zero on wraparound (AUTOSAR PRS §4.1.2.5: SessionId 0 is reserved). All TP
segments emitted from the same `Session.NextPacket` invocation share the
same Session ID; the counter advances once per packet, not once per
segment.

### Common machinery

The fragmentation path uses a ThreadStatic scratch buffer
(`FrameSequenceScratch`, power-of-two grow), a depth-32 offsets array
(`FrameLimits.MaxSupportedDepth`) and an `ApplyPostFixUpTo` walker
cut-off so length / outer-checksum phases for the `NetworkLayer` kind
never overwrite bytes that are payload in subsequent fragments.

## API surface

The fluent `FrameStack.Start(...).Then(...).CreateWithFixedValues()` /
`OpenSession()` entry points are the only public way to compose a stack.
The legacy flat-static `FrameStack.Build(...)` / `FrameStack.GetSize(...)` /
`FrameStack.Compile(...)` overloads (and the `CompiledStack2/3/4` value
types) have been **removed**. Migration: replace `int n =
FrameStack.Build(buf, eth, ip, udp, payload)` with
`var stack = FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues();
var seq = stack.Build(payload); seq.MoveNext(buf, out int n);`. Build the
stack once and reuse it for back-to-back builds — it is a value type and
allocates nothing.

## Performance properties

* `readonly struct` for every layer → stack-allocated, no boxing.
* Generic constraint `where T : struct, IStackNode` → JIT devirtualises
  every `WriteHeader`, `ApplyPostFix`, `PatchFragmentable` and
  `PatchNextProtocol` call.
* Cons-list is a *type*, so each unique stack signature gets its own
  monomorphisation.
* `default(NoInterceptor)` interceptor → all `OnHeaderWritten`/`OnFrameComplete`
  bodies become dead code post-JIT.
* No reflection, no allocations on the hot path; layer values are
  reusable across calls (R12).

## Caller responsibilities

### UDP over IPv6: zero checksum not allowed

RFC 8200 §8.1 requires a UDP checksum over IPv6.  The library does not
enforce this at build time.  Callers must not pass
`checksum: Auto<ushort>.Explicit(0)` to `UdpLayer` when the outer network
layer is IPv6.

### TCP: no MSS-based segmentation

`TcpLayer` and `TcpLayerWithAutoSequence` build a single TCP segment per
`Build(payload)` call.  They do not perform TCP maximum-segment-size (MSS)
segmentation.  If the payload would overflow a single frame, the build
returns `BuildStatus.BufferTooSmall` (stateless) or the sequence advances
only by the bytes that were written (stateful).  The caller is responsible
for pre-splitting payloads into MSS-sized chunks and issuing one
`Build`/`NextPacket` call per segment.  `TcpLayerWithAutoSequence`
advances the sequence number automatically by the payload length between
packets; it does **not** split a single call across multiple TCP segments.

## Testing

* `NetworkInspector.FrameBuilder.Tests` — smoke + capability tests
  for every layer, fragmentation iterator, stateful session, trailer
  + interceptor combinations.
* `NetworkInspector.Protocols.Tests` — round-trips every layer through
  the parser as cross-validation.
* `NetworkInspector.Profiling/Scenarios/FrameBuilder*` — five real-world
  profiling scenarios (single frame, fragmented, TCP/IPv6 session,
  custom interceptor, value reuse).

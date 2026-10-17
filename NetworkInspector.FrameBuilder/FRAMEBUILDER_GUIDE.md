<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# FrameBuilder Implementation Guide

> Canonical reference for implementing, reviewing, and maintaining FrameBuilder
> layers, trailers, and capability markers.
>
> Derived from the modernised application layers
> (`PduTransportLayer`, `PduTransportMultiLayer`, `PduTransportConfigFb`,
> `SignalMessageLayer`, `SignalMessageLayout`, `SignalMessageValueSet`) and the established
> patterns of the link, network, transport and bus layers
> (`EthernetLayer`, `IPv4Layer`, `IPv6Layer`, `ArpLayer`, `UdpLayer`,
> `TcpLayer`, `SocketCanLayer`, `SomeIpLayer`, `SomeIpTpLayer`, …).

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Layer Taxonomy](#2-layer-taxonomy)
3. [Capability Markers](#3-capability-markers)
4. [File & Namespace Organisation](#4-file--namespace-organisation)
5. [Naming Conventions](#5-naming-conventions)
6. [Layer Class Anatomy](#6-layer-class-anatomy)
7. [Header Struct Pattern](#7-header-struct-pattern)
8. [The `Auto<T>` Pattern](#8-the-autot-pattern)
9. [Post-Fix Phases](#9-post-fix-phases)
10. [Composition & Stacking Rules](#10-composition--stacking-rules)
11. [Stateful Layers](#11-stateful-layers)
12. [Fragmentation & Segmentation](#12-fragmentation--segmentation)
13. [MTU Plumbing & Trailers](#13-mtu-plumbing--trailers)
14. [Single-Source-of-Truth Configuration](#14-single-source-of-truth-configuration)
15. [Layout / ValueSet Split (Parameterised Layers)](#15-layout--valueset-split-parameterised-layers)
16. [Validation, Errors & Build Status](#16-validation-errors--build-status)
17. [Performance & Allocation Rules](#17-performance--allocation-rules)
18. [Thread-Safety](#18-thread-safety)
19. [Documentation Requirements](#19-documentation-requirements)
20. [Testing](#20-testing)
21. [Dos & Don'ts Cheat Sheet](#21-dos--donts-cheat-sheet)
22. [Checklist for New Layers](#22-checklist-for-new-layers)
23. [Appendix A: Minimal Stateless Layer Template](#appendix-a-minimal-stateless-layer-template)
24. [Appendix B: Minimal Stateful Layer Template](#appendix-b-minimal-stateful-layer-template)
25. [Appendix C: Capability Decision Matrix](#appendix-c-capability-decision-matrix)

---

## 1. Architecture Overview

`NetworkInspector.FrameBuilder` is a **public, allocation-free frame construction
library** shipped as the NuGet package `NetworkInspector.FrameBuilder`. It composes
fully-typed protocol stacks at compile time and writes wire frames into a
caller-supplied `Span<byte>`. The protocol and exporter test suites of this
repository are its first consumers. Every layer, marker and sequence type is
`public`; only helpers that no caller needs (`StackHelpers`, `FrameEngine`,
`ISequenceState` and its implementations, `FrameSequenceScratch`,
`FragmentGeometryHelper`, `FrameLimits`, `PduTransportEncoding`,
`IPv6ExtensionLayerHelpers`, the header structs) are `internal`. Public members
follow `copilot-instructions.md` §4.12 (misuse analysis, Try-APIs at
expected-failure boundaries) and document every limit.

### 1.1 Generic Cons-List

A protocol stack is a cons-list of layer structs.  Each cons-cell has a
`Head` (the most-recently-added layer) and a `Tail` (the previously-added
outer layers).  Two cons-list flavours exist:

| Type | When |
|------|------|
| `StatelessStack<THead, TTail>` | Every layer is `IStatelessLayer`; statically provable stateless. |
| `Stack<THead, TTail>` | At least one `IStatefulLayer` is mixed in.  Requires a `Session<>`. |

`StackEnd` is the terminating element.  `WriteHeaders` walks the list
**outer→inner**, recording each layer's start offset, then patches the
outer's *next-protocol* field with the inner layer's protocol type
(unless the outer was pinned via `Auto<T>.Explicit`).

### 1.2 Fluent Composition

```csharp
StatelessStack<UdpLayer, StatelessStack<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>>> stack =
    FrameStack
        .Start(new EthernetLayer(dstMac, srcMac))
        .Then(new IPv4Layer(srcIp, dstIp))
        .Then(new UdpLayer(srcPort, dstPort));

CreatedStack<…, NoTrailer, NoInterceptor> created = stack.CreateWithFixedValues();

byte[] buf = new byte[2048];
using FrameSequence<…> seq = created.Build(payload);
seq.MoveNext(buf, out int written);
```

The cons-list type is fully visible to the compiler, so the JIT
specialises every walk: every capability probe in `StackHelpers` folds to a
constant per concrete `THead`/`TTail`, and `NoInterceptor` / `NoTrailer` calls
are erased (see §17.1 for the probe shape that keeps this allocation-free).

### 1.3 Build & Iterate

`CreatedStack<TStack, TTrailer, TInterceptor>.Build(payload)` returns a
`FrameSequence<…>` (or `StatefulFrameSequence<…>` for a session).
`MoveNext(dst, out written)` is **throw-free**: every expected runtime
condition is reported via a `BuildStatus` enum:
`Success`, `BufferTooSmall`, `FragmentationRequired`, `InvalidLayerState`,
`StackTooDeep`, `SessionDisposed` (§16.2).

Multi-frame iteration is automatic: when the unfragmented frame exceeds
the smallest MTU asserted along the cons-list and the stack contains an
`IFragmentable` layer, `MoveNext` yields one frame per fragment.

Both sequence types are thin wrappers over one internal engine
(`FrameEngine<TStack, TTrailer, TInterceptor, TState>`); `TState` is
`NoSessionState` for stateless stacks and `SessionSequenceState` for a session.
A fix to the build state machine therefore lands once for both.

**Lifetime:** declare every sequence with `using`. A fragmenting build reserves
the calling thread's pooled scratch buffer until the last fragment is emitted;
`Dispose` releases it when the caller stops early. Without it an abandoned
fragmenting build keeps the per-thread scratch reserved, and every later
fragmenting build on that thread allocates a private buffer.

---

## 2. Layer Taxonomy

### 2.1 By Position

| Position class | Required marker(s) | Examples |
|----------------|--------------------|----------|
| Root link / bus | `IRootLayer` (+ `IInteriorLayer` if anything stacks under it) | `EthernetLayer`, `SocketCanLayer`, `IPv4Layer` (raw-IP) |
| Network | `IInteriorLayer`, `IProvidesProtocolType`, optionally `IProvidesPseudoHeader`, optionally `IFragmentable` | `IPv4Layer`, `IPv6Layer` |
| Transport (checksum-bearing) | `IRequiresPseudoHeader`, `IProvidesProtocolType` | `UdpLayer`, `TcpLayer`, `IcmpV6EchoLayer` |
| Application interior | `IInteriorLayer`, `IPseudoHeaderIndependent` | `PduTransportLayer` |
| Application terminal | `IPayloadLayer`, `IPseudoHeaderIndependent` | `SomeIpLayer`, `SomeIpTpLayer`, `SignalMessageLayer`, `PduTransportMultiLayer` |
| Pure terminal (no payload coupling) | neither `IInteriorLayer` nor `IPayloadLayer` | `ArpLayer`, `IcmpV4EchoLayer` |
| Trailer | `ITrailerLayer` (orthogonal — attached via `WithTrailer`) | `EthernetFcs`, `NoTrailer` |

### 2.2 By Statefulness

| Marker | Header signature | Reusable across frames? |
|--------|------------------|-------------------------|
| `IStatelessLayer` | `void WriteHeader(Span<byte> dst)` | Yes — fully deterministic. |
| `IStatefulLayer` | `void WriteHeader(Span<byte> dst, ref SessionState state)` | Only inside a `Session<>`. |

A layer **must implement exactly one** of `IStatelessLayer` /
`IStatefulLayer`. Mixing both is a hard contract violation.

### 2.3 By Carrier Behaviour

`IStreamCarrier` (TCP-like) and `IStreamProducer` (today only
`WebSocketLayer`) are orthogonal byte-stream segmentation markers.  A stream
producer is **not** a stack layer: it cannot be appended with `Then(...)` and is
handed to `TcpConnection<>.WriteFromClient/WriteFromServer` instead.  Stream
carriers are normal transport layers with the additional marker.  The HTTP/2,
TLS and DTLS record layers are payload layers (`IPayloadLayer`), not producers.

---

## 3. Capability Markers

C# generic constraints cannot express the *negation* of an interface, so
the framework enforces structural rules through **positive markers**.
Use the markers as published rules — adding or omitting one changes
which `Then(...)` overloads match at the call site.

### 3.1 Structural Markers (compile-time gates)

| Marker | Meaning | Implemented by |
|--------|---------|----------------|
| `IRootLayer` | May be the root of a frame (passed to `FrameStack.Start`). | Link/bus layers, raw-IP roots. |
| `IInteriorLayer` | Accepts an inner layer beneath it. May be the outer operand of `Then(...)`. | Most non-terminal layers. |
| `IPayloadLayer` | Pure terminal payload carrier; outer needs no next-protocol patch. | `SomeIpLayer`, `SignalMessageLayer`, … |
| `IRequiresPseudoHeader` | Needs an outer `IProvidesPseudoHeader` for transport-checksum. | `UdpLayer`, `TcpLayer`, `IcmpV6EchoLayer`. |
| `IPseudoHeaderIndependent` | Needs no pseudo-header from the outer. | Everything that is *not* `IRequiresPseudoHeader`. |

> **Mutual exclusion rule**: `IInteriorLayer` and `IPayloadLayer` are
> *mutually exclusive*.  A pure terminal that publishes a protocol type
> to its outer (e.g. `ArpLayer`) implements *neither* — it cannot be
> appended via the payload `Then(...)` overload because no inner is
> allowed.

### 3.2 Behavioural Markers (runtime contracts)

| Marker | Contract |
|--------|----------|
| `IProvidesProtocolType` | Exposes a 16-bit `ProtocolType` to be written into the predecessor's next-protocol field. |
| `IProvidesNextProtocolValue<TKind>` | Same as above with a phantom-type discriminator (`EtherTypeKind`, `IpNextProtocolKind`) for compile-time namespace documentation. |
| `IConsumesNextProtocolValue` / `<TKind>` | Owns a next-protocol field and patches it via `PatchNextProtocol`. |
| `IProvidesPseudoHeader` | Publishes its addresses and segment offsets into `PostFixContext` during `FixPhase.PublishPseudoHeader`. |
| `IProvidesMtu` | Publishes a link-layer MTU; consumed by `IFragmentable` layers. |
| `IRequiresMtu` | Needs an outer `IProvidesMtu` somewhere along the cons-list. |
| `IFragmentable` | Can split its payload across multiple frames (network or application segmentation). |
| `IIPv6ExtensionLayer` | IPv6 extension header (HopByHop, Routing, DestOpts, Fragment). |
| `IStreamCarrier` | Byte-stream segmenter (TCP family). |
| `IStreamProducer` | Application that serialises into a `IBufferWriter<byte>`. |

### 3.3 Choosing the Right Set

Mechanical rules for any new layer:

1. Start with `IStatelessLayer` *or* `IStatefulLayer` (never both).
2. Decide root/interior/payload position; add `IRootLayer` /
   `IInteriorLayer` / `IPayloadLayer` accordingly.
3. Decide pseudo-header pairing; add `IRequiresPseudoHeader` /
   `IPseudoHeaderIndependent`.  Almost every non-checksum-bearing layer
   needs `IPseudoHeaderIndependent`.
4. If the layer publishes a protocol-type value upward, add
   `IProvidesProtocolType` and the typed
   `IProvidesNextProtocolValue<TKind>` for the appropriate namespace.
5. If the layer owns a next-protocol field (EtherType, IP Protocol,
   IPv6 NextHeader, …), add `IConsumesNextProtocolValue<TKind>`.
6. If the layer carries a pseudo-header (network layer with addresses),
   add `IProvidesPseudoHeader`.
7. If fragmentation/segmentation applies, add `IFragmentable` and
   classify via `FragmentationKind`.
8. For link layers, add `IProvidesMtu`.

See [Appendix C](#appendix-c-capability-decision-matrix) for a full
matrix.

---

## 4. File & Namespace Organisation

```
NetworkInspector.FrameBuilder/
  Capabilities/                             ← All marker interfaces, kind enums
    IProtocolLayer.cs
    IStatelessLayer.cs / IStatefulLayer.cs
    IRootLayer.cs / IInteriorLayer.cs / IPayloadLayer.cs
    IRequiresPseudoHeader.cs / IProvidesPseudoHeader.cs / IPseudoHeaderIndependent.cs
    IProvidesProtocolType.cs / IProvidesNextProtocolValue.cs / IConsumesNextProtocolValue.cs
    IProvidesMtu.cs / IRequiresMtu.cs
    IFragmentable.cs / FragmentationKind.cs
    IIPv6ExtensionLayer.cs
    IStreamCarrier.cs / IStreamProducer.cs
    IStackNode.cs / IStatelessStack.cs
    ITrailerLayer.cs
    EtherTypeKind.cs / IpNextProtocolKind.cs    ← phantom-type discriminators
  Headers/                                  ← [BinaryWritable] header structs
    EthernetHeader.cs / VlanTag.cs / ArpHeader.cs
    IPv4Header.cs / IPv6Header.cs
    IPv6FragmentExtensionHeader.cs / IPv6OptionsExtensionHeader.cs
    UdpHeader.cs / TcpHeader.cs
    IcmpV4Header.cs / IcmpV6Header.cs
    SocketCanHeader.cs / SocketCanFdHeader.cs / SocketCanXlHeader.cs
    SomeIpHeader.cs
  Constants/                                ← Cross-protocol constants only
    EtherTypes.cs / IpProtocols.cs
    TcpFlags.cs / SomeIpMessageType.cs
  Layers/
    Bus/                                    ← Self-contained bus frames
      SocketCanLayer.cs / SocketCanFdLayer.cs / SocketCanXlLayer.cs
      FlexRayLayer.cs / LinLayer.cs
    Link/                                   ← Ethernet & friends
      EthernetLayer.cs / VlanLayer.cs / LlcLayer.cs
      LinuxSllLayer.cs / LinuxSll2Layer.cs
    Network/                                ← IP-family layers
      ArpLayer.cs
      IPv4Layer.cs / IPv4LayerWithOptions.cs
      IPv6Layer.cs
      IPv6HopByHopLayer.cs / IPv6RoutingLayer.cs / IPv6DestinationOptionsLayer.cs
      IPv6FragmentExtensionLayer.cs / IPv6ExtensionLayerHelpers.cs
    Transport/                              ← Checksum-bearing transports
      UdpLayer.cs
      TcpLayer.cs / TcpLayerWithOptions.cs / TcpOptions.cs / TcpOptionsBuilder.cs
      IcmpV4Layer.cs / IcmpV4EchoLayer.cs
      IcmpV4DestUnreachLayer.cs / IcmpV4RedirectLayer.cs / IcmpV4TimeExceededLayer.cs
      IcmpV6Layer.cs / IcmpV6EchoLayer.cs
      IcmpV6NeighborSolicitationLayer.cs / IcmpV6NeighborAdvertisementLayer.cs
      IcmpV6RouterSolicitationLayer.cs / IcmpV6RouterAdvertisementLayer.cs
    Application/                            ← Application protocols and stream producers
      SomeIpLayer.cs / SomeIpTpLayer.cs
      PduTransportLayer.cs / PduTransportMultiLayer.cs / PduTransportSlot.cs / PduTransportConfigFb.cs
      SignalMessageLayer.cs / SignalMessageLayout.cs / SignalMessageValueSet.cs
      DnsLayer.cs
      DhcpV4Layer.cs / DhcpV4Option.cs / DhcpV6Layer.cs / DhcpV6Option.cs
      TlsRecordLayer.cs / DtlsRecordLayer.cs / Http2Layer.cs
      WebSocketLayer.cs / WebSocketFrameOptions.cs / WebSocketOpcode.cs
  Stack/                                    ← Cons-list infrastructure
    FrameStack.cs / Stack.cs / StatelessStack.cs / StackEnd.cs / StackHelpers.cs
    CreatedStack.cs / TrailerStack.cs / FrameLimits.cs
  Stateful/                                 ← Stateful layer & session machinery
    Session.cs / SessionState.cs / SessionSequenceState.cs
    IPv4LayerWithAutoIpId.cs / TcpLayerWithAutoSequence.cs / TcpStreamLayer.cs
    IPv6FragmentExtensionLayerWithAutoId.cs / SomeIpTpLayerWithAutoCounter.cs
    StatefulFrameStack.cs / StatefulCreatedStack.cs / StatefulFrameSequence.cs
    TcpConnection.cs / TcpConnectionOptions.cs / TcpDirection.cs
    TcpSegmentDescriptor.cs / FrameSink.cs
  Composition/                              ← `Then(...)` extension classes
    StackThenExtensions.cs
  Trailers/                                 ← FCS, padding, MIC
    EthernetFcs.cs / NoTrailer.cs
  Build/                                    ← Build engine, sequences, phases, status, interceptors
    FrameEngine.cs / ISequenceState.cs / NoSessionState.cs
    FrameSequence.cs / FrameSequenceScratch.cs / FragmentGeometryHelper.cs
    FixPhase.cs / BuildStatus.cs / PostFixContext.cs
    Auto.cs / IFrameInterceptor.cs / DelegateInterceptor.cs / NoInterceptor.cs
  Core/                                     ← Shared utilities
    ChecksumUtils.cs
  GlobalUsings.cs
```

**Namespace rule**: every type lives in **`NetworkInspector.FrameBuilder`**
(except `Headers/*` which use `NetworkInspector.FrameBuilder.Headers` and
`Constants/*` which use `NetworkInspector.FrameBuilder.Constants`).
The folder structure is purely organisational.

**File rule**: one layer per file.  A layer's helper types
(constants, enums, configs, layouts, value sets) belong either in their
own file or, when small and tightly coupled, at the bottom of the
layer's file.  Examples:

- `PduTransportLayer.cs` contains `PduTransportLayer` and
  `PduTransportEncoding` (the shared big-endian writer used by both
  PDU-Transport variants).
- `SignalMessageLayout.cs` contains `SignalMessageLayout`, `SignalSpec`,
  `MuxSpec`, `MuxGroupSpec`, `DispatchBinding`, `SignalEndian` — the entire layout vocabulary.

---

## 5. Naming Conventions

| Element | Pattern | Examples |
|---------|---------|----------|
| Layer struct | `XxxLayer` | `EthernetLayer`, `UdpLayer`, `SignalMessageLayer` |
| Specialised stateful variant | `XxxLayerWithAutoYyy` | `IPv4LayerWithAutoIpId`, `TcpLayerWithAutoSequence` |
| Multi-element variant | `XxxMultiLayer` | `PduTransportMultiLayer` |
| Header struct | `XxxHeader` | `UdpHeader`, `EthernetHeader` |
| Trailer struct | `XxxFcs` / `XxxTrailer` | `EthernetFcs`, `NoTrailer` |
| Configuration object | `XxxConfigFb` (when paired with parser-side config) or `XxxConfig` | `PduTransportConfigFb` |
| Layout object | `XxxLayout` | `SignalMessageLayout` |
| Per-frame value bag | `XxxValueSet` | `SignalMessageValueSet` |
| Capability marker | `IXxx` | `IRootLayer`, `IPayloadLayer` |
| Phantom-type kind | `XxxKind` | `EtherTypeKind`, `IpNextProtocolKind` |
| Constants class | `XxxTypes` / `XxxFlags` / `XxxProtocols` | `EtherTypes`, `TcpFlags`, `IpProtocols` |
| Private fields | `_PascalCase` | `_SrcPort`, `_PduId` |
| Local layer-internal const | `private const … XxxOffset / XxxMask / XxxSize` | `LengthOffset`, `MoreFragmentsMask` |
| Factory method (struct layer) | `Single(...)` / `Create(...)` / `FromXxx(...)` | `PduTransportLayer.Single`, `PduTransportMultiLayer.Create`, `SignalMessageLayer.FromRawBytes` |

**Unit naming**: do **not** encode physical units in identifiers; put the
unit in a comment instead (e.g. `private readonly int _SegmentOffset; // 16-byte units`).

**Layer-internal constant convention**: keep field-geometry constants uniform
across layers so a reader can infer meaning from the suffix alone:

| Suffix | Meaning | Example |
|--------|---------|---------|
| `XxxOffset` | Byte offset of a field within the header | `LengthOffset`, `ChecksumOffset` |
| `XxxSize` | Byte length of a field or header | `IPv4Header.Size` |
| `XxxMask` | Bit mask applied to a packed word | `MoreFragmentsMask`, `FragmentOffsetMask` |
| `XxxBitPosition` | Zero-based bit index used for shifting | `EcnBitPosition` |

Use `XxxMask` only for bit masks (never for byte offsets) and reserve
`XxxBitPosition` for shift amounts so mask/offset/shift roles never blur.

---

## 6. Layer Class Anatomy

### 6.1 Skeleton

Every layer is a `public readonly struct`:

```csharp
public readonly struct XxxLayer :
    IStatelessLayer,           // OR IStatefulLayer
    IRootLayer,                // optional, if root-eligible
    IInteriorLayer,            // OR IPayloadLayer (mutually exclusive)
    IPseudoHeaderIndependent,  // OR IRequiresPseudoHeader
    IProvidesProtocolType,     // when applicable
    IProvidesNextProtocolValue<XxxKind>,
    IConsumesNextProtocolValue<XxxKind>,
    IProvidesPseudoHeader,
    IFragmentable,
    IProvidesMtu
{
    // 1. Layer-internal constants (offsets, masks, sizes)
    private const int LengthOffset = 4;
    private const ushort MoreFragmentsMask = 0x2000;

    // 2. Private fields (immutable; constructor-initialised)
    private readonly ushort _SrcPort;
    private readonly ushort _DstPort;
    private readonly ushort _ExplicitChecksum;
    private readonly bool _ChecksumIsExplicit;

    // 3. Constructor (or factory) — see §6.2; validates every argument (§16.1)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public XxxLayer(ushort srcPort, ushort dstPort, Auto<ushort> checksum = default) { ... }

    // 4. HeaderSize (constant per instance)
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => XxxHeader.Size;
    }

    // 5. ProtocolType (when IProvidesProtocolType)
    public ushort ProtocolType { get => IpProtocols.Xxx; }

    // 6. WriteHeader (stateless) — eager bytes, length/checksum left at 0
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst) { ... }

    // 7. PatchNextProtocol (when IConsumesNextProtocolValue)
    public void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort next) { ... }

    // 8. ApplyPostFix — single switch over FixPhase
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength,
                             scoped ref PostFixContext ctx) { ... }

    // 9. IFragmentable / IProvidesMtu surface members (only when applicable)
    public bool CanFragment { ... }
    public void PatchFragmentHeader(...) { ... }
    public ushort LinkMtu { ... }
}
```

### 6.2 Public Constructor vs Factory

- **Use a public constructor** when the parameter set is small and the
  constructor can validate every argument itself (range-check and throw
  `ArgumentOutOfRangeException`, §16.1).  Examples: `EthernetLayer`,
  `UdpLayer`, `IPv4Layer`, `SomeIpLayer`, `FlexRayLayer`, `SocketCanLayer`.
- **Use a factory** (and keep the constructor `private`) when validation
  must run before the struct is constructed or when multiple flavours
  share storage but differ in semantics.  Examples:
  - `PduTransportLayer.Single(config, pduId)` — validates `config` and
    pulls field-size bytes out before assigning.
  - `PduTransportMultiLayer.Create(config, slots)` — validates non-empty
    `slots`, range-checks every payload size against the configured
    `Length` field, performs a defensive copy of the slot array.
  - `SignalMessageLayer.FromRawBytes(bytes)` — sentinel factory for the
    legacy raw-payload mode.

> **Rule**: factories must be named after their semantic intent
> (`Single`, `Multi`, `Create`, `FromRawBytes`, …), not after the layer
> name they live on.  `Default` and `Empty` are reserved.

### 6.3 Fields are `readonly` and Private

Layers are ref-shareable across multiple frames in a sequence and across
threads (when stateless).  Field mutability would break determinism and
introduce hidden race conditions.

**Memory ownership:** `ReadOnlyMemory<byte>` constructor parameters are
**referenced, not copied**; every layer follows this rule (zero-copy at
construction).  The caller's memory must stay unchanged until the last frame
built from the layer has been emitted.  Say so in the parameter's XML doc.
Fixed-size inline data passed as `ReadOnlySpan<byte>` (CAN, CAN FD, LIN) is
copied into the struct because a span cannot be stored.

### 6.4 Aggressive Inlining

Annotate every hot-path member with
`[MethodImpl(MethodImplOptions.AggressiveInlining)]`:
constructors, `HeaderSize`/`ProtocolType` getters, `WriteHeader`,
`PatchNextProtocol`, `ApplyPostFix`, `CanFragment`,
`PatchFragmentHeader`, `LinkMtu`.

The cons-list walks specialise per concrete type at JIT time; without
inlining the JIT cannot fold the per-cell capability dispatch into
constants.

---

## 7. Header Struct Pattern

Every fixed-size header sits in a `[BinaryWritable]` struct under
`Headers/`:

```csharp
[BinaryWritable]
internal readonly partial struct UdpHeader
{
    /// <summary>Size of the UDP header in bytes.</summary>
    internal const int Size = 8;

    /// <summary>Source port number.</summary>
    internal U16BE SrcPort { get; init; }

    /// <summary>Destination port number.</summary>
    internal U16BE DstPort { get; init; }

    /// <summary>Length of UDP header + payload. Set to 0 for fixup.</summary>
    internal U16BE Length { get; init; }

    /// <summary>Checksum (0 = no checksum).</summary>
    internal U16BE Checksum { get; init; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static UdpHeader Create(ushort srcPort, ushort dstPort) =>
        new() { SrcPort = srcPort, DstPort = dstPort, Length = (ushort)0, Checksum = (ushort)0 };
}
```

Rules:

1. `internal readonly partial struct` — `partial` is required by
   `[BinaryWritable]`.
2. Always include a `Size` constant (or `MinHeaderSize` for variable-size
   headers).
3. Use ZeroAlloc big-endian wrappers — `U16BE`, `U32BE`, `U64BE`, `I16BE`,
   etc. — for network-order fields.
4. Use `[BinaryField(BitCount = N)]` for sub-byte fields (IP version,
   IHL, DSCP, ECN, …).
5. Length / checksum / fragment-offset fields **must default to 0** in
   the static `Create` factory — they are always patched in post-fix.
6. The header struct never knows about layers; layers know about
   headers.

Layers serialise via:

```csharp
UdpHeader hdr = UdpHeader.Create(_SrcPort, _DstPort);
_ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
```

The unused-result discard is intentional: `dst` is statically sized to
`HeaderSize`, so failure cannot occur.

---

## 8. The `Auto<T>` Pattern

Header fields with two sources — caller-pinned vs auto-computed — use
`Auto<T>`:

```csharp
public UdpLayer(ushort srcPort, ushort dstPort,
                  Auto<ushort> checksum = default,
                  bool computeChecksum = true)
{
    if (!computeChecksum && !checksum.TryGetExplicit(out _))
    {
        _ChecksumIsExplicit = true;
        _ExplicitChecksum = 0;
    }
    else
    {
        _ChecksumIsExplicit = checksum.TryGetExplicit(out ushort v);
        _ExplicitChecksum = v;
    }
}
```

Rules:

| Source | Meaning |
|--------|---------|
| `Auto<T>.Compute` (= `default`) | Layer computes the value (length, checksum, IP-ID, EtherType …). |
| `Auto<T>.Explicit(value)` | Caller pins the value verbatim. |
| Implicit conversion `T → Auto<T>` | Equivalent to `Explicit(value)`. |

In `PatchNextProtocol` / `ApplyPostFix`, **honour the explicit pin**:
return without modifying the field when `_XxxIsExplicit` is true.  This
is what the tests rely on to inject corruption / conformance vectors.

> **Rule**: track the explicit-flag in a separate `bool` field.  Do
> *not* overload "value 0 means auto" — there are legitimate explicit
> zeros (UDP no-checksum on IPv4, IPv6 HopByHop NextHeader = 0,
> Ethernet EtherType pinned to 0 for raw frames).

---

## 9. Post-Fix Phases

After `WriteHeaders` finishes, the cons-list is walked once per
`FixPhase` so layers can patch length, checksum, pseudo-header and
trailer bytes:

| Phase | Walk direction | Use |
|-------|---------------|-----|
| `Length` | outer → inner | Patch length fields (IPv4 TotalLength, IPv6 PayloadLength, UDP Length, SOME/IP Length, PDU-Transport Length, …). |
| `PublishPseudoHeader` | outer → inner | Network layers copy src/dst into `PostFixContext`, set `PseudoIpLength`, `PseudoIsIPv6`, `PseudoProtocol`, `TransportOffset`, `TransportEnd`. |
| `InnerChecksum` | outer → inner | Transport-layer checksums (UDP, TCP, ICMPv6) read the published pseudo-header.  The separate `OuterChecksum` phase that follows lets header-only checksums see the final transport bytes. |
| `OuterChecksum` | outer → inner | Header-only checksums (IPv4 header checksum). |
| `Trailer` | outer → inner | FCS, padding, MIC. Runs last. |

### 9.1 Implementation Pattern

`ApplyPostFix` is a single `switch` over `FixPhase`.  Layers no-op
phases they do not participate in:

```csharp
public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength,
                         scoped ref PostFixContext ctx)
{
    switch (phase)
    {
        case FixPhase.Length:
            // A 16-bit length field cannot represent every frame: report, never wrap.
            if (myLength > ushort.MaxValue)
            {
                ctx.Status = BuildStatus.InvalidLayerState;
                break;
            }
            BinaryPrimitives.WriteUInt16BigEndian(
                frame.Slice(myOffset + LengthOffset, 2), (ushort)myLength);
            break;

        case FixPhase.PublishPseudoHeader:
            PublishPseudoHeader(frame, myOffset, myLength, ref ctx);
            break;

        // ... other phases ...

        default:
            break;   // Phases this layer does not participate in.
    }
}
```

For layers that participate in **at most one** phase, prefer an early
return:

```csharp
public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength,
                         scoped ref PostFixContext ctx)
{
    if (phase != FixPhase.Length)
    {
        return;
    }
    // ... patch Length ...
}
```

### 9.2 Critical Rules

1. **Never throw from `ApplyPostFix`.**  Use defensive checks at
   construction time instead.  Throwing aborts the iterator and the
   caller's fragment loop is left in an inconsistent state.  A violation
   that only the frame can reveal (a length field that cannot represent the
   frame, UDP over IPv6 with a pinned zero checksum) sets
   `ctx.Status = BuildStatus.InvalidLayerState`; the engine checks the status
   after the `Length` and `InnerChecksum` walks and aborts the build.
2. **`myOffset` is the absolute offset of this layer's header inside
   `frame`.**  `myLength` is the number of bytes from `myOffset` to the
   end of the frame (this header + everything beneath it).
3. **Length fields cover *this header + payload*, with format-specific
   adjustments**.  UDP includes the UDP header.  SOME/IP excludes the
   first 8 bytes (per AUTOSAR).  PDU-Transport excludes the
   ID + Length fields.  Always double-check the spec.
4. **Checksum fields must be zeroed before computation**.  The
   `frame[myOffset + ChecksumOffset] = 0` pattern is mandatory because
   the previous fragment / iteration may have left a residual value.
5. **Use `BinaryPrimitives.WriteUInt*BigEndian`** for direct frame
   writes inside post-fix — header serialisation already happened in
   `WriteHeader`, and we now patch raw bytes by offset.
6. **Never rely on a pre-zeroed destination.**  `WriteHeader` writes into the
   caller's buffer or into pooled scratch that holds residue from the previous
   frame; clear every padding byte explicitly (see `IPv6HopByHopLayer`,
   `LinLayer`, `SocketCanXlLayer`).

### 9.3 Pseudo-Header Publication

When a network layer publishes its pseudo-header:

```csharp
private static void PublishPseudoHeader(scoped Span<byte> frame, int myOffset, int myLength,
                                        scoped ref PostFixContext ctx)
{
    frame.Slice(myOffset + SrcAddrOffset, 4).CopyTo(ctx.PseudoSrcIp);
    frame.Slice(myOffset + DstAddrOffset, 4).CopyTo(ctx.PseudoDstIp);
    ctx.PseudoIpLength  = 4;
    ctx.PseudoIsIPv6    = false;
    ctx.PseudoProtocol  = frame[myOffset + ProtocolFieldOffset];   // already patched in write walk
    ctx.TransportOffset = myOffset + IPv4Header.Size;
    ctx.TransportEnd    = myOffset + myLength;
}
```

IPv6 extension headers (`IIPv6ExtensionLayer`) **override** the
pseudo-protocol and advance the transport offset:

```csharp
ctx.PseudoProtocol  = frame[myOffset + NextHeaderOffset];
ctx.TransportOffset = myOffset + IPv6FragmentExtensionHeader.Size;
```

so the transport layer sees the upper-layer protocol number and the
header-after-extensions offset (RFC 8200 §8.1).

---

## 10. Composition & Stacking Rules

### 10.1 The Six `Then(...)` Overloads

`Composition/StackThenExtensions.cs` declares **six** extension methods,
each in its own static class.  C# rejects two methods in the same type
sharing a parameter signature even when their constraints differ
(CS0111), so overloads must be split.  Two structural dimensions
combine:

| Dimension | Variants |
|-----------|----------|
| Stack-shape transition | `StatelessStack + stateless ⇒ StatelessStack` (A) · `StatelessStack + stateful ⇒ Stack` (B) · `Stack + any ⇒ Stack` (C) |
| Pseudo-header pairing | "Loose" (`TNew : IPseudoHeaderIndependent`) · "Strict" (`TNew : IRequiresPseudoHeader`, `TOld : IProvidesPseudoHeader`) |

That's `3 × 2 = 6` overloads.  Every call resolves to exactly one of
them; mismatches are compile errors with informative messages
(e.g. *"UDP requires a pseudo-header but the outer Ethernet layer does
not provide one"*).

### 10.2 Outer Constraints

**Every** overload requires `TOld : IInteriorLayer`.  This is the only
structural gate that prevents stacking onto a terminal payload layer.
A `Then(...)` call onto e.g. `SomeIpLayer` (which is `IPayloadLayer` and
*not* `IInteriorLayer`) is rejected at compile time.

### 10.3 Trailer Attachment (Orthogonal)

`WithTrailer<TTrailer>(...)` returns a `TrailerStack<TStack, TTrailer>`
on which `CreateWithFixedValues()` can be called.  Trailers are
orthogonal to `Then(...)` composition:

```csharp
FrameStack
    .Start(new EthernetLayer(dst, src))
    .Then(new IPv4Layer(srcIp, dstIp))
    .Then(new UdpLayer(srcPort, dstPort))
    .WithTrailer(EthernetFcs.Crc32)
    .CreateWithFixedValues();
```

### 10.4 Position Class Rules

| Layer kind | Allowed Position(s) |
|------------|---------------------|
| Bus root (`SocketCanLayer`) | Root only — no `Then(...)` after it. |
| Link root (`EthernetLayer`) | Root, anything `IInteriorLayer` may follow. |
| IPv4 / IPv6 | Either root (raw-IP) or after Ethernet/VLAN. |
| Transport (UDP, TCP, ICMPv6) | After a pseudo-header-providing network layer. |
| Application terminal | After a transport (or after PDU-Transport / SocketCAN, depending on the layer's pseudo-header marker). |

When in doubt, follow the existing layers' marker sets — every
combination already in `Layers/**/` is known to compose correctly.

---

## 11. Stateful Layers

### 11.1 The `SessionState` Struct

All cross-frame state lives in **one** `public struct SessionState` owned
by the session's pooled internals and passed by `ref` through the write walk.
Each stateful slot is paired with a `Has*` flag:

```csharp
public struct SessionState
{
    public bool   HasIPv4AutoId;       public ushort IPv4NextId;
    public bool   HasTcpAutoSeq;       public uint   TcpNextSeq;       public uint TcpAck;
    public bool   HasIPv6AutoFragId;   public uint   IPv6NextFragId;
    public bool   HasTcpStream;        public uint   TcpStreamNextSeq; public uint TcpStreamAck; ...
    public int    CurrentPayloadLength;
    // ...
}
```

A stateful layer:

1. Implements `IStatefulLayer` (which extends `IProtocolLayer`).
2. Implements `InitializeState(ref SessionState)` — called once per
   `Session.Open`.  Sets the seed and the `Has*` flag.
3. Implements `WriteHeader(Span<byte>, ref SessionState)` — reads its
   slot, writes the header, advances the counter (with `unchecked`
   wrap-around).

```csharp
public void InitializeState(ref SessionState state)
{
    state.IPv4NextId  = _SeedIdentification;
    state.HasIPv4AutoId = true;
}

public void WriteHeader(scoped Span<byte> dst, ref SessionState state)
{
    ushort id = state.IPv4NextId;
    unchecked { state.IPv4NextId = (ushort)(id + 1); }
    // ... write header with `id` ...
}
```

### 11.2 Rules

1. A stateful layer **must not** allocate per-frame state on the layer
   instance itself.  The struct stays immutable; all per-frame state
   lives in `SessionState`.
2. Stateful layers compose only inside a `Session<>` (which
   instantiates a `StatefulFrameSequence<>`).  The compile-time
   `IStatelessStack` constraint on the stateless `Build(...)` overload
   rejects mixed stacks.
3. Two stateful layers from the *same family* (e.g.
   `TcpLayerWithAutoSequence` and `TcpStreamLayer`, or two
   `IPv4LayerWithAutoIpId` in a tunnel) must not coexist — they share one
   slot.  The compiler does not reject this; document slot ownership in the
   layer XML doc.
4. Counters wrap with `unchecked` arithmetic; all counters are unsigned
   so wrap-around is well-defined.
5. A packet whose build fails before its first frame reaches the caller does
   not advance any counter: the engine snapshots `SessionState` before the
   write walk and restores it on failure.

### 11.3 Deliberate Limit: Closed Slot Set

The slot set of `SessionState` is closed.  A new stateful layer needs a new
slot (and `Has*` flag) in this library, so **consumers of the package cannot
add their own stateful layers** in this version.  This is a recorded decision,
not an oversight: the target model is typed per-layer state (a state cons-list
that mirrors the layer cons-list, `IStatefulLayer<TState>`), to be implemented
through a separate plan before FrameBuilder enters the Rust port.  The engine is
already generic over the state provider (`ISequenceState`), so that change does
not touch the build state machine.  Appendix B is therefore a template for
layers added *inside this repository*.

---

## 12. Fragmentation & Segmentation

### 12.1 `IFragmentable` and `FragmentationKind`

Implementing layers participate in multi-frame iteration:

```csharp
public readonly struct IPv4Layer : ..., IFragmentable
{
    public bool CanFragment => _AllowFragmentation;         // !DontFragment
    public FragmentationKind FragmentationKind => FragmentationKind.NetworkLayer;
    public int FragmentAlignment => 8;                       // RFC 791 §3.2

    public void PatchFragmentHeader(scoped Span<byte> frame, int myOffset, int myLength,
                                    int fragmentPayloadOffset, bool moreFragments)
    {
        ushort fragField = (ushort)((fragmentPayloadOffset >> 3) & FragmentOffsetMask);
        if (moreFragments) { fragField |= MoreFragmentsMask; }
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + FlagsFragOffsetOffset, 2), fragField);
    }
}
```

`FragmentationKind` selects the iterator strategy:

| Kind | Strategy | Inner-checksum coverage |
|------|----------|-------------------------|
| `NetworkLayer` (default, alignment 8) | Build-once, slice-many.  `Length`, `PublishPseudoHeader`, `InnerChecksum` run **once** on the unfragmented scratch.  Per fragment: `Length` + `OuterChecksum` + `Trailer` re-runs on the smaller frame. | Once, on the unfragmented scratch; the transport header carrying it is present in fragment 0 only. |
| `ApplicationSegmentation` (alignment 16, e.g. SOME/IP-TP) | Per-segment full post-fix walk.  Every emitted segment is its own complete network-layer datagram with its own transport checksum.  Outer `IFragmentable` layers (e.g. IPv4 with DF cleared) are **not** patched — they keep their full headers. | Per segment. |

### 12.2 Implementation Rules

1. `CanFragment` must report the runtime decision (e.g. `!DontFragment`
   for IPv4).  Returning `true` always when the layer cannot actually
   fragment will land you in `BuildStatus.InvalidLayerState`.
2. `FragmentAlignment` must be a positive power of two.  The iterator
   validates this and reports `BuildStatus.InvalidLayerState` otherwise.
3. `PatchFragmentHeader` must rewrite every fragment-only field
   (offset, MF flag, and any DF bit that needs to be cleared).  Length
   and checksum are repatched by the regular post-fix walk that the
   iterator re-runs on the per-fragment frame.
4. Rewriting the *entire* combined Flags+FragmentOffset 16-bit word
   (instead of a partial patch) is the cleanest way to clear DF and
   write MF + offset in one go (see `IPv4Layer.PatchFragmentHeader`).
5. Network-layer fragmentables must be on the **innermost** position
   that needs slicing — the iterator picks the innermost fragmentable
   layer and slices its payload.  Outer fragmentables along the cons-
   list are intentionally not patched.

---

## 13. MTU Plumbing & Trailers

### 13.1 MTU

- `IProvidesMtu` advertises a link-layer MTU in bytes.  Implemented by
  `EthernetLayer` (default 1518 = 1500 MAC client + 14 header + 4 FCS)
  and the link/bus roots.
- `IProvidesMtu.LinkMtu` is read by `Stack<>.MaxFrameLength` /
  `StatelessStack<>.MaxFrameLength` which take `Math.Min` along the
  cons-list — the smallest MTU wins.
- The fragmenter computes per-fragment payload size as
  `MTU - cached header bytes - trailerSize`, rounded down to
  `FragmentAlignment`.

### 13.2 Trailers

- `ITrailerLayer.TrailerSize` is the fixed trailer byte count.  Counted
  toward total frame size and budgeted away from per-fragment payload.
- `ITrailerLayer.WriteTrailer(Span<byte> frame, int payloadEnd)` is
  invoked **after** every other post-fix on the last bytes of the
  buffer.  Compute checksums over `frame[..payloadEnd]` (everything
  before the trailer slot).
- `NoTrailer` is the empty default; the JIT erases the call when the
  parameter is `NoTrailer`.

---

## 14. Single-Source-of-Truth Configuration

When a layer's wire format is parameterised by configuration that must
agree across **three consumers** — the FrameBuilder layer, the parser
settings/JSON, and the tshark UAT profile — use a dedicated `XxxConfigFb`
type:

```csharp
/// <summary>
/// FrameBuilder-side single source of truth for an AUTOSAR PDU-Transport
/// configuration. The same definition feeds three consumers in a test:
/// the FrameBuilder layer (wire format), the parser settings/JSON
/// (decode rule) and the tshark UAT profile (reference dissector).
/// </summary>
public sealed class PduTransportConfigFb
{
    public byte IdFieldSize { get; }     // 1, 2 or 4
    public byte LengthFieldSize { get; } // 1, 2 or 4
    public ImmutableArray<PduEntry> Pdus { get; }

    public PduTransportConfigFb(byte idFieldSize, byte lengthFieldSize, ImmutableArray<PduEntry> pdus)
    {
        if (idFieldSize is not (1 or 2 or 4))
            throw new ArgumentOutOfRangeException(...);
        if (lengthFieldSize is not (1 or 2 or 4))
            throw new ArgumentOutOfRangeException(...);
        // store fields...
    }
}
```

### 14.1 Rules

1. **Suffix `Fb` (FrameBuilder)** when a parser-side type with the same
   semantic name exists — disambiguates the two and signals that the
   FrameBuilder does not depend on the parser project.
2. **Validate at config-construction time**.  `PduTransportLayer`'s
   `WriteBigEndian` switch trusts that `size ∈ {1, 2, 4}` because the
   config constructor rejected anything else.  Validating in the config
   centralises the check and ensures every consumer (layer, parser,
   UAT) gets the same guarantee.
3. **Make the type immutable**: read-only properties via
   `{ get; init; }` or constructor + `{ get; }`.
4. **Use `ImmutableArray<T>`** for collections that must not change
   after construction.  Default-initialised `ImmutableArray<T>` should
   be normalised to empty in the constructor:
   ```csharp
   Pdus = pdus.IsDefault ? [] : pdus;
   ```
5. **Provide secondary convenience constructors** for the common case
   (e.g. `PduTransportConfigFb(ImmutableArray<PduEntry>)` defaults to
   4-byte ID and Length).

### 14.2 Test-Bridge Integration

The test bridge consumes a single `XxxConfigFb` instance and writes:

- The FrameBuilder layer construction (`PduTransportLayer.Single(config, pduId)`).
- The parser settings (`pdu_transport.id_field_size`,
  `pdu_transport.length_field_size`, `pdu_transport.config_file`).
- The tshark UAT profile preferences.

Because all three sides share the same source object, a wire-format
drift between layer and parser is structurally impossible.

---

## 15. Layout / ValueSet Split (Parameterised Layers)

For layers whose wire format is described by a runtime *layout* (signal
positions, field widths, scaling) and whose payload values vary per
frame, separate the **what** from the **how-much**:

| File | Purpose | Mutability |
|------|---------|-----------|
| `XxxLayout.cs` | Static description: byte length, signal specs, mux groups, dispatch bindings.  One `Layout` instance per logical PDU. | Immutable.  Reusable across frames and threads. |
| `XxxValueSet.cs` | Per-frame values keyed by signal name.  Built once per emitted frame.  Holds physical or raw value dictionaries. | Mutable, single-use, not thread-safe. |
| `XxxLayer.cs` | The encoder.  Holds a `(Layout, ValueSet)` pair (or a raw-bytes fallback).  Validates the layer×value-set binding via `ReferenceEquals`. | Immutable struct. |

### 15.1 Construction Pattern

```csharp
SignalMessageLayout layout = new()
{
    PduId = 0x42, Name = "Engine", ByteLength = 8,
    Signals = ImmutableArray.Create(
        new SignalSpec { Name = "Rpm",  StartBit = 0,  BitLength = 16, Endian = SignalEndian.Big,
                         Factor = 0.25, Offset = 0, Unit = "rpm" }),
    DispatchBindings = ImmutableArray.Create(new DispatchBinding { Table = "udp.port", Key = 30490 }),
};

SignalMessageValueSet values = SignalMessageValueSet
    .For(layout)
    .Set("Rpm", 1500.0);

SignalMessageLayer layer = new(layout, values);
```

### 15.2 Rules

1. **Validate the binding at construction time**: the layer's
   constructor must call `ReferenceEquals(values.Layout, layout)` and
   throw `ArgumentException` on mismatch.  Catches the most common
   test-wiring mistake instead of silently emitting garbage on the wire.
2. **Layout is immutable, ValueSet is mutable but single-use**.
   Document this in the type's `<remarks>`.
3. **Provide a `For(layout)` factory** on the value set to enforce the
   binding at construction time.
4. **Provide a raw-bytes escape hatch** for legacy cases:
   `XxxLayer.FromRawBytes(bytes)`.  Mark it explicitly as legacy in the
   XML doc; production-style tests should always go through the
   structured layer constructor.
5. **Mux selectors are virtual signals**: the encoder reads the
   selector value (raw or physical) from the value set by the mux
   spec's `Name`, then renders only the matching `MuxGroupSpec`.  A
   missing selector value must throw — silent fallback to "group 0" is
   a footgun.
6. **Dispatch bindings (`DispatchBindings`) are pass-through**: the
   FrameBuilder itself does not consume them.  The test bridge writes
   them into the parser JSON and the tshark UAT profile.

---

## 16. Validation, Errors & Build Status

### 16.1 Construction-Time Errors

Use exceptions for **programmer errors** that surface at construction
time (no test data has been built yet, no caller depends on
throw-freedom):

| Scenario | Exception |
|----------|-----------|
| Argument is `null` | `ArgumentNullException.ThrowIfNull(arg)` |
| Argument out of range | `ArgumentOutOfRangeException.ThrowIfNegativeOrZero` / explicit throw |
| Empty required collection | `ArgumentException` |
| Value set bound to wrong layout | `ArgumentException` |

### 16.2 Runtime Build Errors

`MoveNext` is **throw-free**.  All expected runtime conditions surface
via `BuildStatus`:

| Status | Meaning |
|--------|---------|
| `Success` | A frame was written. |
| `BufferTooSmall` | Caller's buffer is shorter than the frame. |
| `FragmentationRequired` | Frame > MTU and either no `IFragmentable` is in the stack or `CanFragment` is `false`. |
| `InvalidLayerState` | A layer reported a state the frame cannot carry: a length field that cannot represent the frame (IPv4 TotalLength, IPv6 PayloadLength, UDP Length > 65535), UDP over IPv6 with a pinned zero checksum, or a non-power-of-two fragment alignment.  No frame is emitted. |
| `StackTooDeep` | Cons-list depth > `FrameLimits.MaxSupportedDepth` (32). |
| `SessionDisposed` | The `Session<>` that produced a `StatefulFrameSequence<>` was disposed before the sequence finished.  The state of a session that re-rented the pooled internals is untouched. |

A failure before the first frame of a packet reaches the caller leaves the
session counters unchanged (§11.2 rule 5).

### 16.3 Layer-Internal Validation in `WriteHeader`

`WriteHeader` may be called repeatedly (sequence iteration).  Avoid any
allocation, throw, or stateful side-effect.  Any validation that
cannot be deferred to construction time belongs in the factory; once
the struct is constructed, every method is allowed to assume valid
inputs.

The single exception today is `SignalMessageLayer.WriteHeader` which throws
`InvalidOperationException` when the active mux selector value matches
no configured group — a structural mismatch that cannot be caught at
construction time but should also never occur in well-formed test data.
Document any such throw-from-write site explicitly.

### 16.4 Constructor Limits

Trust-boundary limits enforced by the constructors; all are constants fixed by
the wire format, not configuration:

| Layer | Limit | Source |
|-------|-------|--------|
| `SocketCanLayer`, `SocketCanFdLayer` | `canId` ≤ 0x7FF (standard) / ≤ 0x1FFFFFFF (extended) | Linux SocketCAN |
| `LinLayer` | `frameId` ≤ 63; `checksumType` ∈ {1, 2} | ISO 17987, DLT_LIN |
| `FlexRayLayer` | `frameId` ≤ 2047; `cycleCount` ≤ 63; payload ≤ 254 bytes; `typeIndex` ≤ 127 | ISO 17458-2 |
| `IPv6HopByHopLayer` | option bytes ≤ 2046 (`MaxOptionBytes`) | RFC 8200 §4.3 |
| `IPv6RoutingLayer` | type-specific data ≤ 2040 bytes (`MaxTypeSpecificDataBytes`) | RFC 8200 §4.4 |
| `TcpOptionsBuilder` | option area ≤ 40 bytes | RFC 9293 §3.1 |
| `TcpConnection.Open` | `Mss` > 0 | — |

---

## 17. Performance & Allocation Rules

The FrameBuilder runs in tight test loops; allocation discipline is
strict.

### 17.1 Mandatory

- All layers are `readonly struct`.
- All public hot-path members are
  `[MethodImpl(MethodImplOptions.AggressiveInlining)]`.
- `WriteHeader` and `ApplyPostFix` do **not** allocate.
- Cross-cell capability tests go through the probes in `StackHelpers`
  (`TryGetLinkMtu`, `TryGetProtocolType`, `PatchNextProtocol`,
  `TryGetFragmentation`, `PatchFragmentHeader`, `WriteHeader`, …).  Each probe
  tests `value is IFoo` without a pattern variable (the JIT folds the test to a
  constant per concrete `THead`) and then calls `((IFoo)value).Member`
  directly (the JIT devirtualises the call and drops the box).  Never probe a
  layer through `object`, a `NoInlining` helper, or an interface reference held
  in a local or `out` parameter: that boxes the struct on every frame.
- `AllocationTests` proves 0 allocated bytes per build for the single-frame and
  the fragmenting path and per packet of an open session.  The test project disables tiered compilation so the
  measurement runs optimised code; Tier-0 code never removes boxes.

### 17.2 Strongly Encouraged

- Use `BinaryPrimitives.Write*BigEndian` for direct buffer writes.
- Use `ReadOnlySpan<byte>` / `Span<byte>` everywhere on the hot path.
- Use `[BinaryWritable]` for fixed-size headers — the source generator
  emits zero-allocation `TryWrite`.
- Use `params Slot[]` in factories that take many small structs and
  defensively copy in the factory (so callers cannot mutate the array
  out from under a sequence iteration — see
  `PduTransportMultiLayer.Create`).

### 17.3 Forbidden

- No reflection.
- No string formatting (`$""`, `string.Format`, `ToString`) on the
  parse / write hot path.  Error messages built once at construction
  time are fine.
- No `LINQ` (`.Select`, `.Where`, …) inside `WriteHeader` /
  `ApplyPostFix`.
- No `lock`, `Mutex`, `Monitor`, etc. inside layer methods.  Layers are
  either immutable (stateless) or driven by a single-threaded session.
- No `#if DEBUG`, `Debug.Assert`, conditional compilation that changes
  behaviour.  Release and Debug builds must produce byte-identical
  frames (top-level engineering rule).

---

## 18. Thread-Safety

| Type | Guarantee | Caller responsibility |
|------|-----------|----------------------|
| Stateless layer struct | Immutable; safe to share across threads. | None. |
| Stateful layer struct | Immutable; per-frame state lives in `SessionState`. | One `Session<>` per producer thread. |
| `SessionState` | Plain struct owned by the session's pooled internals; not thread-safe. | Drive a session from one thread at a time. |
| `FrameSequence<>` / `StatefulFrameSequence<>` | `ref struct`; single-use, single-thread. | Never share an iterator; declare it with `using`. |
| `TcpConnection<,>` | Not thread-safe and not reentrant on one thread. | One connection per thread; a sink must not write on another connection of the same thread while a producer write is in progress. |
| `SignalMessageLayout`, `PduTransportConfigFb`, `SignalMessageValueSet` (read-only consumption) | Immutable. | None. |
| `SignalMessageValueSet` (mutation) | Not thread-safe. | One value set per emitted frame. |

Document these guarantees in the type's XML `<remarks>` block; every
modernised layer follows the convention with an explicit *"Thread
safety: …"* paragraph.

---

## 19. Documentation Requirements

Every layer's XML doc must contain:

1. A `<summary>` describing the layer in one or two sentences and
   referencing the spec (RFC, AUTOSAR §, ISO, IEEE, …).
2. A `<remarks>` block listing capabilities (one bullet per
   `Ixxx` marker) and the post-fix phases the layer participates in
   (one bullet per phase with a short description).
3. A *Thread safety* paragraph.
4. For variable-format layers, a wire-format diagram in `<code>` /
   `<list>` form (see `PduTransportLayer` and `SignalMessageLayer`).

Header structs document their layout in a single-line `<summary>` plus
optional ASCII-art diagram.

Configuration objects document the three consumers (FrameBuilder,
parser, tshark UAT) and the validation rules.

---

## 20. Testing

### 20.1 Test Project

Tests live in `NetworkInspector.FrameBuilder.Tests`:

```
NetworkInspector.FrameBuilder.Tests/
  Application/DhcpLayerValidationTests.cs         ← Application-layer validation
  Application/DnsLayerValidationTests.cs
  Application/PduTransportMultiLayerTests.cs
  Application/SignalMessageLayerWriteRoundTripTests.cs
  Application/TlsRecordLayerValidationTests.cs
  Capabilities/CapabilitySanityTests.cs           ← Marker / overload-set tests
  Capabilities/RawIPv6SmokeTests.cs               ← Position-class tests
  Core/ChecksumUtilsTests.cs
  Frames/FrameTests.cs                            ← Frame bytes and build status
  Layers/SomeIpLayerTests.cs                      ← One file per layer family
  Layers/TcpLayerWithOptionsTests.cs
  Layers/IPv6ExtensionLayerTests.cs
  Layers/LinLayerTests.cs
  Layers/LinuxSllLayerTests.cs / Layers/LinuxSll2LayerTests.cs
  Layers/LlcLayerTests.cs
  Layers/IcmpV4DestUnreachLayerTests.cs / Layers/IcmpV4RedirectLayerTests.cs
  Layers/IcmpV4TimeExceededLayerTests.cs
  Negative/NegativeCompileTests.cs                ← Compile-time rejections
  Negative/NegativeCompileHarness.cs
  Negative/Snippets/*_must_not_compile.cs.txt
  Stacks/ChecksumTests.cs
  Stacks/FrameDepthLimitTests.cs
  Stacks/FrameStackAutoInferenceTests.cs
  Stacks/InterceptorTests.cs
  AllocationTests.cs                              ← Zero-allocation hot path
  BusAndExtensionLayerSmokeTests.cs
  FragmentationSmokeTests.cs / StatefulFragmentationTests.cs
  FrameBuilderReviewExtensionTests.cs
  NewLayerSmokeTests.cs / NewFrameStackSmokeTests.cs
  SessionPoolStressTests.cs
  StatefulSessionSmokeTests.cs
  StreamCapabilitySmokeTests.cs
  TcpConnectionTests.cs / TcpConnectionTsharkTests.cs
  TrailerAndInterceptorSmokeTests.cs
  GlobalUsings.cs
```

### 20.2 Mandatory Test Categories

| Category | Description |
|----------|-------------|
| Header size | `HeaderSize` is constant per instance and matches the spec. |
| Field layout | Each field appears at the documented offset, big-endian where required. |
| `Auto<T>` | Compute path, explicit pin path, and explicit-zero edge case all produce the expected bytes. |
| Length fixup | `FixPhase.Length` patches the layer's length field correctly across header-only, payload, and zero-payload cases. |
| Checksum | When the layer carries a checksum, validate against a precomputed reference (or against the parser round-trip). |
| Pseudo-header | Validate the address bytes, length and protocol number written into `PostFixContext` (use a delegating interceptor). |
| Composition | Each marker combination compiles only the legal `Then(...)` overloads; document expected compile errors via `NegativeCompileTests`. |
| Fragmentation | For `IFragmentable` layers: full unfragmented build, multi-fragment build with min-size MTU, edge alignment cases. |
| Stateful state | Counter wrap-around, multiple-frame increments, restart between sessions, counters unchanged after a failed packet. |
| Constructor limits | Every limit in §16.4: the boundary value builds, the next value throws `ArgumentOutOfRangeException`. |
| Build status | Each `BuildStatus` the layer can cause is asserted on both the stateless and the stateful engine. |
| Allocation | New hot-path code keeps `AllocationTests` at 0 bytes. |
| Round-trip parity | A frame built with the layer and the corresponding parser-side definition produces matching bytes; tshark UAT (where available) decodes the frame the same way. |

### 20.3 Test Helpers

- `FB` is the conventional alias used in tests (see
  `NewFrameStackSmokeTests`).
- `BuildFrame<TApp>(in app, payload)` patterns isolate layer-under-test
  by stubbing the surrounding stack with deterministic outer layers.
- Frame-by-frame byte comparison should use
  `BinaryPrimitives.Read*BigEndian` or `Convert.ToHexString` for human-
  readable failures.

### 20.4 Coverage Goal

100% line and branch coverage for layer code, including the no-op
`ApplyPostFix` paths.  Use `NegativeCompileTests` for compile-time
guarantees that cannot be exercised at runtime.

---

## 21. Dos & Don'ts Cheat Sheet

### Do

- ✅ Use `public readonly struct` for every layer.
- ✅ Declare every `FrameSequence<>` / `StatefulFrameSequence<>` with `using`.
- ✅ Use `[MethodImpl(MethodImplOptions.AggressiveInlining)]` on every
  hot-path member.
- ✅ Pick the correct capability marker set the first time
  ([Appendix C](#appendix-c-capability-decision-matrix)).
- ✅ Use `Auto<T>` for caller-pinnable fields and respect
  `_XxxIsExplicit` in `PatchNextProtocol` / `ApplyPostFix`.
- ✅ Validate all invariants in the constructor / factory; once the
  struct is built, downstream methods assume valid inputs.
- ✅ Use `[BinaryWritable]` headers for fixed-size frame structures.
- ✅ Use a single `XxxConfigFb` for layers paired with a parser config.
- ✅ Split static layout from per-frame values
  ([§15](#15-layout--valueset-split-parameterised-layers)).
- ✅ Document every capability and post-fix phase in the XML
  `<remarks>`.
- ✅ Document thread-safety explicitly.
- ✅ Use `ImmutableArray<T>` / `ImmutableDictionary<TK,TV>` for
  collection fields on layouts and configs; defensively copy `params
  T[]` in factories.

### Don't

- ❌ Don't make a layer both `IInteriorLayer` and `IPayloadLayer`.
- ❌ Don't make a layer both `IRequiresPseudoHeader` and
  `IPseudoHeaderIndependent`.
- ❌ Don't make a layer both `IStatelessLayer` and `IStatefulLayer`.
- ❌ Don't allocate inside `WriteHeader` / `ApplyPostFix`.
- ❌ Don't throw inside `ApplyPostFix` (a single documented exception
  exists in `SignalMessageLayer.WriteHeader` for unmatched mux groups —
  document any future exception explicitly).
- ❌ Don't use "value 0 means auto" — track a separate explicit bool.
- ❌ Don't store mutable per-frame state on the layer struct.
- ❌ Don't use `var`; prefer explicit types or collection expressions
  (`[]`).
- ❌ Don't add a TODO / stub / silent fallback.  Either implement the
  branch fully or `throw` with a precise message.
- ❌ Don't skip the `params T[]` defensive copy: the iterator may emit
  the same stack repeatedly, and a caller-mutated array would drift
  silently.
- ❌ Don't rely on registration order in the cons-list to express
  semantic constraints — use the marker set instead.
- ❌ Don't leak parser-project types into the FrameBuilder (use the
  `Fb` suffix convention).

---

## 22. Checklist for New Layers

### Class Structure

- [ ] `public readonly struct XxxLayer : IStatelessLayer | IStatefulLayer, …`
- [ ] Layer file under `Layers/{Bus,Link,Network,Transport,Application}/`
- [ ] One layer per file (helper types may live in the same file when small)
- [ ] All fields `private readonly`, named `_PascalCase`
- [ ] All hot-path members `[MethodImpl(MethodImplOptions.AggressiveInlining)]`
- [ ] Capability marker set chosen per [Appendix C](#appendix-c-capability-decision-matrix)
- [ ] No double-marker contradictions (Interior×Payload, Stateless×Stateful, Pseudo-header pairing)

### Header Struct

- [ ] `[BinaryWritable] internal readonly partial struct XxxHeader` under `Headers/`
- [ ] `Size` constant in bytes
- [ ] Big-endian fields use `U16BE` / `U32BE` / `U64BE`
- [ ] Bit-fields use `[BinaryField(BitCount = N)]`
- [ ] Length / checksum / fragment-offset fields default to 0 in `Create`
- [ ] XML doc with layout description (single line or ASCII diagram)

### Construction

- [ ] Public constructor that range-checks every argument, OR
- [ ] Private constructor + named factory (`Single`, `Multi`, `Create`,
      `FromXxx`) when validation is non-trivial
- [ ] All invariants validated in the constructor / factory (no validation in
      `WriteHeader` / `ApplyPostFix`); every limit listed in §16.4
- [ ] `ReadOnlyMemory<byte>` parameters documented as referenced, not copied (§6.3)
- [ ] 16-bit length fields report `BuildStatus.InvalidLayerState` instead of wrapping (§9.1)
- [ ] `Auto<T>` used for caller-pinnable fields with a separate
      `_XxxIsExplicit` flag

### Behaviour

- [ ] `HeaderSize` is constant per instance
- [ ] `WriteHeader` uses `[BinaryWritable]`'s `TryWrite` (or direct
      `BinaryPrimitives.Write*` for layers without a header struct)
- [ ] Length / checksum bytes start at 0 in `WriteHeader` (patched in
      post-fix)
- [ ] `ApplyPostFix` is a `switch` on `FixPhase` with a `default: break`
      branch (or an early return for single-phase layers)
- [ ] `PatchNextProtocol` honours the explicit-pin flag
- [ ] Pseudo-header publication populates **all** `PostFixContext`
      fields the spec requires (`PseudoIsIPv6`, `PseudoIpLength`,
      `PseudoProtocol`, `TransportOffset`, `TransportEnd`)
- [ ] Fragmentable layers: `CanFragment` reflects the runtime decision,
      `FragmentAlignment` is a positive power of two,
      `PatchFragmentHeader` rewrites all per-fragment dynamic fields

### Configuration / Layout (when applicable)

- [ ] `XxxConfigFb` (with `Fb` suffix) for parser-paired configs
- [ ] All validation in the config constructor
- [ ] `XxxLayout` is immutable (`{ get; init; }`)
- [ ] `XxxValueSet.For(layout)` factory; binding validated by
      `ReferenceEquals` in the layer constructor
- [ ] Empty `ImmutableArray<T>` normalisation in the constructor

### Documentation

- [ ] `<summary>` references the spec (RFC, AUTOSAR §, ISO, …)
- [ ] `<remarks>` lists capabilities (one bullet per marker) and
      post-fix phases (one bullet per phase)
- [ ] *Thread safety* paragraph
- [ ] Wire-format diagram for variable-layout layers

### Tests

- [ ] Header size
- [ ] Field layout (per offset)
- [ ] `Auto<T>` paths
- [ ] Length fixup
- [ ] Checksum (when applicable)
- [ ] Pseudo-header publication (when applicable)
- [ ] Fragmentation (when applicable, including stateful variants)
- [ ] Round-trip parity with parser and tshark (where applicable)
- [ ] Composition compile-time guarantees in `NegativeCompileTests`

---

## Appendix A: Minimal Stateless Layer Template

```csharp
// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Example transport-class layer (8-byte fixed header) for the new
/// <see cref="FrameStack"/> API.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IStatelessLayer"/> — no per-frame mutable state.</item>
///   <item><see cref="IInteriorLayer"/> — accepts an inner layer.</item>
///   <item><see cref="IRequiresPseudoHeader"/> — needs the IP layer's pseudo-header.</item>
///   <item><see cref="IProvidesProtocolType"/> — IP Protocol value 0x42.</item>
/// </list>
/// <para>Post-fix phases:</para>
/// <list type="bullet">
///   <item><see cref="FixPhase.Length"/> — patches the Length field.</item>
///   <item><see cref="FixPhase.InnerChecksum"/> — computes the checksum
///   over the IP pseudo-header plus this segment.</item>
/// </list>
/// <para>Thread safety: immutable struct, safe for concurrent use.</para>
/// </remarks>
public readonly struct XxxLayer :
    IStatelessLayer, IInteriorLayer, IRequiresPseudoHeader,
    IProvidesProtocolType, IProvidesNextProtocolValue<IpNextProtocolKind>
{
    private const int LengthOffset = 4;
    private const int ChecksumOffset = 6;

    private readonly ushort _SrcPort;
    private readonly ushort _DstPort;
    private readonly ushort _ExplicitChecksum;
    private readonly bool _ChecksumIsExplicit;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public XxxLayer(ushort srcPort, ushort dstPort, Auto<ushort> checksum = default)
    {
        _SrcPort = srcPort;
        _DstPort = dstPort;
        _ChecksumIsExplicit = checksum.TryGetExplicit(out ushort v);
        _ExplicitChecksum = v;
    }

    /// <inheritdoc />
    public int HeaderSize { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => XxxHeader.Size; }

    /// <inheritdoc />
    public ushort ProtocolType { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => 0x42; }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        XxxHeader hdr = XxxHeader.Create(_SrcPort, _DstPort);
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength,
                             scoped ref PostFixContext ctx)
    {
        switch (phase)
        {
            case FixPhase.Length:
                // 16-bit Length: report a frame it cannot represent instead of wrapping.
                if (myLength > ushort.MaxValue)
                {
                    ctx.Status = BuildStatus.InvalidLayerState;
                    break;
                }
                BinaryPrimitives.WriteUInt16BigEndian(
                    frame.Slice(myOffset + LengthOffset, 2), (ushort)myLength);
                break;

            case FixPhase.InnerChecksum:
                if (_ChecksumIsExplicit)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(
                        frame.Slice(myOffset + ChecksumOffset, 2), _ExplicitChecksum);
                }
                else
                {
                    ComputeChecksum(frame, myOffset, myLength, in ctx);
                }
                break;

            default:
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeChecksum(Span<byte> frame, int myOffset, int myLength,
                                        in PostFixContext ctx)
    {
        frame[myOffset + ChecksumOffset]     = 0;
        frame[myOffset + ChecksumOffset + 1] = 0;

        ReadOnlySpan<byte> segment = frame.Slice(myOffset, myLength);
        ReadOnlySpan<byte> srcIp   = ctx.PseudoSrcIp[..ctx.PseudoIpLength];
        ReadOnlySpan<byte> dstIp   = ctx.PseudoDstIp[..ctx.PseudoIpLength];

        ushort checksum = ctx.PseudoIsIPv6
            ? ChecksumUtils.PseudoHeaderIPv6(srcIp, dstIp, 0x42, segment)
            : ChecksumUtils.PseudoHeaderIPv4(srcIp, dstIp, 0x42, segment);

        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + ChecksumOffset, 2), checksum);
    }
}
```

---

## Appendix B: Minimal Stateful Layer Template

> **In-repository template.**  `SessionState` has a closed slot set (§11.3): the
> `XxxNextCounter` / `HasXxxCounter` slot below must be added to
> `Stateful/SessionState.cs` in this library.  Consumers of the NuGet package
> cannot add stateful layers in this version.

```csharp
// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Example stateful layer that auto-increments a sequence counter per
/// emitted frame.  Only usable inside a <see cref="Session{TStack,TTrailer,TInterceptor}"/>.
/// </summary>
/// <remarks>
/// <para>State slot: <see cref="SessionState.XxxNextCounter"/> — initialised
/// to the caller-supplied seed in <see cref="InitializeState"/>;
/// incremented by 1 per frame with natural <see cref="ushort"/>
/// wrap-around.</para>
/// <para>Thread safety: the struct is immutable; the per-frame counter
/// lives in <see cref="SessionState"/>.</para>
/// </remarks>
public readonly struct XxxStatefulLayer :
    IStatefulLayer, IInteriorLayer, IPseudoHeaderIndependent, IProvidesProtocolType
{
    private readonly ushort _SeedCounter;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public XxxStatefulLayer(ushort initialCounter = 0) => _SeedCounter = initialCounter;

    /// <inheritdoc />
    public int HeaderSize { get => XxxHeader.Size; }

    /// <inheritdoc />
    public ushort ProtocolType { get => 0x42; }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InitializeState(ref SessionState state)
    {
        state.XxxNextCounter = _SeedCounter;
        state.HasXxxCounter  = true;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst, ref SessionState state)
    {
        ushort counter = state.XxxNextCounter;
        unchecked { state.XxxNextCounter = (ushort)(counter + 1); }

        XxxHeader hdr = XxxHeader.Create(counter);
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength,
                             scoped ref PostFixContext ctx)
    {
    }
}
```

---

## Appendix C: Capability Decision Matrix

| If the layer is … | Mandatory markers | Optional markers |
|------------------|-------------------|------------------|
| Pure root, no children (CAN, ICMPv4-echo) | `IRootLayer` | `IProvidesMtu` (when carrying a link MTU) |
| Link/bus root with children (Ethernet, raw-IP) | `IRootLayer`, `IInteriorLayer`, `IPseudoHeaderIndependent`, `IConsumesNextProtocolValue<EtherTypeKind>` | `IProvidesMtu` |
| Network layer (IPv4, IPv6) | `IInteriorLayer`, `IPseudoHeaderIndependent`, `IProvidesProtocolType`, `IProvidesNextProtocolValue<EtherTypeKind>`, `IConsumesNextProtocolValue<IpNextProtocolKind>`, `IProvidesPseudoHeader` | `IRootLayer` (raw-IP), `IFragmentable` (IPv4 / IPv6-Frag), `IIPv6ExtensionLayer` (IPv6 ext) |
| Checksum-bearing transport (UDP, TCP, ICMPv6) | `IInteriorLayer` (TCP/UDP) **or** *neither* (terminal echo), `IRequiresPseudoHeader`, `IProvidesProtocolType`, `IProvidesNextProtocolValue<IpNextProtocolKind>` | `IStreamCarrier` (TCP) |
| Application interior (PDU-Transport single) | `IInteriorLayer`, `IPseudoHeaderIndependent`, `IStatelessLayer` | — |
| Application terminal carrier (SOME/IP, SignalPDU, PDU-Transport multi) | `IPayloadLayer`, `IPseudoHeaderIndependent`, `IStatelessLayer` | `IFragmentable` (SOME/IP-TP) with `FragmentationKind.ApplicationSegmentation` |
| Pure terminal with outer protocol-type publication (ARP) | `IPseudoHeaderIndependent`, `IProvidesProtocolType`, `IProvidesNextProtocolValue<EtherTypeKind>` | — |
| Stateful auto-counter variant of an existing layer | Same as the stateless original, but `IStatelessLayer` → `IStatefulLayer` | — |
| IPv6 extension header | `IInteriorLayer`, `IPseudoHeaderIndependent`, `IIPv6ExtensionLayer`, `IProvidesProtocolType`, `IProvidesNextProtocolValue<IpNextProtocolKind>`, `IConsumesNextProtocolValue<IpNextProtocolKind>`, `IProvidesPseudoHeader` | `IFragmentable` (Fragment ext) |
| Trailer (FCS, padding) | `ITrailerLayer` | — |

> When in doubt, find a closely related existing layer and copy its
> marker set; the modernised layers under `Layers/Application/` are the
> canonical references for application-class layers, the
> `Layers/Network/` and `Layers/Transport/` folders for IP-family
> layers.


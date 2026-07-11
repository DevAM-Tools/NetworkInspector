# Review: Protocols & FrameBuilder (CAN, FlexRay, Ethernet, VLAN, UDP, TCP, IPv6, IPv4, Frame)

## Findings Overview

| ID | Bucket | Title | Summary |
|----|--------|-------|---------|
| E1 | E | FlexRay silent truncation (Protocols) | `FlexRayProtocol.Parse` clamps declared payload with `Math.Min` instead of returning `InsufficientData` (`FlexRayProtocol.cs:310`). |
| E2 | E | FlexRay index false positive (Protocols) | Symbol/event frames record `flexray` index group before type-index branch (`FlexRayProtocol.cs:229-230`). |
| E3 | E | UDP thread-safety contract wrong (Protocols) | XML claims concurrent `Parse` on same instance; `_StreamTracker` mutates without synchronization (`UdpProtocol.cs:20-24`, `Udp/UdpStreamTracker.cs:54-108`). |
| E4 | E | UDP sub-minimum length accepted (Protocols) | `length < 8` yields zero payload without error (`UdpProtocol.cs:205-209`). |
| E5 | E | L3/L4 silent truncation (Protocols) | UDP/TCP/IPv4 return `Math.Min(declared, buffer)` without `InsufficientData` when capture is short. |
| E6 | E | TCP checksum length bound (Protocols) | Checksum validation uses `tcpData.Length`, not IP-bounded segment length (`TcpProtocol.cs:660`). |
| E7 | E | Frame FlexRay link-type label (Protocols) | `GetLinkTypeName` missing `LinkType.Flexray` → `"Unknown"` in summaries (`FrameProtocol.cs:182-216`). |
| E8 | E | FlexRay payload field overflow (FrameBuilder) | Payload >254 B wraps 7-bit word count to 0 (`FlexRayLayer.cs:128-131`). |
| E9 | E | IPv6Layer `mtu` ignored for fragmentation (FrameBuilder) | `mtu` sets `MaxPayloadSize` but stack MTU comes only from `IProvidesMtu` on Ethernet (`IPv6Layer.cs:45-74`, `StatelessStack.cs:46-51`). |
| E10 | E | Checksum path throws in throw-free build (FrameBuilder) | `PseudoHeaderIPv4/IPv6` throw; `MoveNext` documents throw-free (`ChecksumUtils.cs:91-142`, `UdpLayer.cs:163-165`). |
| E11 | E | Unchecked `ushort` length writes (FrameBuilder) | IPv4/IPv6/UDP length fields cast without range guard (`IPv4Layer.cs:152`, `IPv6Layer.cs:139-141`, `UdpLayer.cs:114`). |
| E12 | E | VLAN TCI components unvalidated (FrameBuilder) | `pcp > 7` corrupts TCI; `vlanId > 4095` silently truncated (`VlanLayer.cs:45-65`, `VlanTag.cs:44-45`). |
| P1 | P | UDP fully eager (Protocols) | All UDP fields appended in `Parse`; contradicts PROTOCOL_GUIDE lazy hot-path model (`UdpProtocol.cs:299-326`). |
| P2 | P | TCP flags formatted twice (Protocols) | `TcpFlagsFormatter.Format` in `Parse` and `_PopulateTcp` (`TcpProtocol.cs:754-761`, `613`). |
| P3 | P | VLAN header parsed twice (Protocols) | Eager `TryParse` in `Parse` + lazy populator re-parse (`VlanProtocol.cs:146-149`, `96-105`). |
| P4 | P | IPv6 extension chain walked twice (Protocols) | Eager walk in `Parse` duplicates lazy populator work (`IPv6Protocol.cs:490-568`). |
| P5 | P | UDP container stores full buffer (Protocols) | `FieldValue.NewBytes(data)` not bounded to `udp.length` (`UdpProtocol.cs:295-296`). |
| P6 | P | FlexRayLayer heap payload (FrameBuilder) | `ReadOnlyMemory<byte> _Payload` allocates per layer (`FlexRayLayer.cs:47,95`). |
| P7 | P | Capability dispatch boxes structs (FrameBuilder) | `StackHelpers.TryCast` boxes every layer probe (`StackHelpers.cs:38-45`). |
| P8 | P | CAN-FD ctor stack work (FrameBuilder) | 64-byte `stackalloc` + 8× ulong pack per ctor (`SocketCanFdLayer.cs:91-100`). |
| R1 | R | FlexRay parse duplication (Protocols) | Inline decode duplicates `FlexRayLinkTypeFrame.TryParseDataFrame` — drift risk. |
| R2 | R | Eager/lazy policy drift (Protocols) | Ethernet/UDP implementation diverges from PROTOCOL_GUIDE §13b.1. |
| R3 | R | CAN-FD EFF constant drift (FrameBuilder) | `0x80000000u` vs `SocketCanHeader.EffFlag` (`SocketCanFdLayer.cs:66`). |
| C1 | C | `TryWrite` result ignored (FrameBuilder) | All layers discard `TryWrite` success (e.g. `EthernetLayer.cs:103`). |
| C2 | C | Stray `#endregion` in `VlanHeader` (Protocols) | Orphan region marker (`VlanProtocol.cs:244`). |

---

## Summary

| Item | Result |
|------|--------|
| Build (`dotnet build -c Release`) | ✅ 0 warnings, 0 errors |
| Protocols.Tests | ⚠️ 515/614 pass (99 tshark failures — `tshark` unavailable in CI env) |
| FrameBuilder.Tests | ⚠️ 321/327 pass (6 tshark failures — env) |
| ExitPointGaps | ❌ `exitGapCount = 1842` (repo-wide gate) |
| Loaded skills | `tech-csharp.md` |
| Focus | Performance + correctness |

**Dominant themes:** silent truncation vs explicit parse errors; thread-safety/documentation drift on UDP; FlexRay correctness on both parse and build sides; MTU/checksum contract gaps in FrameBuilder; allocation pressure on UDP eager path.

---

## Scope

### In scope — `NetworkInspector.Protocols`

| File | Lines | Role |
|------|------:|------|
| `CanProtocol.cs` | 582 | SocketCAN classic/FD/XL parser |
| `FlexRayProtocol.cs` | 396 | LINKTYPE_FLEXRAY parser |
| `EthernetProtocol.cs` | 408 | Ethernet II / 802.3 |
| `VlanProtocol.cs` | 246 | 802.1Q / QinQ |
| `IPv4Protocol.cs` | 868 | IPv4 + fragmentation |
| `IPv6Protocol.cs` | 1132 | IPv6 + extensions + fragmentation |
| `UdpProtocol.cs` | 490 | UDP + stream tracking |
| `TcpProtocol.cs` | 1233 | TCP + analysis + reassembly |
| `FrameProtocol.cs` | 297 | PCAP frame metadata + link dispatch |

Related: `Udp/UdpStreamTracker.cs`, `PROTOCOL_GUIDE.md`, generated partials under `obj/` (BinaryParsable, Protocol generator).

### In scope — `NetworkInspector.FrameBuilder`

| Area | Key files |
|------|-----------|
| Bus | `SocketCanLayer.cs`, `SocketCanFdLayer.cs`, `FlexRayLayer.cs` |
| Link | `EthernetLayer.cs`, `VlanLayer.cs` |
| Network | `IPv4Layer.cs`, `IPv6Layer.cs`, `IPv6FragmentExtensionLayer.cs` |
| Transport | `UdpLayer.cs`, `TcpLayer.cs`, `TcpLayerWithAutoSequence.cs` |
| Headers | `EthernetHeader.cs`, `VlanTag.cs`, `IPv4Header.cs`, `IPv6Header.cs`, `UdpHeader.cs`, `TcpHeader.cs`, `SocketCan*.cs` |
| Build core | `Auto.cs`, `FrameSequence.cs`, `FixPhase.cs`, `ChecksumUtils.cs`, `FragmentGeometryHelper.cs` |
| Stateful | `TcpConnection.cs`, `TcpLayerWithAutoSequence.cs` |
| Stack | `StatelessStack.cs`, `StackHelpers.cs` |

### Tests reviewed

`NetworkInspector.Core.Tests` (Can, FlexRay, Tcp), `NetworkInspector.Protocols.Tests` (Ethernet, VLAN, UDP, TCP, IPv4, IPv6), `NetworkInspector.FrameBuilder.Tests` (checksums, fragmentation, bus smoke).

### Exclusions

Non-scoped protocols (ARP, DNS, TLS, …), UI, exporters, sources BLF parsers (referenced only for FlexRay wire format).

---

## Errors

## E1 - FlexRay silent truncation (Protocols)
Status: ⬜ Open · Severity: High
### What
`FlexRayProtocol` computes `totalConsumed = Math.Min(_MinHeaderSize + payloadSize, data.Length)` and returns success even when the buffer is shorter than the header-declared payload.
### Why
Violates correctness contract: truncated captures decode as complete frames with shortened payload. Downstream dispatch (`flexray.id`) operates on partial bytes without error signal. Contrasts with CAN which returns `InsufficientData` for short buffers.
### How
- After computing `payloadSize` from header, if `data.Length < _MinHeaderSize + payloadSize`, return `ParseError.InsufficientDataWithInfo(...)`.
- Remove `Math.Min` truncation for data frames; keep explicit minimum check for `_MinHeaderSize` only.
- Add tests: declared 32 B payload, buffer holds 10 B → error.
### Where
`NetworkInspector.Protocols/FlexRayProtocol.cs:296-310`, `378-382`
### Verify
`dotnet test NetworkInspector.Core.Tests -c Release --filter FlexRay`
Expected: new truncation test fails before fix, passes after.

## E2 - FlexRay index false positive (Protocols)
Status: ⬜ Open · Severity: High
### What
`RecordGroupPresence(_FlexrayGroupId)` runs unconditionally at parse entry, including symbol/event frames (`typeIndex != 0x01`) that never emit slot/cycle/payload fields.
### Why
Index consumers filtering on `flexray` group get false positives for non-data frames. Violates PROTOCOL_GUIDE §13c.3 (record group only when fields materialize).
### How
- Move `RecordGroupPresence(_FlexrayGroupId)` into the data-frame branch after header decode.
- Keep `_FlexrayErrGroupId` recording for symbol frames if error fields are always emitted.
- Add test: symbol frame must not set `flexray.frame_id` / `flexray.cycle` index bits.
### Where
`NetworkInspector.Protocols/FlexRayProtocol.cs:229-230`, `260-279`
### Verify
`dotnet test NetworkInspector.Core.Tests -c Release --filter FlexRay`
Expected: index invariant test passes after fix.

## E3 - UDP thread-safety contract wrong (Protocols)
Status: ⬜ Open · Severity: High
### What
`UdpProtocol` XML states concurrent `Parse` on the same instance is safe. `_StreamTracker` (`Dictionary` + LRU arrays + `_NextStreamIndex++`) mutates on every packet without locks.
### Why
If one `UdpProtocol` instance is shared across threads (or parallel packet parsing on one stack), stream indices corrupt and dictionary internal state races. Contradicts Section 4.3 thread-safety rules and `FlexRayProtocol`/`TcpProtocol` honest non-thread-safe remarks.
### How
- **Option A:** Document non-thread-safe (match FlexRay/TCP); require one stack/thread or external sync.
- **Option B:** Move tracker to per-`Stack`/`ParseContext` state (preferred for concurrent multi-stack).
- **Option C:** Protect `_StreamTracker` with `lock` or concurrent dictionary (last resort — hot-path cost).
- Update XML `<remarks>` to match chosen model.
### Where
`NetworkInspector.Protocols/UdpProtocol.cs:20-24`, `147`, `248-273`; `Udp/UdpStreamTracker.cs:54-108`
### Verify
Concurrent stress test: N threads, shared `UdpProtocol`, distinct 4-tuples → monotonic unique stream indices, no exceptions.
### If it fails
Stream index collisions in multi-threaded capture analysis; intermittent wrong `udp.stream` values.

## E4 - UDP sub-minimum length accepted (Protocols)
Status: ⬜ Open · Severity: Medium
### What
When `udp.length < 8`, parser accepts the datagram and reports zero payload without error.
### Why
RFC 768 minimum UDP header is 8 bytes. Values 1–7 are malformed; silent acceptance hides capture corruption.
### How
- After header parse: if `length < UdpHeader.HeaderSize`, return `ParseError.InvalidData` or `InsufficientDataWithInfo`.
- Add test mirroring `TcpChecksumTests` pattern for UDP malformed length.
### Where
`NetworkInspector.Protocols/UdpProtocol.cs:205-209`
### Verify
`dotnet test NetworkInspector.Protocols.Tests -c Release --filter UdpMalformed`

## E5 - L3/L4 silent truncation (Protocols)
Status: ⬜ Open · Severity: Medium
### What
UDP returns `Math.Min(length, data.Length)`; IPv4 returns `Math.Min(totalLength, data.Length)`; TCP derives payload from `data.Length - headerLen` ignoring enclosing IP length. None signal truncation when declared length exceeds buffer.
### Why
Inconsistent with CAN/FlexRay minimum-header checks. Checksum validation and dispatch operate on truncated data without error — false negatives for integrity checks.
### How
- Unified policy: if `declaredLength > data.Length`, return `InsufficientDataWithInfo` naming protocol, expected, actual.
- TCP: bound segment length to min(`data.Length`, IP payload length from sibling/cache).
- Document policy in PROTOCOL_GUIDE.
### Where
`UdpProtocol.cs:351`; `IPv4Protocol.cs:490-531`; `TcpProtocol.cs:737`
### Verify
Truncation tests per protocol; `LinkLayerTruncationTests` pattern extended to L3/L4.

## E6 - TCP checksum length bound (Protocols)
Status: ⬜ Open · Severity: High
### What
`_ValidateChecksum` sets `tcpLength = (ushort)tcpData.Length` for pseudo-header and checksum span, ignoring IP Total Length / payload length.
### Why
Truncated TCP segments (common in captures) produce wrong pseudo-header length → false `[Good]` or `[Bad]` checksum status when `tcp.verify_checksum` enabled.
### How
- Resolve IP payload length from IPv4 `totalLength - ipHeaderLen` or IPv6 payload length minus extensions (use existing caches).
- Use `min(tcpData.Length, ipPayloadRemaining)` for checksum computation.
- Add test: valid checksum on full segment, truncated buffer → `[Bad]` or `[Unverified]`, not `[Good]`.
### Where
`NetworkInspector.Protocols/TcpProtocol.cs:657-688`
### Verify
`dotnet test NetworkInspector.Protocols.Tests -c Release --filter TcpChecksum`

## E7 - Frame FlexRay link-type label (Protocols)
Status: ⬜ Open · Severity: Low
### What
`FrameProtocol.GetLinkTypeName` includes `LinkType.CanSocketcan` but not `LinkType.Flexray` (210) — FlexRay frames show `"Unknown"` in `frame.link_type` summary.
### Why
Display/regression mismatch: dispatch registers FlexRay at link type 210; UI label table incomplete.
### How
- Add `LinkType.Flexray => "FlexRay"` before default arm (mirror `LinkType.cs:533`).
- Add `FrameProtocol` test asserting label for link type 210.
### Where
`NetworkInspector.Protocols/FrameProtocol.cs:182-216`
### Verify
`dotnet test` — new Frame link-type test passes.

## E8 - FlexRay payload field overflow (FrameBuilder)
Status: ⬜ Open · Severity: High
### What
`payloadWords = (payloadBytes + 1) / 2` encoded in 7 bits. Payload 255 B → 128 words → `(128 & 0x7F) == 0` — wire shows zero-length header while copying 255 bytes.
### Why
ISO 17458 / LINKTYPE_FLEXRAY max payload is 254 bytes. Silent wrap produces invalid captures that pass builder without error.
### How
- Validate `payloadBytes <= 254` in ctor (throw or `TryCreate` API).
- Guard `payloadWords <= 127` before encoding.
- Tests: 254 B pass, 255 B fail.
### Where
`NetworkInspector.FrameBuilder/Layers/Bus/FlexRayLayer.cs:64-96`, `128-131`
### Verify
`dotnet test NetworkInspector.FrameBuilder.Tests -c Release --filter FlexRay`

## E9 - IPv6Layer `mtu` ignored for fragmentation (FrameBuilder)
Status: ⬜ Open · Severity: Medium
### What
`IPv6Layer(…, mtu: N)` stores `_Mtu` and exposes `MaxPayloadSize`, but does not implement `IProvidesMtu`. `StatelessStack.MaxFrameLength` reads MTU only from Ethernet.
### Why
API contract mismatch: callers setting IPv6 PMTU expect fragmentation at 1280 (or custom) without also patching Ethernet `maxFrameSize`.
### How
- Implement `IProvidesMtu` on `IPv6Layer` returning `_Mtu` when non-zero, **or**
- Remove `mtu` parameter and document Ethernet-only MTU, **or**
- Compose `min(linkMtu, ipv6Mtu)` in stack walk.
### Where
`IPv6Layer.cs:45-74,88-94`; `StatelessStack.cs:46-51`
### Verify
Build Eth+IPv6(mtu:1280)+2000B UDP → fragment count matches 1280-byte IP budget.

## E10 - Checksum path throws in throw-free build (FrameBuilder)
Status: ⬜ Open · Severity: Medium
### What
`ChecksumUtils.PseudoHeaderIPv4/IPv6` throw `ArgumentOutOfRangeException` on short address spans. `UdpLayer`/`TcpLayer` call them from `FixPhase.InnerChecksum` without try/catch. `FrameSequence.MoveNext` documents throw-free iteration.
### Why
Malformed `PostFixContext` or ordering bugs escape as exceptions, corrupting iterator state instead of `BuildStatus`.
### How
- Add `TryPseudoHeaderIPv4/IPv6` returning `bool`.
- On failure: `ctx.Status = BuildStatus.InvalidLayerState`; skip checksum write.
- Test: broken pseudo context → status, no exception.
### Where
`ChecksumUtils.cs:91-142`; `UdpLayer.cs:163-165`; `TcpLayer.cs:135-137`; `FrameSequence.cs:136-140`
### Verify
`dotnet test NetworkInspector.FrameBuilder.Tests -c Release`

## E11 - Unchecked `ushort` length writes (FrameBuilder)
Status: ⬜ Open · Severity: Medium
### What
IPv4 TotalLength, IPv6 PayloadLength, UDP Length written via `(ushort)` cast without validation.
### Why
Segments >65535 bytes silently truncate length fields → checksum/length mismatch on large synthetic frames.
### How
- Before cast: if value > `ushort.MaxValue`, set `ctx.Status = BuildStatus.InvalidLayerState`.
- Document 65535-byte ceiling in layer XML.
### Where
`IPv4Layer.cs:152`; `IPv6Layer.cs:139-141`; `UdpLayer.cs:114`
### Verify
Oversized payload build test → non-success status.

## E12 - VLAN TCI components unvalidated (FrameBuilder)
Status: ⬜ Open · Severity: Low
### What
`VlanLayer` accepts any `ushort vlanId`, `byte pcp`, `byte dei`. `MakeTci` masks only VID (`& 0x0FFF`); PCP/DEI not masked.
### Why
`pcp > 7` shifts into DEI/VID bits; invalid 802.1Q on wire.
### How
- Validate in ctor: `vlanId <= 4095`, `pcp <= 7`, `dei <= 1`.
- Or mask in `MakeTci`: `(pcp & 7) << 13`, `(dei & 1) << 12`.
### Where
`VlanLayer.cs:45-65`; `VlanTag.cs:44-45`
### Verify
`dotnet test NetworkInspector.FrameBuilder.Tests --filter Vlan`

---

## Cosmetic Issues

## C1 - `TryWrite` result ignored (FrameBuilder)
Status: ⬜ Open
### What
Layers use `_ = hdr.TryWrite(dst, out _)` without checking success.
### Why
Undersized destination spans fail silently; harder to debug custom interceptors.
### How
- On `false`: set `ctx.Status = BuildStatus.BufferTooSmall` in post-fix or document caller guarantee.
### Where
`EthernetLayer.cs:103` (pattern repeated across layers)
### Verify
Undersized buffer test surfaces non-success status.

## C2 - Stray `#endregion` in `VlanHeader` (Protocols)
Status: ⬜ Open
### What
Orphan `#endregion` at end of nested `VlanHeader` partial struct.
### Why
Violates file region structure (tech-csharp § Style); confuses navigation.
### How
- Remove stray `#endregion` or add matching `#region`.
### Where
`VlanProtocol.cs:244`
### Verify
Build + style checker clean.

---

## Refactoring Opportunities

## R1 - FlexRay parse duplication (Protocols)
Status: ⬜ Open
### What
`FlexRayProtocol` inlines ISO header decode instead of delegating to `FlexRayLinkTypeFrame.TryParseDataFrame`.
### Why
`FlexRayLinkTypeFrame` is SSOT for wire format (also used by BLF sources). Duplication risks drift on truncation/validation fixes.
### How
- Replace inline decode with `TryParseDataFrame` call; map `Fields` to MutField tree.
- Behavior unchanged after E1/E2 fixes.
### Where
`FlexRayProtocol.cs:282-310` vs `NetworkInspector.Core/Protocols/FlexRayLinkTypeFrame.cs:263-310`
### Verify
Existing FlexRay tests pass unchanged.

## R2 - Eager/lazy policy drift (Protocols)
Status: ⬜ Open
### What
PROTOCOL_GUIDE §13b.1 lists UDP as fully lazy and Ethernet key fields lazy; implementation is UDP fully eager, Ethernet partially eager.
### Why
Documentation/implementation mismatch blocks performance work and confuses contributors.
### How
- Either align implementation to guide (lazy UDP container + populator), **or** update guide with measured rationale for eager paths.
### Where
`PROTOCOL_GUIDE.md:1402-1408`; `UdpProtocol.cs:299-301`; `EthernetProtocol.cs:212-227`
### Verify
Guide/code cross-reference review; benchmark UDP parse before/after if lazy migration chosen.

## R3 - CAN-FD EFF constant drift (FrameBuilder)
Status: ⬜ Open
### What
`SocketCanFdLayer` uses literal `0x80000000u`; classic CAN uses `SocketCanHeader.EffFlag`.
### Why
Behavior matches today; duplicate magic number risks future flag-bit changes diverging.
### How
- Replace literal with `SocketCanHeader.EffFlag` (or shared `CanFlags` constants type).
### Where
`SocketCanFdLayer.cs:66` vs `SocketCanHeader.cs:25`
### Verify
Build + existing CAN tests pass.

---

## Performance and Allocations

## P1 - UDP fully eager (Protocols)
Status: ⬜ Open
### What
Every UDP field (ports, length, checksum, payload, checksum status) appended during `Parse`.
### Why
Highest allocation profile among scoped L4 protocols. Every packet pays full field-tree materialization even when UI/index only needs ports.
### How
- Migrate to lazy container + `_PopulateUdp` populator (mirror TCP pattern).
- Keep eager: `udp.stream`, `udp.srcport`, `udp.dstport` per guide §13b.1.
### Context
Hot path: millions of UDP packets/sec in capture replay. Section 4.4: minimize allocations; avoid eager work when lazy populator suffices.
### Where
`UdpProtocol.cs:299-326`
### Verify
Allocation benchmark: Gen0 allocs/packet before vs after on 1M UDP datagrams.

## P2 - TCP flags formatted twice (Protocols)
Status: ⬜ Open
### What
`TcpFlagsFormatter.Format(flags)` called in `Parse` for summary and again in `_PopulateTcp` for field display.
### Why
Duplicate string work on every materialized TCP segment.
### How
- Store flags byte only in summary `ZA.Lazy` closure; defer format to populator.
- Or cache formatted string in thread-static scratch keyed by flags nibble (8 values for SYN/FIN/ACK combos if acceptable).
### Context
Runs on every TCP packet when fields materialize.
### Where
`TcpProtocol.cs:754-761`, `613`
### Verify
Benchmark TCP parse + materialize; compare string allocations.

## P3 - VLAN header parsed twice (Protocols)
Status: ⬜ Open
### What
`VlanHeader.TryParse` in `Parse` for dispatch; populator re-parses same 4 bytes from container value.
### Why
4-byte re-parse is cheap but unnecessary; doubles work on every materialized VLAN packet.
### How
- Store parsed TCI/EtherType in thread-static scratch keyed by `PacketId`, or pass via container side-channel.
- Simpler: eager-append VID/PCP/DEI/EtherType (4 fields) and drop populator re-parse.
### Where
`VlanProtocol.cs:146-149`, `96-105`
### Verify
VLAN tests pass; optional micro-benchmark.

## P4 - IPv6 extension chain walked twice (Protocols)
Status: ⬜ Open
### What
`Parse` eagerly walks extension headers for dispatch/reassembly; `_PopulateIpv6` walks again on materialization.
### Why
O(extensions) work per packet even when fields never shown. Known tradeoff per guide but costly on ESP/AH chains.
### How
- Cache extension walk result in thread-static `PacketId` scratch (offset, final next header, frag info).
- Populator reads cache instead of re-walking.
### Context
Extension-heavy IPv6 traffic (mobile, IoT); duplicate walk ~40-200 bytes inspected twice.
### Where
`IPv6Protocol.cs:490-568`, lazy populator ~409-411
### Verify
IPv6 extension tests pass; benchmark extension-heavy capture.

## P5 - UDP container stores full buffer (Protocols)
Status: ⬜ Open
### What
`FieldValue.NewBytes(data)` stores entire IP-provided buffer, not slice bounded to `udp.length`.
### Why
Trailing garbage beyond declared UDP length retained in field value; inflates memory and can skew checksum if span includes extras.
### How
- Use `data.Slice(0, Math.Min(length, data.Length))` for container bytes.
### Where
`UdpProtocol.cs:295-296`
### Verify
UDP tests with IP padding beyond UDP length.

## P6 - FlexRayLayer heap payload (FrameBuilder)
Status: ⬜ Open
### What
`ReadOnlyMemory<byte> _Payload` typically backed by heap array from `new byte[]`.
### Why
Bus capture loops constructing many FlexRay frames pay Gen0 pressure.
### How
- Inline small payload (≤16 B) in struct fields; `ReadOnlyMemory` overload for large payloads.
- Mirror `SocketCanLayer` 8-byte inline pattern.
### Context
Root layer; payload lifetime equals layer lifetime — safe to inline.
### Where
`FlexRayLayer.cs:47,95,125-149`
### Verify
Profiler: 0 Gen0 allocs per ≤16 B FlexRay build.

## P7 - Capability dispatch boxes structs (FrameBuilder)
Status: ⬜ Open
### What
`StackHelpers.TryCast` boxes struct layer heads to test interface capabilities on every header write and post-fix walk.
### Why
Per-layer-per-frame allocation on deep stacks (VLAN QinQ, IPv6 ext chain). Documented CA1508 workaround.
### How
- Source-generated switch on layer type IDs for known stacks.
- Or static generic specialization cache per `THead`.
### Context
Section 4.4: avoid boxing in hot paths; runs N times per frame for N layers.
### Where
`StackHelpers.cs:38-45`; `StatelessStack.cs:94,174`
### Verify
Benchmark Eth+VLAN×4+IPv4+UDP: allocs before/after.

## P8 - CAN-FD ctor stack work (FrameBuilder)
Status: ⬜ Open
### What
Each `SocketCanFdLayer` ctor: `stackalloc byte[64]` + 8× `ReadUInt64LittleEndian` into ulong fields.
### Why
Acceptable for infrequent setup; hot replay creating new layer per frame adds stack zero + copy cost.
### How
- Defer packing to `WriteHeader` from span stored at ctor.
- Or fixed byte array fields for ≤64 B.
### Where
`SocketCanFdLayer.cs:91-100`
### Verify
Benchmark 1M CAN-FD frames.

---

## Closing Assessment

### Architecture quality
Layered sibling-dispatch (Protocols) and cons-list phased post-fix (FrameBuilder) are sound designs. Dispatch caches (dense `ip.proto`, sparse EtherType/port/link-type) are well chosen. Thread-local IP address caches are the right hot-path pattern. FrameBuilder fragmentation “build once, slice many” correctly preserves transport checksum semantics.

### Thread-safety posture
FrameBuilder stateless layers are immutable structs; `FrameSequence`/`TcpConnection` correctly documented as single-use. Protocols side: TCP/IPv4/IPv6/FlexRay honestly mark stack-bound use; **UDP documentation overclaims** (E3).

### Allocation profile
CAN/Ethernet/VLAN/Frame lean acceptable. **UDP is the primary outlier** (fully eager, full-buffer container). FrameBuilder: FlexRay heap payload + capability boxing dominate alloc sources.

### Test coverage gaps (scoped)
| Gap | Area |
|-----|------|
| FlexRay truncation + symbol index | Protocols |
| UDP `verify_checksum` + `length < 8` | Protocols |
| TCP checksum with truncated IP | Protocols |
| Frame link-type display | Protocols |
| FlexRay 254/255 boundary | FrameBuilder |
| IPv6 `mtu` vs fragmentation | FrameBuilder |
| VLAN OOR TCI | FrameBuilder |
| `BuildStatus.InvalidLayerState` paths | FrameBuilder |
| Index invariant corpus excludes CAN/FlexRay/Frame/Ethernet/UDP | Protocols |

### Release Verdict
**Not ready for public release** — prioritize blockers **E1, E2, E3, E6, E8, E10**.

---

## Priority Action List

1. **E1 + E2** — FlexRay truncation error + index group recording fix (Protocols)
2. **E3** — UDP thread-safety: move tracker or fix documentation (Protocols)
3. **E6** — TCP checksum IP-bounded length (Protocols)
4. **E8** — FlexRay 254-byte ceiling in FrameBuilder
5. **E10** — Try-pseudo-header APIs for throw-free build path

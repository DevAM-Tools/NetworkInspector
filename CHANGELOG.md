<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [0.2.0] — FrameBuilder, Sources, Exporters, CLI and Test Projects

### Added

**`NetworkInspector.FrameBuilder`** — Typed, allocation-free frame builder library:
- Generic cons-list API for composing protocol stacks at compile time with full type safety.
- Layers: Ethernet, VLAN, Linux SLL/SLL2, LLC/SNAP, IPv4, IPv6 (with extension headers), TCP, UDP, ICMP, ICMPv6, ARP, DHCP/DHCPv6, DNS, TLS, DTLS, HTTP/1.x, WebSocket, SOME/IP, PDU Transport, Signal PDU, CAN (classic/FD/XL), FlexRay, LIN.
- Automatic checksum calculation, IP fragmentation, TCP segmentation, and pseudo-header computation.
- Stateful builder for multi-frame streams (TCP sessions, fragmented IP, etc.).

**`NetworkInspector.Sources`** — Frame source readers for capture file formats:
- PCAPNG reader with streaming, random-access, error-tolerance, and memory-mapped I/O.
- Vector BLF reader with LZ4 container decompression, multi-channel demux, and configurable cache budget.
- Vector ASC reader with timestamp normalization and channel filtering.
- Random frame source for synthetic test data generation.
- `CachedFrameSource` — in-memory caching wrapper for repeated random-access reads.

**`NetworkInspector.Exporters`** — Frame and packet exporters:
- PCAPNG exporter with optional LZ4 compression and SHB/IDB metadata.
- BLF exporter with configurable compression (off / fast / best).
- CSV exporter with configurable column definitions and delimiter.
- JSON exporter (compact, pretty, array styles).
- PBF exporter (row-oriented and columnar with optional LZ4 compression).
- ASC exporter for Vector CANalyzer ASCII log files (CAN classic, CAN FD, LIN, FlexRay).
- Plain-text exporter with configurable verbosity levels (summary / standard / full) and value truncation.

**`NetworkInspector.CLI`** — Command-line tool (`ni`):
- `ni convert` — Frame-level format conversion between PCAPNG, BLF, and more. Supports file splitting, progress reporting, error tolerance, and BLF cache budgets.
- `ni export` — Parse and export packets to JSON, PBF, or plain-text with full protocol stack dissection. Supports settings profiles.

**Test Projects:**
- `NetworkInspector.Core.Tests` — 100% coverage of Core internals (roaring bitmap, slab allocator, LargeBuffer, etc.).
- `NetworkInspector.Protocols.Tests` — Per-dissector tests for all 30 protocols including tshark UAT integration tests.
- `NetworkInspector.FrameBuilder.Tests` — Smoke tests, integration tests, and negative compilation tests for FrameBuilder.
- `NetworkInspector.Sources.Tests` — Reader tests for PCAPNG, BLF, ASC, random, and cached sources.
- `NetworkInspector.Exporters.Tests` — Exporter tests for all formats including round-trip verification with tshark.
- `NetworkInspector.Testing.Tshark` — Shared tshark test helper library used by Protocols.Tests, FrameBuilder.Tests, and Exporters.Tests.

### Changed
- Version bumped to `0.2.0`.
- `NetworkInspector.Core.csproj`: added `InternalsVisibleTo` for `NetworkInspector.Core.Tests` and `NetworkInspector.Exporters.Tests`.
- `NetworkInspector.Protocols.csproj`: added `InternalsVisibleTo` for `NetworkInspector.Protocols.Tests`.
- `NetworkInspector.Exporters` — fixed CA2014: moved `stackalloc` for timestamp formatting buffer outside the column loop in `CsvExporter`.
- `Directory.Packages.props`: added `TUnit` package version entry.

---

## [0.1.0] — Initial Release

### Added

**`NetworkInspector.Core`**
- Protocol stack (`StackBuilder` / `Stack`) — build, configure, and freeze the protocol registry; parse raw frames into typed packet trees.
- Flat field tree — fields stored as a contiguous array of `FieldBody` structs; parent/child/sibling relationships encoded as `ushort` indices for cache-efficient traversal with zero heap pointers.
- Thread-local slab allocators (`SlabAllocator<T>`) — eliminate per-packet GC allocations; three slabs per parsing thread (FieldBody, ChunkDescriptor, LazyPopulator).
- Lazy fields (`LazyPopulator`) — deferred, cached field materialization for expensive computations (formatting, crypto, deep inspection).
- `PacketIndex` — cross-packet roaring bitmap index for constant-time presence queries across captures.
- `ValueCacheSeries` — columnar time-series storage for field values across packets.
- `FieldValue` — 16-byte discriminated union covering all field types without heap allocation.
- `ParseResult` — 4-byte struct encoding consumed byte count and error code; no exceptions on parse errors.
- Typed dispatch tables (`ProtocolTable`) — `U8`, `U16`, `U32`, `U64`, `String`, and heuristic variants; protocol routing built at stack-construction time with no runtime reflection.
- `DatagramDefragmenter` — IPv4 fragment reassembly with roaring-bitmap completion tracking and FIFO eviction.
- `LargeBuffer` — growable unmanaged memory buffer overcoming the 2 GB managed-array limit via reference-counted segments.
- `TwoQueueCache<K,V>` — generic 2Q eviction cache with weight-based capacity.
- `SettingsManager` — typed runtime settings with JSON persistence; loaded by generated `RegisterFields`.
- Roslyn source generator (`NetworkInspector.Generators`, bundled) — processes `[Protocol]`-annotated partial classes and generates: field ID assignment, `RegisterFields`, `OnStart`, `OnShutdown`, dispatch table registration, setting loading, index group wiring, and public name constants. Supported field attributes: `[NoneField]`, `[BoolField]`, `[I64Field]`, `[U64Field]`, `[F64Field]`, `[StringField]`, `[BytesField]`, `[MacField]`, `[IPv4Field]`, `[IPv6Field]`, `[Eui64Field]`, `[UuidField]`, `[TimestampField]`. Diagnostics NIGEN001–NIGEN013.

**`NetworkInspector.Protocols`** — 30 built-in dissectors:
- Link: `FrameProtocol`, `EthernetProtocol`, `VlanProtocol`, `SllProtocol`, `Sll2Protocol`, `LlcProtocol`
- Network: `IPv4Protocol` (with fragment reassembly), `IPv6Protocol` (with extension headers), `ArpProtocol`, `IcmpProtocol`, `Icmpv6Protocol`
- Transport: `TcpProtocol` (with options and heuristic sub-protocol detection), `UdpProtocol`
- Application: `DnsProtocol`, `DhcpProtocol`, `Dhcpv6Protocol`, `HttpProtocol` (HTTP/1.x), `Http2Protocol`, `TlsProtocol` (1.0–1.3), `DtlsProtocol`, `WebSocketProtocol`, `JsonProtocol`, `TextProtocol`
- Automotive: `CanProtocol` (CAN classic / FD / XL), `FlexRayProtocol`, `LinProtocol`, `SomeIpProtocol`, `PduTransportProtocol`, `SignalPduProtocol`
- Fallback: `DataProtocol`

**`NetworkInspector.Values`** — strongly-typed value types:
- `MacAddress` — 48-bit EUI-48, formatted as `00:1a:2b:3c:4d:5e`
- `IPv4Address` — 32-bit, dotted-decimal notation
- `IPv6Address` — 128-bit, RFC 5952 compressed notation
- `Eui64` — 64-bit EUI-64 identifier
- `Uuid` — 128-bit UUID
- `Timestamp` — nanosecond-precision UNIX timestamp with arithmetic operators and ISO 8601 formatting

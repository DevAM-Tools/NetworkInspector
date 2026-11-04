<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Protocols

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Protocols?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NetworkInspector.Protocols)
[![Downloads](https://img.shields.io/nuget/dt/NetworkInspector.Protocols?logo=nuget&label=downloads)](https://www.nuget.org/packages/NetworkInspector.Protocols)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

Ready-to-use dissectors for `NetworkInspector.Core`: enterprise networking (Ethernet to TLS and HTTP/2) and
automotive buses (CAN, CAN FD, CAN XL, LIN, FlexRay, SOME/IP) in one stack, enabled with one call.

## Highlights

- **29 dissectors plus configurable signal messages** across link, network, transport, application, and bus
  layers.
- **One registration call.** `builder.RegisterStandardProtocols()` wires protocols, dispatch tables, TCP
  heuristics, and stream reassembly.
- **Real-world traffic.** TCP stream reassembly and analysis (retransmissions, keep-alives, window scaling),
  IPv4/IPv6 defragmentation, HTTP/1 messages across segments, HTTP/2 with HPACK, WebSocket with permessage-deflate,
  SOME/IP messages with names and typed payload parameters from a service description.
- **Automotive signal decoding.** Decode CAN or PDU payloads into named, scaled signals from a JSON description
  (DBC-like: start bit, length, byte order, factor, offset, unit, value names, multiplexing).
- **Cross-validated against an independent reference dissector.** Protocol tests compare field values, and for TCP the analysis flags, with `tshark` 4.6.
- **Dotted, display-filter-style field names** (`ip.src`, `tcp.port`, `udp.dstport`, …) for filters, exports, and value caches.
- **Extensible.** Write your own dissector with the same attributes and source generator the built-ins use.

## Install

```bash
dotnet add package NetworkInspector.Core
dotnet add package NetworkInspector.Protocols
```

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Core.Settings;
using NetworkInspector.Protocols;

StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
IReadOnlyList<SettingsLoadWarning> warnings = builder.RegisterStandardProtocols();
foreach (SettingsLoadWarning warning in warnings)
{
    Console.Error.WriteLine(warning); // PDU Transport and signal-message configuration problems
}

using Stack stack = builder.Build();
```

Parse frames with the stack as shown in the [Core README](../NetworkInspector.Core/README.md#quick-start).

## Protocol Coverage

| Layer | Protocols |
| --- | --- |
| Meta | Frame |
| Link | Ethernet II, VLAN (802.1Q, 802.1ad), LLC/SNAP, Linux SLL, Linux SLL2 |
| Network | IPv4, IPv6, ARP (including ATMARP), ICMPv4, ICMPv6 (Neighbor Discovery, Multicast Listener Discovery) |
| Transport | TCP, UDP |
| Application | DNS, DHCPv4, DHCPv6, HTTP/1.x, HTTP/2, TLS, DTLS, WebSocket, JSON, Text |
| Automotive and bus | CAN (classic, FD, XL), LIN, FlexRay, SOME/IP, PDU Transport, signal messages |
| Fallback | Data |

`RegisterStandardProtocols` registers, in this order: Frame, Ethernet, VLAN, IPv4, IPv6, UDP, ARP, ICMP, ICMPv6,
TCP, SLL, SLL2, LLC, DNS, DHCPv4, DHCPv6, TLS, DTLS, CAN, SOME/IP, FlexRay, LIN, HTTP, HTTP/2, WebSocket, Data,
Text, JSON, PDU Transport, and the configured signal messages. It also registers the TCP heuristic table (HTTP/1,
TLS, HTTP/2) and the TCP reassembly configurations for DNS over TCP and HTTP/1.

`RegisterFairProfilingProtocols` registers only Frame, Ethernet, IPv4, IPv6, UDP, and TCP, without heuristics and
reassembly, for like-for-like benchmarks.

## Configuration

Settings are read from the stack's `SettingsManager`. Preload or persist them before `RegisterStandardProtocols`.

| Setting | Purpose |
| --- | --- |
| `pdu_transport.config_file` | JSON file with PDU names (schema in `Schemas/`) |
| `pdu_transport.udp_dispatch_ports` | UDP ports that carry PDU Transport (default: none) |
| `signal_message.config_file` | JSON signal description for CAN or PDU payloads (schema in `Schemas/`) |
| `signal_message.show_raw`, `.show_enum`, `.max_enum_values` | Signal display options (`max_enum_values` is 1 or more) |
| `can.config_file` | JSON CAN message names; an identifier that appears twice keeps its first entry and is reported by `CanProtocol.ConfigLoadWarning` |
| `icmp.verify_checksum` (default off) | Verifies the ICMPv4 checksum over the whole message and emits `icmp.checksum.status` (`[Good]`, `[Bad]`, or `[Unverified]` when the capture length cut the message short) |
| `icmpv6.verify_checksum` (default off) | Verifies the ICMPv6 checksum over the whole message and the IPv6 pseudo header and emits `icmpv6.checksum.status` (`[Good]`, `[Bad]`, or `[Unverified]` when no IPv6 layer encloses the message or the capture length cut it short) |
| `tcp.max_tracked_streams` (default 10000), `tcp.stream_key_last_vlan`, `tcp.stream_key_frame` | TCP stream tracking |
| `udp.max_tracked_streams` (default 10000) | UDP stream tracking: a new conversation beyond the cap gets no `udp.stream` and `udp.error.stream_limit` |
| `http.max_decompressed_bytes`, `ws.max_decompressed_bytes` (default 64 MiB each, range 1 KiB to 128 GiB) | Largest decompressed HTTP body or WebSocket payload; a larger one stays undecoded and is flagged by `http.error.decompress_limit` or `websocket.payload.decompressed.limit`. The sum per packet is a separate setting of each protocol (next row) |
| `http.max_derived_bytes_per_packet`, `ws.max_derived_bytes_per_packet` (default 256 MiB each, range 2 KiB to 128 GiB: the TCP PDU of a message counts too), `someip.tp_max_derived_bytes_per_packet` (default 64 MiB, range 2 KiB to 16 GiB) | Most bytes that HTTP (decoded bodies), WebSocket (unmasked and decompressed payloads), or SOME/IP-TP (reassembled messages) bind to ONE packet in total: the limit is applied to all bytes bound to the packet when the protocol binds, whichever protocol bound them (the TCP PDUs included). Relaxed by default (four times the per-item default; the SOME/IP-TP default equals `someip.tp_max_buffered_bytes`). The body, payload, or message that would pass it is not parsed and `packet.error` names the setting and says it is the total per packet; a value below the per-item limit also limits a single item |

Example: dispatch UDP port 47290 to PDU Transport.

```csharp
using NetworkInspector.Core.Settings;
using NetworkInspector.Protocols.PduTransport;

SettingsManager settings = new();
settings.PreloadValue(PduTransportRegistration.UdpDispatchPortsSetting, SettingValue.U64Array([47290UL]));
StackBuilder builder = new(settings, new FrameInterfaceRegistry());
builder.RegisterStandardProtocols();
```

Signal and PDU configuration loading is best effort: invalid entries are skipped and reported as
`SettingsLoadWarning`, never thrown.

### Schemas and examples

The package ships the JSON schemas and a compact example for every configuration under `Schemas/`, so you can validate a file
in your editor and start from a working configuration. The files are in the NuGet package (`Schemas/`) and in this repository
under [`Schemas/`](Schemas/).

| Configuration | Schema | Examples |
| --- | --- | --- |
| PDU Transport names | [`pdu-transport-config.schema.json`](Schemas/pdu-transport-config.schema.json) | [`pdu_names.json`](Schemas/examples/pdu-transport/pdu_names.json): several PDU ids with names and comments |
| Signal Message | [`signal-message-config.schema.json`](Schemas/signal-message-config.schema.json) | [`can_and_pdu_bindings.json`](Schemas/examples/signal-message/can_and_pdu_bindings.json): one message bound to `can.id` and `pdu_transport.id` with both byte orders, factor and offset, unit, limits and value names; [`multiplexed.json`](Schemas/examples/signal-message/multiplexed.json): a multiplexer with two groups |
| SOME/IP | [`someip-config.schema.json`](Schemas/someip-config.schema.json) | [`minimal_service.json`](Schemas/examples/someip/minimal_service.json), [`full_service.json`](Schemas/examples/someip/full_service.json), [`arrays_and_e2e08.json`](Schemas/examples/someip/arrays_and_e2e08.json), [`unions_bitfields_strings.json`](Schemas/examples/someip/unions_bitfields_strings.json) |
| Settings of a profile | the settings group files | [`profile/`](Schemas/examples/profile/): `pdu_transport.json`, `signal_message.json`, `someip.json` |

The files in [`Schemas/examples/profile/`](Schemas/examples/profile/) show how a profile references the configuration files.
A profile folder holds the settings group files at its top (the settings manager reads every `*.json` there as a group file) and
the referenced configuration files one level down, in `configs/`; the examples name them as `configs/pdu_names.json`,
`configs/can_and_pdu_bindings.json`, and `configs/full_service.json`. A relative path in `*.config_file` resolves against the
profile folder and cannot leave it.

### SOME/IP configuration

The SOME/IP parser decodes service-discovery and message headers without any configuration. A service description adds
names (services, methods, clients) and payload decoding. The format is the JSON of
[`someip-config.schema.json`](Schemas/someip-config.schema.json); start from one of the
[examples](Schemas/examples/someip/).

- **Version.** `schema_version` is `"major.minor"`. The major number changes only on a breaking change of the format; a file
  with another major version is refused as a whole with one warning that names the version found and the version supported.
  The minor number changes for additive extensions, so every `1.x` file loads. A missing or malformed version is refused too.
- **Strict reading.** An unknown property or an unknown `kind` or `category` is refused with one warning that names its
  position in the file, because a silently ignored typo would look like an applied setting. Comments and trailing commas are
  allowed. A bare string in a parameter, member, or alternative position is the type reference; an object adds names
  (`name`, `ui_name`) and `pad_to`. A parameter list is an array (sequential encoding) or an object keyed by TLV
  id (TLV encoding).
- **Limits.** A file with more services than `someip.config_max_services` (default 10,000) or more type definitions than
  `someip.config_max_types` (default 1,000,000) is refused as a whole; the warning names the setting and its value, so you can
  raise it if your file really is that large.
- **Warnings, not exceptions.** Every defect is a `SettingsLoadWarning` with the group `someip` and the setting
  `someip.config_file`; nothing is thrown for file content and nothing is dropped silently. A defect never stops the rest of
  the file: a service with a duplicate id is skipped, a dangling type reference marks only its own parameter, a cycle makes
  only the types on it unavailable. A repeated service (same id and major version), member id, field accessor id, or type
  name keeps the first definition and warns once with both positions.
- **Names of services, methods and decoded values.** Every service, every method and every value in a payload is its own
  filterable field, and its filter name is built from the names of the description, unchanged and with their exact case:
  `[<prefix>.]<service>.v<major>.<member>[.req|.resp].<path>`. A name starts with an ASCII letter or an underscore and
  continues with letters, digits and underscores (no dot, no space, no dash). Service `ClimateControl` at major version 2 is
  `ClimateControl.v2`, its method `SetTemperature` is `ClimateControl.v2.SetTemperature`, and the member `zone` of the request
  parameter `request` is `ClimateControl.v2.SetTemperature.req.request.zone`; the filter input completes all of them and
  none needs quotes. The direction `req` (request) or `resp` (response) appears for methods and for the getter and setter of a
  field (`<field>_get`, `<field>_set`); events and field notifiers (`<field>_notifier`) have none, and a getter request has no
  values. The path lists structure members, union alternatives and bitfield bits by name, a parameter or member without a name
  by the name of its type, a TLV member without a name as `member_<id>`, and array elements as `item`. `ui_name` (service,
  member, parameter, member of a type, bit) is the display text and defaults to the name; one with a control character is
  reported and the name is shown. The setting `someip.field_prefix` (default empty) puts a text such as `acme` or `acme.car` in
  front of every filter name, for users who need the names to stay clear of protocols and plugins. A name is never rewritten
  and never gets a numeric suffix. An entry whose name breaks the rule, or repeats the name of an earlier one, is reported
  once and refused, and everything else loads: an invalid service is skipped; an invalid or repeated member is not registered
  (its messages show the method as unknown); an invalid or repeated parameter, structure member, TLV member or union
  alternative stops the decode of that payload at its position with a diagnostic that names it, and the bytes from there on are
  `someip.payload.unparsed` (decoding past it would read the following values from the wrong bytes); an invalid or repeated bit
  is left out. A name that another component already holds in the stack (a protocol, a field or an alias; the first
  registration keeps a name) skips the service, or else the member, with one warning that names the name and its holder.
- **Limits of the parser.** Eleven bounds protect against hostile captures and files. Each is a changeable setting of the group `someip`:

  | Setting | Default | Range | Protects against |
  | --- | --- | --- | --- |
  | `someip.max_messages_per_unit` | 100 | 1–10,000 | a datagram made of tiny messages |
  | `someip.max_type_depth` | 64 | 1–1,024 | stack exhaustion by deeply nested types |
  | `someip.max_payload_nodes` | 16,384 | 16–60,000 | one array filling the field tree of a packet |
  | `someip.tp_max_sessions` | 1,024 | 1–1,000,000 | segmented messages that never complete |
  | `someip.tp_max_message_size` | 10 MiB | 1 KiB–1 GiB | one forged segment position claiming gigabytes |
  | `someip.tp_max_buffered_bytes` | 64 MiB | 1 MiB–16 GiB | many parallel transfers multiplying the two limits above |
  | `someip.tp_max_derived_bytes_per_packet` | 64 MiB | 2 KiB–16 GiB | one datagram completing many large segmented messages that the packet then keeps alive |
  | `someip.tcp_max_message_size` | 16 MiB | 1 KiB–1 GiB | a forged TCP length field |
  | `someip.config_max_services` | 10,000 | 1–1,000,000 | a huge configuration file |
  | `someip.config_max_types` | 1,000,000 | 1–100,000,000 | a huge configuration file |
  | `someip.timing_max_pending` | 65,536 | 1–10,000,000 | requests that never get a response |

  All are secure by default (finite and tight enough for untrusted input); raising one is an explicit choice. A limit that
  is hit is reported by name, never truncated silently. None of them can be switched off yet.

#### SOME/IP settings, ports, and TCP framing

Besides the eleven limits, the group `someip` holds twelve more settings (all registered by `SomeIpRegistration`; a value outside
its range is refused with a warning that names the range, and the default stays in force):

| Setting | Default | What it does and the risk of changing it |
| --- | --- | --- |
| `someip.config_file` | empty | Path of the service description (JSON). Empty means no description: messages decode at header level. |
| `someip.field_prefix` | empty | Text put in front of every filter name of the description (`acme` gives `acme.ClimateControl.v2…`); letters, digits, underscores, parts joined by dots. An invalid value is reported and not used. A naming choice, not a limit. |
| `someip.decode_payload` | on | Decodes the payload of configured methods into typed fields. Off shows raw bytes. |
| `someip.show_type_metadata` | off | Adds type, length, padding and tag fields to a decoded payload; many more fields per payload. |
| `someip.verify_e2e_crc` | off | Computes and compares the end-to-end checksum; one pass over each protected payload. |
| `someip.method_timing` | off | Pairs responses with requests and shows the delay; keeps unanswered requests in memory. |
| `someip.method_timing_ignore_interface` | off | Pairs across capture interfaces; can pair unrelated messages of two networks. |
| `someip.learn_sd_ports` | off | Decodes UDP ports that service discovery announced; a port named in an option is not proof of SOME/IP. |
| `someip.learn_sd_ports_ignore` | none | Ports (1–65535) that are never learned. |
| `someip.heuristic` | off | Decodes a plausible header on any port; unrelated traffic that looks like a header is decoded as SOME/IP. |
| `someip.tcp_resync` | on | Scans for the next plausible header when a TCP stream lost its position. |
| `someip.detect_dtls` | off | Hands datagrams that look like DTLS to the DTLS parser; a heuristic on untrusted data. |

- **Ports.** UDP always listens on 30490 (service discovery runs there) plus every `udp_ports` entry of the description; TCP
  listens on 30490 only when the description lists no `tcp_ports`, otherwise on the listed ones. A port listed twice is bound
  once. Traffic on any other port stays `udp.payload`/`tcp.payload` unless `someip.heuristic` is on; then a plausible header (length of
  at least 8, protocol version 1, a defined message type) is decoded, and on UDP the first message must also fit into the datagram
  (a message is never split across datagrams, and the rule removes most random matches). `someip.detect_dtls` (UDP ports that SOME/IP
  is bound to) hands a datagram to the DTLS parser when it starts with a DTLS record header (content type 20 to 63, version 0xFEFC to
  0xFEFF, a first record that fits) and is not a plausible SOME/IP message, so mixed fleets can share ports; a SOME/IP message always
  wins a tie, and both detections guess on untrusted bytes, which is why they are off by default.
- **Encrypted transports.** `tls_ports` and `dtls_ports` name the ports whose decrypted payload is SOME/IP. A protocol that
  decrypts TLS or DTLS owns the table of its transport (`tls.port`, `dtls.port`, keyed by the server port), where the listed
  ports are bound, and marks the plaintext it hands on; SOME/IP accepts such a unit when the marker carries a listed port of
  the matching transport. The TLS and DTLS parsers of this version do not decrypt and register no such table, so a stack
  without a decrypting protocol reports one warning per non-empty list: the ports have no effect until such a protocol is
  registered (before SOME/IP, as the standard stack does).
- **TCP framing.** A message is framed out of the byte stream by its length field. A header that does not look like a header
  (version other than 1, an undefined message type, a length below 8) means the stream lost its position: with
  `someip.tcp_resync` on, the next plausible header is searched and the skipped bytes are reported as `tcp.error.resync`. A
  message above `someip.tcp_max_message_size` is never framed; when the stream buffer (the limit plus 1 MiB) is exceeded, the
  drop is reported as `tcp.error.reassembly_limit`.
- **Message tree.** Every message is a `someip` container with `messageid`, `serviceid`, `methodid`, `length` (counts from
  the client id), `clientid`, `sessionid`, `version`, `ifversion`, `msgtype` (with children `base`, `ack`, `tp`) and
  `returncode`; `service.name`, `method.name` and `client.name` appear when the description knows the id (they show the
  `ui_name`); diagnostics are `someip.error.length_min`, `.truncated`, `.version`, `.unit_limit`, `.incomplete` and `.trailing`.
  A message whose service the description defines also holds the service node (`ClimateControl.v2`) and, below it, the method
  node (`ClimateControl.v2.SetTemperature`), whatever the message type, the payload length, the decode setting, the
  segmentation, or whether another parser took the payload, so `ClimateControl.v2.SetTemperature && someip.msgtype == 0x80`
  selects the responses of a method. The method node holds the payload bytes and, below it, the lazily decoded values (see
  Payload decoding). `someip.payload` is always the raw bytes of a payload that no other parser took, also for a decoded one, so
  `len(someip.payload) > 100` selects by size with or without a description.
- **Payload decoding.** With a service description, the payload of a message of a configured member is decoded into typed
  fields, lazily: the parse records which member direction the packet holds and attaches the method node as a container
  that is filled when somebody reads it (a view, a filter on a parameter, an export). The request list decodes a request
  and a request without return, the response list a response and a notification; an error message and a member the
  description does not define show the bytes raw. Categories: the thirteen base types (unsigned and signed integers of
  8 to 64 bits and floating-point values as numbers, `bool`, `string`, `uuid`), arrays (variable with a length field, fixed
  without one, nested), structures (with or without a length field), TLV structures and lists (all eight wire types, tag
  length replaces an inline length, unknown ids shown raw), unions (length, selector, alternative), bitfields (raw word plus
  one boolean per named bit) and aliases. A value with a lookup table shows `Name (value)`; array elements show `[i] value`.
  Strings decode as UTF-8, ASCII, UTF-16BE or UTF-16LE; one terminating NUL and a byte order mark are removed, and what is
  not valid text or is a control character is written as `\xNN` (a lone surrogate as `\uNNNN`), so a payload cannot rewrite
  a terminal. A `float` is stored widened to a `double` and shown in the shortest text of the single.
  What does not fit never throws: a value that does not fit its range shows one `someip.payload.error` (the first shortage
  explains the rest), bytes behind the last parameter are `someip.payload.unparsed`, a dangling type reference shows a
  marker and consumes nothing, and an array outside `min_elements`/`max_elements` is flagged and still shown. The limits
  `someip.max_type_depth` and `someip.max_payload_nodes` stop a decode with a diagnostic that names the setting and its value.
  `someip.decode_payload` off keeps the raw bytes and adds `someip.payload.disabled`. `someip.show_type_metadata` on adds
  `someip.payload.type`, `.length` (every length field read), `.array.count`, `.padding`, `.tlv.tag` (with `.wire_type`,
  `.data_id`, `.reserved`) and `.union.type`; no value changes.
  **Index.** The service node and the method node each have a group that is exact: a packet is in it exactly when it holds a
  message of that service version or member, so a filter on a service or a method is answered without decoding any payload.
  Every member direction is one more index group (`someip.payload.<service>.v<major>.<member>[.req|.resp]`, never carrying the
  field prefix) shared by all its parameters. A packet is in the group when it holds a message of that member in that
  direction, so a filter on any parameter narrows the candidates without decoding payloads. The group is a superset by design: a TLV member that is absent,
  or a payload that was cut off before its parameters, leaves the packet in the group without the field, and the filter
  verifies each candidate on the tree. The generic payload fields (`someip.payload.*`) are one group, `someip.payload`.
- **End-to-end protection.** A member with `e2e_profile` shows the protection header as `someip.e2e` (the container text names the
  profile, "Profile 04") before its parameters, which decode from the bytes behind the header exactly as for an unprotected member.
  Profiles 04 and 44 have a 12-byte header (`length`, `counter` 16 bit, `data_id`, `crc` 32 bit), profile 08 a 16-byte header (`crc`,
  `length`, `counter`, `data_id`, all 32 bit). With `someip.verify_e2e_crc` on, a profile 04 or 44 message adds
  `someip.e2e.crc_calculated` and `someip.e2e.crc_valid`: CRC-32/AUTOSAR (polynomial 0xF4ACFB13, reflected, initial value and final
  XOR all ones) over the SOME/IP header bytes from the client id to the return code with the TP flag cleared, then the first eight
  bytes of the E2E header, then the payload behind the header. A message that was segmented verifies on the segment that completes
  it and gives the same verdict as its unsegmented form; the gap-free prefix of an incomplete message verifies nothing. Profile 08
  shows its stored checksum and no verdict: no construction is pinned for it. A payload shorter than the header shows its bytes
  raw with `someip.e2e.error` ("6 of 12"), and an unknown profile shows "(unknown profile 99)", the raw bytes and one
  `someip.e2e.error`. Verification costs one pass over each protected payload and, for a verified message, one small delegate
  allocation at parse time (the checksum needs header bytes that the lazy container cannot reach later); it is off by default.
- **Method timing (opt-in).** With `someip.method_timing` on, a response shows a lazy `someip.timing` container behind its
  payload with `request_in` (the packet id of the request it answers) and `delay` (seconds), or `request_missing` when no
  request was pending. A request shows nothing: it is final before its response exists, and a request whose tree changed when
  the response was parsed could not be re-parsed identically. A response takes the **latest** pending request with the same
  service, method, client, session, interface version and capture interface (`someip.method_timing_ignore_interface` pairs across
  interfaces); older requests of the same key stay pending for the next response. A segmented request registers with its first
  segment, a segmented response pairs on the segment that completes it. Notifications, requests without return, errors and
  acknowledgements take no part, and an error does not consume its request. At `someip.timing_max_pending` pending requests the
  oldest one is forgotten; the response that arrives later shows `request_missing` and `someip.timing.error`, which names the
  setting. A response that is stamped before its request shows a delay of zero. The pairing happens in the first parse of a packet
  and is stored with the response, so a re-parse and a lazy expansion show what the first parse showed; each stack pairs for itself.
- **Service discovery.** A message with id `0xFFFF8100` shows a lazy `someip_sd` container ("SOME/IP-SD (unicast)" or "(multicast)") that
  is expanded when it is opened. It holds `flags` (with `reboot`, `unicast`, `initial_events`), `reserved`, `entries_length`, an
  `entries` array and an `options` array. Each `entry` shows its type ("Offer Service", "Stop Offer Service", "Subscribe Eventgroup",
  "Subscribe Eventgroup Ack"/"Nack", "Find Service"), the option runs it references (`options_referenced`, for example `0-2,5-6`), the
  service, instance, major version and TTL with the annotations `(ANY)`, `(other service)`, `(infinite)` and `(unknown)`, the service
  and eventgroup names from the description, and the last word as minor version or as reserved/initial events/counter/eventgroup. Each
  `option` shows IPv4/IPv6 endpoints (address, protocol, port), configuration items (`config.item` with `key` and `value`), load
  balancing (priority, weight), or raw `data` for an option it does not know. Anomalies are named: `someip_sd.error.truncated`,
  `entries_malformed`, `entries_empty`, `entry_unknown`, `offer_without_endpoint`, `subscribe_after_stop`, `options_truncated`,
  `options_bytes_left`, `option_unknown`, `option_wrong_length`, `l4_unsupported`, `config_malformed` and `too_many_options` (more
  than 271 options; the rest is shown raw). The packet info lists the kinds of the entries ("SOME/IP-SD [Find] [Offer] (multicast)"). A
  filter on a diagnostic is answered from the index without opening the message.
- **Port learning (opt-in).** With `someip.learn_sd_ports` on, the UDP endpoints that an offer or a subscribe announces in the options  it references are learned, and datagrams on those ports decode as SOME/IP from the announcing packet on (the message shows  `someip.learned_port: true`). Nothing before the announcement changes, and a re-parse of an earlier packet shows what its first  parse showed. Learned: UDP endpoints of entries with a time to live above zero whose service id is not `0xFFFE`. Not learned:  TCP endpoints, ports on `someip.learn_sd_ports_ignore`, options no entry references. A port named in an option is not proof that  SOME/IP runs there; unrelated UDP traffic on it would be decoded as SOME/IP. Each stack learns for itself; a new session starts  empty.
- **Segmented messages (SOME/IP-TP).** A message with the TP flag is collected from its segments across packets. Each segment shows
  `someip.tp` with `offset` (bytes), `more` and `reserved`. The segment that completes the message shows the reassembled payload
  as `someip.payload` (or the discovery container), `someip.tp.segments` (the packet ids of all contributing segments in offset
  order), `segment_count`, `reassembled_length` and `partial`; earlier segments show only their TP word and never change
  afterwards, so a message that completes later is not announced on its first segment. Segments may arrive in any order. When the
  last segment arrives while a hole remains, the bytes without a gap are shown as the payload once (`partial` true,
  `someip.tp.region` for each covered range behind the hole, `someip.tp.error.gap` naming the missing ranges); the session stays
  open, and a late segment that closes the hole delivers the complete message. A second segment at offset 0 restarts the transfer
  (`someip.tp.error.reset`), overlapping bytes keep the first arrival (`someip.tp.error.overlap` when the content differs), a
  segment that is not the last and not a non-zero multiple of 16 bytes is dropped (`someip.tp.error.malformed`), and the limits
  `someip.tp_max_sessions`, `someip.tp_max_message_size` and `someip.tp_max_buffered_bytes` evict or refuse with a diagnostic that
  names the setting and its value (`someip.tp.error.evicted`, `someip.tp.error.size_limit`). Segments are keyed by service, method,
  client, session, base message type and interface version.

## Adding Custom Protocols

Custom dissectors implement `IProtocol` with a public `Parse` method and declare their fields with attributes from
`NetworkInspector.Protocols.Attributes`; the source generator bundled with Core writes the registration code.

- The first parse of each packet id is ordered and single-threaded; a re-parse of an already parsed id may run
  concurrently.
- Stateful protocols store per-packet state in `EffectStore<T>`, keyed with `MutField.TryGetEffectLayerKey`, so a
  re-parse sees the same state as the first pass.

Step-by-step guide: [`PROTOCOL_GUIDE.md`](PROTOCOL_GUIDE.md). Generator reference:
[`NetworkInspector.Generators`](../NetworkInspector.Generators/README.md).

## Limits And Thread-Safety Notes

- Register protocols during stack construction only; the built stack is immutable.
- Captures are untrusted input: dissectors bound lengths and counts, report malformed data as `packet.error`, and
  cap tracked TCP and UDP streams (`tcp.max_tracked_streams`, `udp.max_tracked_streams`), decompressed HTTP bodies and WebSocket payloads (`http.max_decompressed_bytes`, `ws.max_decompressed_bytes`, secure by default at 64 MiB), the derived bytes one packet keeps (`http.max_derived_bytes_per_packet`, `ws.max_derived_bytes_per_packet`, `someip.tp_max_derived_bytes_per_packet`) and reassembly buffers.
- Connection keys hash with a process-seeded hash, so crafted captures cannot force hash-table collisions.

## Learn More

- [Protocol guide](PROTOCOL_GUIDE.md): writing dissectors, fields, tables, settings, effects, reassembly
- [Core](../NetworkInspector.Core/README.md) · [Filter](../NetworkInspector.Filter/README.md) ·
  [FrameBuilder](../NetworkInspector.FrameBuilder/README.md) · [Root overview](../README.md)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector) ·
  [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)

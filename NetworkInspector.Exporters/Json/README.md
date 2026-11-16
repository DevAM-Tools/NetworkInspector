<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# JSON Exporter

[![NuGet][nuget-badge]][nuget]

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NetworkInspector.Exporters
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters

`JsonExporter` writes the full field tree of every parsed packet as JSON, for scripts, log pipelines, and any
tool that reads JSON. It is a packet exporter (`IPacketListener`): packets must be parsed with a field tree
(`FieldTreeMode.Build`, the default) for the tree styles; the flat shapes below also accept a tree-free parse.

## Highlights

- **Three styles:** compact for volume, pretty for people, a flat array for downstream tools.
- **Compact by default.** Repeated field metadata is deduplicated, which keeps large exports small.
- **SIMD string escaping** on the hot path.
- **Valid JSON always.** Every style writes one JSON array; an empty export is an empty array.

## Output Styles

| Style | Best For |
| --- | --- |
| `JsonExportFormat.Compact` (default) | Smallest output, machine ingestion |
| `JsonExportFormat.Pretty` | Human inspection and debugging |
| `JsonExportFormat.Array` | Flat full-key records for downstream tooling |

The field metadata key `type` / `TY` holds the numeric `FieldType` value in **Compact** and the enum member
name (for example `"MacAddress"`, `"U64"`) in **Pretty** and **Array**.

**Frame bytes (opt-in).** The packet keys are `id` / `ID`, `timestamp` / `TS`, `info` / `IN`, and the fields. With
`ExportContent.FrameBytes = true` (CLI `json:frames=true`) every packet also has the key `frame_bytes`: the frame as standard
Base64 (RFC 4648 §4 alphabet, with `=` padding; `""` for an empty frame), after `info` (after `IN` and `SF` in the compact style). The key is
spelled `frame_bytes` in every style and shape, also in the compact style whose other packet keys are abbreviated, so a reader has one name for it. It is
off by default: the field tree already holds the parsed bytes, so the copy of every frame would roughly double a default export.
The compact style does not elide a frame that equals the previous one.

**Additional buffers (opt-in).** Protocols attach bytes to a packet that the field tree shows only as parsed values: the datagram an
IPv4 or IPv6 fragment completes, the PDU a TCP stream completes, a decoded (dechunked or inflated) HTTP body, an unmasked WebSocket
payload (the packet's additional buffers, index 1 to 255; index 0 is the frame). With `ExportContent.Buffers = true` (CLI
`json:buffers=true`) every packet that has such a buffer also has the key `buffers`, after `frame_bytes`: an array of records
`{"index":1,"bytes":"<Base64>"}` in ascending index order, the bytes as standard Base64 with padding (`""` for a bound empty
buffer). A packet without additional buffers has no `buffers` key. Like `frame_bytes`, the key and the record keys are spelled the
same in every style and shape (the compact style does not abbreviate them and does not elide a repeated buffer), and the field
keys are the same with the member on and off. It is off by default: a buffer can hold decoded content that the frame does not show
in clear and can be much larger than the frames.

## Export Shapes

By default the exporter writes the field tree of every packet (the styles above). `WithContent(ExportContent)` chooses a flat
shape instead, which needs no field tree: the exporter states its parse need as `IParseDemandSource.ParseDemand`, so a
tree-free parse delivers only the fields that are written.

| Shape | Packet object |
| --- | --- |
| `SelectedFields` | `{"id":5,"timestamp":…[,"info":…],"fields":{"ip.src":["10.0.0.1","192.168.0.1"],"udp.dstport":53}}` |
| `AllFields` | `{"id":5,"timestamp":…[,"info":…],"fields":[{"name":"ip.src","occurrence":0,"value":"10.0.0.1"},…]}` |

- The packet keys `id`, `timestamp`, and `info` are the same as in the tree styles; `info` is written when the content asks for
  it (`Info`, default on, in every shape and style) and the packet has one.
- The flat shapes use full key names in every style (`Pretty` indents with two spaces; `Compact` and `Array` write one line per
  packet): the compact style's short keys and deduplication exist for repeated tree records, which flat records do not have.
- A field selected with `Occurrences.All` is always an array (also with one value); `First` and `Last` give a scalar. A key is
  omitted when the packet lacks the field.
- With custom texts on, an occurrence is an object `{"value":…,"custom_representation":"…","custom_text":"…"}` (a text key is
  omitted when the value has none); with both off it is the plain value.
- **Texts.** In every shape and style (the tree styles included), `custom_text` and `custom_representation` (`CT` and `CR` in the
  compact style) exist only when the export asks for them and the dissector set one: a missing key means absent, `""` means an
  empty text, and with `CustomText = false` or `CustomRepresentation = false` the key is never written (and the text is not read).
  A reader rebuilds the display line with the rule of the [exporter guide](../EXPORTER_GUIDE.md#123-rebuilding-the-display-line):
  the custom text alone when the key exists; else the UI name for a node without a `value`; else UI name, `: `, and the custom
  representation or the default text of the value.
  Addresses are written in their readable notation (`192.168.1.1`, `2001:DB8::1`), which is already their default text; there is no
  integer form to convert back.
- Tree packets are accepted for every shape and give the same output. The flat shapes write `frame_bytes` after `info`, in the
  same form as the tree styles, when `FrameBytes` is set, and `buffers` after that when `Buffers` is set and the packet has an
  additional buffer.

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Exporters.Json;

// packets: parsed packets with a field tree, see the end-to-end example in the Exporters README.
static void WriteJson(IEnumerable<Packet> packets)
{
    using JsonExporter exporter = JsonExporter.CreateBuilder()
        .ToFile("packets.json")
        .WithFormat(JsonExportFormat.Pretty)
        .Build();

    foreach (Packet packet in packets)
    {
        if (!exporter.OnPacket(packet))
        {
            break;
        }
    }

    exporter.OnFinish();
}
```

## Builder Options

| Method | Purpose | Default |
| --- | --- | --- |
| `ToFile` / `ToStream` / `ToStdout` / `ToTarget` | Output target (`ToTarget` takes a host `IExportTarget` and writes the one entry `Main`) | required |
| `WithUiName` / `WithDescription` | User-facing metadata (not written to the output) | — |
| `WithFormat(format)` | JSON style | `Compact` |
| `WithFlushPerPacket(bool)` | Flush after each packet for live consumers | `false` |
| `WithContent(ExportContent)` | Shape (tree, selected fields, all fields), selected fields, info, frame bytes (default off), additional buffers (default off), and texts | `DefaultContent` (tree) |
| `WithTargetPacketCount(int)` | Stop after N packets (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()` and `OnFinish()` from one thread.
- The output is one JSON array, not newline-delimited JSON.
- The tree format skips packets parsed with `FieldTreeMode.Skip`: the packet is counted as skipped and `ItemSkipped` carries
  `ExportErrorKind.SerializationError` and the message `Skip packets cannot be exported (HasFieldTree is false).` (the same kind
  and message as every exporter with a hierarchy shape, and no exception). A flat shape skips a packet that has neither a field
  tree nor fields staged by the exporter's observer, with a message that names the cause.
- A packet that cannot be serialized or written is skipped (`ItemSkipped`), never thrown out of `OnPacket()`. In the
  compact format the next packet is then written in full, because the skipped packet is not in the file and cannot be
  the "previous" one. `WithFormat` rejects values that are not a `JsonExportFormat` member.
- The compact field tree refuses a packet of another protocol stack than its first written packet's (`StackMismatch`, fatal
  whatever the error tolerance; the packets written before stay a valid document). Field ids are dense per stack, so after a
  session restart the compact document would name the fields of the second stack with the names of the first. Pretty, Array, and
  the flat shapes write the name of every field and are not affected; a field selection is bound to its stack in every style.
- The output contains every decoded field, including payload-derived text. Treat it as sensitive.

## Links

- [Exporters overview](../README.md)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)

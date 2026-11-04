<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# Text Exporter

[![NuGet][nuget-badge]][nuget]

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NetworkInspector.Exporters
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters

`TextExporter` prints parsed packets as an indented protocol tree, like the packet detail view of common analyzers. Use it
to look at parse results without a viewer, in CI artifacts, and in triage logs. It is a packet exporter
(`IPacketListener`): packets must carry a field tree (`FieldTreeMode.Build`, the default) for the tree format; the flat shapes below
also accept a tree-free parse.

## Highlights

- **Readable protocol trees** with the same display texts as the dissectors (`Ethernet II, Src: …`).
- **One line rule.** Every line is the display line of its field, the same text as `Field.ToString()` (see Display Lines).
- **Three detail levels.**
- **Bounded output.** Long strings and byte values are truncated (256 characters by default).
- **Stdout support** for live inspection in a terminal.

## Display Lines

A line is the display line of its field, by the rule of the [exporter guide](../EXPORTER_GUIDE.md#123-rebuilding-the-display-line):

| Case | Line |
| --- | --- |
| The field has a custom text (also an empty one) | The custom text alone: a dissector that wants the label writes it into the text |
| The field has no value (a group, for example) | The UI name |
| Any other field | The UI name, `: `, and the custom representation, or the default text of the value (`Bytes` as upper-case hex, `0A FF 00`) |

- With `WithContent` and `CustomText = false`, the custom text is not used (the line is built from the other parts and the
  dissectors are not asked for it in a tree-free parse); with `CustomRepresentation = false` the default text of the value is
  used. A reader that rebuilds lines from a data export with the same switches gets these lines.
- Addresses are written in their readable notation (`192.168.1.1`, `2001:DB8::1`, `00:1A:2B:3C:4D:5E`): the default text of the value.
  The binary formats store them as integers instead (see the exporter guide).
- The detail level only selects which fields get a line; it never changes a line. A protocol layer with no custom text shows the
  hex of its bytes after its label, like any bytes field.
- The decision of the table above is the one that `Field.ToString()` uses. The export writes the decided parts itself, so the
  text limit counts only the custom text or the value part (never the label) and only capture text is escaped (see Limits And
  Thread-Safety Notes). With the default limit (256 characters) and printable text a line is exactly `Field.ToString()`; the Text
  tests compare every line with it.

## Frame Bytes

The frame bytes are off by default: the field tree already holds the parsed bytes of every layer. With
`ExportContent.FrameBytes = true` (CLI `text:frames=true`) every packet ends with a hex dump of its frame, after its lines and
before the blank separator line, in every shape and at every detail level:

```text
Frame bytes (20):
0000  00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F
0010  10 11 12 13
```

- The title line is `Frame bytes (n):` with the length in bytes; an empty frame writes the title line only.
- A dump line holds 16 bytes: the offset (at least four upper-case hex digits, more beyond `0xFFFF`), two spaces, and the bytes as
  upper-case hex pairs separated by one space, with no trailing space. There is no ASCII column, so no byte of the capture reaches
  the output as a character that a terminal could interpret.
- The dump is the packet's own bytes, not a value of the field tree: `WithMaxTextLength` does not cut it.

## Additional Buffers

Protocols attach bytes to a packet that the field tree shows only as parsed values: the datagram an IPv4 or IPv6 fragment completes,
the PDU a TCP stream completes, a decoded (dechunked or inflated) HTTP body, an unmasked WebSocket payload (the packet's additional
buffers, index 1 to 255; index 0 is the frame). With `ExportContent.Buffers = true` (CLI `text:buffers=true`) every packet that has
such a buffer writes, after the frame dump (when that is on) and before the blank separator line, one block per buffer in ascending
index order, in the line format of the frame dump:

```text
Buffer 1 (32):
0000  9C 40 9C 41 00 20 00 00 01 02 03 04 05 06 07 08
0010  09 0A 0B 0C 0D 0E 0F 10 11 12 13 14 15 16 17 18
```

- The title line is `Buffer i (n):` with the buffer index and the length in bytes; an empty buffer writes the title line only. A packet
  without additional buffers writes no block.
- Like the frame dump it is never cut by `WithMaxTextLength` and has no ASCII column, so no byte of the capture reaches the output
  as a character. The lines above it are the same with the member on and off.
- Off by default: a buffer can hold decoded content that the frame does not show in clear and can be much larger than the frames.

## Detail Levels

| Level | Output |
| --- | --- |
| `TextDetailLevel.Summary` | One line per top-level container (protocol layers and top-level groups; nested containers are not listed), no value fields |
| `TextDetailLevel.Standard` (default) | Protocol layers and all value fields, without raw byte buffers |
| `TextDetailLevel.Full` | Every field; `Bytes` values as upper-case hex |

A protocol layer (`frame`, `eth`, `ip`, `udp`, …) carries its raw bytes as a `Bytes` value and has children. It counts as a
container: `Summary` and `Standard` list it, and `Standard` goes on with its value fields. Only a `Bytes` field without children
(a payload, for example) is a raw byte buffer that `Standard` leaves out.

Excerpt of `Standard` output for one UDP packet (a layer's line is its custom text; a field shows its UI name and value unless its
dissector put its own text there):

```text
Packet 1  [1970-01-01T00:00:00.000000000Z]
Ethernet II, Src: 00:11:22:33:44:55, Dst: AA:BB:CC:DD:EE:FF
Internet Protocol Version 4, Src: 192.168.1.1, Dst: 192.168.1.2
  Source Address: 192.168.1.1
  Destination Address: 192.168.1.2
  Time To Live: 64
User Datagram Protocol, Src Port: 12345, Dst Port: 53
  Source Port: 12345
  Destination Port: 53
  Length: 40
```

## Export Shapes

By default the exporter writes the field tree of every packet at the configured detail level (see above).
`WithContent(ExportContent)` chooses a flat shape instead, which needs no field tree: the exporter states its parse need as
`IParseDemandSource.ParseDemand`, so a tree-free parse delivers only the fields that are written.

- **Selected fields:** one line per packet,
  `Packet 5  [timestamp]  ip.src=Source Address: 10.0.0.1|Source Address: 192.168.0.1  udp.dstport=Destination Port: 53`:
  the header, then for each selected field the packet has two spaces and `name=line`, several occurrences joined by `|`, where
  `line` is the field's display line (see Display Lines). The detail level does not apply (you named the fields, so a bytes
  field is written as hex too).
- **All fields:** the header line, then one line `name[occurrence]=line` per value in production order, then a blank line.
- The `line` of a flat shape is the same text the tree shows for the field (`Field.ToString()`); the custom text member and the
  custom representation member work as in the tree. The custom text or the value part of a line is cut at the maximum text length;
  the label never is.
- Tree packets are accepted for every shape and give the same lines. The flat shapes write the frame dump (see Frame Bytes) after
  their lines like the tree does, when `FrameBytes` is set, and the buffer dumps after that when `Buffers` is set.

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Exporters.Text;

// packets: parsed packets with a field tree, see the end-to-end example in the Exporters README.
static void WriteText(IEnumerable<Packet> packets)
{
    using TextExporter exporter = TextExporter.CreateBuilder()
        .ToStdout()
        .WithDetailLevel(TextDetailLevel.Full)
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
| `ToFile` / `ToStream` / `ToStdout` | Output target | required |
| `WithUiName` / `WithDescription` | User-facing metadata | — |
| `WithDetailLevel(level)` | Summary, Standard, or Full | `Standard` |
| `WithMaxTextLength(int)` | Truncate string and byte values (`0` = no limit) | 256 |
| `WithContent(ExportContent)` | Shape (tree, selected fields, all fields), selected fields, the frame bytes and the additional buffers (both default off), and the custom text and representation members. `Info` has no effect: the tree shows the info as the `packet.info` field line and the flat shapes write none | `DefaultContent` (tree) |
| `WithTargetPacketCount(int)` | Stop after N packets (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()` and `OnFinish()` from one thread.
- The packet header number is the running count of written packets, not `Packet.Id`.
- `Full` is also truncated by `WithMaxTextLength`; pass `0` for complete values. Full detail on byte-heavy
  traffic can produce very large files.
- A value longer than `WithMaxTextLength` is cut after that many characters (never inside a character or a surrogate
  pair) and `…` is appended; a value that fits is written without it. The `…` is not counted in the limit, and neither is the
  label of a line (the limit cuts the custom text or the value part). Byte values print `AA BB CC` (three characters per byte
  except the last), so they show as many whole bytes as fit.
- Control characters in capture text are written escaped (`\n`, `\r`, `\t`, else `\x1b`; the C1 controls U+0080–U+009F as
  `\u009b`), and a backslash is written as `\\`, so a value never ends a line, cannot send an escape sequence to the terminal of
  `ToStdout` (U+009B is a control sequence introducer on terminals that honor C1), and a literal `\n` in a value reads differently
  from an escaped line feed. An escape counts with its escaped width toward the maximum text length. Printable text, including
  non-ASCII, is unchanged; Unicode bidirectional controls are written as they are. In the selected-fields line the `|` between
  occurrences and the two spaces between parts are not escaped inside values: the parts are for reading, not for parsing.
- In `Strict` mode a packet that cannot be written counts as skipped and as an error, like in the other exporters.
- The tree format skips packets parsed with `FieldTreeMode.Skip`: the packet is counted as skipped and `ItemSkipped` carries
  `ExportErrorKind.SerializationError` and the message `Skip packets cannot be exported (HasFieldTree is false).` (the same kind
  and message as every exporter with a hierarchy shape, and no exception). A flat shape skips a packet that has neither a field
  tree nor fields staged by the exporter's observer, with a message that names the cause.

## Links

- [Exporters overview](../README.md)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)

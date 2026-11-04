<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# CSV Exporter

[![NuGet][nuget-badge]][nuget]

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NetworkInspector.Exporters
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters

`CsvExporter` writes parsed packets as CSV, ready for Excel, LibreOffice, pandas, or any SQL import. It is a packet exporter
(`IPacketListener`). By default it writes one row per packet; with `WithContent(ExportContent)` it writes the fields of a
packet in one of three layouts (see Export Shapes). It states its parse need as `IParseDemandSource.ParseDemand`: packet rows
and the field layouts need no field tree (a tree-free parse is enough); only the hierarchy layout demands one.

## Highlights

- **Pick your columns:** packet number, time, info, frame length, plus any protocol fields by name.
- **Wide, long, or tree:** one column per selected field, one row per field occurrence, or one row per tree node.
- **Repeated fields are kept.** A field that occurs twice in a packet (an address in a tunnel) is written into one cell,
  joined by a separator, or as separate rows in the long layout.
- **Correct quoting.** Values that contain the delimiter, a quote, CR, or LF are quoted and escaped.
- **Safe to open in a spreadsheet.** Text cells that start with `=`, `+`, `-`, `@`, TAB, or CR get a leading
  apostrophe (CSV injection protection, on by default, see below).
- **Any ASCII delimiter except letters, digits, and `-+.:`** (those occur in the unquoted number and timestamp cells), so TSV
  and semicolon-separated files for European spreadsheet locales work too.
- **Spreadsheet friendly by default:** UTF-8 BOM, header row, CRLF line endings.

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Exporters.Csv;

// packets: parsed packets, see the end-to-end example in the Exporters README.
static void WriteCsv(IEnumerable<Packet> packets)
{
    using CsvExporter exporter = CsvExporter.CreateBuilder()
        .ToFile("packets.csv")
        .Build(); // no columns configured: No., Time, Info, Length

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

## Export Shapes

| Content | Layout | Row |
| --- | --- | --- |
| none (default) | packet rows | the configured packet columns (default `No.`, `Time`, `Info`, `Length`) |
| `SelectedFields` | **wide** | packet columns + one column per selected field (+ `<name>.repr` / `<name>.text` when the content asks for texts); several occurrences are joined in one cell with the occurrence separator (default `\|`) |
| `AllFields` | **long** | packet prefix + `field,occurrence,value[,custom_repr][,custom_text]`, one row per field occurrence |
| `Hierarchy` | **long + tree** | packet prefix + `node_id,parent_node_id,path,field,value[,custom_repr][,custom_text]`, one row per field node in pre-order |

The packet prefix of the field layouts is the configured packet columns, or `No.`, `Time` when none were configured (the
default `Info` and `Length` columns would repeat on every field row). Start from `CsvExporter.ContentTemplate` (no info string,
no frame bytes, no buffers: use `WithColumn(CsvColumnKind.Info)`, `WithColumn(CsvColumnKind.FrameBytes)`, and
`WithColumn(CsvColumnKind.Buffers)` for those; texts on) and change the shape. The template is not
the default: without `WithContent` the exporter writes packet rows, which no `ExportContent` describes.

```csharp
ExportContent content = CsvExporter.ContentTemplate with
{
    Shape = ExportShape.SelectedFields,
    Fields = selection,        // FieldSelection.TryCreate(stack, [new FieldRequest("ip.src"), new FieldRequest("udp.dstport")], Occurrences.All, ...)
    CustomText = false,
    CustomRepresentation = false,
};
CsvExporter exporter = CsvExporter.CreateBuilder().ToFile("addresses.csv").WithContent(content).Build();
ParseDemand demand = exporter.ParseDemand;   // combine with other demands in a ParsePlan, then parse with plan.Options
```

Example for a tunnel packet (IPv4 in IPv4) with `ip.src` (all occurrences) and `udp.dstport`:

```text
No.,Time,ip.src,udp.dstport
5,2026-01-01T00:00:00.000000000Z,10.0.0.1|192.168.0.1,53
```

- The wide layout writes a field that a packet lacks as an empty cell, so every row has the header's column count.
- `First` and `Last` (per field, through `FieldRequest`) write a single value; `All` joins every occurrence.
- **Texts.** A custom representation or custom text cell (`custom_repr`, `custom_text`, `<name>.repr`, `<name>.text`) is an empty
  unquoted cell where the dissector set none and the quoted empty cell `""` where it set an empty text; a default text is never
  written into it. A reader rebuilds the display line with the rule of the
  [exporter guide](../EXPORTER_GUIDE.md#123-rebuilding-the-display-line): the custom text alone when it is present; else the UI
  name for a node without a value; else UI name, `: `, and the representation or the default text of the value. The file names a
  field by its machine name (`field`, the header of a wide column); the UI name is the one registered for that name in the stack
  (a DuckDB export carries it in `catalog.ui_name`). A text is "present" when its cell is not an empty unquoted cell.
  Addresses are written in their readable notation (`192.168.1.1`, `2001:DB8::1`, `00:1A:2B:3C:4D:5E`), which is already their default
  text; unlike in the binary formats there is no integer form to convert back.
  - **Limit of the wide layout:** a cell that joins several occurrences cannot tell an absent text from an empty one (both add
    nothing between the separators, so the texts of `(empty, absent)` are the cell `\|`). One occurrence per cell can: `""` versus
    nothing.
  - **Readers that unquote** (spreadsheets, most data-frame readers) show `""` and an empty cell alike. Read the file with a reader
    that keeps the quoting to keep the difference, for example the Python `csv` module from Python 3.12 with
    `quoting=csv.QUOTE_NOTNULL` (`None` for an unquoted empty field, `''` for `""`).
- A tree-free parse and a tree packet give the same wide rows. The long layout holds the same rows for both, in the order
  the feed produces them (a parse delivers the packet-level `packet.info` field last, the tree holds it before the frame).
- The hierarchy layout needs the field tree; a packet without one is skipped and counted: `ItemSkipped` carries
  `ExportErrorKind.SerializationError` and the message `Skip packets cannot be exported (HasFieldTree is false).` (the same kind
  and message as every exporter with a hierarchy shape).
- Packet rows are written for a packet parsed without a field tree, with the same bytes as for a tree packet.
- The long and hierarchy layouts write the field names of the stack of each packet, so an export that spans a session restart
  (a new stack with other field ids) labels every value with the right name.

### Frame Bytes

The frame bytes are off by default: the field tree already holds the parsed bytes, so a copy of every frame is written only on
request. CSV keeps packet-level data in columns, so the frame is a column, `CsvColumnKind.FrameBytes`, not a content member
(`WithContent` refuses `FrameBytes = true` and names the column kind):

```csharp
using CsvExporter exporter = CsvExporter.CreateBuilder()
    .ToFile("frames.csv")
    .WithDefaultColumns()                      // No., Time, Info, Length
    .WithColumn(CsvColumnKind.FrameBytes)      // header "frame_bytes"
    .Build();
```

- **Cell:** the frame as upper-case hex digits without separators or prefix (two digits per byte, `0A1B2C`); an empty frame is an
  empty cell. The cell needs no quoting, because hex digits are neither the delimiter (the builder refuses a letter or a digit as
  the delimiter), a quote, nor a line break, and no formula neutralization, because no hex digit starts a formula.
- **Where:** packet rows and the wide layout (`SelectedFields`: packet columns, then one column per field). `Build` refuses the
  frame column next to `AllFields` and `Hierarchy` with an `ArgumentException` ("The FrameBytes column is only valid in packet
  rows and the selected-fields layout, not with the shape AllFields."): those layouts repeat the packet columns on every field row,
  so the frame would be written once per field.
- **CLI:** `ni export … -f csv:frames=true` adds the column last (after the default columns `No.`, `Time`, `Info`, `Length`, or
  after `No.` and `Time` in the wide layout, before the field columns); `csv:shape=all,frames=true` and `csv:shape=hierarchy,frames=true`
  are refused with the same message, naming the option `frames`.
- **Size:** a frame of n bytes adds 2n characters to its row (JSON writes about 4n/3 characters of Base64, the binary formats n bytes).

### Buffers

Protocols attach bytes to a packet that the field tree shows only as parsed values: the datagram an IPv4 or IPv6 fragment completes,
the PDU a TCP stream completes, a decoded (dechunked or inflated) HTTP body, an unmasked WebSocket payload. These are the packet's
additional buffers (index 1 to 255; index 0 is the frame). They are off by default and, like the frame, a column,
`CsvColumnKind.Buffers` (`WithContent` refuses `Buffers = true` and names the column kind):

```csharp
using CsvExporter exporter = CsvExporter.CreateBuilder()
    .ToFile("buffers.csv")
    .WithDefaultColumns()                      // No., Time, Info, Length
    .WithColumn(CsvColumnKind.Buffers)         // header "buffers"
    .Build();
```

- **Cell:** one record per additional buffer, `<index>:<HEX>`, joined by `;` in ascending index order, for example `1:0A0B;2:FF`.
  The digits are upper-case hex without separators; a bound empty buffer is a record without digits (`2:`); a packet without
  additional buffers has an empty cell. Nothing in the cell can start a formula or contain a quote or a line break, so it needs no
  neutralization. The semicolon, a common CSV delimiter, is one of the cell's own characters and would split it, so the exporter
  quotes the cell then (a digit, a letter, and the colon cannot be the delimiter).
- **Where:** packet rows and the wide layout, like the frame column; `Build` refuses it next to `AllFields` and `Hierarchy` with the
  same `ArgumentException`.
- **CLI:** `ni export … -f csv:buffers=true` adds the column after the frame column (after the default columns `No.`, `Time`,
  `Info`, `Length`, or after `No.` and `Time` in the wide layout, before the field columns); `csv:shape=all,buffers=true` and
  `csv:shape=hierarchy,buffers=true` are refused with the same message, naming the option `buffers`.
- **Size:** a buffer of n bytes adds 2n characters plus the index to its row. A buffer can hold decoded content that the frame does
  not show in clear and can be much larger than the frames.

### Write TSV Without A BOM

```csharp
using NetworkInspector.Exporters.Csv;

using CsvExporter exporter = CsvExporter.CreateBuilder()
    .ToFile("packets.tsv")
    .WithDelimiter('\t')
    .WithBom(false)
    .Build();
```

## Builder Options

| Method | Purpose | Default |
| --- | --- | --- |
| `ToFile` / `ToStream` / `ToStdout` | Output target | required |
| `WithUiName` / `WithDescription` | User-facing metadata | — |
| `WithBom(bool)` | UTF-8 byte order mark | `true` |
| `WithDelimiter(char)` | Column delimiter | `,` |
| `WithHeader(bool)` | Header row | `true` |
| `WithFormulaNeutralization(bool)` | Prefix text cells that start with a formula character with `'` | `true` |
| `WithColumn(kind, header?)` | Built-in column (`PacketNumber`, `Timestamp`, `Info`, `FrameLength`, `FrameBytes`, `Buffers`; the frame and buffers columns are for packet rows and the wide layout) | — |
| `WithDefaultColumns()` | `No.`, `Time`, `Info`, `Length` | used when none set (packet rows) |
| `WithContent(ExportContent)` | Field layout: shape, selected fields, texts (`Info`, `FrameBytes`, and `Buffers` must be off: all three are columns) | packet rows |
| `WithOccurrenceSeparator(string)` | Text between the occurrences of one field in a wide cell (no CR or LF) | `\|` |
| `WithTargetPacketCount(int)` | Stop after N packets (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

A separator that contains the delimiter or a quote is safe: the whole joined cell is quoted once (RFC 4180).

## Formula Neutralization (CSV Injection)

Capture text is untrusted: a DNS name or an info line can start with `=`, `+`, `-`, or `@`, and a spreadsheet reads
such a cell as a formula when the file is opened. By default the exporter writes a single quote in front of any text
cell whose first character is `=`, `+`, `-`, `@`, TAB, or CR (the quote goes inside the CSV quotes when the cell is
quoted). Example: the text `=SUM(A1,A2)` is written as `"'=SUM(A1,A2)"`.

- **What is changed:** the `Info` column, field values of type string, and the custom representation and text cells.
  Numbers, timestamps, addresses, byte values, and the header row are written as they are, so a negative number keeps
  its minus sign. A joined cell is judged as a whole, by its first byte, which is what a spreadsheet looks at.
- **Posture:** secure by default. Turn it off with `WithFormulaNeutralization(false)` when a program reads the file
  and must see the exact text (the added quote is data for it).
- **Residual risk:** with the option off, any text cell can run as a formula in a spreadsheet. With it on, a formula
  that starts after leading white space is not covered, and tools that do not treat the quote as a text marker show it.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()` and `OnFinish()` from one thread (and parse on that thread, so the observer and the
  commit stay in order per packet).
- The delimiter must be ASCII (≤ 0x7F) and must not be NUL, CR, LF, `"`, an ASCII letter or digit, or one of `-+.:`; the unquoted
  number, timestamp, and hex cells contain those characters, so such a delimiter would split them (`WithDelimiter` throws
  `ArgumentOutOfRangeException` and names the set). With `'` as the delimiter, a cell that gets the
  neutralizing apostrophe is quoted (`"'=1"`), so the added apostrophe cannot end the cell.
- `WithColumn` accepts the six defined `CsvColumnKind` values (`PacketNumber`, `Timestamp`, `Info`, `FrameLength`, `FrameBytes`,
  `Buffers`) and throws `ArgumentOutOfRangeException` for any other number.
- A packet whose rows could not be written because the output failed raises `ItemSkipped` with `ExportErrorKind.IoError`; any
  other failure of a packet (for example an info text that throws) is `SerializationError`.
- A flat layout skips a packet that has neither a field tree nor fields staged by this exporter's observer, with a message
  that names the cause.
- No file is created until the first packet arrives.
- Treat CSV output as sensitive when packet text can contain secrets.

## Links

- [Exporters overview](../README.md)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>
/// PBF protobuf field numbers for the standard and columnar block formats.
/// These correspond to the PBF wire protocol, format version <see cref="FormatVersion"/>. A file without
/// <see cref="HeaderShape"/> reads as the hierarchy shape; the flat shapes carry the shape code and the
/// <see cref="FieldOccurrence"/> position column.
/// Changing a value breaks every existing file, so these are format constants, not tuning knobs.
/// <para>
/// Field numbers are scoped to the message they appear in, so the same number is reused across messages (for example
/// 1 is the packet count in a block header, the packet ID in a packet, and the field ID in a field). Within a standard
/// block payload the number 5 is used twice: <see cref="BlockMaxTimestamp"/> (varint, wire type 0) and
/// <see cref="PacketField"/> (length-delimited, wire type 2), which carries each packet; a reader tells them apart
/// by wire type. In the columnar format a string column, the frame-bytes column or a delta column is a length-delimited
/// sub-message: string and frame-bytes columns repeat sub-field 1 once per row; delta-encoded integer columns use sub-field 1 for the
/// first value and sub-field 2 for each following difference; the custom-text and custom-representation columns use sub-field 1 for
/// one present text and sub-field 2 for a run of absent rows (<see cref="TextColumnPresent"/>).
/// The additional buffers of a block are one sub-message of three parallel columns (<see cref="ColumnarBuffers"/>).
/// </para>
/// </summary>
internal static class PbfFieldNumbers
{
    #region Block envelope and packet records

    // === Block envelope ===
    /// <summary>Block type discriminator.</summary>
    internal const int BlockType = 1;
    /// <summary>Block payload (length-delimited nested message).</summary>
    internal const int BlockPayload = 2;

    // === Packet message ===
    /// <summary>Packet ID (varint).</summary>
    internal const int PacketId = 1;
    /// <summary>Packet timestamp in nanos (sint64 zigzag).</summary>
    internal const int PacketTimestamp = 2;
    /// <summary>Packet info string.</summary>
    internal const int PacketInfo = 3;
    /// <summary>Same-as-previous flags for packet-level fields.</summary>
    internal const int PacketSameFlags = 4;
    /// <summary>Nested field message (repeated).</summary>
    internal const int PacketField = 5;
    /// <summary>
    /// Raw captured frame bytes of the packet (length-delimited bytes, at most once per packet record). Written only in the standard
    /// layout and only when <c>ExportContent.FrameBytes</c> is set; an empty frame is a zero-length field, so the field is present in
    /// every packet record of an export that asked for frames and in none of an export that did not. The columnar layout writes the
    /// same bytes as the column <see cref="ColumnarFrameBytes"/>. Sync: the PBF README § Packet records and the field scanner of the
    /// exporter tests (<c>PbfFileScanner</c>).
    /// </summary>
    internal const int PacketFrameBytes = 6;
    /// <summary>
    /// One additional packet buffer (length-delimited sub-message with <see cref="PacketBufferIndex"/> and
    /// <see cref="PacketBufferBytes"/>, repeated once per additional buffer in ascending index order). Written only in the standard
    /// layout and only when <c>ExportContent.Buffers</c> is set; a packet without additional buffers has no such field, and a
    /// bound empty buffer is a record with a zero-length <see cref="PacketBufferBytes"/>. The columnar layout writes the same data
    /// as the column <see cref="ColumnarBuffers"/>. Sync: the PBF README § Packet records and the field scanner of the exporter
    /// tests (<c>PbfFileScanner</c>).
    /// </summary>
    internal const int PacketBuffer = 7;
    /// <summary>Sub-field of <see cref="PacketBuffer"/>: the buffer index, 1 to 255 (varint).</summary>
    internal const int PacketBufferIndex = 1;
    /// <summary>Sub-field of <see cref="PacketBuffer"/>: the buffer bytes (length-delimited; empty for a bound empty buffer).</summary>
    internal const int PacketBufferBytes = 2;

    // === Field message ===
    /// <summary>Field ID (varint).</summary>
    internal const int FieldFieldId = 1;
    /// <summary>Field name (string, first occurrence only).</summary>
    internal const int FieldName = 2;
    /// <summary>Field UI name (string, first occurrence only).</summary>
    internal const int FieldUiName = 3;
    /// <summary>Field type (varint, first occurrence only).</summary>
    internal const int FieldType = 4;
    /// <summary>
    /// Field value; the wire type depends on the field type, so a reader decodes it by the field's type (field 4): Bool, U64
    /// varint; I64, Timestamp zigzag varint; F64 fixed64; String and Bytes length-delimited; IPv4 address fixed32 (the
    /// address as an unsigned integer); MAC address varint (the 48-bit address as an unsigned integer); EUI-64 fixed64; IPv6
    /// address and UUID fixed64 with the high 64 bits here and the low 64 bits in <see cref="FieldValueLow"/>. Every address
    /// integer is the big-endian reading of the address bytes, so the numeric order is the address order. Sync: EXPORTER_GUIDE.md
    /// § Rebuilding the display line (integer address forms) and the PBF README § Field values.
    /// </summary>
    internal const int FieldValue = 5;
    /// <summary>
    /// Field custom representation text (string). Presence: written only when the dissector set a representation, also an empty
    /// one (a zero-length field); no field means absent, or "same as the previous occurrence" when the same-flag says so.
    /// </summary>
    internal const int FieldValueText = 6;
    /// <summary>Field custom text (string); the same presence rule as <see cref="FieldValueText"/>.</summary>
    internal const int FieldCustomText = 7;
    /// <summary>Same-as-previous flags for field.</summary>
    internal const int FieldSameFlags = 8;
    /// <summary>Nested child field (repeated).</summary>
    internal const int FieldChild = 9;
    /// <summary>
    /// Field record: occurrence number of the value in its packet (varint). Written only in the flat shapes (header shape 1 or 2),
    /// where a packet holds a flat list of field records without <see cref="FieldChild"/>; absent means 0.
    /// </summary>
    internal const int FieldOccurrence = 10;
    /// <summary>
    /// Field record: the low 64 bits of an IPv6 address or a UUID (fixed64); the high 64 bits are <see cref="FieldValue"/>. Written
    /// only for those two field types, and omitted together with <see cref="FieldValue"/> when the value equals the previous
    /// occurrence's (the same-flag covers both halves).
    /// </summary>
    internal const int FieldValueLow = 11;

    #endregion

    #region Block header

    // === Block header ===
    /// <summary>Packet count in block.</summary>
    internal const int BlockPacketCount = 1;
    /// <summary>Min packet ID in block.</summary>
    internal const int BlockMinPacketId = 2;
    /// <summary>Max packet ID in block.</summary>
    internal const int BlockMaxPacketId = 3;
    /// <summary>Min timestamp in block.</summary>
    internal const int BlockMinTimestamp = 4;
    /// <summary>Max timestamp in block.</summary>
    internal const int BlockMaxTimestamp = 5;

    #endregion

    #region Block types and file framing

    // === Block types ===
    /// <summary>Standard (row-oriented) block.</summary>
    internal const int BlockTypeStandard = 1;
    /// <summary>Columnar block.</summary>
    internal const int BlockTypeColumnar = 2;

    // === File structure ===
    /// <summary>File header version.</summary>
    internal const int HeaderVersion = 1;
    /// <summary>File header creation timestamp.</summary>
    internal const int HeaderCreationTimestamp = 2;
    /// <summary>
    /// File header: export shape (varint; 0 = hierarchy, 1 = selected fields, 2 = all fields). Absent in the files of 0.19.0, which are
    /// always hierarchy. In the flat shapes the per-column position column (<see cref="ColumnNodeIds"/>) carries occurrence numbers.
    /// </summary>
    internal const int HeaderShape = 3;
    /// <summary>
    /// Format version written by this exporter. It is deliberately not raised for the shape code, the occurrence column, the
    /// presence-encoded texts, and the integer addresses, although a reader written for the files of 0.19.0 cannot read them:
    /// the owner decided that the number stays 1. A reader reads <see cref="HeaderShape"/> from the header before the first
    /// block, and a missing shape is the hierarchy shape.
    /// </summary>
    internal const int FormatVersion = 1;
    /// <summary>Trailer total packet count.</summary>
    internal const int TrailerPacketCount = 1;
    /// <summary>Trailer total block count.</summary>
    internal const int TrailerBlockCount = 2;
    /// <summary>Trailer field presence bitmap.</summary>
    internal const int TrailerFieldBitmap = 3;
    /// <summary>Trailer block index: one length-delimited entry per block, holding the two sub-fields below.</summary>
    internal const int TrailerBlockIndex = 4;
    /// <summary>Sub-field of a trailer block-index entry: minimum timestamp of the block (sint64).</summary>
    internal const int TrailerBlockIndexMinTimestamp = 1;
    /// <summary>Sub-field of a trailer block-index entry: maximum timestamp of the block (sint64).</summary>
    internal const int TrailerBlockIndexMaxTimestamp = 2;

    #endregion

    #region Columnar block payload

    // The top level of a columnar block payload shares the fields 1-5 above (BlockPacketCount, BlockMinPacketId, BlockMaxPacketId,
    // BlockMinTimestamp, BlockMaxTimestamp) with the standard header.

    /// <summary>Packet ID column: base value + per-row deltas (sint64 zigzag), length-delimited.</summary>
    internal const int ColumnarPacketIds = 10;
    /// <summary>Timestamp column: base value + per-row deltas (sint64 zigzag), length-delimited.</summary>
    internal const int ColumnarTimestamps = 11;
    /// <summary>
    /// Per-packet info strings: a sub-message repeating string sub-field 1 once per packet. Present only when
    /// <c>IncludeInfo</c> was requested.
    /// </summary>
    internal const int ColumnarInfos = 12;
    /// <summary>
    /// Raw captured frame bytes: a sub-message repeating bytes sub-field 1 once per packet. Present only when
    /// <c>ExportContent.FrameBytes</c> was requested (off by default). The standard layout writes the same bytes as the packet field
    /// <see cref="PacketFrameBytes"/>.
    /// </summary>
    internal const int ColumnarFrameBytes = 13;
    /// <summary>Flattened field-tree topology, encoded as a single <see cref="ColumnarTopology"/> sub-message (see the Topology* constants below).</summary>
    internal const int ColumnarTopology = 14;
    /// <summary>Repeated field-value column sub-messages (see the Column* constants below).</summary>
    internal const int ColumnarFieldColumns = 15;
    /// <summary>
    /// The additional packet buffers of the block, encoded as a single sub-message (see the Buffers* constants below): one row per
    /// additional buffer of all packets of the block, in packet order and ascending buffer index. Present only when
    /// <c>ExportContent.Buffers</c> was requested and at least one packet of the block has an additional buffer; the standard layout
    /// writes the same data as the packet field <see cref="PacketBuffer"/>. Field number 16 is fixed by the format.
    /// </summary>
    internal const int ColumnarBuffers = 16;

    // === Columnar buffers sub-message (nested under ColumnarBuffers) ===
    // The three columns are parallel: row i is the i-th element of each. Sync: the PBF README § Columnar buffers and the field
    // scanner of the exporter tests (PbfFileScanner).

    /// <summary>
    /// Owning packet ID per buffer row: a length-delimited sub-message with the base value (sub-field 1) and per-row differences
    /// (sub-field 2), the same delta encoding as <see cref="ColumnarPacketIds"/>.
    /// </summary>
    internal const int BuffersPacketIds = 1;
    /// <summary>Buffer index (1 to 255) per buffer row (repeated varint, one tag per row; not the packed encoding).</summary>
    internal const int BuffersIndexes = 2;
    /// <summary>Buffer bytes per buffer row (repeated length-delimited, one per row; an empty buffer is a zero-length entry).</summary>
    internal const int BuffersBytes = 3;

    // === Columnar topology sub-message (nested under ColumnarTopology) ===
    /// <summary>Owning packet ID per topology row: base value + per-row deltas (sint64 zigzag).</summary>
    internal const int TopologyPacketIds = 1;
    /// <summary>Depth-first node ID per topology row (repeated varint, one tag per row; not the packed encoding).</summary>
    internal const int TopologyNodeIds = 2;
    /// <summary>Registered field ID per topology row (repeated varint, one tag per row; not the packed encoding).</summary>
    internal const int TopologyFieldIds = 3;
    /// <summary>
    /// Parent node ID per topology row, or -1 for top-level fields (repeated sint64 zigzag; zigzag keeps the -1 to
    /// one byte).
    /// </summary>
    internal const int TopologyParentNodeIds = 4;
    /// <summary>
    /// Display line of a node without a value (a protocol layer, a group) per topology row, presence-encoded like
    /// <see cref="ColumnCustomTexts"/> (<see cref="TextColumnPresent"/> and <see cref="TextColumnAbsentRun"/>): a present text, also an
    /// empty one, is sub-field 1; a run of rows without a text (every value node and every node whose dissector set none) is
    /// sub-field 2. Written only when the export asks for custom texts and at least one node has one; a missing column means every
    /// row is absent. Field number 5 is fixed by the format.
    /// </summary>
    internal const int TopologyCustomTexts = 5;

    // === Columnar field-value column sub-message (nested under ColumnarFieldColumns) ===
    /// <summary>Field ID this column belongs to (varint, first field in the sub-message).</summary>
    internal const int ColumnFieldId = 1;
    /// <summary>The field's declared <see cref="NetworkInspector.Core.Fields.FieldType"/> (varint).</summary>
    internal const int ColumnFieldType = 2;
    /// <summary>Owning packet ID per row: base value + per-row deltas (sint64 zigzag).</summary>
    internal const int ColumnPacketIds = 3;
    /// <summary>Topology node ID per row (repeated varint), scoped to <see cref="ColumnPacketIds"/>.</summary>
    internal const int ColumnNodeIds = 4;
    /// <summary>Values for a <c>Bool</c> column (repeated varint 0/1).</summary>
    internal const int ColumnBoolValues = 5;
    /// <summary>Values for an <c>I64</c> column (repeated sint64 zigzag).</summary>
    internal const int ColumnI64Values = 6;
    /// <summary>Values for a <c>U64</c> column (repeated varint).</summary>
    internal const int ColumnU64Values = 7;
    /// <summary>Values for an <c>F64</c> column (repeated fixed64 IEEE 754 double).</summary>
    internal const int ColumnF64Values = 8;
    /// <summary>Values (nanoseconds since Unix epoch) for a <c>Timestamp</c> column (repeated sint64 zigzag).</summary>
    internal const int ColumnTimestampValues = 9;
    /// <summary>Values for a <c>Bytes</c> column (repeated length-delimited). The address types have their own columns (19 to 21).</summary>
    internal const int ColumnBytesValues = 10;
    /// <summary>
    /// Plain string values for a <c>String</c> column: a sub-message repeating string sub-field 1 once per row, with
    /// a null value written as an empty string. Writers emit one string per row; the field number 12 is fixed by the format.
    /// </summary>
    internal const int ColumnStringValues = 12;
    /// <summary>
    /// Custom-representation texts per row, presence-encoded (see <see cref="TextColumnPresent"/> and
    /// <see cref="TextColumnAbsentRun"/>): a present text, also an empty one, is sub-field 1; a run of rows without a text is sub-field
    /// 2. The column is omitted when no row has a text. Field number 15 is fixed by the format.
    /// </summary>
    internal const int ColumnCustomRepresentations = 15;
    /// <summary>
    /// Custom-text texts per row, presence-encoded like <see cref="ColumnCustomRepresentations"/>. Field number 18 is fixed by the
    /// format.
    /// </summary>
    internal const int ColumnCustomTexts = 18;
    /// <summary>
    /// Values of an <c>IPv4Address</c> column: one packed field (length-delimited) of fixed32 values, 4 bytes per row, little-endian,
    /// each the address as an unsigned integer (<c>192.168.1.1</c> is <c>0xC0A80101</c>). Field number 19 is fixed by the format.
    /// </summary>
    internal const int ColumnFixed32Values = 19;
    /// <summary>
    /// Values of a <c>MacAddress</c> or <c>Eui64</c> column: one packed field of fixed64 values, 8 bytes per row, little-endian, each
    /// the address as an unsigned integer (a MAC address uses the low 48 bits). For an <c>IPv6Address</c> or <c>Uuid</c> column it
    /// holds the high 64 bits of every row, and <see cref="ColumnFixed64LowValues"/> the low 64 bits. Field number 20 is fixed by the
    /// format.
    /// </summary>
    internal const int ColumnFixed64Values = 20;
    /// <summary>
    /// Low 64 bits of an <c>IPv6Address</c> or <c>Uuid</c> column: one packed field of fixed64 values with one entry per row, in the
    /// row order of <see cref="ColumnFixed64Values"/>. Field number 21 is fixed by the format.
    /// </summary>
    internal const int ColumnFixed64LowValues = 21;

    // === Presence-encoded text column sub-message (nested under ColumnCustomRepresentations, ColumnCustomTexts, and TopologyCustomTexts) ===
    // A text column lists its rows in order; each entry is either one present text or a run of absent rows, so a reader walks the
    // entries and advances the row index by 1 or by the run length. Sync: ColumnarBlockBuilder.WriteTextColumn, the PBF README
    // § Texts, and the decoder of the exporter tests (PbfFileScanner.DecodeTextColumn).

    /// <summary>
    /// One row with a text (length-delimited UTF-8 string, repeated). The empty string is a present, empty text: it is not the
    /// same as an absent row.
    /// </summary>
    internal const int TextColumnPresent = 1;
    /// <summary>
    /// A run of consecutive rows without a text (varint, repeated): the number of absent rows, at least 1. Runs are maximal: two
    /// absent entries never follow each other.
    /// </summary>
    internal const int TextColumnAbsentRun = 2;

    #endregion
}

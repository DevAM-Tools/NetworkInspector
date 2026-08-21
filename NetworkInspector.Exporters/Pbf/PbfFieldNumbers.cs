// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>
/// PBF protobuf field numbers for the standard block format.
/// These correspond to the PBF wire protocol specification.
/// </summary>
internal static class PbfFieldNumbers
{
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

    // === Field message ===
    /// <summary>Field ID (varint).</summary>
    internal const int FieldFieldId = 1;
    /// <summary>Field name (string, first occurrence only).</summary>
    internal const int FieldName = 2;
    /// <summary>Field UI name (string, first occurrence only).</summary>
    internal const int FieldUiName = 3;
    /// <summary>Field type (varint, first occurrence only).</summary>
    internal const int FieldType = 4;
    /// <summary>Field value (type-dependent encoding).</summary>
    internal const int FieldValue = 5;
    /// <summary>Field custom representation text.</summary>
    internal const int FieldValueText = 6;
    /// <summary>Field custom text.</summary>
    internal const int FieldCustomText = 7;
    /// <summary>Same-as-previous flags for field.</summary>
    internal const int FieldSameFlags = 8;
    /// <summary>Nested child field (repeated).</summary>
    internal const int FieldChild = 9;

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
    /// <summary>Trailer total packet count.</summary>
    internal const int TrailerPacketCount = 1;
    /// <summary>Trailer total block count.</summary>
    internal const int TrailerBlockCount = 2;
    /// <summary>Trailer field presence bitmap.</summary>
    internal const int TrailerFieldBitmap = 3;

    // ========================================================================
    // === Columnar block payload (top level; shares fields 1-5 above      ===
    // === — BlockPacketCount/BlockMinPacketId/BlockMaxPacketId/            ===
    // === BlockMinTimestamp/BlockMaxTimestamp — with the standard header). ===
    // ========================================================================

    /// <summary>Packet ID column: base value + per-row deltas (sint64 zigzag), length-delimited.</summary>
    internal const int ColumnarPacketIds = 10;
    /// <summary>Timestamp column: base value + per-row deltas (sint64 zigzag), length-delimited.</summary>
    internal const int ColumnarTimestamps = 11;
    /// <summary>Per-packet info strings (repeated string). Present only when <c>IncludeInfo</c> was requested.</summary>
    internal const int ColumnarInfos = 12;
    /// <summary>Raw captured frame bytes, one per packet (repeated bytes). Present only when <c>IncludeFrameBytes</c> was requested.</summary>
    internal const int ColumnarFrameBytes = 13;
    /// <summary>Flattened field-tree topology, encoded as a single <see cref="ColumnarTopology"/> sub-message (see the Topology* constants below).</summary>
    internal const int ColumnarTopology = 14;
    /// <summary>Repeated field-value column sub-messages (see the Column* constants below).</summary>
    internal const int ColumnarFieldColumns = 15;

    // === Columnar topology sub-message (nested under ColumnarTopology) ===
    /// <summary>Owning packet ID per topology row: base value + per-row deltas (sint64 zigzag).</summary>
    internal const int TopologyPacketIds = 1;
    /// <summary>Depth-first node ID per topology row (packed as repeated varint).</summary>
    internal const int TopologyNodeIds = 2;
    /// <summary>Registered field ID per topology row (packed as repeated varint).</summary>
    internal const int TopologyFieldIds = 3;
    /// <summary>Parent node ID per topology row, or -1 for top-level fields (repeated sint64 zigzag).</summary>
    internal const int TopologyParentNodeIds = 4;

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
    /// <summary>Values for a <c>Bytes</c> or fixed-size address column (repeated length-delimited).</summary>
    internal const int ColumnBytesValues = 10;
    /// <summary>
    /// Plain string values for a <c>String</c> column (repeated string). Wire tag 12 is stable for
    /// PBF v1 (historically reserved for dictionary entries; writers emit one string per row).
    /// </summary>
    internal const int ColumnStringValues = 12;
    /// <summary>
    /// Plain custom-representation strings per row (repeated string). Wire tag 15 is stable for PBF v1.
    /// </summary>
    internal const int ColumnCustomRepresentations = 15;
    /// <summary>
    /// Plain custom-text strings per row (repeated string). Wire tag 18 is stable for PBF v1.
    /// </summary>
    internal const int ColumnCustomTexts = 18;
}

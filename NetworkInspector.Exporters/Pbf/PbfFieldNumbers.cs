// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

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
}

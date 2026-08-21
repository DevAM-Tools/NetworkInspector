// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// Per-field column accumulator for the shared columnar batch format. Stores all values for a
/// single field across all packets in a <see cref="ColumnarPacketBatch"/>, keyed by the owning
/// packet ID and the field's topology node ID (so repeated occurrences of the same field within
/// one packet remain distinguishable).
/// <para>
/// Only the value list matching <see cref="FieldType"/> is populated by <see cref="Add"/>; the
/// others stay empty. Values are taken from <see cref="FieldValueData"/> (and optional custom
/// strings) — the same Core types used by the protocol stack.
/// </para>
/// </summary>
internal sealed class FieldColumnBag
{
    #region Fields

    private readonly List<int> _PacketIds = new(256);
    private readonly List<int> _NodeIds = new(256);

    private readonly List<bool> _BoolValues = new();
    private readonly List<long> _I64Values = new();
    private readonly List<ulong> _U64Values = new();
    private readonly List<double> _F64Values = new();
    private readonly List<long> _TimestampValues = new();
    private readonly List<string?> _StringValues = new();
    /// <summary>
    /// Materialized byte payloads for <see cref="FieldType.Bytes"/> (owned copies) and, after first
    /// <see cref="BytesValues"/> access, for fixed-size address types deferred via
    /// <see cref="_FixedBytePayloads"/>.
    /// </summary>
    private readonly List<byte[]?> _BytesValues = new();
    /// <summary>
    /// Deferred fixed-size address payloads (MAC/IPv4/IPv6/EUI64/UUID) stored as
    /// <see cref="FieldValueData"/> until a sink requests <see cref="BytesValues"/>, avoiding a
    /// heap <c>byte[]</c> per value during batch accumulation.
    /// </summary>
    private readonly List<FieldValueData> _FixedBytePayloads = new();

    private readonly List<string?> _CustomRepresentations = new();
    private readonly List<string?> _CustomTexts = new();

    private long _EstimatedSizeAccumulator { get; set; }

    #endregion

    #region Constructor

    /// <summary>Creates a column bag for the given field.</summary>
    /// <param name="fieldIdValue">The field identifier this bag accumulates values for.</param>
    /// <param name="fieldType">The field's declared value type (from field metadata).</param>
    internal FieldColumnBag(int fieldIdValue, FieldType fieldType)
    {
        FieldIdValue = fieldIdValue;
        FieldType = fieldType;
    }

    #endregion

    #region Properties

    /// <summary>The field identifier this bag accumulates values for.</summary>
    internal int FieldIdValue { get; }

    /// <summary>The field's declared value type.</summary>
    internal FieldType FieldType { get; }

    /// <summary>Number of rows (values) accumulated.</summary>
    internal int RowCount => _PacketIds.Count;

    /// <summary>Rough estimate of the memory consumed by this bag's accumulated rows, in bytes.</summary>
    internal long EstimatedSizeBytes => _EstimatedSizeAccumulator;

    /// <summary>Packet identifiers, one per row (same range as <see cref="PacketId"/>).</summary>
    internal IReadOnlyList<int> PacketIds => _PacketIds;

    /// <summary>Topology node identifiers, one per row, scoped to <see cref="PacketIds"/>.</summary>
    internal IReadOnlyList<int> NodeIds => _NodeIds;

    /// <summary>Values for a <see cref="FieldType.Bool"/> bag.</summary>
    internal IReadOnlyList<bool> BoolValues => _BoolValues;

    /// <summary>Values for an <see cref="FieldType.I64"/> bag.</summary>
    internal IReadOnlyList<long> I64Values => _I64Values;

    /// <summary>Values for an <see cref="FieldType.U64"/> bag.</summary>
    internal IReadOnlyList<ulong> U64Values => _U64Values;

    /// <summary>Values for an <see cref="FieldType.F64"/> bag.</summary>
    internal IReadOnlyList<double> F64Values => _F64Values;

    /// <summary>Values (nanoseconds since Unix epoch) for a <see cref="FieldType.Timestamp"/> bag.</summary>
    internal IReadOnlyList<long> TimestampValues => _TimestampValues;

    /// <summary>Values for a <see cref="FieldType.String"/> bag.</summary>
    internal IReadOnlyList<string?> StringValues => _StringValues;

    /// <summary>
    /// Values for a <see cref="FieldType.Bytes"/> bag or one of the fixed-size address bags
    /// (<see cref="FieldType.MacAddress"/>, <see cref="FieldType.IPv4Address"/>,
    /// <see cref="FieldType.IPv6Address"/>, <see cref="FieldType.Eui64"/>, <see cref="FieldType.Uuid"/>).
    /// Fixed-size address types are materialized from deferred <see cref="FieldValueData"/> on first access.
    /// </summary>
    internal IReadOnlyList<byte[]?> BytesValues
    {
        get
        {
            _MaterializeFixedBytePayloads();
            return _BytesValues;
        }
    }

    /// <summary>Optional custom representation strings, one per row (nullable when absent).</summary>
    internal IReadOnlyList<string?> CustomRepresentations => _CustomRepresentations;

    /// <summary>Optional custom text strings, one per row (nullable when absent).</summary>
    internal IReadOnlyList<string?> CustomTexts => _CustomTexts;

    #endregion

    #region Public API

    /// <summary>
    /// Appends one row from a Core <see cref="FieldValueData"/> payload plus optional custom strings.
    /// </summary>
    /// <param name="packetId">The owning packet's identifier.</param>
    /// <param name="nodeId">The field's topology node identifier within the packet.</param>
    /// <param name="data">Payload; <see cref="FieldValueData.Type"/> must match <see cref="FieldType"/>.</param>
    /// <param name="customRepresentation">Optional custom representation (already filtered by detail flags).</param>
    /// <param name="customText">Optional custom text (already filtered by detail flags).</param>
    internal void Add(
        int packetId,
        int nodeId,
        in FieldValueData data,
        string? customRepresentation,
        string? customText)
    {
        // Extract typed payload before mutating row lists so a type mismatch leaves the bag unchanged.
        switch (FieldType)
        {
            case FieldType.Bool:
                if (!data.TryGetAsBool(out bool boolValue))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected Bool but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _BoolValues.Add(boolValue);
                _EstimatedSizeAccumulator += 9; // ids (8) + bool (1)
                break;
            case FieldType.I64:
                if (!data.TryGetAsI64(out long i64Value))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected I64 but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _I64Values.Add(i64Value);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.U64:
                if (!data.TryGetAsU64(out ulong u64Value))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected U64 but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _U64Values.Add(u64Value);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.F64:
                if (!data.TryGetAsF64(out double f64Value))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected F64 but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _F64Values.Add(f64Value);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.Timestamp:
                if (!data.TryGetAsTimestamp(out Timestamp timestamp))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected Timestamp but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _TimestampValues.Add(timestamp.AsNanos);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.String:
                if (!data.TryGetAsString(out string stringValue))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected String but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _StringValues.Add(stringValue);
                _EstimatedSizeAccumulator += 8 + _EstimateStringBytes(stringValue);
                break;
            case FieldType.Bytes:
            {
                byte[]? bytes = FieldValueMaterializer.ToBytesArray(in data);
                if (bytes is null)
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected Bytes but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _BytesValues.Add(bytes);
                _EstimatedSizeAccumulator += 8 + bytes.Length;
                break;
            }
            case FieldType.MacAddress:
                if (!data.TryGetAsMacAddress(out _))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected MacAddress but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _FixedBytePayloads.Add(data);
                _EstimatedSizeAccumulator += 14;
                break;
            case FieldType.IPv4Address:
                if (!data.TryGetAsIPv4(out _))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected IPv4Address but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _FixedBytePayloads.Add(data);
                _EstimatedSizeAccumulator += 12;
                break;
            case FieldType.IPv6Address:
                if (!data.TryGetAsIPv6(out _))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected IPv6Address but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _FixedBytePayloads.Add(data);
                _EstimatedSizeAccumulator += 24;
                break;
            case FieldType.Eui64:
                if (!data.TryGetAsEui64(out _))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected Eui64 but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _FixedBytePayloads.Add(data);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.Uuid:
                if (!data.TryGetAsUuid(out _))
                {
                    throw new InvalidOperationException(
                        $"Field {FieldIdValue}: expected Uuid but value type was {data.Type}.");
                }
                _PacketIds.Add(packetId);
                _NodeIds.Add(nodeId);
                _FixedBytePayloads.Add(data);
                _EstimatedSizeAccumulator += 24;
                break;
            default:
                throw new InvalidOperationException(
                    $"Field {FieldIdValue} has unsupported column type {FieldType}.");
        }

        _CustomRepresentations.Add(customRepresentation);
        _CustomTexts.Add(customText);
        _EstimatedSizeAccumulator += _EstimateStringBytes(customRepresentation);
        _EstimatedSizeAccumulator += _EstimateStringBytes(customText);
    }

    /// <summary>Resets all accumulated rows for reuse in the next block.</summary>
    internal void Reset()
    {
        _PacketIds.Clear();
        _NodeIds.Clear();
        _BoolValues.Clear();
        _I64Values.Clear();
        _U64Values.Clear();
        _F64Values.Clear();
        _TimestampValues.Clear();
        _StringValues.Clear();
        _BytesValues.Clear();
        _FixedBytePayloads.Clear();
        _CustomRepresentations.Clear();
        _CustomTexts.Clear();
        _EstimatedSizeAccumulator = 0;
    }

    #endregion

    #region Private Helpers

    private void _MaterializeFixedBytePayloads()
    {
        if (_FixedBytePayloads.Count == 0)
        {
            return;
        }

        for (int i = 0; i < _FixedBytePayloads.Count; i++)
        {
            FieldValueData payload = _FixedBytePayloads[i];
            _BytesValues.Add(FieldValueMaterializer.ToBytesArray(in payload));
        }

        _FixedBytePayloads.Clear();
    }

    private static long _EstimateStringBytes(string? value) =>
        value is null
            ? 0
            : (value.Length * 2L) + 4;

    #endregion
}

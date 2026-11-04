// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// Per-field column accumulator for the shared columnar batch format. Stores all values for a
/// single field across all packets in a <see cref="ColumnarPacketBatch"/>, keyed by the owning
/// packet ID and a position (so repeated occurrences of the same field within
/// one packet remain distinguishable).
/// <para>
/// Only the value list matching <see cref="FieldType"/> is populated by <see cref="Add"/>; the
/// others stay empty. Values are taken from <see cref="FieldValueData"/> (and optional custom
/// strings) — the same Core types used by the protocol stack.
/// </para>
/// <para>
/// <b>Retention:</b> a bag keeps its rows until the batch is flushed, while the packet buffers behind the values may be
/// recycled. Scalars, strings and the fixed-size addresses are self-contained (inline or immutable), but a
/// <see cref="FieldType.Bytes"/> value can alias a caller's array, so it is copied into the batch's <see cref="BatchByteArena"/>
/// on <see cref="Add"/> (no <c>byte[]</c> per value). The Parquet sink, which needs arrays for its byte column, reads
/// <see cref="BytesValues"/>, which builds them on demand; the DuckDB sink and the PBF columnar builder read <see cref="GetBlob"/> and
/// allocate nothing.
/// </para>
/// <para>
/// <b>Addresses:</b> the five fixed-size address types (MAC, IPv4, IPv6, EUI-64, UUID) are kept as the inline
/// <see cref="FieldValueData"/> they arrived as, and a sink reads the integer value of each row through
/// <see cref="FixedValueSpan"/> and the typed accessors of <see cref="FieldValueData"/>; no byte form of an address exists in the
/// bag. Sync: EXPORTER_GUIDE.md § Rebuilding the display line (the stored integer forms).
/// </para>
/// <para>
/// <b>Size estimate:</b> <see cref="EstimatedSizeBytes"/> is a deliberately cheap approximation used only to decide when
/// a block is full: 8 bytes for the two id columns plus the payload size (strings count 2 bytes per char plus 4 bytes
/// of overhead). It ignores list growth slack and object headers, so it is neither exact nor a limit.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; the owning <see cref="ColumnarPacketBatch"/> is single-threaded.
/// </para>
/// </summary>
internal sealed class FieldColumnBag
{
    #region Configuration and state

    // Parallel row lists: row i of every populated list belongs together (packet id, position, value, custom strings).
    // Only the value list of the bag's FieldType grows; the others stay empty. The 256 initial capacity of the id lists is
    // a sizing hint, not a limit.

    /// <summary>Owning packet id per row.</summary>
    private readonly List<int> _PacketIds = new(256);

    /// <summary>Position per row: the topology node id (hierarchy shape) or the occurrence number (flat shapes).</summary>
    private readonly List<int> _Positions = new(256);

    /// <summary>Values of a <see cref="FieldType.Bool"/> bag, one per row.</summary>
    private readonly List<bool> _BoolValues = new();

    /// <summary>Values of an <see cref="FieldType.I64"/> bag, one per row.</summary>
    private readonly List<long> _I64Values = new();

    /// <summary>Values of a <see cref="FieldType.U64"/> bag, one per row.</summary>
    private readonly List<ulong> _U64Values = new();

    /// <summary>Values of an <see cref="FieldType.F64"/> bag, one per row.</summary>
    private readonly List<double> _F64Values = new();

    /// <summary>Values of a <see cref="FieldType.Timestamp"/> bag in nanoseconds since the Unix epoch, one per row.</summary>
    private readonly List<long> _TimestampValues = new();

    /// <summary>Values of a <see cref="FieldType.String"/> bag, one per row.</summary>
    private readonly List<string?> _StringValues = new();

    /// <summary>Locations of the <see cref="FieldType.Bytes"/> payloads in the batch's <see cref="BatchByteArena"/>, one per row.</summary>
    private readonly List<ArenaRef> _ByteRefs = new();

    /// <summary>
    /// Address values (MAC, IPv4, IPv6, EUI-64, UUID) stored inline as <see cref="FieldValueData"/>, one per row, so no heap
    /// object is needed while accumulating and the sink reads the integer form straight from the struct.
    /// </summary>
    private readonly List<FieldValueData> _AddressValues = new();

    /// <summary>
    /// <c>byte[]</c> copies of the first rows of a <see cref="FieldType.Bytes"/> bag, built by <see cref="BytesValues"/> on demand
    /// for the sink that needs arrays. A cache of <see cref="_ByteRefs"/>: it never holds more rows than the bag and is cleared with it.
    /// </summary>
    private readonly List<byte[]?> _BytesValues = new();

    /// <summary>The batch's arena that holds the bytes of <see cref="FieldType.Bytes"/> rows.</summary>
    private readonly BatchByteArena _Arena;

    /// <summary>Custom representation per row; null when absent.</summary>
    private readonly List<string?> _CustomRepresentations = new();

    /// <summary>Custom text per row; null when absent.</summary>
    private readonly List<string?> _CustomTexts = new();

    /// <summary>Running size estimate in bytes (see the class remarks); updated by <see cref="Add"/>, cleared by <see cref="Reset"/>.</summary>
    private long _EstimatedSizeAccumulator { get; set; }

    /// <summary>The value of <see cref="_EstimatedSizeAccumulator"/> before the last row was added (see <see cref="ReplaceLast"/>).</summary>
    private long _LastRowSizeBefore;

    /// <summary>Creates a column bag for the given field.</summary>
    /// <param name="fieldIdValue">The field identifier this bag accumulates values for.</param>
    /// <param name="fieldType">The field's declared value type (from field metadata).</param>
    /// <param name="arena">Byte store of the owning batch; <see cref="FieldType.Bytes"/> values are copied into it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    internal FieldColumnBag(int fieldIdValue, FieldType fieldType, BatchByteArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);
        FieldIdValue = fieldIdValue;
        FieldType = fieldType;
        _Arena = arena;
    }

    /// <summary>The field identifier this bag accumulates values for.</summary>
    internal int FieldIdValue { get; }

    /// <summary>The field's declared value type.</summary>
    internal FieldType FieldType { get; }

    #endregion

    #region Column views

    /// <summary>Number of rows (values) accumulated.</summary>
    internal int RowCount => _PacketIds.Count;

    /// <summary>Rough estimate of the memory consumed by this bag's accumulated rows, in bytes.</summary>
    internal long EstimatedSizeBytes => _EstimatedSizeAccumulator;

    /// <summary>Packet identifiers, one per row (same range as <see cref="PacketId"/>).</summary>
    internal IReadOnlyList<int> PacketIds => _PacketIds;

    /// <summary>
    /// Positions, one per row, scoped to <see cref="PacketIds"/>: the topology node id in the hierarchy shape, the occurrence
    /// number of the value in its packet in the flat shapes. The shape of the owning batch says which of the two it is.
    /// </summary>
    internal IReadOnlyList<int> Positions => _Positions;

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

    /// <summary>Values for a <see cref="FieldType.Bytes"/> bag as arrays, one per row; empty for every other type.</summary>
    /// <remarks>
    /// Built on first access from the arena and cached until <see cref="Reset"/>; rows added later are appended on the next
    /// access. This costs one array per row, so a sink that can read spans uses <see cref="GetBlob"/> instead (the DuckDB sink and
    /// the PBF columnar builder do). The address types have no array form: they are read through <see cref="FixedValueSpan"/>.
    /// </remarks>
    internal IReadOnlyList<byte[]?> BytesValues
    {
        get
        {
            _MaterializeBytesValues();
            return _BytesValues;
        }
    }

    /// <summary>Optional custom representation strings, one per row (nullable when absent).</summary>
    internal IReadOnlyList<string?> CustomRepresentations => _CustomRepresentations;

    /// <summary>Optional custom text strings, one per row (nullable when absent).</summary>
    internal IReadOnlyList<string?> CustomTexts => _CustomTexts;

    // Span views for the flush loops of a sink: indexing a span avoids an interface call per cell. A span is valid only
    // while the bag is not modified (a list that grows moves its array), so a sink takes them inside WriteBatch, where the
    // batch is not written to (see ColumnarPacketBatch thread safety).

    /// <summary>Span view of <see cref="PacketIds"/>.</summary>
    internal ReadOnlySpan<int> PacketIdSpan => CollectionsMarshal.AsSpan(_PacketIds);

    /// <summary>Span view of <see cref="Positions"/>.</summary>
    internal ReadOnlySpan<int> PositionSpan => CollectionsMarshal.AsSpan(_Positions);

    /// <summary>Span view of <see cref="BoolValues"/>.</summary>
    internal ReadOnlySpan<bool> BoolSpan => CollectionsMarshal.AsSpan(_BoolValues);

    /// <summary>Span view of <see cref="I64Values"/>.</summary>
    internal ReadOnlySpan<long> I64Span => CollectionsMarshal.AsSpan(_I64Values);

    /// <summary>Span view of <see cref="U64Values"/>.</summary>
    internal ReadOnlySpan<ulong> U64Span => CollectionsMarshal.AsSpan(_U64Values);

    /// <summary>Span view of <see cref="F64Values"/>.</summary>
    internal ReadOnlySpan<double> F64Span => CollectionsMarshal.AsSpan(_F64Values);

    /// <summary>Span view of <see cref="TimestampValues"/>.</summary>
    internal ReadOnlySpan<long> TimestampSpan => CollectionsMarshal.AsSpan(_TimestampValues);

    /// <summary>Span view of <see cref="StringValues"/>.</summary>
    internal ReadOnlySpan<string?> StringSpan => CollectionsMarshal.AsSpan(_StringValues);

    /// <summary>Span view of <see cref="CustomRepresentations"/>.</summary>
    internal ReadOnlySpan<string?> CustomRepresentationSpan => CollectionsMarshal.AsSpan(_CustomRepresentations);

    /// <summary>Span view of <see cref="CustomTexts"/>.</summary>
    internal ReadOnlySpan<string?> CustomTextSpan => CollectionsMarshal.AsSpan(_CustomTexts);

    /// <summary>
    /// Span view of the address values of a MAC, IPv4, IPv6, EUI-64, or UUID bag, one entry per row; empty for every other type.
    /// A sink reads the integer form of row <c>i</c> with the typed accessor that matches <see cref="FieldType"/>
    /// (<see cref="FieldValueData.TryGetAsIPv4"/>, <see cref="FieldValueData.TryGetAsMacAddress"/>, and so on).
    /// </summary>
    /// <remarks>
    /// Perf: the entries are read in place (<c>ref readonly</c>), so the 24-byte structs are not copied per row. Same validity rule
    /// as the other span views: valid only while the bag is not modified.
    /// </remarks>
    internal ReadOnlySpan<FieldValueData> FixedValueSpan => CollectionsMarshal.AsSpan(_AddressValues);

    /// <summary>
    /// Appends <c>byte[]</c> copies of the rows that <see cref="_BytesValues"/> does not hold yet. Idempotent and
    /// incremental: the source is never consumed, so a second call with no new rows does nothing, and rows added later are
    /// appended in row order.
    /// </summary>
    private void _MaterializeBytesValues()
    {
        // _ByteRefs is empty for a bag of another type, so BytesValues stays empty there.
        for (int row = _BytesValues.Count; row < _ByteRefs.Count; row++)
        {
            _BytesValues.Add(_Arena.Get(_ByteRefs[row]).ToArray());
        }
    }

    /// <summary>
    /// Returns the bytes of row <paramref name="row"/> of a <see cref="FieldType.Bytes"/> bag without allocating.
    /// </summary>
    /// <param name="row">Row index, from 0 to <see cref="RowCount"/> - 1.</param>
    /// <returns>
    /// A view of the batch's arena; empty for an empty value. It is valid until the bag or the arena changes.
    /// </returns>
    /// <remarks>
    /// Omitted validation: the bag's type is not checked; only a <see cref="FieldType.Bytes"/> bag has arena rows, so another type
    /// fails on the row index (the sinks pick the loop by <see cref="FieldType"/> before they call this).
    /// Writable only because the appender API of DuckDB.NET takes <c>Span&lt;byte&gt;</c>; callers must not write.
    /// </remarks>
    internal Span<byte> GetBlob(int row) => _Arena.Get(_ByteRefs[row]);

    #endregion

    #region Adding and replacing rows

    /// <summary>
    /// Appends one row from a Core <see cref="FieldValueData"/> payload plus optional custom strings.
    /// </summary>
    /// <param name="packetId">The owning packet's identifier.</param>
    /// <param name="position">Node id of the field within the packet (hierarchy shape) or occurrence number (flat shapes).</param>
    /// <param name="data">Payload; <see cref="FieldValueData.Type"/> must match <see cref="FieldType"/>.</param>
    /// <param name="customRepresentation">Optional custom representation (already filtered by the export content).</param>
    /// <param name="customText">Optional custom text (already filtered by the export content).</param>
    /// <exception cref="InvalidOperationException">
    /// The value type of <paramref name="data"/> does not match <see cref="FieldType"/>, or the bag's type has no column.
    /// Nothing is added in that case.
    /// </exception>
    /// <remarks>
    /// <paramref name="data"/> is passed by <c>in</c> reference because <see cref="FieldValueData"/> is a
    /// 24-byte struct; this avoids a copy per value on the export path.
    /// </remarks>
    internal void Add(
        int packetId,
        int position,
        in FieldValueData data,
        string? customRepresentation,
        string? customText)
    {
        // The size before this row is remembered at the end of a successful add (a rejected value must not move it), so that
        // ReplaceLast can cut exactly this row off again without a second size computation.
        long sizeBeforeRow = _EstimatedSizeAccumulator;

        // Extract typed payload before mutating row lists so a type mismatch leaves the bag unchanged.
        switch (FieldType)
        {
            case FieldType.Bool:
                if (!data.TryGetAsBool(out bool boolValue))
                {
                    ThrowBrokenColumnType(FieldIdValue, FieldType.Bool, data.Type);
                }
                _PacketIds.Add(packetId);
                _Positions.Add(position);
                _BoolValues.Add(boolValue);
                _EstimatedSizeAccumulator += 9; // ids (8) + bool (1)
                break;
            case FieldType.I64:
                if (!data.TryGetAsI64(out long i64Value))
                {
                    ThrowBrokenColumnType(FieldIdValue, FieldType.I64, data.Type);
                }
                _PacketIds.Add(packetId);
                _Positions.Add(position);
                _I64Values.Add(i64Value);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.U64:
                if (!data.TryGetAsU64(out ulong u64Value))
                {
                    ThrowBrokenColumnType(FieldIdValue, FieldType.U64, data.Type);
                }
                _PacketIds.Add(packetId);
                _Positions.Add(position);
                _U64Values.Add(u64Value);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.F64:
                if (!data.TryGetAsF64(out double f64Value))
                {
                    ThrowBrokenColumnType(FieldIdValue, FieldType.F64, data.Type);
                }
                _PacketIds.Add(packetId);
                _Positions.Add(position);
                _F64Values.Add(f64Value);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.Timestamp:
                if (!data.TryGetAsTimestamp(out Timestamp timestamp))
                {
                    ThrowBrokenColumnType(FieldIdValue, FieldType.Timestamp, data.Type);
                }
                _PacketIds.Add(packetId);
                _Positions.Add(position);
                _TimestampValues.Add(timestamp.AsNanos);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.String:
                if (!data.TryGetAsString(out string stringValue))
                {
                    ThrowBrokenColumnType(FieldIdValue, FieldType.String, data.Type);
                }
                _PacketIds.Add(packetId);
                _Positions.Add(position);
                _StringValues.Add(stringValue);
                _EstimatedSizeAccumulator += 8 + _EstimateStringBytes(stringValue);
                break;
            case FieldType.Bytes:
            {
                // Owned copy in the arena: the value may alias a recycled packet buffer, and the bag outlives the packet until
                // flush. The arena hands out ranges of pooled chunks, so no byte[] is allocated per value.
                if (!data.TryGetAsBytes(out ReadOnlyMemory<byte> bytes))
                {
                    ThrowBrokenColumnType(FieldIdValue, FieldType.Bytes, data.Type);
                }
                ArenaRef reference = _Arena.Append(bytes.Span);
                _PacketIds.Add(packetId);
                _Positions.Add(position);
                _ByteRefs.Add(reference);
                _EstimatedSizeAccumulator += 8 + reference.Length;
                break;
            }
            // Fixed-size address types (MAC, IPv4, IPv6, EUI-64, UUID): the type is validated here, but the value is kept
            // as the inline FieldValueData and a sink turns it into its integer form when it flushes (FixedValueSpan). Perf: no
            // heap object per value while accumulating; the struct holds the address inline (no reference to a packet buffer),
            // so retaining it after the packet is recycled is safe. Estimates are 8 (ids) + address size.
            case FieldType.MacAddress:
                if (!data.TryGetAsMacAddress(out _))
                {
                    ThrowBrokenColumnType(FieldIdValue, FieldType.MacAddress, data.Type);
                }
                _PacketIds.Add(packetId);
                _Positions.Add(position);
                _AddressValues.Add(data);
                _EstimatedSizeAccumulator += 14;
                break;
            case FieldType.IPv4Address:
                if (!data.TryGetAsIPv4(out _))
                {
                    ThrowBrokenColumnType(FieldIdValue, FieldType.IPv4Address, data.Type);
                }
                _PacketIds.Add(packetId);
                _Positions.Add(position);
                _AddressValues.Add(data);
                _EstimatedSizeAccumulator += 12;
                break;
            case FieldType.IPv6Address:
                if (!data.TryGetAsIPv6(out _))
                {
                    ThrowBrokenColumnType(FieldIdValue, FieldType.IPv6Address, data.Type);
                }
                _PacketIds.Add(packetId);
                _Positions.Add(position);
                _AddressValues.Add(data);
                _EstimatedSizeAccumulator += 24;
                break;
            case FieldType.Eui64:
                if (!data.TryGetAsEui64(out _))
                {
                    ThrowBrokenColumnType(FieldIdValue, FieldType.Eui64, data.Type);
                }
                _PacketIds.Add(packetId);
                _Positions.Add(position);
                _AddressValues.Add(data);
                _EstimatedSizeAccumulator += 16;
                break;
            case FieldType.Uuid:
                if (!data.TryGetAsUuid(out _))
                {
                    ThrowBrokenColumnType(FieldIdValue, FieldType.Uuid, data.Type);
                }
                _PacketIds.Add(packetId);
                _Positions.Add(position);
                _AddressValues.Add(data);
                _EstimatedSizeAccumulator += 24;
                break;
            default:
                ThrowUnsupportedColumnType(FieldIdValue, FieldType);
                break;
        }

        // The custom string columns always get one entry per row (null when absent) so every column has the same row
        // count and row i lines up across all lists.
        _CustomRepresentations.Add(customRepresentation);
        _CustomTexts.Add(customText);
        _EstimatedSizeAccumulator += _EstimateStringBytes(customRepresentation);
        _EstimatedSizeAccumulator += _EstimateStringBytes(customText);
        _LastRowSizeBefore = sizeBeforeRow;
    }

    /// <summary>
    /// Reports a value whose type does not match its column: a broken invariant (a bag is created for one field type and the
    /// export core only adds values of that type), never an expected failure.
    /// </summary>
    /// <param name="fieldIdValue">The field id of the bag.</param>
    /// <param name="expected">The type the bag stores.</param>
    /// <param name="actual">The type of the rejected value.</param>
    /// <exception cref="InvalidOperationException">Always; nothing was added to the bag before this call.</exception>
    /// <remarks>
    /// Perf: the throw sits in this separate method so the per-value paths (<see cref="Add"/> and the sinks' flush loops, which
    /// read values through the same typed accessors) contain no throw site, which keeps them small enough to inline and keeps the
    /// message formatting off the hot path. The Exporters assembly has no shared throw-helper class, and the one of Core is
    /// internal to Core.
    /// </remarks>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowBrokenColumnType(int fieldIdValue, FieldType expected, FieldType actual) =>
        throw new InvalidOperationException(
            Invariant($"Field {fieldIdValue}: expected {expected} but value type was {actual}."));

    /// <summary>Reports a bag whose declared type has no column; a broken invariant of the batch, see <see cref="ThrowBrokenColumnType"/>.</summary>
    /// <param name="fieldIdValue">The field id of the bag.</param>
    /// <param name="fieldType">The unsupported declared type.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowUnsupportedColumnType(int fieldIdValue, FieldType fieldType) =>
        throw new InvalidOperationException(
            Invariant($"Field {fieldIdValue} has unsupported column type {fieldType}."));

    /// <summary>ReplaceLast was called on a bag without a row: a broken invariant of the staging, reported from a cold helper.</summary>
    /// <param name="fieldIdValue">The field id of the bag.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowNoRowToReplace(int fieldIdValue) =>
        throw new InvalidOperationException(Invariant($"Field {fieldIdValue}: there is no row to replace."));

    /// <summary>
    /// Overwrites the value (and the texts) of the last row, which belongs to the packet being staged
    /// (<see cref="Occurrences.Last"/>). The row count does not change; the size estimate is adjusted by the difference. A bytes
    /// payload is appended to the arena again; the old bytes stay unused until the batch resets (bounded by the batch).
    /// </summary>
    /// <param name="data">The new payload; its type must match <see cref="FieldType"/>.</param>
    /// <param name="customRepresentation">Custom representation of the new value, or <see langword="null"/>.</param>
    /// <param name="customText">Custom text of the new value, or <see langword="null"/>.</param>
    /// <exception cref="InvalidOperationException">The bag has no row, or the value type does not match the column.</exception>
    /// <remarks>
    /// Implemented as "cut off the last row, add the new one": the packet id and the position of the row stay, so the row keeps
    /// its place in every parallel list. If the new value is rejected, the old row is already gone; the caller (the batch's
    /// staging) then fails the whole packet and rolls back to the marks it took before the packet, so no half row survives.
    /// </remarks>
    internal void ReplaceLast(in FieldValueData data, string? customRepresentation, string? customText)
    {
        if (_Positions.Count == 0)
        {
            _ThrowNoRowToReplace(FieldIdValue);
        }

        int last = _Positions.Count - 1;
        int packetId = _PacketIds[last];
        int position = _Positions[last];
        long sizeBeforeRow = _LastRowSizeBefore;
        TruncateTo(last, sizeBeforeRow);
        Add(packetId, position, in data, customRepresentation, customText);
    }

    /// <summary>
    /// Sets the custom texts of row <paramref name="row"/>; used when deferred texts are evaluated at the end of a packet.
    /// </summary>
    /// <param name="row">Row index, from 0 to <see cref="RowCount"/> - 1.</param>
    /// <param name="customRepresentation">The custom representation, or <see langword="null"/>.</param>
    /// <param name="customText">The custom text, or <see langword="null"/>.</param>
    internal void SetTexts(int row, string? customRepresentation, string? customText)
    {
        // The size estimate follows the strings: the old ones are subtracted, the new ones added, so the running total stays
        // equal to the sum of the rows (the debug check in the batch's Reset compares exactly that).
        _EstimatedSizeAccumulator -= _EstimateStringBytes(_CustomRepresentations[row]);
        _EstimatedSizeAccumulator -= _EstimateStringBytes(_CustomTexts[row]);
        _CustomRepresentations[row] = customRepresentation;
        _CustomTexts[row] = customText;
        _EstimatedSizeAccumulator += _EstimateStringBytes(customRepresentation);
        _EstimatedSizeAccumulator += _EstimateStringBytes(customText);
    }

    /// <summary>The estimated size of a custom string: 2 bytes per char plus 4 bytes of overhead; 0 for an absent string.</summary>
    /// <param name="value">The string, or <see langword="null"/>.</param>
    /// <returns>The estimate in bytes.</returns>
    private static long _EstimateStringBytes(string? value) =>
        value is null
            ? 0
            : (value.Length * 2L) + 4;

    #endregion

    #region Rollback and release

    /// <summary>
    /// Removes the rows after the first <paramref name="rowCount"/> rows and restores the size estimate. Used by
    /// <see cref="ColumnarPacketBatch"/> to undo the rows of a packet that could not be added completely, so a rejected
    /// packet leaves no partial rows behind.
    /// </summary>
    /// <param name="rowCount">Number of rows to keep; must be between 0 and <see cref="RowCount"/>.</param>
    /// <param name="estimatedSizeBytes">The value of <see cref="EstimatedSizeBytes"/> when the bag held exactly those rows.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowCount"/> is negative or larger than <see cref="RowCount"/>.</exception>
    internal void TruncateTo(int rowCount, long estimatedSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowCount, RowCount);

        // Every parallel list is cut back to the kept row count; a list that never received a row is shorter and stays as
        // it is. The array cache is cut too, so it never holds a row that no longer exists. The arena is not touched here:
        // the batch truncates it to the position it marked before the packet.
        _TrimTo(_PacketIds, rowCount);
        _TrimTo(_Positions, rowCount);
        _TrimTo(_BoolValues, rowCount);
        _TrimTo(_I64Values, rowCount);
        _TrimTo(_U64Values, rowCount);
        _TrimTo(_F64Values, rowCount);
        _TrimTo(_TimestampValues, rowCount);
        _TrimTo(_StringValues, rowCount);
        _TrimTo(_ByteRefs, rowCount);
        _TrimTo(_AddressValues, rowCount);
        _TrimTo(_BytesValues, rowCount);
        _TrimTo(_CustomRepresentations, rowCount);
        _TrimTo(_CustomTexts, rowCount);
        _EstimatedSizeAccumulator = estimatedSizeBytes;
    }

    /// <summary>Resets all accumulated rows for reuse in the next block.</summary>
    internal void Reset()
    {
        _PacketIds.Clear();
        _Positions.Clear();
        _BoolValues.Clear();
        _I64Values.Clear();
        _U64Values.Clear();
        _F64Values.Clear();
        _TimestampValues.Clear();
        _StringValues.Clear();
        _ByteRefs.Clear();
        _AddressValues.Clear();
        _BytesValues.Clear();
        _CustomRepresentations.Clear();
        _CustomTexts.Clear();
        _EstimatedSizeAccumulator = 0;
    }

    /// <summary>Shortens <paramref name="list"/> to at most <paramref name="count"/> elements.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="list">The list to shorten.</param>
    /// <param name="count">The length to keep.</param>
    private static void _TrimTo<T>(List<T> list, int count)
    {
        if (list.Count > count)
        {
            list.RemoveRange(count, list.Count - count);
        }
    }

    #endregion
}

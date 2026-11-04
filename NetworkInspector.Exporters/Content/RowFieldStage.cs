// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Content;

/// <summary>
/// The stage of the row formats (CSV, JSON, Text, PBF Standard): holds the fields of exactly one packet until the exporter
/// formats them in <c>OnPacket</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a stage at all:</b> a row format writes one packet as one unit (a CSV row, a JSON object, a PBF record), but the fields
/// arrive one by one during the parse, before the filter has decided. The stage keeps them until the commit; it is the one
/// buffer of the packet, reused for every packet, so a warm stage allocates nothing.
/// </para>
/// <para>
/// <b>Ownership of values:</b> scalar and address payloads are inline in <see cref="FieldValueData"/> and stay valid; a
/// <see cref="FieldType.Bytes"/> payload may alias a recycled packet buffer and is copied into the
/// <see cref="BatchByteArena"/> at once (the entry keeps an <see cref="ArenaRef"/> and its value is cleared). Strings are immutable.
/// Custom texts stay <see cref="LazyString"/> structs until <see cref="EndPacket"/> evaluates them: a deferred text may be
/// completed by the dissector later in the parse, so it must not be read earlier.
/// </para>
/// <para><b>Thread safety:</b> not thread-safe (parse thread and commit on the same thread).</para>
/// </remarks>
/// <seealso cref="IFieldStage"/>
/// <seealso cref="ExportFieldCollector"/>
internal sealed class RowFieldStage : IFieldStage, IDisposable
{
    #region Entries and state

    /// <summary>One staged value. <see cref="Value"/> is default for bytes (see <see cref="Bytes"/>).</summary>
    internal struct Entry
    {
        /// <summary>Field id in the stack of the parse.</summary>
        internal int FieldId;

        /// <summary>Occurrence number in the packet.</summary>
        internal int Occurrence;

        /// <summary>Value type of the entry.</summary>
        internal FieldType Type;

        /// <summary>The value; default for <see cref="FieldType.Bytes"/> (the bytes are in the arena).</summary>
        internal FieldValueData Value;

        /// <summary>Location of a <see cref="FieldType.Bytes"/> payload in the stage's arena.</summary>
        internal ArenaRef Bytes;

        /// <summary>Kept custom representation until <see cref="EndPacket"/>.</summary>
        internal LazyString PendingRepresentation;

        /// <summary>Kept custom text until <see cref="EndPacket"/>.</summary>
        internal LazyString PendingText;

        /// <summary>Custom representation, evaluated in <see cref="EndPacket"/>.</summary>
        internal string? Representation;

        /// <summary>Custom text, evaluated in <see cref="EndPacket"/>.</summary>
        internal string? Text;
    }

    /// <summary>
    /// Where the staging is in its life. <see cref="Committed"/> keeps the entries readable for the exporter's formatting while
    /// refusing a second commit of the same packet (a replayed or doubled <c>OnPacket</c> must not write the packet twice).
    /// </summary>
    private enum StageState : byte
    {
        /// <summary>Nothing staged.</summary>
        Empty,

        /// <summary>Fields of the packet are arriving.</summary>
        Open,

        /// <summary>The packet ended completely; it can be committed.</summary>
        Closed,

        /// <summary>The packet was committed; the entries stay readable until the next <see cref="BeginPacket"/>.</summary>
        Committed,

        /// <summary>The staging cannot be committed (a muted observer, a faulty text).</summary>
        Failed,
    }

    /// <summary>The staged entries of the current packet, in arrival order; cleared per packet, capacity kept.</summary>
    private readonly List<Entry> _Entries = new(64);

    /// <summary>Byte store of the <see cref="FieldType.Bytes"/> payloads of the current packet; reset per packet.</summary>
    private readonly BatchByteArena _Bytes = new();

    /// <summary>Id of the staged packet; -1 when nothing is staged.</summary>
    private int _PacketId = -1;

    /// <summary>Timestamp of the staged packet in nanoseconds.</summary>
    private long _TimestampNanos;

    /// <summary>Where the staging is in its life.</summary>
    private StageState _State;

    /// <summary>
    /// The reason of a <see cref="StageState.Failed"/> staging that has a specific cause (a faulty text); null for the generic
    /// "not received completely".
    /// </summary>
    private string? _FailureMessage;

    /// <inheritdoc/>
    public int StagedPacketId => _PacketId;

    /// <summary>Timestamp of the staged packet in nanoseconds.</summary>
    internal long TimestampNanos => _TimestampNanos;

    /// <summary>The committed packet's entries, in arrival order; valid until the next <see cref="BeginPacket"/>.</summary>
    internal ReadOnlySpan<Entry> Entries => CollectionsMarshal.AsSpan(_Entries);

    /// <summary>The bytes of a <see cref="FieldType.Bytes"/> entry.</summary>
    /// <param name="entry">An entry of <see cref="Entries"/>.</param>
    /// <returns>The copied payload; valid until the next <see cref="BeginPacket"/>.</returns>
    internal ReadOnlySpan<byte> GetBytes(in Entry entry) => _Bytes.Get(entry.Bytes);

    /// <summary>The bytes of a <see cref="FieldType.Bytes"/> entry as memory, so a caller can wrap them without a copy.</summary>
    /// <param name="entry">An entry of <see cref="Entries"/>.</param>
    /// <returns>The copied payload in the stage's arena; valid until the next <see cref="BeginPacket"/>.</returns>
    internal ReadOnlyMemory<byte> GetBytesMemory(in Entry entry) => _Bytes.GetMemory(entry.Bytes);

    #endregion

    #region Opening and adding values

    /// <summary>
    /// The last entry of a field in this packet (occurrence choice <c>Last</c>), by field id. Valid only when the field's entry
    /// in <see cref="_LastStampById"/> equals <see cref="_Sequence"/>.
    /// </summary>
    private int[] _LastIndexById = new int[64];

    /// <summary>Per field id, the <see cref="_Sequence"/> of the packet its <see cref="_LastIndexById"/> entry belongs to.</summary>
    private int[] _LastStampById = new int[64];

    /// <summary>Packet sequence number, stamped like the collector's counters; never 0 while a packet is staged.</summary>
    private int _Sequence;

    /// <summary>
    /// Moves the packet sequence so that the wrap at 2^32 packets can be reached in a few packets; production code never calls it.
    /// </summary>
    /// <param name="sequence">The sequence after which the next <see cref="BeginPacket"/> increments; -1 makes the next packet wrap.</param>
    internal void RestartSequenceAt(int sequence) => _Sequence = sequence;

    /// <inheritdoc/>
    public void BeginPacket(int packetId, long timestampNanos)
    {
        // A staging that was never committed (filter rejected, parse failed) is dropped here; no reject call is needed.
        _Clear();

        // Invariant: a stamp equals _Sequence only for an entry of this packet. At the wrap (once per 2^32 packets) the stamps
        // are cleared and the sequence restarts at 1, because a field last seen at the wrapped value would otherwise point a
        // Last replacement at a stale entry index.
        _Sequence = unchecked(_Sequence + 1);
        if (_Sequence == 0)
        {
            Array.Clear(_LastStampById);
            _Sequence = 1;
        }

        _PacketId = packetId;
        _TimestampNanos = timestampNanos;
        _State = StageState.Open;
    }

    /// <inheritdoc/>
    public void AddValue(int fieldId, int occurrence, in FieldValueData value, LazyString customRepresentation, LazyString customText) =>
        _Add(fieldId, occurrence, in value, customRepresentation, customText);

    /// <inheritdoc/>
    public void ReplaceLastValue(int fieldId, in FieldValueData value, LazyString customRepresentation, LazyString customText)
    {
        // The last staged entry of this field in this packet is overwritten in place (Occurrences.Last). A bytes payload is
        // appended to the arena again; the old bytes stay unused until the next packet resets the arena (bounded by one packet).
        int index = -1;
        if (fieldId < _LastStampById.Length && _LastStampById[fieldId] == _Sequence)
        {
            index = _LastIndexById[fieldId];
        }

        if (index < 0)
        {
            AddValue(fieldId, 0, in value, customRepresentation, customText);
            return;
        }

        ref Entry entry = ref CollectionsMarshal.AsSpan(_Entries)[index];
        _SetValue(ref entry, in value);
        entry.PendingRepresentation = customRepresentation;
        entry.PendingText = customText;
    }

    /// <summary>Appends one entry and remembers it as the last entry of its field.</summary>
    /// <param name="fieldId">Field id in the stack of the parse.</param>
    /// <param name="occurrence">Occurrence number in the packet.</param>
    /// <param name="value">The value; valid only for the call (bytes are copied).</param>
    /// <param name="customRepresentation">Custom representation, kept until <see cref="EndPacket"/>.</param>
    /// <param name="customText">Custom text, kept until <see cref="EndPacket"/>.</param>
    /// <exception cref="OverflowException">The id tables would exceed <see cref="int.MaxValue"/> entries.</exception>
    private void _Add(
        int fieldId,
        int occurrence,
        in FieldValueData value,
        LazyString customRepresentation,
        LazyString customText)
    {
        Entry entry = new()
        {
            FieldId = fieldId,
            Occurrence = occurrence,
            PendingRepresentation = customRepresentation,
            PendingText = customText,
        };
        _SetValue(ref entry, in value);
        _Entries.Add(entry);

        // Remember the entry for Occurrences.Last (stamped like the collector's counters; no clearing per packet).
        if (fieldId >= 0)
        {
            if (fieldId >= _LastIndexById.Length)
            {
                int size = Math.Max(checked(fieldId + 1), checked(_LastIndexById.Length * 2));
                Array.Resize(ref _LastIndexById, size);
                Array.Resize(ref _LastStampById, size);
            }

            _LastIndexById[fieldId] = _Entries.Count - 1;
            _LastStampById[fieldId] = _Sequence;
        }
    }

    /// <summary>Stores a value into an entry; a bytes payload is copied into the arena and the inline value cleared.</summary>
    /// <param name="entry">The entry to fill.</param>
    /// <param name="value">The value; valid only for the call.</param>
    private void _SetValue(ref Entry entry, in FieldValueData value)
    {
        entry.Type = value.Type;
        if (value.Type == FieldType.Bytes && value.TryGetAsBytes(out ReadOnlyMemory<byte> bytes))
        {
            // Copy now: the payload may alias the packet buffer, which the caller recycles after the parse.
            entry.Bytes = _Bytes.Append(bytes.Span);
            entry.Value = default;
        }
        else
        {
            entry.Bytes = default;
            entry.Value = value;
        }
    }

    #endregion

    #region Closing and committing

    /// <inheritdoc/>
    public void EndPacket(bool complete)
    {
        if (_State != StageState.Open)
        {
            return;
        }

        if (!complete)
        {
            // A muted observer missed fields of this packet; a partial row must never be written.
            _State = StageState.Failed;
            return;
        }

        // Deferred texts are complete now (the parse ended); evaluate each once, into the entry. Evaluating may throw (a faulty text
        // factory of a dissector); the packet then fails like in the columnar stage, nothing of it is written, and the refusal at
        // commit names the cause instead of a missing staging.
        try
        {
            Span<Entry> entries = CollectionsMarshal.AsSpan(_Entries);
            for (int i = 0; i < entries.Length; i++)
            {
                ref Entry entry = ref entries[i];
                entry.Representation = OptionalTextReader.ToNullableString(entry.PendingRepresentation);
                entry.Text = OptionalTextReader.ToNullableString(entry.PendingText);
                entry.PendingRepresentation = default;
                entry.PendingText = default;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _State = StageState.Failed;
            _FailureMessage = Invariant($"Packet {_PacketId}: a custom text failed: {ex.Message}");
            return;
        }

        _State = StageState.Closed;
    }

    /// <inheritdoc/>
    public bool TryCommit(Packet packet, out ExportErrorKind kind, out string? message)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // The exporter formats Entries right after a successful commit and before the next BeginPacket.
        bool sameId = _PacketId == packet.Id.Value;
        if (_State == StageState.Closed && sameId)
        {
            _State = StageState.Committed;
            kind = default;
            message = null;
            return true;
        }

        // Every refusal is a SerializationError (the enum has no "none" member); the callers decide the feed before they call
        // (StagedPacketId, HasFieldTree), so a false result is always a reportable packet error, never "try the tree".
        kind = ExportErrorKind.SerializationError;
        message = (_State, sameId) switch
        {
            (StageState.Failed, true) => _FailureMessage
                ?? Invariant($"Packet {packet.Id.Value} was not exported: its fields were not received completely (an observer failed)."),
            (StageState.Committed, true) =>
                Invariant($"Packet {packet.Id.Value} was already committed."),
            (StageState.Open or StageState.Closed, false) =>
                Invariant($"Packet {packet.Id.Value} was committed while fields of packet {_PacketId} are staged."),
            _ => Invariant($"Packet {packet.Id.Value} carries neither a field tree nor staged fields of this exporter."),
        };
        return false;
    }

    /// <inheritdoc/>
    public void Discard() => _Clear();

    /// <summary>Returns the pooled chunks of the byte arena.</summary>
    public void Dispose() => _Bytes.Dispose();

    /// <summary>Drops the staged entries and their bytes and returns the stage to <see cref="StageState.Empty"/>.</summary>
    private void _Clear()
    {
        _Entries.Clear();
        _Bytes.Reset();
        _PacketId = -1;
        _FailureMessage = null;
        _State = StageState.Empty;
    }

    #endregion
}

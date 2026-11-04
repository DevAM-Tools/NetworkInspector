// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// Accumulates packets into a column-oriented in-memory batch shared by every columnar exporter
/// (PBF columnar blocks, Parquet, DuckDB). Each field becomes a <see cref="FieldColumnBag"/>
/// keyed by field ID; the field-tree topology is flattened into <see cref="Topology"/> rows so
/// sinks can reconstruct hierarchy with a self-join instead of nested containers.
/// <para>
/// The <see cref="Catalog"/> and per-field <see cref="FieldColumnBag"/> instances are kept alive
/// across <see cref="Reset"/> calls (only their row-level state is cleared) so field metadata does
/// not need to be re-derived for every block within one export run. The row lists keep their capacity across blocks, so
/// steady-state blocks do not regrow them. Frame bytes, the additional packet buffers, and <see cref="FieldType.Bytes"/> values are
/// copied into one <see cref="BatchByteArena"/> of pooled chunks instead of one <c>byte[]</c> each. The buffers are packet-level
/// rows beside the frames (<see cref="BufferRowCount"/>) and never touch the field bags, so the field rows are the same with
/// the frames and the buffers on or off.
/// </para>
/// <para>
/// The <see cref="Catalog"/> has one row per field id that occurs in the export: a field with a value gets a row with a
/// table name, and (in the hierarchy shape, which captures the topology) a structure node such as a protocol or a group
/// gets a row without one, so every <see cref="TopologyNode.FieldId"/> can be named.
/// </para>
/// <para>
/// <b>Two ways in, one batch:</b> the hierarchy shape adds a finished packet with a field tree (<see cref="AddPacket"/>); the flat
/// shapes (<see cref="ExportShape.SelectedFields"/>, <see cref="ExportShape.AllFields"/>) are fed field by field while the
/// packet is parsed (the batch is the <see cref="IFieldStage"/> of the exporter's <c>ExportFieldCollector</c>) and committed in
/// <c>OnPacket</c> with <see cref="TryCommit"/>. In both ways the values go straight into the typed column lists of the bags; there
/// is no intermediate structure and no second copy, which is what makes a tree-free export cheap.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; one thread owns the batch at a time. The filling thread calls <see cref="AddPacket"/> and
/// the staging members. The exporter's pipeline then hands a full batch to its writer thread, which calls
/// <see cref="IColumnarBatchSink.WriteBatch"/> and afterwards <see cref="Reset"/>; from the hand-off until <see cref="Reset"/>
/// returns, the filling thread does not touch the batch, and the hand-off publishes with a full barrier
/// (<c>BatchHandOff</c>). <see cref="Dispose"/> runs on the finishing thread after the writer thread was joined.
/// </para>
/// </summary>
/// <seealso cref="IFieldStage"/>
/// <seealso cref="IColumnarBatchSink"/>
/// <seealso cref="FieldColumnBag"/>
internal sealed class ColumnarPacketBatch : IExportBatch, IFieldStage
{
    #region Configuration and state

    /// <summary>The content the batch was created for: shape, selected fields, and the optional columns.</summary>
    internal ExportContent Content { get; }

    /// <summary>
    /// Whether the packet summary string is read per packet. The content members are read once into booleans: the add and
    /// staging paths test them per packet and per field, and a property read of a record is one more indirection than a field read.
    /// </summary>
    private readonly bool _IncludeInfo;

    /// <summary>Whether the captured frame bytes are copied per packet.</summary>
    private readonly bool _IncludeFrameBytes;

    /// <summary>Whether the additional packet buffers (index 1 and up) are copied per packet.</summary>
    private readonly bool _IncludeBuffers;

    /// <summary>Whether the custom text of a field is evaluated and stored.</summary>
    private readonly bool _IncludeCustomText;

    /// <summary>Whether the custom representation of a value is evaluated and stored.</summary>
    private readonly bool _IncludeCustomRepresentation;

    /// <summary>Whether the topology is captured. Only the hierarchy shape captures it: the flat shapes have no node ids to relate.</summary>
    private readonly bool _IncludeTopology;

    /// <summary>Packet-count flush threshold reported by <see cref="AddPacket"/>.</summary>
    private int _MaxPacketsPerBlock { get; }

    /// <summary>Estimated-size flush threshold (bytes) reported by <see cref="AddPacket"/>; bounds the memory of one block.</summary>
    private long _MaxBlockSize { get; }

    /// <summary>Whether packets are added in non-decreasing timestamp order; sinks may use it to declare a sorted column.</summary>
    internal bool IsTimestampSorted { get; }

    /// <summary>Creates a new columnar packet batch.</summary>
    /// <param name="content">
    /// Validated export content; decides the optional columns and whether the topology is captured (only
    /// <see cref="ExportShape.Hierarchy"/>). For <see cref="ExportShape.SelectedFields"/> the catalog and an empty bag per
    /// selected field are created up front (see <see cref="SeedCatalog"/>).
    /// </param>
    /// <param name="maxPacketsPerBlock">Maximum packets before <see cref="AddPacket"/> signals a flush.</param>
    /// <param name="maxBlockSize">Maximum estimated size (bytes) before <see cref="AddPacket"/> signals a flush.</param>
    /// <param name="isTimestampSorted">Whether the caller guarantees packets are added in non-decreasing timestamp order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    internal ColumnarPacketBatch(
        ExportContent content,
        int maxPacketsPerBlock,
        long maxBlockSize,
        bool isTimestampSorted)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        _IncludeInfo = content.Info;
        _IncludeFrameBytes = content.FrameBytes;
        _IncludeBuffers = content.Buffers;
        _IncludeCustomText = content.CustomText;
        _IncludeCustomRepresentation = content.CustomRepresentation;
        _IncludeTopology = content.Shape == ExportShape.Hierarchy;
        _MaxPacketsPerBlock = maxPacketsPerBlock;
        _MaxBlockSize = maxBlockSize;
        IsTimestampSorted = isTimestampSorted;

        // A selected-fields export lists every selected field in the catalog, also those that never occur, and every format
        // can create its tables up front. The seed is per batch (the bags belong to the batch), so every batch of a pipeline
        // carries the same catalog and a sink may read it from whichever batch it receives.
        if (content.Shape == ExportShape.SelectedFields && content.Fields is not null)
        {
            SeedCatalog(content.Fields);
        }
    }

    #endregion

    #region Field bags and catalog

    /// <summary>Column bags by field id. Created on first sight of a field and kept across <see cref="Reset"/>.</summary>
    private readonly Dictionary<int, FieldColumnBag> _FieldBags = new(64);

    /// <summary>
    /// Dense lookup of <see cref="_FieldBags"/> by field id: registered ids are indexes into the stack's field table, so the
    /// per-value lookup is one bounds check and one array load instead of a dictionary probe. The dictionary stays for the
    /// enumeration by the sinks. An id outside the array (a field the stack does not register) is served by the dictionary.
    /// </summary>
    private FieldColumnBag?[] _BagsById = [];

    /// <summary>
    /// Catalog rows by field id: created together with the bag in <see cref="_GetOrCreateBag"/> (value fields) or by
    /// <see cref="_EnsureStructureCatalogEntry"/> (structure nodes).
    /// </summary>
    private readonly Dictionary<int, FieldCatalogEntry> _Catalog = new(64);

    /// <summary>
    /// Running total of the bytes appended to all <see cref="FieldColumnBag"/>s since the last <see cref="Reset"/>.
    /// Kept incrementally so the flush check in <see cref="AddPacket"/> is O(1) instead of O(distinct fields).
    /// </summary>
    private long _FieldBagsEstimatedSize { get; set; }

    /// <summary>
    /// Field metadata keyed by field ID, populated lazily as fields are first observed. Value fields have a table name;
    /// structure nodes (only in the hierarchy shape) have none.
    /// </summary>
    internal IReadOnlyDictionary<int, FieldCatalogEntry> Catalog => _Catalog;

    /// <summary>Per-field column bags, keyed by field ID.</summary>
    internal IReadOnlyDictionary<int, FieldColumnBag> FieldBags => _FieldBags;

    /// <summary>
    /// Running total of the estimated bytes held by all <see cref="FieldBags"/>. Always equal to the sum of
    /// every bag's <see cref="FieldColumnBag.EstimatedSizeBytes"/>; maintained incrementally by <see cref="AddPacket"/>
    /// and reset by <see cref="Reset"/>.
    /// </summary>
    internal long FieldBagsEstimatedSizeBytes => _FieldBagsEstimatedSize;

    /// <summary>
    /// Creates the catalog row and an empty bag of the declared type for every field of <paramref name="selection"/>, so the
    /// catalog lists fields that never occur and every format can create its tables up front ("empty table = never present").
    /// </summary>
    /// <param name="selection">The selected fields; their declared types come from the stack they were resolved against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="selection"/> is null.</exception>
    internal void SeedCatalog(FieldSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        foreach (SelectedField selected in selection.Fields)
        {
            int fieldIdValue = selected.Id.Value;
            if (_FieldBags.ContainsKey(fieldIdValue))
            {
                continue;
            }

            FieldInfo? info = selection.Stack.GetField(selected.Id);
            _ = _RegisterBag(fieldIdValue, selected.FieldType, selection.Stack.FieldCount);
            _Catalog[fieldIdValue] = new(
                fieldIdValue,
                selected.Name,
                info?.UiName ?? selected.Name,
                selected.FieldType,
                info?.ProtocolId.Value ?? -1,
                Invariant($"field_{fieldIdValue}"),
                info?.Description);
        }
    }

    /// <summary>
    /// Creates a bag and registers it in the dictionary (for the sinks' enumeration) and in the dense array (for the per-value
    /// lookup). The catalog row is the caller's business: it is known at different times in the two ways in.
    /// </summary>
    /// <param name="fieldIdValue">Field id the bag accumulates.</param>
    /// <param name="fieldType">The column type of the bag, fixed for the whole export.</param>
    /// <param name="denseSize">The size the dense array is grown to when it is smaller (the stack's field count).</param>
    /// <returns>The new bag.</returns>
    private FieldColumnBag _RegisterBag(int fieldIdValue, FieldType fieldType, int denseSize)
    {
        FieldColumnBag bag = new(fieldIdValue, fieldType, _Bytes);
        _FieldBags[fieldIdValue] = bag;

        // Registered ids are indexes into the stack's field table, so the array is sized once to that table. A negative or
        // unregistered id stays in the dictionary only.
        if (_BagsById.Length < denseSize)
        {
            Array.Resize(ref _BagsById, denseSize);
        }

        if ((uint)fieldIdValue < (uint)_BagsById.Length)
        {
            _BagsById[fieldIdValue] = bag;
        }

        return bag;
    }

    /// <summary>Finds the bag of a field in the dense array first and in the dictionary second.</summary>
    /// <param name="fieldId">The field id.</param>
    /// <returns>The bag of <paramref name="fieldId"/>, or <see langword="null"/> when the field has none in this batch yet.</returns>
    private FieldColumnBag? _FindBag(int fieldId)
    {
        FieldColumnBag?[] bagsById = _BagsById;
        if ((uint)fieldId < (uint)bagsById.Length && bagsById[fieldId] is FieldColumnBag existing)
        {
            return existing;
        }

        return _FieldBags.GetValueOrDefault(fieldId);
    }

    #endregion

    #region Packet columns

    // Initial capacities (256 packets, 8 topology nodes per packet) are sizing hints that avoid the first few List regrowths;
    // they are not limits.

    /// <summary>Packet identifiers, one per packet.</summary>
    private readonly List<int> _PacketIds = new(256);

    /// <summary>Packet timestamps (nanoseconds since Unix epoch), one per packet.</summary>
    private readonly List<long> _Timestamps = new(256);

    /// <summary>Packet summary strings; one per packet when the content asks for them, otherwise empty.</summary>
    private readonly List<string> _Infos = new(256);

    /// <summary>Arena references of the frame bytes, one per packet when the content asks for them, otherwise empty.</summary>
    private readonly List<ArenaRef> _FrameRefs = new(256);

    /// <summary>Byte store of the frame bytes, the additional buffers, and the <see cref="FieldType.Bytes"/> values of all bags.</summary>
    private readonly BatchByteArena _Bytes = new();

    /// <summary>
    /// <c>byte[]</c> copies of the first frames, built by <see cref="FrameBytesList"/> on demand for the sinks that need
    /// arrays (Parquet, PBF); a cache of <see cref="_FrameRefs"/> that is cleared with it.
    /// </summary>
    private readonly List<byte[]> _FrameBytesCache = new(256);

    // The buffer rows are three parallel lists with one entry per additional buffer of the batch (not per packet): a packet
    // without additional buffers adds nothing, a packet with three adds three entries in index order. Invariant: the three lists
    // always have the same length; _AppendPacketColumns adds to all three together and _RollBack trims all three to one mark.

    /// <summary>Packet id of each buffer row; non-decreasing, because packets are added in order.</summary>
    private readonly List<int> _BufferPacketIds = new(16);

    /// <summary>Buffer index (1 to 255) of each buffer row; ascending within one packet.</summary>
    private readonly List<byte> _BufferIndexes = new(16);

    /// <summary>Arena references of the buffer bytes, one per buffer row.</summary>
    private readonly List<ArenaRef> _BufferRefs = new(16);

    /// <summary>
    /// <c>byte[]</c> copies of the buffers, built by <see cref="BufferBytesList"/> on demand for the sinks that need arrays
    /// (Parquet, PBF); a cache of <see cref="_BufferRefs"/> that is cleared with it.
    /// </summary>
    private readonly List<byte[]> _BufferBytesCache = new(16);

    /// <summary>Estimated bytes of the packet-level lists (ids, timestamps, infos, frame bytes, buffers, topology text).</summary>
    private long _BatchEstimatedSize { get; set; }

    /// <summary>Number of packets accumulated in this batch.</summary>
    public int PacketCount => _PacketIds.Count;

    /// <summary>
    /// Appends the packet-level columns of <paramref name="packet"/> (id, timestamp, and the optional info, frame bytes, and
    /// additional buffers). The block both ways in share: <see cref="AddPacket"/> calls it before the field tree,
    /// <see cref="TryCommit"/> after the staged fields, so the two shapes write exactly the same packet rows and the same buffer
    /// rows (the buffers belong to the packet, not to the parse mode).
    /// </summary>
    /// <param name="packet">The packet whose columns are added.</param>
    /// <exception cref="Exception">
    /// Reading the info string runs the dissector's lazy formatter, which may throw anything; the caller rolls the batch back.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A buffer between index 1 and <see cref="Packet.BufferCount"/> - 1 is missing, which is a broken invariant of the packet's
    /// buffer table; the caller rolls the batch back, so no half-copied packet stays.
    /// </exception>
    private void _AppendPacketColumns(Packet packet)
    {
        // The size estimate is a running total (O(1)) so the flush check never walks the data.
        _PacketIds.Add(packet.Id.Value);
        _Timestamps.Add(packet.Timestamp.AsNanos);
        _BatchEstimatedSize += 12; // int PacketId (4) + long Timestamp (8)

        // Optional columns: each is read only when its content flag is set, so a disabled column costs neither time nor memory.
        if (_IncludeInfo)
        {
            string info = packet.Info;
            _Infos.Add(info);
            _BatchEstimatedSize += info.Length * 2; // UTF-16: two bytes per char
        }

        if (_IncludeFrameBytes)
        {
            // Copy required: the batch keeps the bytes until flush and packet buffers may be recycled after OnPacket
            // returns. The copy goes into the arena (pooled chunks), so no array is allocated per frame.
            ReadOnlySpan<byte> frameSpan = packet.Frame.Data.Span;
            _FrameRefs.Add(_Bytes.Append(frameSpan));
            _BatchEstimatedSize += frameSpan.Length;
        }

        if (_IncludeBuffers)
        {
            _AppendBuffers(packet);
        }
    }

    /// <summary>
    /// Copies the additional buffers of <paramref name="packet"/> (index 1 up to <see cref="Packet.BufferCount"/> - 1) into the
    /// arena and records one buffer row per buffer in index order. A packet without additional buffers adds nothing.
    /// </summary>
    /// <param name="packet">The packet whose buffers are copied.</param>
    /// <exception cref="InvalidOperationException">A buffer inside the packet's own buffer count is missing (broken invariant).</exception>
    /// <exception cref="OverflowException">An index above 255 (cannot happen: the packet's table holds at most 255 buffers).</exception>
    /// <remarks>
    /// <para>
    /// Perf: the copy goes into the arena (pooled chunks), so a warm batch allocates nothing per buffer; the byte total is added to
    /// the batch estimate (5 bytes per row for the id and the index plus the bytes), so the batch limits bound this memory too.
    /// Copy required for the same reason as the frame: the packet's buffers may be recycled after <c>OnPacket</c> returns, while
    /// the batch keeps the rows until the flush.
    /// </para>
    /// <para>
    /// Security: the threat is memory exhaustion by hostile input, because a decoded buffer (an inflated body) can be much larger
    /// than the frame it came from. Mitigation: the option is opt-in and the byte estimate includes every copied buffer, so the
    /// batch byte limit flushes before the copies grow without bound. Rejected: capping the size of one buffer here, because a
    /// silent truncation would corrupt the export and the decompression limits of the protocols already bound each buffer.
    /// Residual risk: one batch can still hold the buffers of the packet that crosses the byte limit.
    /// </para>
    /// </remarks>
    private void _AppendBuffers(Packet packet)
    {
        int bufferCount = packet.BufferCount;
        for (int index = 1; index < bufferCount; index++)
        {
            // PacketBuffers.Get reports a missing buffer (a broken invariant of the packet) instead of skipping it, because a
            // skipped buffer would silently shift the exported buffer list.
            ReadOnlySpan<byte> bytes = PacketBuffers.Get(packet, index);

            // Invariant: index <= 255, because the packet's additional-buffer table holds at most 255 entries (the effect-layer key
            // packs the index into eight bits); the checked narrowing reports a broken table instead of truncating the index.
            byte narrowedIndex = checked((byte)index);

            // The two steps that can fail (the narrowing above and the arena copy, which rents a chunk) come first, and the three row
            // lists are then extended together, so they keep the same length even when one of them throws.
            ArenaRef reference = _Bytes.Append(bytes);
            _BufferPacketIds.Add(packet.Id.Value);
            _BufferIndexes.Add(narrowedIndex);
            _BufferRefs.Add(reference);
            _BatchEstimatedSize += 5 + bytes.Length;
        }
    }

    /// <summary>
    /// A packet without a field tree reached the hierarchy feed: the exporters refuse skip packets before the batch, so this is a
    /// broken invariant, reported from a cold helper (the same message every exporter with a hierarchy shape reports).
    /// </summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowSkipPacket() =>
        throw new InvalidOperationException("Skip packets cannot be exported (HasFieldTree is false).");

    /// <summary>
    /// A packet was added to the hierarchy feed while the flat-shape staging holds fields of another packet: a programming error
    /// (one exporter feeds one path), reported from a cold helper.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowStagingOpen() =>
        throw new InvalidOperationException("A packet cannot be added while fields of another packet are staged.");

    /// <summary>
    /// The declared type of a field differs from the type of its staged values, found when the commit completes the catalog:
    /// reported from a cold helper. The commit turns it into a packet failure.
    /// </summary>
    /// <param name="fieldIdValue">The field id.</param>
    /// <param name="declaredType">The type the stack declares for the field.</param>
    /// <param name="stagedType">The type of the values staged for the field.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowDeclaredTypeMismatch(int fieldIdValue, FieldType declaredType, FieldType stagedType) =>
        throw new InvalidOperationException(
            Invariant($"Field {fieldIdValue}: declared type {declaredType} does not match the staged value type {stagedType}."));

    #endregion

    #region Hierarchy feed

    /// <summary>Flattened field-tree topology rows across all packets in this batch.</summary>
    private readonly List<TopologyNode> _Topology = new(256 * 8);

    /// <summary>
    /// Adds a packet's identifiers, optional info/frame bytes, and its full field tree
    /// (topology and per-field values) to the batch. The hierarchy shape's way in.
    /// </summary>
    /// <param name="packet">The packet to add.</param>
    /// <returns><see langword="true"/> if the caller should flush this batch (packet count or estimated size threshold reached).</returns>
    /// <exception cref="InvalidOperationException">
    /// The packet has no field tree (a skip packet), a field value does not match the column type of its bag, an additional buffer
    /// inside the packet's buffer count is missing (a broken invariant, see <see cref="PacketBuffers.Get"/>), or a staging of
    /// the flat shapes is open (one exporter feeds one path per packet; a mix is a broken invariant). The batch is unchanged in
    /// all cases (the packet is added completely or not at all), so the caller can skip the packet and keep using the batch.
    /// </exception>
    internal bool AddPacket(Packet packet)
    {
        // A skip packet has no parsed field tree, so there is nothing to put into the columns: fail loudly instead of
        // writing a packet row without values.
        if (!packet.HasFieldTree)
        {
            _ThrowSkipPacket();
        }

        // The journal and the marks are shared with the staging path: a packet added here while fields of another packet are
        // staged would be undone by the staging's rollback or would undo it. That is a programming error, not a data error.
        if (_StageState != StageState.Empty)
        {
            _ThrowStagingOpen();
        }

        // All-or-nothing: a packet that fails part-way (a value that does not fit its column, or a deferred text that
        // throws) must not leave a packet row without all its field rows. The row counts of the packet-level lists, the
        // arena position and the running size totals are remembered here; every field row is journaled with the state of its
        // bag before the row is added. On failure the journals and these marks undo everything, so the caller can skip the
        // packet and continue with a consistent batch (the rows of a half-added packet would otherwise be written to the
        // output).
        BatchMarks marks = _TakeMarks();
        _Journal.Clear();
        _CatalogJournal.Clear();

        try
        {
            _AppendPacketColumns(packet);

            // Field tree: the root is an invisible container, so its children are the top-level nodes (parent id -1).
            // Children(materialize: true) expands a lazily expanded container itself (its children would otherwise be missing
            // from the export) and yields nothing for a leaf. nodeCounter numbers the nodes in depth-first pre-order,
            // restarting at 0 for every packet, so a parent's node id is always smaller than its children's ids.
            Field root = packet.RootField();
            int nodeCounter = 0;
            foreach (Field child in root.Children(materialize: true))
            {
                _EncodeField(packet, child, parentNodeId: -1, ref nodeCounter);
            }
        }
        catch
        {
            _RollBack(in marks);
            throw;
        }

        // Flush when either bound is hit. The size bound is an estimate (see FieldColumnBag), so a block may exceed it
        // slightly; the packet that crosses the bound stays in this block (a packet is never split across blocks).
        return ShouldFlush;
    }

    /// <summary>
    /// Adds one field (topology row and, when it carries a value, a column row) and then its children recursively.
    /// Recursion depth equals the nesting depth of the packet's field tree, which is bounded by the protocol nesting.
    /// </summary>
    /// <param name="packet">Owning packet.</param>
    /// <param name="field">Field to encode.</param>
    /// <param name="parentNodeId">Node id of the parent, or -1 for top-level fields.</param>
    /// <param name="nodeCounter">Next free node id of this packet; incremented once per field, with or without a value.</param>
    private void _EncodeField(Packet packet, Field field, int parentNodeId, ref int nodeCounter)
    {
        // The node id is taken before the children are visited (pre-order), and it is consumed even when the field has no
        // value column or the topology is disabled, so ids stay identical regardless of the detail flags.
        int nodeId = nodeCounter++;
        int packetIdValue = packet.Id.Value;

        // The value is read once: it decides both whether the node has a value column and what the column row holds.
        FieldValue fieldValue = field.Value;
        FieldValueData data = fieldValue.Data;
        bool hasValue = data.Type != FieldType.None;

        if (_IncludeTopology)
        {
            // A node without a value column (a protocol layer, a group) keeps its display line in the topology row; a value
            // node keeps its text in its field table.
            string? nodeText = hasValue
                ? null
                : OptionalTextReader.GetCustomText(field, _IncludeCustomText);
            _Topology.Add(new(packetIdValue, nodeId, field.FieldId.Value, parentNodeId, nodeText));
            if (nodeText is not null)
            {
                // Same estimate as a string in a bag (2 bytes per char plus 4); _RollBack restores the total.
                _BatchEstimatedSize += (nodeText.Length * 2L) + 4;
            }

            // A structure node is named only through the catalog; without this row the topology field id of a protocol or
            // group node could not be resolved without the dissector assemblies.
            if (!hasValue)
            {
                _EnsureStructureCatalogEntry(field, packet.Stack);
            }
        }

        if (hasValue)
        {
            FieldColumnBag bag = _GetOrCreateBag(
                field, packet.Stack, data.Type, out bool bagCreated, out FieldCatalogEntry? replacedEntry);

            // Account only the bytes this row appends, so the batch total stays O(1) to read. A type mismatch
            // throws before the bag mutates, so the delta is never applied for a rejected row.
            // The journal entry (reused list, no per-packet allocation) is what AddPacket uses to undo this row if a later
            // field of the same packet fails.
            long bagSizeBefore = bag.EstimatedSizeBytes;
            _Journal.Add(new RowMark(bag, bag.RowCount, bagSizeBefore, bagCreated, replacedEntry));
            bag.Add(
                packetIdValue,
                nodeId,
                in data,
                OptionalTextReader.GetCustomRepresentation(fieldValue, _IncludeCustomRepresentation),
                OptionalTextReader.GetCustomText(field, _IncludeCustomText));
            _FieldBagsEstimatedSize += bag.EstimatedSizeBytes - bagSizeBefore;
        }

        // Children(materialize: true) expands a lazy container itself and yields nothing for a leaf.
        foreach (Field child in field.Children(materialize: true))
        {
            _EncodeField(packet, child, nodeId, ref nodeCounter);
        }
    }

    /// <summary>
    /// Adds a catalog row with no table for a structure node on first sight; journaled for rollback so a rejected packet
    /// leaves no row behind.
    /// </summary>
    /// <param name="field">The structure node (a field without a value).</param>
    /// <param name="stack">Protocol stack that holds the field registration.</param>
    private void _EnsureStructureCatalogEntry(Field field, Stack stack)
    {
        int fieldIdValue = field.FieldId.Value;
        if (_Catalog.ContainsKey(fieldIdValue))
        {
            return;
        }

        FieldInfo? info = stack.GetField(field.FieldId);
        string name = info?.Name ?? fieldIdValue.ToString(CultureInfo.InvariantCulture);
        _Catalog[fieldIdValue] = new(
            fieldIdValue, name, info?.UiName ?? name, FieldType.None, info?.ProtocolId.Value ?? -1, null, info?.Description);
        _CatalogJournal.Add(fieldIdValue);
    }

    /// <summary>
    /// Returns the column bag of a field, creating the bag and its catalog row on first sight of the field.
    /// </summary>
    /// <param name="field">The field being added.</param>
    /// <param name="stack">Protocol stack that holds the field registration.</param>
    /// <param name="extractedType">Type of the value that triggered the lookup; used when the field is not registered.</param>
    /// <param name="created"><see langword="true"/> when this call created the bag (and its catalog row).</param>
    /// <param name="replacedEntry">
    /// The structure catalog row that the new value row replaced, or <see langword="null"/> when there was none or the bag
    /// already existed.
    /// </param>
    /// <returns>The bag for the field id.</returns>
    private FieldColumnBag _GetOrCreateBag(
        Field field, Stack stack, FieldType extractedType, out bool created, out FieldCatalogEntry? replacedEntry)
    {
        // Fast path, one bounds check and one array load per value row.
        int fieldIdValue = field.FieldId.Value;
        FieldColumnBag?[] bagsById = _BagsById;
        if ((uint)fieldIdValue < (uint)bagsById.Length && bagsById[fieldIdValue] is FieldColumnBag existing)
        {
            created = false;
            replacedEntry = null;
            return existing;
        }

        // An id outside the dense array is served by the dictionary.
        if (_FieldBags.TryGetValue(fieldIdValue, out FieldColumnBag? known))
        {
            created = false;
            replacedEntry = null;
            return known;
        }

        created = true;

        // Slow path, once per field and export: the declared (registered) type fixes the column type for the whole
        // export, so every row of a column has one type. Only a field that is unregistered or declares no type (None) falls
        // back to the type of the first value seen; later rows of a different type are rejected by FieldColumnBag.Add
        // instead of changing the column.
        FieldInfo? info = stack.GetField(field.FieldId);
        FieldType fieldType = info is { FieldType: not FieldType.None } ? info.FieldType : extractedType;

        FieldColumnBag bag = _RegisterBag(fieldIdValue, fieldType, stack.FieldCount);

        // Unregistered fields still get a catalog row, named by their numeric id, so every column in the output can be
        // looked up in the catalog. The table name is derived from the id only (field_{id}). The row replaces a structure
        // row of the same id, which _RollBack restores.
        _Catalog.TryGetValue(fieldIdValue, out replacedEntry);
        string name = info?.Name ?? fieldIdValue.ToString(CultureInfo.InvariantCulture);
        string uiName = info?.UiName ?? name;
        int protocolIdValue = info?.ProtocolId.Value ?? -1;
        _Catalog[fieldIdValue] = new(
            fieldIdValue, name, uiName, fieldType, protocolIdValue, Invariant($"field_{fieldIdValue}"), info?.Description);

        return bag;
    }

    #endregion

    #region Flat-shape staging

    /// <summary>State of the flat-shape staging. There is no committed state: a commit empties the staging.</summary>
    private enum StageState : byte
    {
        /// <summary>Nothing staged.</summary>
        Empty,

        /// <summary>Fields of the packet are arriving.</summary>
        Open,

        /// <summary>The packet ended completely; it can be committed.</summary>
        Closed,

        /// <summary>The staging cannot be committed (a muted observer, a value that does not fit, a faulty text).</summary>
        Failed,
    }

    /// <summary>A custom text kept as a <see cref="LazyString"/> until the end of the packet (see <see cref="EndPacket"/>).</summary>
    /// <param name="Bag">The bag of the staged row.</param>
    /// <param name="Row">Row index in <paramref name="Bag"/>.</param>
    /// <param name="Representation">The custom representation, evaluated at the end of the packet.</param>
    /// <param name="Text">The custom text, evaluated at the end of the packet.</param>
    private readonly record struct PendingText(FieldColumnBag Bag, int Row, LazyString Representation, LazyString Text);

    /// <summary>The undo marks taken at <see cref="BeginPacket"/>; the same marks <see cref="AddPacket"/> uses.</summary>
    private BatchMarks _StagingMarks;

    /// <summary>Id of the packet being staged; -1 when nothing is staged.</summary>
    private int _StagedPacketId = -1;

    /// <summary>Where the staging is in its life: empty, open, closed, or failed.</summary>
    private StageState _StageState;

    /// <summary>The reason a failed staging cannot be committed; null until a failure happened.</summary>
    private string? _FailureMessage;

    /// <summary>
    /// Rows whose texts are evaluated at the end of the packet. Reused list; the <see cref="LazyString"/> structs are kept because a
    /// dissector may complete a deferred text later in the parse (a count that is known only after the elements).
    /// </summary>
    private readonly List<PendingText> _PendingTexts = new(64);

    /// <inheritdoc/>
    public int StagedPacketId => _StagedPacketId;

    /// <inheritdoc/>
    public void BeginPacket(int packetId, long timestampNanos)
    {
        // An uncommitted staging (the filter rejected the packet, the parse failed, a rejected packet was replayed) is undone
        // first: the next packet starts from the state the batch had when the committed packets ended.
        if (_StageState != StageState.Empty)
        {
            Discard();
        }

        _StagingMarks = _TakeMarks();
        _Journal.Clear();
        _CatalogJournal.Clear();
        _PendingTexts.Clear();
        _FailureMessage = null;
        _StagedPacketId = packetId;
        _StageState = StageState.Open;
    }

    /// <inheritdoc/>
    public void AddValue(int fieldId, int occurrence, in FieldValueData value, LazyString customRepresentation, LazyString customText)
    {
        if (_StageState != StageState.Open)
        {
            return; // a failed or closed staging takes no more rows
        }

        FieldColumnBag bag = _GetOrCreateStagedBag(fieldId, value.Type, out bool created);

        // A value whose type differs from the type of its column (a dissector that reuses a field id with another type) is an
        // expected failure of this packet, decided by a type comparison before the bag is touched: no exception is thrown and
        // caught per value. The packet fails at commit with this message (the same outcome as the rollback of AddPacket); the
        // staging is rolled back there. A bag created by this call has the type of the value, so only a known bag can mismatch.
        if (bag.FieldType != value.Type)
        {
            _FailStaging(fieldId, bag.FieldType, value.Type);
            return;
        }

        // Journal first, then add: the mark records the bag as it was, so a rollback cuts exactly this row off again.
        long sizeBefore = bag.EstimatedSizeBytes;
        _Journal.Add(new RowMark(bag, bag.RowCount, sizeBefore, created, ReplacedCatalogEntry: null));
        bag.Add(_StagedPacketId, occurrence, in value, customRepresentation: null, customText: null);
        _FieldBagsEstimatedSize += bag.EstimatedSizeBytes - sizeBefore;
        _KeepText(bag, bag.RowCount - 1, customRepresentation, customText);
    }

    /// <summary>
    /// Fails the open staging because a value does not fit the type of its column: the staging takes no more rows and the commit
    /// refuses the packet with a message that names the field and both types.
    /// </summary>
    /// <param name="fieldId">The field id of the value.</param>
    /// <param name="columnType">The type of the column of that field.</param>
    /// <param name="valueType">The type of the rejected value.</param>
    private void _FailStaging(int fieldId, FieldType columnType, FieldType valueType)
    {
        _StageState = StageState.Failed;
        _FailureMessage = Invariant(
            $"Packet {_StagedPacketId}: Field {fieldId}: expected {columnType} but value type was {valueType}.");
    }

    /// <inheritdoc/>
    public void ReplaceLastValue(int fieldId, in FieldValueData value, LazyString customRepresentation, LazyString customText)
    {
        if (_StageState != StageState.Open)
        {
            return;
        }

        // No row of this staging in the bag yet (the collector replaces only after it added a first value): treat it as the
        // first value so the call can never lose a value or overwrite a committed row. The journal holds one mark per row this
        // staging added, so a mark for the bag proves that the bag's last row belongs to this packet.
        FieldColumnBag? bag = _FindBag(fieldId);
        if (bag is null || !_StagedRowExists(bag))
        {
            AddValue(fieldId, 0, in value, customRepresentation, customText);
            return;
        }

        // Same type check as AddValue, before the old row is cut off: a rejected value leaves the bag unchanged.
        if (bag.FieldType != value.Type)
        {
            _FailStaging(fieldId, bag.FieldType, value.Type);
            return;
        }

        long sizeBefore = bag.EstimatedSizeBytes;
        bag.ReplaceLast(in value, customRepresentation: null, customText: null);
        _FieldBagsEstimatedSize += bag.EstimatedSizeBytes - sizeBefore;

        // The replaced value's kept texts belong to the old value: drop them and keep the new ones.
        _DropText(bag, bag.RowCount - 1);
        _KeepText(bag, bag.RowCount - 1, customRepresentation, customText);
    }

    /// <inheritdoc/>
    public void EndPacket(bool complete)
    {
        if (_StageState != StageState.Open)
        {
            return;
        }

        if (!complete)
        {
            // A muted observer missed fields of this packet; a partial packet must never be written.
            _StageState = StageState.Failed;
            _FailureMessage = Invariant(
                $"Packet {_StagedPacketId} was not exported: its fields were not received completely (an observer failed).");
            return;
        }

        // The parse ended, so deferred texts are complete: evaluate each once into its row. Evaluating may throw (a faulty
        // text factory); the packet then fails like a value that does not fit, and nothing of it is written.
        try
        {
            foreach (PendingText pending in _PendingTexts)
            {
                string? representation = _IncludeCustomRepresentation
                    ? OptionalTextReader.ToNullableString(pending.Representation)
                    : null;
                string? text = _IncludeCustomText
                    ? OptionalTextReader.ToNullableString(pending.Text)
                    : null;
                long sizeBefore = pending.Bag.EstimatedSizeBytes;
                pending.Bag.SetTexts(pending.Row, representation, text);
                _FieldBagsEstimatedSize += pending.Bag.EstimatedSizeBytes - sizeBefore;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _StageState = StageState.Failed;
            _FailureMessage = Invariant($"Packet {_StagedPacketId}: a custom text failed: {ex.Message}");
            return;
        }

        _PendingTexts.Clear();
        _StageState = StageState.Closed;
    }

    /// <inheritdoc/>
    public bool TryCommit(Packet packet, out ExportErrorKind kind, out string? message)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // Contract checks first: only the closed staging of this very packet becomes output (the commit rule, exporter guide
        // §15). Every refusal is a SerializationError (the enum has no "none" member); the caller decides the feed before it
        // calls, so a false result is always a reportable packet error, never "try the tree".
        bool sameId = _StagedPacketId == packet.Id.Value;
        if (_StageState != StageState.Closed || !sameId)
        {
            kind = ExportErrorKind.SerializationError;
            message = _CommitRefusal(packet, sameId);

            // A failed staging of this packet is cleaned up now; a staging of another packet stays until its own packet
            // commits or the next BeginPacket drops it.
            if (_StageState == StageState.Failed && sameId)
            {
                Discard();
            }

            return false;
        }

        // Packet-level columns are read from the packet now (Info works without a field tree through the packet's lazy info); then
        // the catalog rows of the bags this packet created are completed with the packet's stack. A declared type that differs
        // from the value type of a new bag fails the packet, exactly as the type check of AddPacket does.
        // All-or-nothing like AddPacket: the info text is a dissector's lazy formatter and may throw anything, and a type
        // mismatch of a new bag throws InvalidOperationException. Either way the staging (and the packet columns appended so
        // far) is rolled back to the marks taken at BeginPacket, so the batch is exactly as it was. Out of memory propagates.
        try
        {
            _AppendPacketColumns(packet);
            _CompleteStagedCatalog(packet.Stack);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Discard();
            kind = ex is IOException
                ? ExportErrorKind.IoError
                : ExportErrorKind.SerializationError;
            message = Invariant($"Packet {packet.Id.Value}: {ex.Message}");
            return false;
        }

        _Journal.Clear();
        _CatalogJournal.Clear();
        _PendingTexts.Clear();
        _StagedPacketId = -1;
        _StageState = StageState.Empty;
        kind = default;
        message = null;
        return true;
    }

    /// <inheritdoc/>
    public void Discard()
    {
        if (_StageState == StageState.Empty)
        {
            return;
        }

        _RollBack(in _StagingMarks);
        _PendingTexts.Clear();
        _StagedPacketId = -1;
        _StageState = StageState.Empty;
    }

    /// <summary>Whether the open staging added a row to <paramref name="bag"/> (a journal mark for the bag exists).</summary>
    /// <param name="bag">The bag of the field being replaced.</param>
    /// <returns><see langword="true"/> when the bag's last row belongs to the staged packet.</returns>
    /// <remarks>Cost: a backward scan of the journal, which holds the rows of one packet only.</remarks>
    private bool _StagedRowExists(FieldColumnBag bag)
    {
        for (int index = _Journal.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(_Journal[index].Bag, bag))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The staging counterpart of <see cref="_GetOrCreateBag"/>: finds the bag of a field or creates it with the type of the first
    /// value, without a catalog row. The row follows at commit (<see cref="_CompleteStagedCatalog"/>), when the packet's stack is
    /// at hand; a selected field's bag already exists (<see cref="SeedCatalog"/>) with its declared type.
    /// </summary>
    /// <param name="fieldId">The field id.</param>
    /// <param name="valueType">The type of the first value; becomes the column type of a new bag.</param>
    /// <param name="created"><see langword="true"/> when this call created the bag.</param>
    /// <returns>The bag for <paramref name="fieldId"/>.</returns>
    /// <exception cref="OverflowException">The dense array would exceed <see cref="int.MaxValue"/> entries.</exception>
    private FieldColumnBag _GetOrCreateStagedBag(int fieldId, FieldType valueType, out bool created)
    {
        FieldColumnBag? existing = _FindBag(fieldId);
        if (existing is not null)
        {
            created = false;
            return existing;
        }

        created = true;

        // The dense array grows with the largest id seen (doubling), because the stack's field count is not known here; the
        // ids come from the collector, which delivers only registered ids.
        int denseSize = _BagsById.Length;
        if (fieldId >= denseSize)
        {
            denseSize = Math.Max(checked(fieldId + 1), checked(denseSize * 2));
        }

        return _RegisterBag(fieldId, valueType, denseSize);
    }

    /// <summary>
    /// Adds the catalog rows of the bags the staged packet created, with the declared types and names of the packet's stack.
    /// A declared type that is neither <see cref="FieldType.None"/> nor the bag's type fails the packet: AddPacket would have
    /// created the bag with the declared type and rejected the value, and the two ways in must agree.
    /// </summary>
    /// <param name="stack">The stack of the committed packet.</param>
    /// <exception cref="InvalidOperationException">A declared type differs from the type of the staged values.</exception>
    private void _CompleteStagedCatalog(Stack stack)
    {
        foreach (RowMark mark in _Journal)
        {
            if (!mark.Created)
            {
                continue;
            }

            int fieldIdValue = mark.Bag.FieldIdValue;
            if (_Catalog.ContainsKey(fieldIdValue))
            {
                continue; // the same bag was journaled by an earlier value of this packet
            }

            FieldInfo? info = stack.GetField(new FieldId(fieldIdValue));
            if (info is { FieldType: not FieldType.None } && info.FieldType != mark.Bag.FieldType)
            {
                _ThrowDeclaredTypeMismatch(fieldIdValue, info.FieldType, mark.Bag.FieldType);
            }

            string name = info?.Name ?? fieldIdValue.ToString(CultureInfo.InvariantCulture);
            _Catalog[fieldIdValue] = new(
                fieldIdValue,
                name,
                info?.UiName ?? name,
                mark.Bag.FieldType,
                info?.ProtocolId.Value ?? -1,
                Invariant($"field_{fieldIdValue}"),
                info?.Description);
        }
    }

    /// <summary>Keeps the texts of a staged row until the end of the packet, when deferred texts are complete.</summary>
    /// <param name="bag">The bag of the staged row.</param>
    /// <param name="row">Row index in <paramref name="bag"/>.</param>
    /// <param name="representation">The custom representation; absent stays absent.</param>
    /// <param name="text">The custom text; absent stays absent.</param>
    private void _KeepText(FieldColumnBag bag, int row, LazyString representation, LazyString text)
    {
        if (representation.IsNull && text.IsNull)
        {
            return;
        }

        _PendingTexts.Add(new PendingText(bag, row, representation, text));
    }

    /// <summary>Forgets the kept texts of a row (the row got a new value; see <see cref="ReplaceLastValue"/>).</summary>
    /// <param name="bag">The bag of the replaced row.</param>
    /// <param name="row">Row index in <paramref name="bag"/>.</param>
    private void _DropText(FieldColumnBag bag, int row)
    {
        for (int index = _PendingTexts.Count - 1; index >= 0; index--)
        {
            PendingText pending = _PendingTexts[index];
            if (ReferenceEquals(pending.Bag, bag) && pending.Row == row)
            {
                _PendingTexts.RemoveAt(index);
            }
        }
    }

    /// <summary>The reason a commit is refused, naming the cause so the error event is actionable.</summary>
    /// <param name="packet">The packet whose commit is refused.</param>
    /// <param name="sameId">Whether the staging belongs to <paramref name="packet"/>.</param>
    /// <returns>The message for the error event.</returns>
    private string _CommitRefusal(Packet packet, bool sameId)
    {
        int id = packet.Id.Value;
        if (_StageState == StageState.Failed && sameId)
        {
            if (_FailureMessage is not null)
            {
                return _FailureMessage;
            }

            return Invariant($"Packet {id} was not exported: its fields could not be staged.");
        }

        if (_StageState != StageState.Empty && !sameId)
        {
            return Invariant($"Packet {id} was committed while fields of packet {_StagedPacketId} are staged.");
        }

        return Invariant($"Packet {id} carries neither a field tree nor staged fields of this exporter.");
    }

    #endregion

    #region Rollback

    /// <summary>
    /// Undo log of the packet being added: one entry per field row, holding the bag's state before the row. Cleared at the
    /// start of every <see cref="AddPacket"/>; the list is reused, so adding a packet allocates nothing for it once warm.
    /// </summary>
    private readonly List<RowMark> _Journal = new(64);

    /// <summary>Field ids whose structure catalog row was added by the packet being added; removed again on rollback.</summary>
    private readonly List<int> _CatalogJournal = new(16);

    /// <summary>State of one column bag before a row was added to it; the undo record of <see cref="_Journal"/>.</summary>
    /// <param name="Bag">The bag the row went into.</param>
    /// <param name="RowCount">Row count of the bag before the row.</param>
    /// <param name="EstimatedSizeBytes">Size estimate of the bag before the row.</param>
    /// <param name="Created">Whether the bag was created for this row (then it is removed again on rollback).</param>
    /// <param name="ReplacedCatalogEntry">
    /// The structure catalog row the bag's value row replaced (the field was a structure node earlier in the export), or
    /// <see langword="null"/>; restored on rollback so the earlier row survives.
    /// </param>
    private readonly record struct RowMark(
        FieldColumnBag Bag, int RowCount, long EstimatedSizeBytes, bool Created, FieldCatalogEntry? ReplacedCatalogEntry);

    /// <summary>
    /// Everything a rollback needs to put the packet-level state back: the list lengths, the size totals, and the arena
    /// position. Taken before <see cref="AddPacket"/> adds a packet and before a staging opens, so a discarded staging leaves
    /// the batch exactly as it was.
    /// </summary>
    /// <param name="PacketCount">Length of the packet id and timestamp lists.</param>
    /// <param name="InfoCount">Length of the info list.</param>
    /// <param name="FrameCount">Length of the frame reference list and its array cache.</param>
    /// <param name="BufferCount">Length of the three buffer row lists and of the buffer array cache.</param>
    /// <param name="TopologyCount">Length of the topology list.</param>
    /// <param name="BatchSize">The packet-level size estimate.</param>
    /// <param name="FieldBagsSize">The field-bag size total.</param>
    /// <param name="Arena">The arena position.</param>
    private readonly record struct BatchMarks(
        int PacketCount,
        int InfoCount,
        int FrameCount,
        int BufferCount,
        int TopologyCount,
        long BatchSize,
        long FieldBagsSize,
        ArenaMark Arena);

    /// <summary>Remembers the packet-level state that a rollback restores (see <see cref="BatchMarks"/>).</summary>
    /// <returns>The current list lengths, size totals, and arena position.</returns>
    private BatchMarks _TakeMarks() =>
        new(
            _PacketIds.Count,
            _Infos.Count,
            _FrameRefs.Count,
            _BufferRefs.Count,
            _Topology.Count,
            _BatchEstimatedSize,
            _FieldBagsEstimatedSize,
            _Bytes.Mark());

    /// <summary>
    /// Undoes a packet that failed part-way (see <see cref="AddPacket"/>) or a staging that was never committed: the field rows go
    /// first, newest to oldest, so the earliest mark of a bag is applied last, and a bag created for this packet is dropped again
    /// together with its catalog row (a structure row it replaced comes back). Then the structure rows added for this packet go,
    /// the arena is cut back, and the packet-level lists and the size totals return to their remembered values.
    /// </summary>
    /// <param name="marks">The state before the packet.</param>
    private void _RollBack(in BatchMarks marks)
    {
        int packetCount = marks.PacketCount;
        int infoCount = marks.InfoCount;
        int frameCount = marks.FrameCount;
        int bufferCount = marks.BufferCount;
        int topologyCount = marks.TopologyCount;
        long batchSize = marks.BatchSize;
        long fieldBagsSize = marks.FieldBagsSize;
        ArenaMark arenaMark = marks.Arena;
        for (int index = _Journal.Count - 1; index >= 0; index--)
        {
            RowMark mark = _Journal[index];
            mark.Bag.TruncateTo(mark.RowCount, mark.EstimatedSizeBytes);
            if (mark.Created)
            {
                int fieldIdValue = mark.Bag.FieldIdValue;
                _FieldBags.Remove(fieldIdValue);
                if ((uint)fieldIdValue < (uint)_BagsById.Length)
                {
                    _BagsById[fieldIdValue] = null;
                }

                if (mark.ReplacedCatalogEntry is not null)
                {
                    // The field was a structure node earlier in the export; its row must survive the rollback of this packet.
                    _Catalog[fieldIdValue] = mark.ReplacedCatalogEntry;
                }
                else
                {
                    _Catalog.Remove(fieldIdValue);
                }
            }
        }

        _Journal.Clear();

        // A structure row added by this packet that no value row replaced (the replaced case is handled above).
        foreach (int fieldIdValue in _CatalogJournal)
        {
            if (_Catalog.TryGetValue(fieldIdValue, out FieldCatalogEntry? entry) && entry.TableName is null)
            {
                _Catalog.Remove(fieldIdValue);
            }
        }

        _CatalogJournal.Clear();

        _Bytes.TruncateTo(arenaMark);
        _TrimTo(_PacketIds, packetCount);
        _TrimTo(_Timestamps, packetCount);
        _TrimTo(_Infos, infoCount);
        _TrimTo(_FrameRefs, frameCount);
        _TrimTo(_FrameBytesCache, frameCount);

        // The three buffer row lists and their array cache return to the mark together (same-length invariant).
        _TrimTo(_BufferPacketIds, bufferCount);
        _TrimTo(_BufferIndexes, bufferCount);
        _TrimTo(_BufferRefs, bufferCount);
        _TrimTo(_BufferBytesCache, bufferCount);
        _TrimTo(_Topology, topologyCount);
        _BatchEstimatedSize = batchSize;
        _FieldBagsEstimatedSize = fieldBagsSize;
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

    #region Sink views

    /// <summary>Flattened field-tree topology rows across all packets in this batch.</summary>
    internal IReadOnlyList<TopologyNode> Topology => _Topology;

    /// <summary>Packet identifiers, one per packet (same range as <see cref="PacketIds"/>).</summary>
    internal IReadOnlyList<int> PacketIds => _PacketIds;

    /// <summary>Packet timestamps (nanoseconds since Unix epoch), one per packet.</summary>
    internal IReadOnlyList<long> Timestamps => _Timestamps;

    /// <summary>
    /// Packet summary strings, populated only when <see cref="ExportContent.Info"/>
    /// is set; empty otherwise.
    /// </summary>
    internal IReadOnlyList<string> Infos => _Infos;

    /// <summary>
    /// Raw captured frame bytes as arrays, populated only when <see cref="ExportContent.FrameBytes"/>
    /// is set; empty otherwise.
    /// </summary>
    /// <remarks>
    /// Built on first access from the arena and cached until <see cref="Reset"/>; this costs one array per frame, so a sink
    /// that can read spans uses <see cref="GetFrameBytes"/> instead (the DuckDB sink does).
    /// </remarks>
    internal IReadOnlyList<byte[]> FrameBytesList
    {
        get
        {
            for (int index = _FrameBytesCache.Count; index < _FrameRefs.Count; index++)
            {
                _FrameBytesCache.Add(_Bytes.Get(_FrameRefs[index]).ToArray());
            }

            return _FrameBytesCache;
        }
    }

    // Span views for the flush loops of a sink: indexing a span avoids an interface call per cell. A span is valid only
    // while the batch is not modified (a list that grows moves its array), so a sink takes them inside WriteBatch, where
    // the owning thread does not call AddPacket.

    /// <summary>Span view of <see cref="PacketIds"/>.</summary>
    internal ReadOnlySpan<int> PacketIdSpan => CollectionsMarshal.AsSpan(_PacketIds);

    /// <summary>Span view of <see cref="Timestamps"/>.</summary>
    internal ReadOnlySpan<long> TimestampSpan => CollectionsMarshal.AsSpan(_Timestamps);

    /// <summary>Span view of <see cref="Infos"/>.</summary>
    internal ReadOnlySpan<string> InfoSpan => CollectionsMarshal.AsSpan(_Infos);

    /// <summary>Span view of <see cref="Topology"/>.</summary>
    internal ReadOnlySpan<TopologyNode> TopologySpan => CollectionsMarshal.AsSpan(_Topology);

    /// <summary>
    /// Returns the frame bytes of packet <paramref name="packetIndex"/> without allocating. Valid only when
    /// <see cref="ExportContent.FrameBytes"/> is set.
    /// </summary>
    /// <param name="packetIndex">Index of the packet in this batch, from 0 to <see cref="PacketCount"/> - 1.</param>
    /// <returns>A view of the arena, valid until the next <see cref="AddPacket"/>, <see cref="Reset"/> or <see cref="Dispose"/>.</returns>
    /// <remarks>Writable only because the appender API of DuckDB.NET takes <c>Span&lt;byte&gt;</c>; callers must not write.</remarks>
    internal Span<byte> GetFrameBytes(int packetIndex) => _Bytes.Get(_FrameRefs[packetIndex]);

    // The buffer rows: one row per additional buffer of the batch, in packet order and ascending buffer index within a packet. They
    // are empty unless ExportContent.Buffers is set, and a packet without additional buffers has no row.

    /// <summary>
    /// Number of buffer rows in the batch (all additional buffers of all packets); 0 unless <see cref="ExportContent.Buffers"/> is
    /// set. This is not the packet count: a packet adds one row per additional buffer.
    /// </summary>
    internal int BufferRowCount => _BufferRefs.Count;

    /// <summary>
    /// Packet id of each buffer row (same length as <see cref="BufferRowCount"/>); non-decreasing. Valid only while the batch is not
    /// modified (see the span rule above).
    /// </summary>
    internal ReadOnlySpan<int> BufferPacketIdSpan => CollectionsMarshal.AsSpan(_BufferPacketIds);

    /// <summary>
    /// Buffer index (1 to 255) of each buffer row (same length as <see cref="BufferRowCount"/>); ascending within one packet.
    /// Valid only while the batch is not modified (see the span rule above).
    /// </summary>
    internal ReadOnlySpan<byte> BufferIndexSpan => CollectionsMarshal.AsSpan(_BufferIndexes);

    /// <summary>
    /// Returns the bytes of buffer row <paramref name="row"/> without allocating. Valid only when
    /// <see cref="ExportContent.Buffers"/> is set.
    /// </summary>
    /// <param name="row">Index of the buffer row, from 0 to <see cref="BufferRowCount"/> - 1.</param>
    /// <returns>
    /// A view of the arena, valid until the next <see cref="AddPacket"/>, <see cref="Reset"/> or <see cref="Dispose"/>; empty for a
    /// bound empty buffer (a writer keeps an empty buffer as an empty value, never as absent).
    /// </returns>
    /// <remarks>Writable only because the appender API of DuckDB.NET takes <c>Span&lt;byte&gt;</c>; callers must not write.</remarks>
    internal Span<byte> GetBufferBytes(int row) => _Bytes.Get(_BufferRefs[row]);

    /// <summary>
    /// The bytes of every buffer row as arrays (same length as <see cref="BufferRowCount"/>); empty unless
    /// <see cref="ExportContent.Buffers"/> is set.
    /// </summary>
    /// <remarks>
    /// Built on first access from the arena and cached until <see cref="Reset"/>, like <see cref="FrameBytesList"/>; this costs one
    /// array per buffer, so a sink that can read spans uses <see cref="GetBufferBytes"/> instead (the DuckDB and PBF sinks do).
    /// </remarks>
    internal IReadOnlyList<byte[]> BufferBytesList
    {
        get
        {
            for (int index = _BufferBytesCache.Count; index < _BufferRefs.Count; index++)
            {
                _BufferBytesCache.Add(_Bytes.Get(_BufferRefs[index]).ToArray());
            }

            return _BufferBytesCache;
        }
    }

    #endregion

    #region Block lifecycle

    /// <summary><see langword="true"/> once <see cref="Dispose"/> ran.</summary>
    private bool _Disposed;

    /// <summary>
    /// Rough estimate of the memory consumed by this batch, in bytes: the packet-level lists plus
    /// <see cref="FieldBagsEstimatedSizeBytes"/>. O(1); both parts are running totals.
    /// </summary>
    public long EstimatedSizeBytes => _BatchEstimatedSize + _FieldBagsEstimatedSize;

    /// <summary>Whether a flush bound is reached (packet count or estimated size); the exporter reads it after every packet.</summary>
    internal bool ShouldFlush => PacketCount >= _MaxPacketsPerBlock || EstimatedSizeBytes >= _MaxBlockSize;

    /// <summary>
    /// Clears all row-level state (packets, topology, and field bag rows) for reuse by the next
    /// block. Field metadata (<see cref="Catalog"/> and the <see cref="FieldColumnBag"/> instances
    /// themselves) is retained.
    /// </summary>
    public void Reset()
    {
        // A staging that was never committed belongs to the block being cleared; undoing it first keeps the size totals exact.
        Discard();

        // The running total must match the summed form; checked here (once per block) rather than per packet.
        System.Diagnostics.Debug.Assert(
            _FieldBagsEstimatedSize == _SumFieldBagSizes(),
            "Running field-bag size total diverged from the sum of the bags.");

        _PacketIds.Clear();
        _Timestamps.Clear();
        _Infos.Clear();
        _FrameRefs.Clear();
        _FrameBytesCache.Clear();
        _ClearBufferRows();
        _Topology.Clear();
        _BatchEstimatedSize = 0;
        _FieldBagsEstimatedSize = 0;

        // The bags drop their arena references together with the arena content below.
        foreach (FieldColumnBag bag in _FieldBags.Values)
        {
            bag.Reset();
        }

        _Bytes.Reset();
    }

    /// <summary>Clears all state, including field metadata. The batch must not be used after disposal.</summary>
    public void Dispose()
    {
        if (_Disposed)
        {
            return;
        }

        _Disposed = true;
        _PendingTexts.Clear();
        _StagedPacketId = -1;
        _StageState = StageState.Empty;

        _PacketIds.Clear();
        _Timestamps.Clear();
        _Infos.Clear();
        _FrameRefs.Clear();
        _FrameBytesCache.Clear();
        _ClearBufferRows();
        _Topology.Clear();
        _FieldBags.Clear();
        Array.Clear(_BagsById);
        _Journal.Clear();
        _CatalogJournal.Clear();
        _Catalog.Clear();
        _BatchEstimatedSize = 0;
        _FieldBagsEstimatedSize = 0;
        _Bytes.Dispose();
    }

    /// <summary>Empties the three buffer row lists and the buffer array cache together (the same-length invariant).</summary>
    private void _ClearBufferRows()
    {
        _BufferPacketIds.Clear();
        _BufferIndexes.Clear();
        _BufferRefs.Clear();
        _BufferBytesCache.Clear();
    }

    /// <summary>Sums every bag's estimate; the reference form of <see cref="_FieldBagsEstimatedSize"/> for the debug check.</summary>
    /// <returns>Sum of <see cref="FieldColumnBag.EstimatedSizeBytes"/> over all bags.</returns>
    private long _SumFieldBagSizes()
    {
        long total = 0;
        foreach (FieldColumnBag bag in _FieldBags.Values)
        {
            total += bag.EstimatedSizeBytes;
        }

        return total;
    }

    #endregion
}

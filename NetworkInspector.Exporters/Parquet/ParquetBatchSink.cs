// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Parquet;

/// <summary>
/// Writes flushed <see cref="ColumnarPacketBatch"/> instances to a dataset of Parquet files, one entry of an
/// <see cref="IExportTarget"/> per file. The sink holds no path and calls no file-system API: where an entry lands is the
/// target's decision, and the streams of the entries need not seek.
/// <para>
/// Dataset layout (the entry names):
/// <list type="bullet">
///   <item><c>packets.parquet</c> — one row per packet: <c>packet_id</c>, <c>timestamp_ns</c>,
///     and optionally <c>info</c> / <c>frame_bytes</c>.</item>
///   <item><c>packet_buffers.parquet</c> — only with <see cref="ExportContent.Buffers"/>: one row per additional packet buffer
///     (index 1 to 255: reassembled datagrams, TCP PDUs, decoded bodies): <c>packet_id</c> (INT32), <c>buffer_index</c> (UINT8),
///     <c>bytes</c> (BYTE_ARRAY; an empty buffer is an empty array, never null); packets without additional buffers add no row, and
///     the file is not created when no packet had one.</item>
///   <item><c>topology.parquet</c> — one row per field-tree node: <c>packet_id</c>, <c>node_id</c>,
///     <c>field_id</c>, <c>parent_node_id</c>, and with <see cref="ExportContent.CustomText"/> the nullable string column
///     <c>custom_text</c> (the display line of a node without a value; null where the node has a value or its dissector set no
///     text).</item>
///   <item><c>catalog.parquet</c> — one row per distinct field ID observed, written once in
///     <see cref="Complete"/>: <c>field_id</c>, <c>name</c>, <c>ui_name</c>, <c>field_type</c>,
///     <c>protocol_id</c>, <c>table_name</c>, <c>description</c> (nullable).</item>
///   <item><c>protocols.parquet</c> — one row per protocol registered in the parsing stack, written once
///     when the sink is created: <c>protocol_id</c>, <c>name</c>, <c>ui_name</c>.</item>
///   <item><c>export_info.parquet</c> — <c>key</c>, <c>value</c>: the shape marker (<c>shape</c>, <c>fields</c>,
///     <c>occurrences</c>, <c>layout_version</c>; see <see cref="ExportInfo"/>), written when the sink is created.</item>
///   <item><c>fields/field_{id}.parquet</c> — one row per value: <c>packet_id</c>, the position column (<c>node_id</c> in the
///     hierarchy shape, <c>occurrence</c> in the flat shapes), a type-specific <c>value</c> column (including
///     <c>DataField&lt;string&gt;</c> for <see cref="FieldType.String"/>; <see cref="FieldType.Timestamp"/> uses an INT64
///     column named <c>value_timestamp_ns</c>; bytes use a byte-array column; an IPv4 address is a UINT32 column, a MAC address and
///     an EUI-64 are UINT64 columns, and an IPv6 address and a UUID have the two UINT64 columns <c>value_hi</c> and
///     <c>value_lo</c> instead of <c>value</c>), and optional
///     <c>custom_repr</c> / <c>custom_text</c> string columns (null where the dissector set no text, the empty string where it
///     set an empty one).</item>
/// </list>
/// </para>
/// <para>
/// Each table file is written as a single Parquet file with one row group per flushed batch.
/// One file per field ID (instead of one wide table) keeps every file narrow and fully typed: a field's value
/// column has exactly one Parquet type, and no row stores a null for fields it does not have.
/// </para>
/// <para>
/// Files are opened lazily on the first non-empty batch for that table, so a table that never receives rows
/// (for example <c>topology.parquet</c> in the flat export shapes) is never created.
/// </para>
/// <para>
/// <b>Compression:</b> the sink holds one <see cref="ParquetOptions"/> with the codec and the level of the export, and every
/// writer of the dataset takes it as a required constructor argument (the table files, the one-shot files, and every field file), so
/// no file can fall back to the Parquet.Net default and the dataset has one codec throughout. The options object is read-only after
/// construction and is shared by the writers, which run one after the other.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="WriteBatch"/> and <see cref="Complete"/>
/// must be called sequentially; the calls may come from different threads (the exporter's writer thread writes batches and
/// the finishing thread completes after joining it) as long as each call happens after the previous one finished.
/// </para>
/// </summary>
/// <remarks>
/// Sync: Parquet/README.md § Directory Layout states the same layout.
/// </remarks>
internal sealed class ParquetBatchSink : IColumnarBatchSink, IDisposable
{
    #region Configuration and state

    /// <summary>The entry of <c>packets.parquet</c>.</summary>
    private static readonly ExportEntryName _PacketsEntry = EntryOf("packets.parquet");

    /// <summary>The entry of <c>topology.parquet</c> (opened only in the hierarchy shape).</summary>
    private static readonly ExportEntryName _TopologyEntry = EntryOf("topology.parquet");

    /// <summary>The entry of <c>packet_buffers.parquet</c> (opened only with <see cref="ExportContent.Buffers"/> and a first buffer row).</summary>
    private static readonly ExportEntryName _BuffersEntry = EntryOf("packet_buffers.parquet");

    /// <summary>The entry of <c>catalog.parquet</c> (opened in <see cref="Complete"/>).</summary>
    private static readonly ExportEntryName _CatalogEntry = EntryOf("catalog.parquet");

    /// <summary>The entry of <c>protocols.parquet</c> (opened when the sink is created, unless the stack has no protocol).</summary>
    private static readonly ExportEntryName _ProtocolsEntry = EntryOf("protocols.parquet");

    /// <summary>The entry of <c>export_info.parquet</c> (opened when the sink is created).</summary>
    private static readonly ExportEntryName _ExportInfoEntry = EntryOf("export_info.parquet");

    /// <summary>
    /// The six table files with a fixed name. <see cref="ParquetDirectoryTarget"/> removes exactly these, and the files of
    /// <see cref="FieldsFolder"/>, before it writes a new dataset; a table file added to the sink is added here, so the clean-up
    /// follows the layout.
    /// </summary>
    internal static IReadOnlyList<ExportEntryName> FixedEntries { get; } =
    [
        _PacketsEntry,
        _TopologyEntry,
        _BuffersEntry,
        _CatalogEntry,
        _ProtocolsEntry,
        _ExportInfoEntry,
    ];

    /// <summary>The folder of the field files: every field entry is <c>fields/field_{id}.parquet</c>.</summary>
    internal const string FieldsFolder = "fields";

    /// <summary>Receives one entry per table file; the sink touches no file system.</summary>
    private readonly IExportTarget _Target;

    /// <summary>Content of the export; fixed for the lifetime of the sink, so every row group of a file has the same columns.</summary>
    private readonly ExportContent _Content;

    /// <summary>Name of the second key column of the field files: <c>node_id</c> (hierarchy) or <c>occurrence</c> (flat shapes).</summary>
    private readonly string _PositionColumn;

    /// <summary>
    /// The writer options of every file of the dataset: the codec and the level the export chose, set explicitly so the files never
    /// follow the Parquet.Net default. Created once in the constructor and never changed afterwards.
    /// </summary>
    private readonly ParquetOptions _Options;

    // Schema column definitions are created once and reused for every row group of the export, so each row group
    // of a file is written against exactly the columns its schema was created with.

    /// <summary>The <c>packet_id</c> column of <c>packets.parquet</c>.</summary>
    private readonly DataField _PacketIdField = new DataField<int>("packet_id");

    /// <summary>The <c>timestamp_ns</c> column of <c>packets.parquet</c>.</summary>
    private readonly DataField _TimestampField = new DataField<long>("timestamp_ns");

    /// <summary>The optional <c>info</c> column of <c>packets.parquet</c>.</summary>
    private readonly DataField _InfoField = new DataField<string>("info");

    /// <summary>The optional <c>frame_bytes</c> column of <c>packets.parquet</c>.</summary>
    private readonly DataField _FrameBytesField = new DataField<byte[]>("frame_bytes");

    /// <summary>The <c>packet_id</c> column of <c>packet_buffers.parquet</c>.</summary>
    private readonly DataField _BufferPacketIdField = new DataField<int>("packet_id");

    /// <summary>The <c>buffer_index</c> column of <c>packet_buffers.parquet</c>; <c>byte</c> is the UINT8 logical type (index 1 to 255).</summary>
    private readonly DataField _BufferIndexField = new DataField<byte>("buffer_index");

    /// <summary>The <c>bytes</c> column of <c>packet_buffers.parquet</c>.</summary>
    private readonly DataField _BufferBytesField = new DataField<byte[]>("bytes");

    /// <summary>The <c>packet_id</c> column of <c>topology.parquet</c>.</summary>
    private readonly DataField _TopoPacketIdField = new DataField<int>("packet_id");

    /// <summary>The <c>node_id</c> column of <c>topology.parquet</c>.</summary>
    private readonly DataField _TopoNodeIdField = new DataField<int>("node_id");

    /// <summary>The <c>field_id</c> column of <c>topology.parquet</c>.</summary>
    private readonly DataField _TopoFieldIdField = new DataField<int>("field_id");

    /// <summary>The <c>parent_node_id</c> column of <c>topology.parquet</c>; −1 for top-level nodes.</summary>
    private readonly DataField _TopoParentNodeIdField = new DataField<int>("parent_node_id");

    /// <summary>
    /// The optional <c>custom_text</c> column of <c>topology.parquet</c> (only with <see cref="ExportContent.CustomText"/>): the
    /// display line of a node without a value (a protocol layer, a group); null for a value node and for a node whose dissector set
    /// no text, the empty string for a set but empty text.
    /// </summary>
    private readonly DataField _TopoCustomTextField = new DataField<string?>("custom_text");

    // Writers are created lazily so an unused table produces no file; they stay open across batches (one row group
    // per batch) and are closed in Complete or Dispose.

    /// <summary>Writer of <c>packets.parquet</c>; <see langword="null"/> until the first batch and after <see cref="Complete"/>.</summary>
    private ParquetFileWriter? _PacketsWriter;

    /// <summary>Writer of <c>topology.parquet</c>; <see langword="null"/> until the first batch with topology rows and after <see cref="Complete"/>.</summary>
    private ParquetFileWriter? _TopologyWriter;

    /// <summary>
    /// Writer of <c>packet_buffers.parquet</c>; <see langword="null"/> until the first batch with buffer rows and after
    /// <see cref="Complete"/>.
    /// </summary>
    private ParquetFileWriter? _BuffersWriter;

    /// <summary>
    /// Writer state per field id; an entry exists from the field's first non-empty batch until <see cref="Complete"/> or
    /// <see cref="Dispose"/>.
    /// </summary>
    private readonly Dictionary<int, FieldWriterState> _FieldWriters = new(64);

    // The sink keeps its own copy of the catalog rows so catalog.parquet can be written in Complete, once, with
    // every field that was seen in any batch (a field may first appear in a late batch).

    /// <summary>The catalog rows by field id, merged from every batch (see <see cref="WriteBatch"/>); written once in <see cref="Complete"/>.</summary>
    private readonly Dictionary<int, FieldCatalogEntry> _Catalog = new(64);

    /// <summary>Whether <see cref="Dispose"/> already ran; makes a second call a no-op.</summary>
    private bool _Disposed;

    /// <summary>
    /// Creates a sink writing into <paramref name="target"/> and writes <c>protocols.parquet</c> and <c>export_info.parquet</c>
    /// immediately, because the protocol set and the shape are fixed for the whole export.
    /// </summary>
    /// <param name="target">Receives one entry per table file; the sink holds no path and touches no file system.</param>
    /// <param name="content">Content of the export; decides the shape, the optional columns, and the position column of the field files.</param>
    /// <param name="protocols">Protocols of the parsing stack; no <c>protocols.parquet</c> is written when empty.</param>
    /// <param name="method">The Parquet.Net codec of every file; already mapped and validated by the exporter's builder.</param>
    /// <param name="level">The compression level of every file; already mapped and validated by the exporter's builder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> or <paramref name="protocols"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The target refused an entry; the message carries the target's reason.</exception>
    /// <remarks>
    /// <paramref name="method"/> and <paramref name="level"/> are not validated here: they come from the Try mappings of
    /// <see cref="ParquetCompressions"/> in the builder, which refuses an undefined value, so this sink checks nothing of its own.
    /// </remarks>
    internal ParquetBatchSink(
        IExportTarget target,
        ExportContent content,
        IReadOnlyList<ProtocolCatalogEntry> protocols,
        CompressionMethod method,
        CompressionLevel level)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(protocols);

        _Target = target;
        _Content = content;

        // Compression is always explicit: the dataset must not change when Parquet.Net changes its default.
        _Options = new ParquetOptions { CompressionMethod = method, CompressionLevel = level };

        // The second key column of the field files says what the position means: the node id of the hierarchy shape, or the
        // occurrence number of the flat shapes. The shape is also written to export_info.parquet, which a reader checks first.
        if (content.Shape == ExportShape.Hierarchy)
        {
            _PositionColumn = "node_id";
        }
        else
        {
            _PositionColumn = "occurrence";
        }

        // The two one-shot files come first, protocols before the shape marker. Clearing an earlier dataset is the job of the
        // directory target of ToDirectory, not of the sink: the sink only names entries.
        _WriteProtocols(target, protocols, _Options);
        _WriteExportInfo(target, content, _Options);
    }

    /// <summary>Creates an entry name the sink knows to be valid.</summary>
    /// <param name="text">A fixed or field entry text.</param>
    /// <returns>The entry name.</returns>
    /// <exception cref="InvalidOperationException">The text breaks the grammar of <see cref="ExportEntryName"/>: a bug of the sink.</exception>
    internal static ExportEntryName EntryOf(string text)
    {
        if (!ExportEntryName.TryCreate(text, out ExportEntryName name, out string? problem))
        {
            throw new InvalidOperationException(problem);
        }

        return name;
    }

    #endregion

    #region Batch writing

    /// <inheritdoc/>
    /// <remarks>
    /// Not atomic: the row groups of one batch are appended file by file, so an exception part-way leaves the
    /// tables with different numbers of row groups for that batch. <see cref="ParquetExporter"/> reports the failure
    /// as an export error. The catalog rows of the batch are merged before the first file is written, so every field file has
    /// a catalog row even after such a failure.
    /// </remarks>
    public void WriteBatch(ColumnarPacketBatch batch)
    {
        if (batch.PacketCount == 0)
        {
            return;
        }

        // Copy the catalog rows first; they are written once, at Complete. Invariant: every field file that receives a row has a
        // catalog row, also when a later file of this batch fails (a partial batch then stays nameable). A value row (with a
        // table) wins over a structure row of the same id: background mode alternates two batches with their own catalogs, and
        // one batch can see an id only as a node without a value while the other wrote its field file. A batch creates a
        // field's catalog entry once and never changes it, so keeping the first value row is harmless.
        // Sync: DuckDbBulkWriter.WriteBatch applies the same rule.
        foreach (KeyValuePair<int, FieldCatalogEntry> entry in batch.Catalog)
        {
            if (!_Catalog.TryGetValue(entry.Key, out FieldCatalogEntry? existing) || existing.TableName is null)
            {
                _Catalog[entry.Key] = entry.Value;
            }
        }

        // One row group per table per batch; every table carries packet_id so the tables can be joined back together.
        _WritePacketsRowGroup(batch);

        // The buffer rows are packet-level rows like the frames: a row group of their own file, never part of a field file.
        if (_Content.Buffers && batch.BufferRowCount > 0)
        {
            _WriteBuffersRowGroup(batch);
        }

        if (_Content.Shape == ExportShape.Hierarchy && batch.Topology.Count > 0)
        {
            _WriteTopologyRowGroup(batch);
        }

        // The batch keeps its field bags (and their lists) across Reset, so bags with no rows in this batch are
        // skipped: an empty row group would only add footer metadata, and the field file may never be needed.
        foreach (KeyValuePair<int, FieldColumnBag> entry in batch.FieldBags)
        {
            if (entry.Value.RowCount == 0)
            {
                continue;
            }

            FieldWriterState state = _GetOrCreateFieldWriter(entry.Key, entry.Value.FieldType);
            state.WriteRowGroup(entry.Value, _Content);
        }
    }

    #endregion

    #region Completion and disposal

    /// <inheritdoc/>
    /// <remarks>
    /// Closing a writer writes the Parquet footer, which is what makes the file readable. Complete closes the packet,
    /// buffer, topology and field files and then writes <c>catalog.parquet</c>, which only exists after this call
    /// (<see cref="Dispose"/> closes the same files but never writes the catalog).
    /// </remarks>
    public void Complete()
    {
        _PacketsWriter?.Dispose();
        _PacketsWriter = null;
        _BuffersWriter?.Dispose();
        _BuffersWriter = null;
        _TopologyWriter?.Dispose();
        _TopologyWriter = null;

        foreach (FieldWriterState state in _FieldWriters.Values)
        {
            state.Complete();
        }
        _FieldWriters.Clear();

        _WriteCatalog();
    }

    /// <summary>
    /// Releases any still-open writers (which closes their files and writes their footers). Safe to call after
    /// <see cref="Complete"/> (no-op) or instead of it on an error path, in which case <c>catalog.parquet</c> is
    /// left unwritten, matching the "best-effort partial output" behaviour of other exporters when finalization fails.
    /// </summary>
    public void Dispose()
    {
        if (_Disposed)
        {
            return;
        }
        _Disposed = true;

        _PacketsWriter?.Dispose();
        _PacketsWriter = null;
        _BuffersWriter?.Dispose();
        _BuffersWriter = null;
        _TopologyWriter?.Dispose();
        _TopologyWriter = null;
        foreach (FieldWriterState state in _FieldWriters.Values)
        {
            state.Dispose();
        }
        _FieldWriters.Clear();
    }

    #endregion

    #region Table writers

    /// <summary>
    /// Returns the writer state for a field ID, creating it on the field's first non-empty batch. The field's
    /// <see cref="FieldType"/> is taken from the first bag seen and then fixed for the whole export, because the
    /// Parquet column type of <c>field_{id}.parquet</c> cannot change after the file is created.
    /// </summary>
    /// <param name="fieldIdValue">The field ID.</param>
    /// <param name="fieldType">The field's value type; used only when the state is created.</param>
    /// <returns>The writer state of the field; the same instance for every call with the same ID.</returns>
    private FieldWriterState _GetOrCreateFieldWriter(int fieldIdValue, FieldType fieldType)
    {
        if (!_FieldWriters.TryGetValue(fieldIdValue, out FieldWriterState? state))
        {
            state = new FieldWriterState(_Target, fieldIdValue, fieldType, _PositionColumn, _Options);
            _FieldWriters[fieldIdValue] = state;
        }
        return state;
    }

    /// <summary>Appends one row group to <c>packets.parquet</c>, creating the file with the flag-dependent schema on first use.</summary>
    /// <param name="batch">The batch whose packet ids, timestamps, and optional info and frame bytes become the row group.</param>
    private void _WritePacketsRowGroup(ColumnarPacketBatch batch)
    {
        // The schema depends on the export content, which is fixed for the export, so it is built once when the file
        // is created. Optional columns are added to the schema only when the batch collected them; otherwise the
        // batch lists (Infos, FrameBytesList) would be empty and shorter than the other columns.
        List<DataField> fields = [_PacketIdField, _TimestampField];
        bool includeInfo = _Content.Info;
        bool includeFrameBytes = _Content.FrameBytes;
        if (includeInfo)
        {
            fields.Add(_InfoField);
        }
        if (includeFrameBytes)
        {
            fields.Add(_FrameBytesField);
        }

        // Each column of a row group must have the same row count, and Parquet.Net requires the columns to be
        // written in schema order.
        _PacketsWriter ??= ParquetFileWriter.Open(_Target, _PacketsEntry, new ParquetSchema(fields), _Options);

        // The id and timestamp columns are copied into pooled arrays (Parquet.Net's value-type overloads take memory, the batch
        // keeps lists): no per-flush array garbage. The lambda captures these two locals, so a closure object exists once per
        // row group, per flush and not per value — acceptable on this cold path. Both are returned after the awaited write.
        using PooledColumn<int> packetIds = PooledColumn<int>.CopyOf(batch.PacketIdSpan);
        using PooledColumn<long> timestamps = PooledColumn<long>.CopyOf(batch.TimestampSpan);
        _PacketsWriter.WriteRowGroup(rowGroup =>
        {
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_PacketIdField, packetIds.Memory));
            ParquetFileWriter.Block(rowGroup.WriteAsync<long>(_TimestampField, timestamps.Memory));
            if (includeInfo)
            {
                ParquetFileWriter.Block(rowGroup.WriteAsync(_InfoField, batch.Infos));
            }
            if (includeFrameBytes)
            {
                ParquetFileWriter.Block(rowGroup.WriteAsync(_FrameBytesField, batch.FrameBytesList));
            }
        });
    }

    /// <summary>
    /// Appends one row group to <c>packet_buffers.parquet</c> (one row per additional packet buffer), creating the file on the
    /// first batch that has a buffer row.
    /// </summary>
    /// <param name="batch">The batch whose buffer rows (not empty) become the row group.</param>
    /// <remarks>
    /// Perf: the id and index columns are copied into pooled arrays like the packet columns (no per-flush array garbage); the
    /// bytes column needs one <c>byte[]</c> per buffer because Parquet.Net's byte-array column takes arrays, the same cost as the
    /// frame column (<see cref="ColumnarPacketBatch.BufferBytesList"/>).
    /// </remarks>
    private void _WriteBuffersRowGroup(ColumnarPacketBatch batch)
    {
        // Created lazily so an export whose packets have no additional buffer leaves no empty file; an existing reader of the
        // dataset therefore sees packet_buffers.parquet only when it holds at least one row.
        _BuffersWriter ??= ParquetFileWriter.Open(
            _Target, _BuffersEntry, new ParquetSchema(_BufferPacketIdField, _BufferIndexField, _BufferBytesField), _Options);

        using PooledColumn<int> packetIds = PooledColumn<int>.CopyOf(batch.BufferPacketIdSpan);
        using PooledColumn<byte> indexes = PooledColumn<byte>.CopyOf(batch.BufferIndexSpan);
        _BuffersWriter.WriteRowGroup(rowGroup =>
        {
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_BufferPacketIdField, packetIds.Memory));
            ParquetFileWriter.Block(rowGroup.WriteAsync<byte>(_BufferIndexField, indexes.Memory));
            ParquetFileWriter.Block(rowGroup.WriteAsync(_BufferBytesField, batch.BufferBytesList));
        });
    }

    /// <summary>
    /// Appends one row group to <c>topology.parquet</c> (flat node rows; <c>parent_node_id</c> is −1 for top-level nodes; with
    /// <see cref="ExportContent.CustomText"/> the last column holds the display line of a node without a value).
    /// </summary>
    /// <param name="batch">The batch whose topology rows (not empty) become the row group.</param>
    /// <remarks>
    /// Sync: Parquet/README.md § Directory Layout and EXPORTER_GUIDE.md § Rebuilding the display line (the layer text).
    /// </remarks>
    private void _WriteTopologyRowGroup(ColumnarPacketBatch batch)
    {
        // The batch stores topology as a list of structs (one row per node), but Parquet is written one column at a
        // time from an array, so the rows are split into four int columns in one pass. The four arrays are rented from the
        // pool and returned in the finally block after the awaited write. Perf: a flush leaves no array garbage behind; four
        // new int[] of the topology's size per flush would survive into generation 1.
        ReadOnlySpan<TopologyNode> topology = batch.TopologySpan;
        int count = topology.Length;
        bool includeCustomText = _Content.CustomText;
        int[] packetIds = ArrayPool<int>.Shared.Rent(Math.Max(1, count));
        int[] nodeIds = ArrayPool<int>.Shared.Rent(Math.Max(1, count));
        int[] fieldIds = ArrayPool<int>.Shared.Rent(Math.Max(1, count));
        int[] parentNodeIds = ArrayPool<int>.Shared.Rent(Math.Max(1, count));

        // The text column is an exact-size array (Parquet.Net takes the strings as an array, like every other string column of
        // this sink): one array per flush, and only when the content asks for custom texts.
        string?[] texts = includeCustomText
            ? new string?[count]
            : [];
        try
        {
            for (int i = 0; i < count; i++)
            {
                TopologyNode node = topology[i];
                packetIds[i] = node.PacketId;
                nodeIds[i] = node.NodeId;
                fieldIds[i] = node.FieldId;
                parentNodeIds[i] = node.ParentNodeId;
                if (includeCustomText)
                {
                    // Null stays null (an absent text); a set but empty text stays the empty string. Nothing may replace one by the other.
                    texts[i] = node.CustomText;
                }
            }

            // The schema depends on the content, which is fixed for the export, so it is built once when the file is created.
            if (_TopologyWriter is null)
            {
                List<DataField> topologyFields = [_TopoPacketIdField, _TopoNodeIdField, _TopoFieldIdField, _TopoParentNodeIdField];
                if (includeCustomText)
                {
                    topologyFields.Add(_TopoCustomTextField);
                }

                _TopologyWriter = ParquetFileWriter.Open(_Target, _TopologyEntry, new ParquetSchema(topologyFields), _Options);
            }

            // The memory is sliced to the row count: a rented array is longer than asked, and its spare slots must not
            // become rows. The lambda captures the arrays (one closure per row group, not per value).
            _TopologyWriter.WriteRowGroup(rowGroup =>
            {
                ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_TopoPacketIdField, packetIds.AsMemory(0, count)));
                ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_TopoNodeIdField, nodeIds.AsMemory(0, count)));
                ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_TopoFieldIdField, fieldIds.AsMemory(0, count)));
                ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_TopoParentNodeIdField, parentNodeIds.AsMemory(0, count)));
                if (includeCustomText)
                {
                    ParquetFileWriter.Block(rowGroup.WriteAsync(_TopoCustomTextField, texts));
                }
            });
        }
        finally
        {
            ArrayPool<int>.Shared.Return(packetIds);
            ArrayPool<int>.Shared.Return(nodeIds);
            ArrayPool<int>.Shared.Return(fieldIds);
            ArrayPool<int>.Shared.Return(parentNodeIds);
        }
    }

    #endregion

    #region One-shot tables

    /// <summary>
    /// Writes <c>catalog.parquet</c> (one row per distinct field ID observed) as a single row group. Skipped when no
    /// field was ever observed. The catalog holds one row per field definition, not per occurrence, so it is small
    /// and is built in memory in one go.
    /// </summary>
    private void _WriteCatalog()
    {
        if (_Catalog.Count == 0)
        {
            return;
        }

        DataField fieldId = new DataField<int>("field_id");
        DataField name = new DataField<string>("name");
        DataField uiName = new DataField<string>("ui_name");
        DataField fieldType = new DataField<int>("field_type");
        DataField protocolId = new DataField<int>("protocol_id");
        // Nullable: a structure node (protocol, group) has a catalog row but no field table.
        DataField tableName = new DataField<string?>("table_name");
        // Appended last so readers that address catalog columns by position keep working (new catalog
        // columns are only ever appended); nullable because a field may have no description.
        DataField description = new DataField<string?>("description");

        int count = _Catalog.Count;
        int[] fieldIds = new int[count];
        string[] names = new string[count];
        string[] uiNames = new string[count];
        int[] fieldTypes = new int[count];
        int[] protocolIds = new int[count];
        string?[] tableNames = new string?[count];
        string?[] descriptions = new string?[count];
        int row = 0;
        foreach (FieldCatalogEntry entry in _Catalog.Values)
        {
            fieldIds[row] = entry.FieldIdValue;
            names[row] = entry.Name;
            uiNames[row] = entry.UiName;
            fieldTypes[row] = (int)entry.FieldType;
            protocolIds[row] = entry.ProtocolIdValue;
            tableNames[row] = entry.TableName;
            descriptions[row] = entry.Description;
            row++;
        }

        using ParquetFileWriter writer = ParquetFileWriter.Open(
            _Target, _CatalogEntry, new ParquetSchema(fieldId, name, uiName, fieldType, protocolId, tableName, description), _Options);
        writer.WriteRowGroup(rowGroup =>
        {
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(fieldId, fieldIds));
            ParquetFileWriter.Block(rowGroup.WriteAsync(name, names));
            ParquetFileWriter.Block(rowGroup.WriteAsync(uiName, uiNames));
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(fieldType, fieldTypes));
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(protocolId, protocolIds));
            ParquetFileWriter.Block(rowGroup.WriteAsync(tableName, tableNames));
            ParquetFileWriter.Block(rowGroup.WriteAsync(description, descriptions));
        });
    }

    /// <summary>
    /// Writes <c>protocols.parquet</c> (<c>protocol_id</c>, <c>name</c>, <c>ui_name</c>) as a single row group.
    /// Skipped when <paramref name="protocols"/> is empty, mirroring the empty-catalog behaviour.
    /// </summary>
    /// <param name="target">The export's target; receives the <c>protocols.parquet</c> entry.</param>
    /// <param name="protocols">Protocols of the parsing stack.</param>
    /// <param name="options">The writer options of the dataset (codec and level).</param>
    /// <exception cref="IOException">The target refused the entry.</exception>
    private static void _WriteProtocols(IExportTarget target, IReadOnlyList<ProtocolCatalogEntry> protocols, ParquetOptions options)
    {
        if (protocols.Count == 0)
        {
            return;
        }

        DataField protocolId = new DataField<int>("protocol_id");
        DataField name = new DataField<string>("name");
        DataField uiName = new DataField<string>("ui_name");

        int count = protocols.Count;
        int[] protocolIds = new int[count];
        string[] names = new string[count];
        string[] uiNames = new string[count];
        for (int row = 0; row < count; row++)
        {
            ProtocolCatalogEntry entry = protocols[row];
            protocolIds[row] = entry.ProtocolIdValue;
            names[row] = entry.Name;
            uiNames[row] = entry.UiName;
        }

        using ParquetFileWriter writer = ParquetFileWriter.Open(target, _ProtocolsEntry, new ParquetSchema(protocolId, name, uiName), options);
        writer.WriteRowGroup(rowGroup =>
        {
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(protocolId, protocolIds));
            ParquetFileWriter.Block(rowGroup.WriteAsync(name, names));
            ParquetFileWriter.Block(rowGroup.WriteAsync(uiName, uiNames));
        });
    }

    /// <summary>
    /// Writes <c>export_info.parquet</c> (<c>key</c>, <c>value</c> string columns) as a single row group: the shape marker that
    /// a reader checks first (see <see cref="ExportInfo"/>). Written when the sink is created, so even an export without a
    /// single packet says what it would have contained.
    /// </summary>
    /// <param name="target">The export's target; receives the <c>export_info.parquet</c> entry.</param>
    /// <param name="content">The content of the export.</param>
    /// <param name="options">The writer options of the dataset (codec and level).</param>
    /// <exception cref="IOException">The target refused the entry.</exception>
    private static void _WriteExportInfo(IExportTarget target, ExportContent content, ParquetOptions options)
    {
        IReadOnlyList<KeyValuePair<string, string>> rows = ExportInfo.CreateRows(content);
        DataField key = new DataField<string>("key");
        DataField value = new DataField<string>("value");
        string[] keys = new string[rows.Count];
        string[] values = new string[rows.Count];
        for (int row = 0; row < rows.Count; row++)
        {
            keys[row] = rows[row].Key;
            values[row] = rows[row].Value;
        }

        using ParquetFileWriter writer = ParquetFileWriter.Open(target, _ExportInfoEntry, new ParquetSchema(key, value), options);
        writer.WriteRowGroup(rowGroup =>
        {
            ParquetFileWriter.Block(rowGroup.WriteAsync(key, keys));
            ParquetFileWriter.Block(rowGroup.WriteAsync(value, values));
        });
    }

    #endregion
}

/// <summary>
/// Thin wrapper around a lazily-opened <see cref="global::Parquet.ParquetWriter"/> for a single
/// table file. Bridges the writer's async-only API to the synchronous exporter pipeline through
/// <see cref="Block(System.Threading.Tasks.Task)"/> / <see cref="Block{T}(System.Threading.Tasks.Task{T})"/>, the only blocking sites of the Parquet exporter.
/// Callers must not run the exporter on a thread with a <see cref="SynchronizationContext"/> (UI thread).
/// <para>
/// The entry is opened when the writer is created and the footer is written by <see cref="Dispose"/>; a file whose writer
/// was never disposed is not a valid Parquet file. <see cref="Dispose"/> must be called at most once. The entry stream may be
/// write-only and non-seekable: Parquet.Net writes forward and the writer never reads the stream back.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; owned by one <see cref="ParquetBatchSink"/> and used from its single caller thread.
/// </para>
/// </summary>
internal sealed class ParquetFileWriter : IDisposable
{
    /// <summary>The entry stream of the table; owned by this instance and closed by <see cref="Dispose"/>.</summary>
    private readonly Stream _Stream;

    /// <summary>The Parquet.Net writer over <see cref="_Stream"/>; disposing it writes the footer.</summary>
    private readonly ParquetWriter _Writer;

    /// <summary>Opens <paramref name="entry"/> of <paramref name="target"/> and a Parquet writer on it.</summary>
    /// <param name="target">The export's target.</param>
    /// <param name="entry">The table file's entry.</param>
    /// <param name="schema">Schema of the file; fixed for the lifetime of the writer.</param>
    /// <param name="options">
    /// The writer options (codec and level) of the dataset. Required, with no default, so a new writer of the dataset cannot be
    /// created without choosing its compression: Parquet.Net would otherwise fall back to its own default.
    /// </param>
    /// <returns>The open writer.</returns>
    /// <exception cref="IOException">The target refused the entry; the message names the entry and carries the reason.</exception>
    internal static ParquetFileWriter Open(IExportTarget target, ExportEntryName entry, ParquetSchema schema, ParquetOptions options)
    {
        // A refused entry is an expected failure of the target; inside the sink it travels as IOException, the error channel the
        // exporter already catches (start failure in _Start, lost batch on the writer thread).
        if (!target.TryOpenWrite(entry, out Stream? stream, out string failure))
        {
            throw new IOException(Invariant($"Cannot open '{entry}': {failure}"));
        }

        return new ParquetFileWriter(stream, schema, options);
    }

    /// <summary>Opens a Parquet writer on an entry stream; closes the stream when the writer cannot be created.</summary>
    /// <param name="stream">The write-only entry stream; this instance owns it from now on.</param>
    /// <param name="schema">Schema of the file; fixed for the lifetime of the writer.</param>
    /// <param name="options">The writer options (codec and level) of the dataset.</param>
    private ParquetFileWriter(Stream stream, ParquetSchema schema, ParquetOptions options)
    {
        _Stream = stream;
        try
        {
            // Compression is always explicit: the dataset must not change when Parquet.Net changes its default.
            _Writer = Block(ParquetWriter.CreateAsync(schema, _Stream, options));
        }
        catch
        {
            // The caller never receives this instance when the constructor throws, so nobody could dispose the stream;
            // closing it here keeps the half-created entry from staying open.
            _Stream.Dispose();
            throw;
        }
    }

    /// <summary>Synchronously waits for a Parquet.Net operation; exceptions surface unwrapped (not as <see cref="AggregateException"/>).</summary>
    /// <param name="task">The pending Parquet.Net operation.</param>
    internal static void Block(System.Threading.Tasks.Task task)
    {
        // Parquet.Net exposes only async writers; the IPacketListener contract is synchronous. Approved sync-over-async
        // (rule "no .Result/.Wait", reason: no sync API, scope: this method, user approved).
        // In ExportWriteMode.Background every row-group write runs on the exporter's own writer thread, which has no
        // SynchronizationContext, so the blocking wait cannot deadlock there. Residual risk only in Synchronous mode (and for the
        // file creation and catalog writes, which run on the caller's thread): a caller thread with a SynchronizationContext that
        // the task needs. Mitigation: the documented caller contract.
        // GetAwaiter().GetResult() is used instead of Wait() so the original exception is rethrown directly rather
        // than wrapped in an AggregateException.
        task.ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>Synchronously waits for a Parquet.Net operation and returns its result; exceptions surface unwrapped.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="task">The pending Parquet.Net operation.</param>
    /// <returns>The operation's result.</returns>
    internal static T Block<T>(System.Threading.Tasks.Task<T> task)
    {
        // Same sync-over-async bridge as the non-generic overload (see there for the reasoning and residual risk).
        // Parquet.Net exposes only async writers; the IPacketListener contract is synchronous. Approved sync-over-async
        // (rule "no .Result/.Wait", reason: no sync API, scope: this method, user approved). Callers must not run the
        // exporter on a thread with a SynchronizationContext.
        return task.ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Writes one row group, invoking <paramref name="writeColumns"/> to populate it. Disposing the row group
    /// writer at the end of the method is what flushes the group to the file.
    /// </summary>
    /// <param name="writeColumns">Writes every column of the schema, in schema order, with equal row counts.</param>
    internal void WriteRowGroup(Action<ParquetRowGroupWriter> writeColumns)
    {
        using ParquetRowGroupWriter rowGroup = _Writer.CreateRowGroup();
        writeColumns(rowGroup);
    }

    /// <summary>
    /// Finalizes the file's footer and releases the stream. <see cref="ParquetWriter"/> only
    /// exposes <see cref="IAsyncDisposable"/>; blocked on through <see cref="Block(System.Threading.Tasks.Task)"/> (see the type-level remarks).
    /// </summary>
    public void Dispose()
    {
        // ValueTask.AsTask() materializes it into an ordinary Task before blocking, avoiding the
        // "may not be safe to consume twice / while pending" hazard CA2012 warns about for raw
        // ValueTask instances. The stream is closed in a finally block: when writing the footer fails, the exception
        // still propagates, but the file handle must not stay open (the file would remain locked for the process).
        try
        {
            Block(_Writer.DisposeAsync().AsTask());
        }
        finally
        {
            _Stream.Dispose();
        }
    }
}

/// <summary>
/// Per-field Parquet writer state: the lazily-created <c>fields/field_{id}.parquet</c> writer
/// and its schema (fixed once the field's <see cref="FieldType"/> and the export's
/// <see cref="ExportContent"/> are known). String columns are written as plain
/// <c>DataField&lt;string&gt;</c> columns.
/// <para>
/// <b>Thread safety:</b> Not thread-safe; owned by one <see cref="ParquetBatchSink"/> and used from its single caller thread.
/// </para>
/// </summary>
internal sealed class FieldWriterState : IDisposable
{
    #region Schema

    /// <summary>The field ID; names the file <c>field_{id}.parquet</c>.</summary>
    private readonly int _FieldIdValue;

    /// <summary>The field's value type; fixed for the file and selects the <c>value</c> column and the typed write.</summary>
    private readonly FieldType _FieldType;

    /// <summary>The <c>packet_id</c> key column.</summary>
    private readonly DataField _PacketIdField = new DataField<int>("packet_id");

    /// <summary>The position key column: <c>node_id</c> (hierarchy shape) or <c>occurrence</c> (flat shapes).</summary>
    private readonly DataField _PositionField;

    /// <summary>
    /// The typed <c>value</c> column (named <c>value_timestamp_ns</c> for timestamps); <see langword="null"/> for the 128-bit types,
    /// which have the two columns below instead.
    /// </summary>
    private readonly DataField? _ValueField;

    /// <summary>The <c>value_hi</c> column of an IPv6 or UUID file: the high 64 bits; <see langword="null"/> for every other type.</summary>
    private readonly DataField? _HighField;

    /// <summary>The <c>value_lo</c> column of an IPv6 or UUID file: the low 64 bits; <see langword="null"/> for every other type.</summary>
    private readonly DataField? _LowField;

    /// <summary>The optional <c>custom_repr</c> column.</summary>
    private readonly DataField _CustomReprField = new DataField<string?>("custom_repr");

    /// <summary>The optional <c>custom_text</c> column.</summary>
    private readonly DataField _CustomTextField = new DataField<string?>("custom_text");

    /// <summary>Writer of the field file; <see langword="null"/> until the first row group and after <see cref="Complete"/>.</summary>
    private ParquetFileWriter? _Writer;

    /// <summary>The writer options (codec and level) of the dataset; the sink's object, read-only, passed to the file writer.</summary>
    private readonly ParquetOptions _Options;

    /// <summary>The export's target; the field file is opened on it as the entry <c>fields/field_{id}.parquet</c>.</summary>
    private readonly IExportTarget _Target;

    /// <summary>
    /// The entry of this field's file, <c>fields/field_{id}.parquet</c>. The field id is a non-negative integer, so the text
    /// always matches the grammar of <see cref="ExportEntryName"/>.
    /// </summary>
    private readonly ExportEntryName _Entry;

    /// <summary>Creates the writer state for one field, deriving its Parquet schema from <paramref name="fieldType"/>.</summary>
    /// <param name="target">The export's target that receives the field file.</param>
    /// <param name="fieldIdValue">The field ID; names the entry <c>fields/field_{id}.parquet</c>.</param>
    /// <param name="fieldType">The field's value type; selects the Parquet type of the <c>value</c> column.</param>
    /// <param name="positionColumn"><c>node_id</c> in the hierarchy shape, <c>occurrence</c> in the flat shapes.</param>
    /// <param name="options">The writer options (codec and level) of the dataset; every field file uses them.</param>
    internal FieldWriterState(IExportTarget target, int fieldIdValue, FieldType fieldType, string positionColumn,
        ParquetOptions options)
    {
        _Target = target;
        _Entry = ParquetBatchSink.EntryOf(Invariant($"{ParquetBatchSink.FieldsFolder}/field_{fieldIdValue}.parquet"));
        _FieldIdValue = fieldIdValue;
        _FieldType = fieldType;
        _Options = options;
        _PositionField = new DataField<int>(positionColumn);

        // One native Parquet column type per FieldType, so consumers get typed values without parsing. Timestamp is
        // plain INT64 nanoseconds since the Unix epoch (no Parquet logical timestamp type), hence the column name
        // value_timestamp_ns. The addresses are unsigned integers whose numeric order is the address order: IPv4 UINT32, MAC and
        // EUI-64 UINT64. The default case stores FieldType.Bytes as a byte array. _WriteTypedValue must stay in sync with this
        // switch. Sync: Parquet/README.md § Directory Layout (field files) and EXPORTER_GUIDE.md § Rebuilding the display line.
        _ValueField = fieldType switch
        {
            FieldType.Bool => new DataField<bool>("value"),
            FieldType.I64 => new DataField<long>("value"),
            FieldType.U64 => new DataField<ulong>("value"),
            FieldType.F64 => new DataField<double>("value"),
            FieldType.Timestamp => new DataField<long>("value_timestamp_ns"),
            FieldType.String => new DataField<string?>("value"),
            FieldType.IPv4Address => new DataField<uint>("value"),
            FieldType.MacAddress or FieldType.Eui64 => new DataField<ulong>("value"),

            // 128-bit values have no single column: Parquet has no 128-bit integer type, and Parquet.Net 6.1 writes no raw
            // fixed-length byte array (its only 16-byte column is a Guid with the UUID logical type, whose byte order a reader
            // cannot rely on, and which would be wrong for an IPv6 address anyway). So the value is two UINT64 halves, high
            // first, the big-endian order of the address. A reader needs both: value_hi alone is the /64 prefix of an IPv6
            // address. Rejected: BYTE_ARRAY (larger, not an integer).
            FieldType.IPv6Address or FieldType.Uuid => null,
            _ => new DataField<byte[]>("value"), // Bytes
        };
        if (_ValueField is null)
        {
            _HighField = new DataField<ulong>("value_hi");
            _LowField = new DataField<ulong>("value_lo");
        }
    }

    /// <summary>Builds the schema of the field file; column order here is the order in which row groups must write them.</summary>
    /// <param name="content">Content of the export; decides which optional columns the file has.</param>
    /// <returns>The schema of <c>field_{id}.parquet</c>.</returns>
    private ParquetSchema _BuildSchema(ExportContent content)
    {
        List<DataField> fields = [_PacketIdField, _PositionField];
        if (_ValueField is not null)
        {
            fields.Add(_ValueField);
        }
        else
        {
            fields.Add(_HighField!); // Invariant: a field type without a value column has both halves (set together in the constructor).
            fields.Add(_LowField!); // Invariant: same as _HighField.
        }
        if (content.CustomRepresentation)
        {
            fields.Add(_CustomReprField);
        }
        if (content.CustomText)
        {
            fields.Add(_CustomTextField);
        }
        return new ParquetSchema(fields);
    }

    #endregion

    #region Row group writing

    /// <summary>Writes one row group for this field from a single flushed batch's <see cref="FieldColumnBag"/>.</summary>
    /// <param name="bag">The batch's rows for this field; must be non-empty.</param>
    /// <param name="content">Content of the export; decides which optional columns exist in the file.</param>
    /// <exception cref="IOException">The target refused the entry of the field file on the first batch.</exception>
    internal void WriteRowGroup(FieldColumnBag bag, ExportContent content)
    {
        // The entry is opened on the first batch that has rows for this field, with a schema derived from the
        // content; later batches must write the same columns, which holds because the content never changes.
        _Writer ??= ParquetFileWriter.Open(_Target, _Entry, _BuildSchema(content), _Options);

        // Pooled copies of the two key columns (see PooledColumn); one closure per row group, not per value.
        using PooledColumn<int> packetIds = PooledColumn<int>.CopyOf(bag.PacketIdSpan);
        using PooledColumn<int> positions = PooledColumn<int>.CopyOf(bag.PositionSpan);
        _Writer.WriteRowGroup(rowGroup =>
        {
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_PacketIdField, packetIds.Memory));
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_PositionField, positions.Memory));

            _WriteTypedValue(rowGroup, bag);

            if (content.CustomRepresentation)
            {
                ParquetFileWriter.Block(rowGroup.WriteAsync(_CustomReprField, _ToNullableStringArray(bag.CustomRepresentations)));
            }
            if (content.CustomText)
            {
                ParquetFileWriter.Block(rowGroup.WriteAsync(_CustomTextField, _ToNullableStringArray(bag.CustomTexts)));
            }
        });
    }

    /// <summary>Writes the <c>value</c> column from the bag list that matches the field type (the only list the bag fills).</summary>
    /// <param name="rowGroup">The row group being written; the <c>value</c> column is its third column.</param>
    /// <param name="bag">The batch's rows for this field.</param>
    private void _WriteTypedValue(ParquetRowGroupWriter rowGroup, FieldColumnBag bag)
    {
        switch (_FieldType)
        {
            // Value-type columns: copied into a pooled array that goes back to the pool right after the awaited write (see
            // PooledColumn); each case has its own scope so the rented array lives exactly as long as its write.
            case FieldType.Bool:
            {
                using PooledColumn<bool> values = PooledColumn<bool>.CopyOf(bag.BoolSpan);
                ParquetFileWriter.Block(rowGroup.WriteAsync<bool>(_ValueField!, values.Memory));
                break;
            }

            case FieldType.I64:
            {
                using PooledColumn<long> values = PooledColumn<long>.CopyOf(bag.I64Span);
                ParquetFileWriter.Block(rowGroup.WriteAsync<long>(_ValueField!, values.Memory));
                break;
            }

            case FieldType.U64:
            {
                using PooledColumn<ulong> values = PooledColumn<ulong>.CopyOf(bag.U64Span);
                ParquetFileWriter.Block(rowGroup.WriteAsync<ulong>(_ValueField!, values.Memory));
                break;
            }

            case FieldType.F64:
            {
                using PooledColumn<double> values = PooledColumn<double>.CopyOf(bag.F64Span);
                ParquetFileWriter.Block(rowGroup.WriteAsync<double>(_ValueField!, values.Memory));
                break;
            }

            case FieldType.Timestamp:
            {
                using PooledColumn<long> values = PooledColumn<long>.CopyOf(bag.TimestampSpan);
                ParquetFileWriter.Block(rowGroup.WriteAsync<long>(_ValueField!, values.Memory));
                break;
            }

            case FieldType.String:
                ParquetFileWriter.Block(rowGroup.WriteAsync(_ValueField!, _ToNullableStringArray(bag.StringValues)));
                break;

            // The address types: the integer of every row is read from the bag's inline values into a pooled column (the same
            // copy the numeric columns above make, one pass per column, no array per value). The typed accessor cannot fail
            // for a bag of its own type; a false is a broken invariant, reported from a cold helper.
            case FieldType.IPv4Address:
            {
                using PooledColumn<uint> values = PooledColumn<uint>.Rent(bag.FixedValueSpan.Length);
                _FillIPv4(bag, values.WritableSpan);
                ParquetFileWriter.Block(rowGroup.WriteAsync<uint>(_ValueField!, values.Memory));
                break;
            }

            case FieldType.MacAddress:
            case FieldType.Eui64:
            {
                using PooledColumn<ulong> values = PooledColumn<ulong>.Rent(bag.FixedValueSpan.Length);
                _FillIdentifiers(bag, values.WritableSpan);
                ParquetFileWriter.Block(rowGroup.WriteAsync<ulong>(_ValueField!, values.Memory));
                break;
            }

            case FieldType.IPv6Address:
            case FieldType.Uuid:
            {
                // Two pooled columns, filled in one pass over the rows, then written in schema order (high, then low).
                using PooledColumn<ulong> high = PooledColumn<ulong>.Rent(bag.FixedValueSpan.Length);
                using PooledColumn<ulong> low = PooledColumn<ulong>.Rent(bag.FixedValueSpan.Length);
                _FillHalves(bag, high.WritableSpan, low.WritableSpan);
                ParquetFileWriter.Block(rowGroup.WriteAsync<ulong>(_HighField!, high.Memory)); // Invariant: the halves exist for these two types.
                ParquetFileWriter.Block(rowGroup.WriteAsync<ulong>(_LowField!, low.Memory)); // Invariant: same as _HighField.
                break;
            }

            default:
                // Bytes: reading BytesValues builds one array per row from the batch's byte arena, once per flush, instead of per
                // value while accumulating.
                ParquetFileWriter.Block(rowGroup.WriteAsync(_ValueField!, bag.BytesValues));
                break;
        }
    }

    /// <summary>Copies the 32-bit integer of every row of an IPv4 bag into <paramref name="destination"/>.</summary>
    /// <param name="bag">An IPv4 bag.</param>
    /// <param name="destination">One entry per row of the bag.</param>
    /// <exception cref="InvalidOperationException">A row is no IPv4 value (a broken invariant).</exception>
    private static void _FillIPv4(FieldColumnBag bag, Span<uint> destination)
    {
        ReadOnlySpan<FieldValueData> values = bag.FixedValueSpan;
        for (int row = 0; row < values.Length; row++)
        {
            if (!values[row].TryGetAsIPv4(out IPv4Address address))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.IPv4Address, values[row].Type);
            }

            destination[row] = address.RawValue;
        }
    }

    /// <summary>
    /// Copies the 64-bit integer of every row of a MAC (48 bits used) or EUI-64 bag into <paramref name="destination"/>. The two
    /// loops are separate because the accessor differs per type and a per-row type switch would cost a branch per value.
    /// </summary>
    /// <param name="bag">A MAC or EUI-64 bag.</param>
    /// <param name="destination">One entry per row of the bag.</param>
    /// <exception cref="InvalidOperationException">A row does not hold the bag's type (a broken invariant).</exception>
    private static void _FillIdentifiers(FieldColumnBag bag, Span<ulong> destination)
    {
        ReadOnlySpan<FieldValueData> values = bag.FixedValueSpan;
        if (bag.FieldType == FieldType.MacAddress)
        {
            for (int row = 0; row < values.Length; row++)
            {
                if (!values[row].TryGetAsMacAddress(out MacAddress mac))
                {
                    FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.MacAddress, values[row].Type);
                }

                destination[row] = mac.RawValue;
            }

            return;
        }

        for (int row = 0; row < values.Length; row++)
        {
            if (!values[row].TryGetAsEui64(out Eui64 identifier))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.Eui64, values[row].Type);
            }

            destination[row] = identifier.RawValue;
        }
    }

    /// <summary>
    /// Copies the high and the low 64 bits of every row of an IPv6 or UUID bag into the two destinations, in one pass.
    /// </summary>
    /// <param name="bag">An IPv6 or UUID bag.</param>
    /// <param name="high">One entry per row: the high 64 bits (the first eight bytes of the address in network order).</param>
    /// <param name="low">One entry per row: the low 64 bits.</param>
    /// <exception cref="InvalidOperationException">A row does not hold the bag's type (a broken invariant).</exception>
    private static void _FillHalves(FieldColumnBag bag, Span<ulong> high, Span<ulong> low)
    {
        ReadOnlySpan<FieldValueData> values = bag.FixedValueSpan;
        if (bag.FieldType == FieldType.IPv6Address)
        {
            for (int row = 0; row < values.Length; row++)
            {
                if (!values[row].TryGetAsIPv6(out IPv6Address address))
                {
                    FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.IPv6Address, values[row].Type);
                }

                high[row] = address.High;
                low[row] = address.Low;
            }

            return;
        }

        for (int row = 0; row < values.Length; row++)
        {
            if (!values[row].TryGetAsUuid(out Uuid uuid))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.Uuid, values[row].Type);
            }

            high[row] = uuid.High;
            low[row] = uuid.Low;
        }
    }

    /// <summary>
    /// Materializes a nullable string list, preserving <see langword="null"/> entries so Parquet
    /// matches DuckDB null semantics for absent custom text / string values: an absent text is a null entry (a null in the
    /// column), a set but empty text is the empty string. Nothing here may replace a null by an empty string.
    /// Sync: <see cref="OptionalTextReader"/> (null = absent) and EXPORTER_GUIDE.md § Rebuilding the display line.
    /// </summary>
    /// <param name="source">The bag's string list.</param>
    /// <returns>The entries in order: <paramref name="source"/> itself when it already is an array, otherwise a copy.</returns>
    private static string?[] _ToNullableStringArray(IReadOnlyList<string?> source)
    {
        if (source is string?[] array)
        {
            return array;
        }

        if (source is List<string?> list)
        {
            return list.ToArray();
        }

        string?[] result = new string?[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            result[i] = source[i];
        }
        return result;
    }

    #endregion

    #region Completion

    /// <summary>Closes the field's data file (writes its footer).</summary>
    internal void Complete()
    {
        _Writer?.Dispose();
        _Writer = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _Writer?.Dispose();
        _Writer = null;
    }

    #endregion
}

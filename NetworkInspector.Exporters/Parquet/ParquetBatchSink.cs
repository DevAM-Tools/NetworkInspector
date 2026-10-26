// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Parquet;

/// <summary>
/// Writes flushed <see cref="ColumnarPacketBatch"/> instances to a directory of Parquet files.
/// <para>
/// Directory layout:
/// <list type="bullet">
///   <item><c>packets.parquet</c> — one row per packet: <c>packet_id</c>, <c>timestamp_ns</c>,
///     and optionally <c>info</c> / <c>frame_bytes</c>.</item>
///   <item><c>topology.parquet</c> — one row per field-tree node: <c>packet_id</c>, <c>node_id</c>,
///     <c>field_id</c>, <c>parent_node_id</c>.</item>
///   <item><c>catalog.parquet</c> — one row per distinct field ID observed, written once in
///     <see cref="Complete"/>: <c>field_id</c>, <c>name</c>, <c>ui_name</c>, <c>field_type</c>,
///     <c>protocol_id</c>, <c>table_name</c>, <c>description</c> (nullable).</item>
///   <item><c>protocols.parquet</c> — one row per protocol registered in the parsing stack, written once
///     when the sink is created: <c>protocol_id</c>, <c>name</c>, <c>ui_name</c>.</item>
///   <item><c>fields/field_{id}.parquet</c> — one row per field occurrence: <c>packet_id</c>,
///     <c>node_id</c>, a type-specific <c>value</c> column (including <c>DataField&lt;string&gt;</c>
///     for <see cref="FieldType.String"/>; <see cref="FieldType.Timestamp"/> uses an INT64 column named
///     <c>value_timestamp_ns</c>; bytes and all fixed-size address types use a byte-array column), and optional
///     <c>custom_repr</c> / <c>custom_text</c> string columns.</item>
/// </list>
/// </para>
/// <para>
/// Each table file is written as a single Parquet file with one row group per flushed batch.
/// One file per field ID (instead of one wide table) keeps every file narrow and fully typed: a field's value
/// column has exactly one Parquet type, and no row stores a null for fields it does not have.
/// </para>
/// <para>
/// Files are opened lazily on the first non-empty batch for that table, so a table that never receives rows
/// (for example <c>topology.parquet</c> without <see cref="ColumnarDetailFlags.IncludeTopology"/>) is never created.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="WriteBatch"/> and <see cref="Complete"/>
/// must be called sequentially from a single thread, matching the exporter caller contract.
/// </para>
/// </summary>
internal sealed class ParquetBatchSink : IColumnarBatchSink, IDisposable
{
    #region Fields

    private readonly string _FieldsDirectory;
    private readonly ColumnarDetailFlags _Flags;

    // Schema column definitions are created once and reused for every row group of the export, so each row group
    // of a file is written against exactly the columns its schema was created with.
    private readonly DataField _PacketIdField = new DataField<int>("packet_id");
    private readonly DataField _TimestampField = new DataField<long>("timestamp_ns");
    private readonly DataField _InfoField = new DataField<string>("info");
    private readonly DataField _FrameBytesField = new DataField<byte[]>("frame_bytes");

    private readonly DataField _TopoPacketIdField = new DataField<int>("packet_id");
    private readonly DataField _TopoNodeIdField = new DataField<int>("node_id");
    private readonly DataField _TopoFieldIdField = new DataField<int>("field_id");
    private readonly DataField _TopoParentNodeIdField = new DataField<int>("parent_node_id");

    // Writers are created lazily so an unused table produces no file; they stay open across batches (one row group
    // per batch) and are closed in Complete or Dispose.
    private ParquetFileWriter? _PacketsWriter;
    private ParquetFileWriter? _TopologyWriter;
    private readonly Dictionary<int, FieldWriterState> _FieldWriters = new(64);

    // The sink keeps its own copy of the catalog rows so catalog.parquet can be written in Complete, once, with
    // every field that was seen in any batch (a field may first appear in a late batch).
    private readonly Dictionary<int, FieldCatalogEntry> _Catalog = new(64);
    private bool _Disposed;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a sink writing into <paramref name="rootDirectory"/> (must already exist) and writes
    /// <c>protocols.parquet</c> immediately, because the protocol set is fixed by the stack for the whole export.
    /// </summary>
    /// <param name="rootDirectory">Root output directory.</param>
    /// <param name="flags">Detail flags the batches were configured with; controls optional columns.</param>
    /// <param name="protocols">Protocols of the parsing stack; no <c>protocols.parquet</c> is written when empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="protocols"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">Old dataset files cannot be removed or the new files cannot be created.</exception>
    /// <remarks>
    /// <paramref name="rootDirectory"/> is not validated here: <see cref="ParquetExporter"/> passes the full path it
    /// normalized in its builder, and the constructor is internal.
    /// </remarks>
    internal ParquetBatchSink(string rootDirectory, ColumnarDetailFlags flags, IReadOnlyList<ProtocolCatalogEntry> protocols)
    {
        ArgumentNullException.ThrowIfNull(protocols);

        _FieldsDirectory = Path.Combine(rootDirectory, "fields");
        _Flags = flags;

        // Remove the previous dataset before anything new is written so a re-export never mixes old and new files.
        _ClearPriorArtifacts(rootDirectory);
        Directory.CreateDirectory(_FieldsDirectory);

        string PacketsPath() => Path.Combine(rootDirectory, "packets.parquet");
        string TopologyPath() => Path.Combine(rootDirectory, "topology.parquet");
        _PacketsPath = PacketsPath();
        _TopologyPath = TopologyPath();
        _CatalogPath = Path.Combine(rootDirectory, "catalog.parquet");

        _WriteProtocols(Path.Combine(rootDirectory, "protocols.parquet"), protocols);
    }

    private readonly string _PacketsPath;
    private readonly string _TopologyPath;
    private readonly string _CatalogPath;

    #endregion

    #region IColumnarBatchSink

    /// <inheritdoc/>
    /// <remarks>
    /// Not atomic: the row groups of one batch are appended file by file, so an exception part-way leaves the
    /// tables with different numbers of row groups for that batch. <see cref="ParquetExporter"/> reports the failure
    /// as an export error.
    /// </remarks>
    public void WriteBatch(ColumnarPacketBatch batch)
    {
        if (batch.PacketCount == 0)
        {
            return;
        }

        // One row group per table per batch; every table carries packet_id so the tables can be joined back together.
        _WritePacketsRowGroup(batch);

        if ((_Flags & ColumnarDetailFlags.IncludeTopology) != 0 && batch.Topology.Count > 0)
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
            state.WriteRowGroup(_FieldsDirectory, entry.Value, _Flags);
        }

        // Copy the catalog rows; they are only written once, at Complete. Overwriting by id is harmless because the
        // batch creates a field's catalog entry once and never changes it.
        foreach (KeyValuePair<int, FieldCatalogEntry> entry in batch.Catalog)
        {
            _Catalog[entry.Key] = entry.Value;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Closing a writer writes the Parquet footer, which is what makes the file readable. Complete closes the packet,
    /// topology and field files and then writes <c>catalog.parquet</c>, which only exists after this call
    /// (<see cref="Dispose"/> closes the same files but never writes the catalog).
    /// </remarks>
    public void Complete()
    {
        _PacketsWriter?.Dispose();
        _PacketsWriter = null;
        _TopologyWriter?.Dispose();
        _TopologyWriter = null;

        foreach (FieldWriterState state in _FieldWriters.Values)
        {
            state.Complete();
        }
        _FieldWriters.Clear();

        _WriteCatalog();
    }

    #endregion

    #region IDisposable

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
        _TopologyWriter?.Dispose();
        _TopologyWriter = null;
        foreach (FieldWriterState state in _FieldWriters.Values)
        {
            state.Dispose();
        }
        _FieldWriters.Clear();
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Deletes prior Parquet artifacts under <paramref name="rootDirectory"/> so a re-export to
    /// the same path does not leave orphan <c>fields/field_*.parquet</c> files from a previous
    /// run with a different field-ID set. Base files are overwritten via <c>File.Create</c>
    /// anyway; clearing the fields directory is what makes re-export deterministic. The base files are deleted
    /// as well because some are created conditionally (<c>topology.parquet</c>, <c>catalog.parquet</c>,
    /// <c>protocols.parquet</c>), so a stale copy from an earlier run would otherwise survive next to the new dataset.
    /// </summary>
    /// <remarks>
    /// Security: this deletes files in a caller-chosen directory. The threat is destroying unrelated data, so the
    /// deletion is restricted to the four fixed dataset file names and to <c>*.parquet</c> files directly inside
    /// <c>fields</c>; no recursion and no other pattern. Rejected alternative: deleting the whole directory, which
    /// would remove foreign files. Accepted residual risk: a user's own <c>fields/*.parquet</c> files in the chosen
    /// directory are removed; the Parquet README documents this as the re-export behaviour.
    /// </remarks>
    private static void _ClearPriorArtifacts(string rootDirectory)
    {
        foreach (string name in (string[])
                 [
                     "packets.parquet",
                     "topology.parquet",
                     "catalog.parquet",
                     "protocols.parquet"
                 ])
        {
            string path = Path.Combine(rootDirectory, name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        string fieldsDirectory = Path.Combine(rootDirectory, "fields");
        if (!Directory.Exists(fieldsDirectory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(fieldsDirectory, "*.parquet"))
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Returns the writer state for a field ID, creating it on the field's first non-empty batch. The field's
    /// <see cref="FieldType"/> is taken from the first bag seen and then fixed for the whole export, because the
    /// Parquet column type of <c>field_{id}.parquet</c> cannot change after the file is created.
    /// </summary>
    private FieldWriterState _GetOrCreateFieldWriter(int fieldIdValue, FieldType fieldType)
    {
        if (!_FieldWriters.TryGetValue(fieldIdValue, out FieldWriterState? state))
        {
            state = new FieldWriterState(fieldIdValue, fieldType);
            _FieldWriters[fieldIdValue] = state;
        }
        return state;
    }

    /// <summary>Appends one row group to <c>packets.parquet</c>, creating the file with the flag-dependent schema on first use.</summary>
    private void _WritePacketsRowGroup(ColumnarPacketBatch batch)
    {
        // The schema depends on the detail flags, which are fixed for the export, so it is built once when the file
        // is created. Optional columns are added to the schema only when the batch collected them; otherwise the
        // batch lists (Infos, FrameBytesList) would be empty and shorter than the other columns.
        List<DataField> fields = [_PacketIdField, _TimestampField];
        bool includeInfo = (_Flags & ColumnarDetailFlags.IncludeInfo) != 0;
        bool includeFrameBytes = (_Flags & ColumnarDetailFlags.IncludeFrameBytes) != 0;
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
        _PacketsWriter ??= new ParquetFileWriter(_PacketsPath, new ParquetSchema(fields));
        _PacketsWriter.WriteRowGroup(rowGroup =>
        {
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_PacketIdField, ToArray(batch.PacketIds)));
            ParquetFileWriter.Block(rowGroup.WriteAsync<long>(_TimestampField, ToArray(batch.Timestamps)));
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

    /// <summary>Appends one row group to <c>topology.parquet</c> (flat node rows; <c>parent_node_id</c> is −1 for top-level nodes).</summary>
    private void _WriteTopologyRowGroup(ColumnarPacketBatch batch)
    {
        // The batch stores topology as a list of structs (one row per node), but Parquet is written one column at a
        // time from an array, so the rows are split into four int[] columns. This is one pass and four allocations
        // per flush, not per node.
        IReadOnlyList<TopologyNode> topology = batch.Topology;
        int count = topology.Count;
        int[] packetIds = new int[count];
        int[] nodeIds = new int[count];
        int[] fieldIds = new int[count];
        int[] parentNodeIds = new int[count];
        for (int i = 0; i < count; i++)
        {
            TopologyNode node = topology[i];
            packetIds[i] = node.PacketId;
            nodeIds[i] = node.NodeId;
            fieldIds[i] = node.FieldId;
            parentNodeIds[i] = node.ParentNodeId;
        }

        _TopologyWriter ??= new ParquetFileWriter(
            _TopologyPath,
            new ParquetSchema(_TopoPacketIdField, _TopoNodeIdField, _TopoFieldIdField, _TopoParentNodeIdField));
        _TopologyWriter.WriteRowGroup(rowGroup =>
        {
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_TopoPacketIdField, packetIds));
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_TopoNodeIdField, nodeIds));
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_TopoFieldIdField, fieldIds));
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_TopoParentNodeIdField, parentNodeIds));
        });
    }

    /// <summary>
    /// Returns an array holding the elements of <paramref name="source"/>: the same array when it already is one,
    /// otherwise a new copy. <see cref="ParquetRowGroupWriter"/>'s
    /// value-type <c>WriteAsync</c> overloads take <see cref="ReadOnlyMemory{T}"/> (which arrays
    /// convert to implicitly), not <see cref="IReadOnlyList{T}"/>, so batch/bag columns backed by
    /// <see cref="List{T}"/> must be materialized before writing. This costs one copy of each column per flush
    /// (a transient allocation proportional to the block size); <see cref="List{T}.ToArray"/> is used for lists
    /// because it is a single block copy, while the indexed loop is only the fallback for other list types.
    /// The returned array must not be modified by the caller.
    /// </summary>
    internal static T[] ToArray<T>(IReadOnlyList<T> source)
    {
        if (source is T[] array)
        {
            return array;
        }

        if (source is List<T> list)
        {
            return list.ToArray();
        }

        T[] result = new T[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            result[i] = source[i];
        }
        return result;
    }

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
        DataField tableName = new DataField<string>("table_name");
        // Appended last so readers that address catalog columns by position keep working (new catalog
        // columns are only ever appended); nullable because a field may have no description.
        DataField description = new DataField<string?>("description");

        int count = _Catalog.Count;
        int[] fieldIds = new int[count];
        string[] names = new string[count];
        string[] uiNames = new string[count];
        int[] fieldTypes = new int[count];
        int[] protocolIds = new int[count];
        string[] tableNames = new string[count];
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

        using ParquetFileWriter writer = new(
            _CatalogPath, new ParquetSchema(fieldId, name, uiName, fieldType, protocolId, tableName, description));
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
    /// <param name="path">Target file path.</param>
    /// <param name="protocols">Protocols of the parsing stack.</param>
    private static void _WriteProtocols(string path, IReadOnlyList<ProtocolCatalogEntry> protocols)
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

        using ParquetFileWriter writer = new(path, new ParquetSchema(protocolId, name, uiName));
        writer.WriteRowGroup(rowGroup =>
        {
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(protocolId, protocolIds));
            ParquetFileWriter.Block(rowGroup.WriteAsync(name, names));
            ParquetFileWriter.Block(rowGroup.WriteAsync(uiName, uiNames));
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
/// The file is created when the writer is constructed (truncating an existing file) and the footer is written by
/// <see cref="Dispose"/>; a file whose writer was never disposed is not a valid Parquet file. <see cref="Dispose"/>
/// must be called at most once.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; owned by one <see cref="ParquetBatchSink"/> and used from its single caller thread.
/// </para>
/// </summary>
internal sealed class ParquetFileWriter : IDisposable
{
    private readonly FileStream _Stream;
    private readonly ParquetWriter _Writer;

    /// <summary>Creates the file and opens a Parquet writer against it with the given schema.</summary>
    /// <param name="path">Target file path; an existing file is truncated.</param>
    /// <param name="schema">Schema of the file; fixed for the lifetime of the writer.</param>
    internal ParquetFileWriter(string path, ParquetSchema schema)
    {
        _Stream = File.Create(path);
        try
        {
            _Writer = Block(ParquetWriter.CreateAsync(schema, _Stream));
        }
        catch
        {
            // The caller never receives this instance when the constructor throws, so nobody could dispose the stream;
            // closing it here keeps the half-created file from staying locked.
            _Stream.Dispose();
            throw;
        }
    }

    /// <summary>Synchronously waits for a Parquet.Net operation; exceptions surface unwrapped (not as <see cref="AggregateException"/>).</summary>
    /// <param name="task">The pending Parquet.Net operation.</param>
    internal static void Block(System.Threading.Tasks.Task task)
    {
        // Parquet.Net exposes only async writers; the IPacketListener contract is synchronous. Approved sync-over-async
        // (rule "no .Result/.Wait", reason: no sync API, scope: this method, user approved). Callers must not run the
        // exporter on a thread with a SynchronizationContext.
        // GetAwaiter().GetResult() is used instead of Wait() so the original exception is rethrown directly rather
        // than wrapped in an AggregateException. The deadlock risk (blocking a thread that owns a
        // SynchronizationContext while the task needs that context to complete) is the accepted residual risk,
        // mitigated only by the documented caller contract.
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
/// <see cref="ColumnarDetailFlags"/> are known). String columns are written as plain
/// <c>DataField&lt;string&gt;</c> columns.
/// <para>
/// <b>Thread safety:</b> Not thread-safe; owned by one <see cref="ParquetBatchSink"/> and used from its single caller thread.
/// </para>
/// </summary>
internal sealed class FieldWriterState : IDisposable
{
    #region Fields

    private readonly int _FieldIdValue;
    private readonly FieldType _FieldType;

    private readonly DataField _PacketIdField = new DataField<int>("packet_id");
    private readonly DataField _NodeIdField = new DataField<int>("node_id");
    private readonly DataField? _ValueField;
    private readonly DataField _CustomReprField = new DataField<string?>("custom_repr");
    private readonly DataField _CustomTextField = new DataField<string?>("custom_text");

    private ParquetFileWriter? _Writer;

    #endregion

    #region Constructor

    /// <summary>Creates the writer state for one field, deriving its Parquet schema from <paramref name="fieldType"/>.</summary>
    /// <param name="fieldIdValue">The field ID; names the file <c>field_{id}.parquet</c>.</param>
    /// <param name="fieldType">The field's value type; selects the Parquet type of the <c>value</c> column.</param>
    internal FieldWriterState(int fieldIdValue, FieldType fieldType)
    {
        _FieldIdValue = fieldIdValue;
        _FieldType = fieldType;

        // One native Parquet column type per FieldType, so consumers get typed values without parsing. Timestamp is
        // plain INT64 nanoseconds since the Unix epoch (no Parquet logical timestamp type), hence the column name
        // value_timestamp_ns. The default case stores raw bytes: FieldType.Bytes and the fixed-size address types
        // (MAC, IPv4, IPv6, EUI-64, UUID) as their binary form. _WriteTypedValue must stay in sync with this switch.
        _ValueField = fieldType switch
        {
            FieldType.Bool => new DataField<bool>("value"),
            FieldType.I64 => new DataField<long>("value"),
            FieldType.U64 => new DataField<ulong>("value"),
            FieldType.F64 => new DataField<double>("value"),
            FieldType.Timestamp => new DataField<long>("value_timestamp_ns"),
            FieldType.String => new DataField<string?>("value"),
            _ => new DataField<byte[]>("value"), // Bytes + fixed-size address types
        };
    }

    #endregion

    #region Internal API

    /// <summary>Writes one row group for this field from a single flushed batch's <see cref="FieldColumnBag"/>.</summary>
    /// <param name="fieldsDirectory">Directory that holds the per-field files.</param>
    /// <param name="bag">The batch's rows for this field; must be non-empty.</param>
    /// <param name="flags">Detail flags of the export; decide which optional columns exist in the file.</param>
    internal void WriteRowGroup(string fieldsDirectory, FieldColumnBag bag, ColumnarDetailFlags flags)
    {
        // The file is created on the first batch that has rows for this field, with a schema derived from the
        // flags; later batches must write the same columns, which holds because the flags never change.
        _Writer ??= new ParquetFileWriter(
            Path.Combine(fieldsDirectory, $"field_{_FieldIdValue}.parquet"), _BuildSchema(flags));

        _Writer.WriteRowGroup(rowGroup =>
        {
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_PacketIdField, ParquetBatchSink.ToArray(bag.PacketIds)));
            ParquetFileWriter.Block(rowGroup.WriteAsync<int>(_NodeIdField, ParquetBatchSink.ToArray(bag.NodeIds)));

            _WriteTypedValue(rowGroup, bag);

            if ((flags & ColumnarDetailFlags.IncludeCustomRepresentation) != 0)
            {
                ParquetFileWriter.Block(rowGroup.WriteAsync(_CustomReprField, _ToNullableStringArray(bag.CustomRepresentations)));
            }
            if ((flags & ColumnarDetailFlags.IncludeCustomText) != 0)
            {
                ParquetFileWriter.Block(rowGroup.WriteAsync(_CustomTextField, _ToNullableStringArray(bag.CustomTexts)));
            }
        });
    }

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

    #region Private Helpers

    /// <summary>Builds the schema of the field file; column order here is the order in which row groups must write them.</summary>
    private ParquetSchema _BuildSchema(ColumnarDetailFlags flags)
    {
        List<DataField> fields = [_PacketIdField, _NodeIdField];
        if (_ValueField is not null)
        {
            fields.Add(_ValueField);
        }
        if ((flags & ColumnarDetailFlags.IncludeCustomRepresentation) != 0)
        {
            fields.Add(_CustomReprField);
        }
        if ((flags & ColumnarDetailFlags.IncludeCustomText) != 0)
        {
            fields.Add(_CustomTextField);
        }
        return new ParquetSchema(fields);
    }

    /// <summary>Writes the <c>value</c> column from the bag list that matches the field type (the only list the bag fills).</summary>
    private void _WriteTypedValue(ParquetRowGroupWriter rowGroup, FieldColumnBag bag)
    {
        switch (_FieldType)
        {
            case FieldType.Bool:
                ParquetFileWriter.Block(rowGroup.WriteAsync<bool>(_ValueField!, ParquetBatchSink.ToArray(bag.BoolValues)));
                break;
            case FieldType.I64:
                ParquetFileWriter.Block(rowGroup.WriteAsync<long>(_ValueField!, ParquetBatchSink.ToArray(bag.I64Values)));
                break;
            case FieldType.U64:
                ParquetFileWriter.Block(rowGroup.WriteAsync<ulong>(_ValueField!, ParquetBatchSink.ToArray(bag.U64Values)));
                break;
            case FieldType.F64:
                ParquetFileWriter.Block(rowGroup.WriteAsync<double>(_ValueField!, ParquetBatchSink.ToArray(bag.F64Values)));
                break;
            case FieldType.Timestamp:
                ParquetFileWriter.Block(rowGroup.WriteAsync<long>(_ValueField!, ParquetBatchSink.ToArray(bag.TimestampValues)));
                break;
            case FieldType.String:
                ParquetFileWriter.Block(rowGroup.WriteAsync(_ValueField!, _ToNullableStringArray(bag.StringValues)));
                break;
            default:
                // Reading BytesValues is what turns the bag's deferred fixed-size address payloads into byte[]:
                // the per-value arrays are allocated here, once per flush, instead of per value while accumulating.
                ParquetFileWriter.Block(rowGroup.WriteAsync(_ValueField!, bag.BytesValues));
                break;
        }
    }

    /// <summary>
    /// Materializes a nullable string list, preserving <see langword="null"/> entries so Parquet
    /// matches DuckDB null semantics for absent custom text / string values.
    /// </summary>
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
}

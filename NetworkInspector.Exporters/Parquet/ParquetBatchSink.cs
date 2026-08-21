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
///     <c>protocol_id</c>, <c>table_name</c>.</item>
///   <item><c>fields/field_{id}.parquet</c> — one row per field occurrence: <c>packet_id</c>,
///     <c>node_id</c>, a type-specific <c>value</c> column (including <c>DataField&lt;string&gt;</c>
///     for <see cref="FieldType.String"/>), and optional <c>custom_repr</c> / <c>custom_text</c>
///     string columns.</item>
/// </list>
/// </para>
/// <para>
/// Each table file is written as a single Parquet file with one row group per flushed batch.
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

    private readonly DataField _PacketIdField = new DataField<int>("packet_id");
    private readonly DataField _TimestampField = new DataField<long>("timestamp_ns");
    private readonly DataField _InfoField = new DataField<string>("info");
    private readonly DataField _FrameBytesField = new DataField<byte[]>("frame_bytes");

    private readonly DataField _TopoPacketIdField = new DataField<int>("packet_id");
    private readonly DataField _TopoNodeIdField = new DataField<int>("node_id");
    private readonly DataField _TopoFieldIdField = new DataField<int>("field_id");
    private readonly DataField _TopoParentNodeIdField = new DataField<int>("parent_node_id");

    private ParquetFileWriter? _PacketsWriter;
    private ParquetFileWriter? _TopologyWriter;
    private readonly Dictionary<int, FieldWriterState> _FieldWriters = new(64);
    private readonly Dictionary<int, FieldCatalogEntry> _Catalog = new(64);
    private bool _Disposed;

    #endregion

    #region Constructor

    /// <summary>Creates a sink writing into <paramref name="rootDirectory"/> (must already exist).</summary>
    /// <param name="rootDirectory">Root output directory.</param>
    /// <param name="flags">Detail flags the batches were configured with; controls optional columns.</param>
    internal ParquetBatchSink(string rootDirectory, ColumnarDetailFlags flags)
    {
        _FieldsDirectory = Path.Combine(rootDirectory, "fields");
        _Flags = flags;
        _ClearPriorArtifacts(rootDirectory);
        Directory.CreateDirectory(_FieldsDirectory);

        string PacketsPath() => Path.Combine(rootDirectory, "packets.parquet");
        string TopologyPath() => Path.Combine(rootDirectory, "topology.parquet");
        _PacketsPath = PacketsPath();
        _TopologyPath = TopologyPath();
        _CatalogPath = Path.Combine(rootDirectory, "catalog.parquet");
    }

    private readonly string _PacketsPath;
    private readonly string _TopologyPath;
    private readonly string _CatalogPath;

    #endregion

    #region IColumnarBatchSink

    /// <inheritdoc/>
    public void WriteBatch(ColumnarPacketBatch batch)
    {
        if (batch.PacketCount == 0)
        {
            return;
        }

        _WritePacketsRowGroup(batch);

        if ((_Flags & ColumnarDetailFlags.IncludeTopology) != 0 && batch.Topology.Count > 0)
        {
            _WriteTopologyRowGroup(batch);
        }

        foreach (KeyValuePair<int, FieldColumnBag> entry in batch.FieldBags)
        {
            if (entry.Value.RowCount == 0)
            {
                continue;
            }

            FieldWriterState state = _GetOrCreateFieldWriter(entry.Key, entry.Value.FieldType);
            state.WriteRowGroup(_FieldsDirectory, entry.Value, _Flags);
        }

        foreach (KeyValuePair<int, FieldCatalogEntry> entry in batch.Catalog)
        {
            _Catalog[entry.Key] = entry.Value;
        }
    }

    /// <inheritdoc/>
    public void Complete()
    {
        _PacketsWriter?.Dispose();
        _PacketsWriter = null;
        _TopologyWriter?.Dispose();
        _TopologyWriter = null;

        foreach (FieldWriterState state in _FieldWriters.Values)
        {
            state.Complete(_FieldsDirectory, _Flags);
        }
        _FieldWriters.Clear();

        _WriteCatalog();
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Releases any still-open writers. Safe to call after <see cref="Complete"/> (no-op) or
    /// instead of it on an error path (leaves catalog / field files unwritten, matching the
    /// "best-effort partial output" behaviour of other exporters when finalization fails).
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
    /// anyway; clearing the fields directory is what makes re-export deterministic.
    /// </summary>
    private static void _ClearPriorArtifacts(string rootDirectory)
    {
        foreach (string name in (string[])
                 [
                     "packets.parquet",
                     "topology.parquet",
                     "catalog.parquet"
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

    private FieldWriterState _GetOrCreateFieldWriter(int fieldIdValue, FieldType fieldType)
    {
        if (!_FieldWriters.TryGetValue(fieldIdValue, out FieldWriterState? state))
        {
            state = new FieldWriterState(fieldIdValue, fieldType);
            _FieldWriters[fieldIdValue] = state;
        }
        return state;
    }

    private void _WritePacketsRowGroup(ColumnarPacketBatch batch)
    {
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

        _PacketsWriter ??= new ParquetFileWriter(_PacketsPath, new ParquetSchema(fields));
        _PacketsWriter.WriteRowGroup(rowGroup =>
        {
            rowGroup.WriteAsync<int>(_PacketIdField, _ToArray(batch.PacketIds)).GetAwaiter().GetResult();
            rowGroup.WriteAsync<long>(_TimestampField, _ToArray(batch.Timestamps)).GetAwaiter().GetResult();
            if (includeInfo)
            {
                rowGroup.WriteAsync(_InfoField, batch.Infos).GetAwaiter().GetResult();
            }
            if (includeFrameBytes)
            {
                rowGroup.WriteAsync(_FrameBytesField, batch.FrameBytesList).GetAwaiter().GetResult();
            }
        });
    }

    private void _WriteTopologyRowGroup(ColumnarPacketBatch batch)
    {
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
            rowGroup.WriteAsync<int>(_TopoPacketIdField, packetIds).GetAwaiter().GetResult();
            rowGroup.WriteAsync<int>(_TopoNodeIdField, nodeIds).GetAwaiter().GetResult();
            rowGroup.WriteAsync<int>(_TopoFieldIdField, fieldIds).GetAwaiter().GetResult();
            rowGroup.WriteAsync<int>(_TopoParentNodeIdField, parentNodeIds).GetAwaiter().GetResult();
        });
    }

    /// <summary>
    /// Copies <paramref name="source"/> into a new array. <see cref="ParquetRowGroupWriter"/>'s
    /// value-type <c>WriteAsync</c> overloads take <see cref="ReadOnlyMemory{T}"/> (which arrays
    /// convert to implicitly), not <see cref="IReadOnlyList{T}"/>, so batch/bag columns backed by
    /// <see cref="List{T}"/> must be materialized before writing.
    /// Prefer <see cref="List{T}.ToArray"/> when the source is already a list.
    /// </summary>
    private static T[] _ToArray<T>(IReadOnlyList<T> source)
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

        int count = _Catalog.Count;
        int[] fieldIds = new int[count];
        string[] names = new string[count];
        string[] uiNames = new string[count];
        int[] fieldTypes = new int[count];
        int[] protocolIds = new int[count];
        string[] tableNames = new string[count];
        int row = 0;
        foreach (FieldCatalogEntry entry in _Catalog.Values)
        {
            fieldIds[row] = entry.FieldIdValue;
            names[row] = entry.Name;
            uiNames[row] = entry.UiName;
            fieldTypes[row] = (int)entry.FieldType;
            protocolIds[row] = entry.ProtocolIdValue;
            tableNames[row] = entry.TableName;
            row++;
        }

        using ParquetFileWriter writer = new(
            _CatalogPath, new ParquetSchema(fieldId, name, uiName, fieldType, protocolId, tableName));
        writer.WriteRowGroup(rowGroup =>
        {
            rowGroup.WriteAsync<int>(fieldId, fieldIds).GetAwaiter().GetResult();
            rowGroup.WriteAsync(name, names).GetAwaiter().GetResult();
            rowGroup.WriteAsync(uiName, uiNames).GetAwaiter().GetResult();
            rowGroup.WriteAsync<int>(fieldType, fieldTypes).GetAwaiter().GetResult();
            rowGroup.WriteAsync<int>(protocolId, protocolIds).GetAwaiter().GetResult();
            rowGroup.WriteAsync(tableName, tableNames).GetAwaiter().GetResult();
        });
    }

    #endregion
}

/// <summary>
/// Thin wrapper around a lazily-opened <see cref="global::Parquet.ParquetWriter"/> for a single
/// table file. Bridges the writer's async-only API to the synchronous exporter pipeline via
/// <c>GetAwaiter().GetResult()</c> — safe here because file I/O on a dedicated stream has no
/// captured synchronization context to deadlock against.
/// </summary>
internal sealed class ParquetFileWriter : IDisposable
{
    private readonly FileStream _Stream;
    private readonly ParquetWriter _Writer;

    /// <summary>Creates the file and opens a Parquet writer against it with the given schema.</summary>
    internal ParquetFileWriter(string path, ParquetSchema schema)
    {
        _Stream = File.Create(path);
        _Writer = ParquetWriter.CreateAsync(schema, _Stream).GetAwaiter().GetResult();
    }

    /// <summary>Writes one row group, invoking <paramref name="writeColumns"/> to populate it.</summary>
    internal void WriteRowGroup(Action<ParquetRowGroupWriter> writeColumns)
    {
        using ParquetRowGroupWriter rowGroup = _Writer.CreateRowGroup();
        writeColumns(rowGroup);
    }

    /// <summary>
    /// Finalizes the file's footer and releases the stream. <see cref="ParquetWriter"/> only
    /// exposes <see cref="IAsyncDisposable"/>; blocked on synchronously for the same reason as
    /// <see cref="WriteRowGroup"/> (see the type-level remarks).
    /// </summary>
    public void Dispose()
    {
        // ValueTask.AsTask() materializes it into an ordinary Task before blocking, avoiding the
        // "may not be safe to consume twice / while pending" hazard CA2012 warns about for raw
        // ValueTask instances.
        _Writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _Stream.Dispose();
    }
}

/// <summary>
/// Per-field Parquet writer state: the lazily-created <c>fields/field_{id}.parquet</c> writer
/// and its schema (fixed once the field's <see cref="FieldType"/> and the export's
/// <see cref="ColumnarDetailFlags"/> are known). String columns are written as plain
/// <c>DataField&lt;string&gt;</c> columns.
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
    internal FieldWriterState(int fieldIdValue, FieldType fieldType)
    {
        _FieldIdValue = fieldIdValue;
        _FieldType = fieldType;
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
    internal void WriteRowGroup(string fieldsDirectory, FieldColumnBag bag, ColumnarDetailFlags flags)
    {
        _Writer ??= new ParquetFileWriter(
            Path.Combine(fieldsDirectory, $"field_{_FieldIdValue}.parquet"), _BuildSchema(flags));

        _Writer.WriteRowGroup(rowGroup =>
        {
            rowGroup.WriteAsync<int>(_PacketIdField, _ToArray(bag.PacketIds)).GetAwaiter().GetResult();
            rowGroup.WriteAsync<int>(_NodeIdField, _ToArray(bag.NodeIds)).GetAwaiter().GetResult();

            _WriteTypedValue(rowGroup, bag);

            if ((flags & ColumnarDetailFlags.IncludeCustomRepresentation) != 0)
            {
                rowGroup.WriteAsync(_CustomReprField, _ToNullableStringArray(bag.CustomRepresentations)).GetAwaiter().GetResult();
            }
            if ((flags & ColumnarDetailFlags.IncludeCustomText) != 0)
            {
                rowGroup.WriteAsync(_CustomTextField, _ToNullableStringArray(bag.CustomTexts)).GetAwaiter().GetResult();
            }
        });
    }

    /// <summary>Closes the field's data file.</summary>
    internal void Complete(string fieldsDirectory, ColumnarDetailFlags flags)
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

    private void _WriteTypedValue(ParquetRowGroupWriter rowGroup, FieldColumnBag bag)
    {
        switch (_FieldType)
        {
            case FieldType.Bool:
                rowGroup.WriteAsync<bool>(_ValueField!, _ToArray(bag.BoolValues)).GetAwaiter().GetResult();
                break;
            case FieldType.I64:
                rowGroup.WriteAsync<long>(_ValueField!, _ToArray(bag.I64Values)).GetAwaiter().GetResult();
                break;
            case FieldType.U64:
                rowGroup.WriteAsync<ulong>(_ValueField!, _ToArray(bag.U64Values)).GetAwaiter().GetResult();
                break;
            case FieldType.F64:
                rowGroup.WriteAsync<double>(_ValueField!, _ToArray(bag.F64Values)).GetAwaiter().GetResult();
                break;
            case FieldType.Timestamp:
                rowGroup.WriteAsync<long>(_ValueField!, _ToArray(bag.TimestampValues)).GetAwaiter().GetResult();
                break;
            case FieldType.String:
                rowGroup.WriteAsync(_ValueField!, _ToNullableStringArray(bag.StringValues)).GetAwaiter().GetResult();
                break;
            default:
                rowGroup.WriteAsync(_ValueField!, bag.BytesValues).GetAwaiter().GetResult();
                break;
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> into a new array — required because <c>WriteAsync</c>'s
    /// value-type overloads take <see cref="ReadOnlyMemory{T}"/>, not <see cref="IReadOnlyList{T}"/>.
    /// Prefer <see cref="List{T}.ToArray"/> when the source is already a list.
    /// </summary>
    private static T[] _ToArray<T>(IReadOnlyList<T> source)
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

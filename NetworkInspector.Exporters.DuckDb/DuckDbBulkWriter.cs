// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.DuckDb;

/// <summary>
/// Writes flushed <see cref="ColumnarPacketBatch"/> instances to a DuckDB database file using the
/// bulk-load <see cref="DuckDBAppender"/> API.
/// <para>
/// Table layout:
/// <list type="bullet">
///   <item><c>packets(packet_id INTEGER, timestamp_ns BIGINT [, info VARCHAR] [, frame_bytes BLOB])</c>
///     — one row per packet. <c>packet_id</c> matches Core <see cref="PacketId"/> (<see cref="int"/>).</item>
///   <item><c>topology(packet_id INTEGER, node_id INTEGER, field_id INTEGER, parent_node_id INTEGER)</c>
///     — one row per field-tree node.</item>
///   <item><c>catalog(field_id INTEGER, name VARCHAR, ui_name VARCHAR, field_type INTEGER,
///     protocol_id INTEGER, table_name VARCHAR)</c> — one row per distinct field ID observed,
///     written once in <see cref="Complete"/>.</item>
///   <item><c>field_{id}(packet_id INTEGER, node_id INTEGER, value &lt;type&gt;
///     [, custom_repr VARCHAR] [, custom_text VARCHAR])</c>
///     — one row per field occurrence. <c>value</c> is <c>VARCHAR</c> for
///     <see cref="FieldType.String"/>.</item>
/// </list>
/// </para>
/// <para>
/// <see cref="FieldType.U64"/> values are stored as DuckDB's native <c>UBIGINT</c>, matching
/// Parquet's <c>DataField&lt;ulong&gt;</c> / <c>UINT_64</c> unsigned semantics.
/// </para>
/// <para>
/// <b>Write performance:</b> every <see cref="WriteBatch"/> call wraps its appends in a single
/// explicit transaction (<c>BEGIN</c>/<c>COMMIT</c>) and uses <see cref="DuckDBAppender"/> — never
/// row-at-a-time <c>INSERT</c> — for all data tables. <c>CHECKPOINT</c> is never issued mid-batch;
/// it only runs once, at the very end of <see cref="Complete"/>, so the WAL is not fsynced on
/// every flush.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="WriteBatch"/> and <see cref="Complete"/>
/// must be called sequentially from a single thread, matching the exporter caller contract.
/// </para>
/// </summary>
internal sealed class DuckDbBulkWriter : IColumnarBatchSink, IDisposable
{
    #region Fields

    private readonly DuckDBConnection _Connection;
    private readonly ColumnarDetailFlags _Flags;

    private readonly HashSet<int> _CreatedFieldTables = new();
    private readonly Dictionary<int, FieldCatalogEntry> _Catalog = new(64);

    private bool _Disposed;

    #endregion

    #region Constructor

    /// <summary>
    /// Opens a fresh DuckDB file at <paramref name="path"/> and creates the base tables.
    /// Any pre-existing file (and its <c>.wal</c> sidecar) at the same path is deleted first so
    /// re-exports overwrite rather than appending duplicate rows into an existing database.
    /// </summary>
    /// <param name="path">Target <c>.duckdb</c> file path.</param>
    /// <param name="flags">Detail flags the batches were configured with; controls optional columns.</param>
    internal DuckDbBulkWriter(string path, ColumnarDetailFlags flags)
    {
        _Flags = flags;
        _DeleteExistingDatabase(path);
        _Connection = new DuckDBConnection($"Data Source={path}");
        _Connection.Open();

        using (DuckDBCommand tuning = _Connection.CreateCommand())
        {
            tuning.CommandText = FormattableString.Invariant(
                $"SET threads TO {Environment.ProcessorCount}; SET memory_limit = '4GB';");
            tuning.ExecuteNonQuery();
        }

        using (DuckDBCommand ddl = _Connection.CreateCommand())
        {
            ddl.CommandText =
                "CREATE TABLE IF NOT EXISTS packets(packet_id INTEGER, timestamp_ns BIGINT"
                + ((_Flags & ColumnarDetailFlags.IncludeInfo) != 0 ? ", info VARCHAR" : string.Empty)
                + ((_Flags & ColumnarDetailFlags.IncludeFrameBytes) != 0 ? ", frame_bytes BLOB" : string.Empty)
                + ");"
                + "CREATE TABLE IF NOT EXISTS topology(packet_id INTEGER, node_id INTEGER, field_id INTEGER, parent_node_id INTEGER);"
                + "CREATE TABLE IF NOT EXISTS catalog(field_id INTEGER, name VARCHAR, ui_name VARCHAR, field_type INTEGER, protocol_id INTEGER, table_name VARCHAR);";
            ddl.ExecuteNonQuery();
        }
    }

    #endregion

    #region IColumnarBatchSink

    /// <inheritdoc/>
    public void WriteBatch(ColumnarPacketBatch batch)
    {
        if (batch.PacketCount == 0)
        {
            return;
        }

        using DuckDBTransaction transaction = _Connection.BeginTransaction();

        _WritePackets(batch);

        if ((_Flags & ColumnarDetailFlags.IncludeTopology) != 0 && batch.Topology.Count > 0)
        {
            _WriteTopology(batch);
        }

        foreach (KeyValuePair<int, FieldColumnBag> entry in batch.FieldBags)
        {
            if (entry.Value.RowCount == 0)
            {
                continue;
            }

            _EnsureFieldTables(entry.Key, entry.Value.FieldType);
            _WriteFieldRows(entry.Key, entry.Value);
        }

        foreach (KeyValuePair<int, FieldCatalogEntry> entry in batch.Catalog)
        {
            _Catalog[entry.Key] = entry.Value;
        }

        transaction.Commit();
    }

    /// <inheritdoc/>
    public void Complete()
    {
        using (DuckDBTransaction transaction = _Connection.BeginTransaction())
        {
            _WriteCatalog();
            transaction.Commit();
        }

        using DuckDBCommand checkpoint = _Connection.CreateCommand();
        checkpoint.CommandText = "CHECKPOINT;";
        checkpoint.ExecuteNonQuery();
    }

    #endregion

    #region IDisposable

    /// <summary>Closes the underlying DuckDB connection. Safe to call after <see cref="Complete"/> or on an error path.</summary>
    public void Dispose()
    {
        if (_Disposed)
        {
            return;
        }
        _Disposed = true;
        _Connection.Dispose();
    }

    #endregion

    #region Private Helpers — Packets & Topology

    private void _WritePackets(ColumnarPacketBatch batch)
    {
        bool includeInfo = (_Flags & ColumnarDetailFlags.IncludeInfo) != 0;
        bool includeFrameBytes = (_Flags & ColumnarDetailFlags.IncludeFrameBytes) != 0;

        using DuckDBAppender appender = _Connection.CreateAppender("packets");
        int count = batch.PacketCount;
        for (int i = 0; i < count; i++)
        {
            IDuckDBAppenderRow row = appender.CreateRow()
                .AppendValue(batch.PacketIds[i])
                .AppendValue(batch.Timestamps[i]);
            if (includeInfo)
            {
                row = row.AppendValue(batch.Infos[i]);
            }
            if (includeFrameBytes)
            {
                row = row.AppendValue(batch.FrameBytesList[i]);
            }
            row.EndRow();
        }
    }

    private void _WriteTopology(ColumnarPacketBatch batch)
    {
        using DuckDBAppender appender = _Connection.CreateAppender("topology");
        foreach (TopologyNode node in batch.Topology)
        {
            appender.CreateRow()
                .AppendValue(node.PacketId)
                .AppendValue(node.NodeId)
                .AppendValue(node.FieldId)
                .AppendValue(node.ParentNodeId)
                .EndRow();
        }
    }

    #endregion

    #region Private Helpers — Fields

    private void _EnsureFieldTables(int fieldIdValue, FieldType fieldType)
    {
        if (!_CreatedFieldTables.Add(fieldIdValue))
        {
            return;
        }

        string tableName = FormattableString.Invariant($"field_{fieldIdValue}");
        string valueType = fieldType switch
        {
            FieldType.Bool => "BOOLEAN",
            FieldType.I64 => "BIGINT",
            FieldType.U64 => "UBIGINT",
            FieldType.F64 => "DOUBLE",
            FieldType.Timestamp => "BIGINT",
            FieldType.String => "VARCHAR",
            _ => "BLOB",
        };

        StringBuilder sql = new();
        sql.Append(CultureInfo.InvariantCulture, $"CREATE TABLE IF NOT EXISTS {tableName}(packet_id INTEGER, node_id INTEGER, value {valueType}");
        if ((_Flags & ColumnarDetailFlags.IncludeCustomRepresentation) != 0)
        {
            sql.Append(", custom_repr VARCHAR");
        }
        if ((_Flags & ColumnarDetailFlags.IncludeCustomText) != 0)
        {
            sql.Append(", custom_text VARCHAR");
        }
        sql.Append(");");

        using DuckDBCommand command = _Connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.ExecuteNonQuery();
    }

    private void _WriteFieldRows(int fieldIdValue, FieldColumnBag bag)
    {
        string tableName = FormattableString.Invariant($"field_{fieldIdValue}");
        bool includeCustomRepr = (_Flags & ColumnarDetailFlags.IncludeCustomRepresentation) != 0;
        bool includeCustomText = (_Flags & ColumnarDetailFlags.IncludeCustomText) != 0;

        using DuckDBAppender appender = _Connection.CreateAppender(tableName);
        int count = bag.RowCount;
        for (int i = 0; i < count; i++)
        {
            IDuckDBAppenderRow row = appender.CreateRow()
                .AppendValue(bag.PacketIds[i])
                .AppendValue(bag.NodeIds[i]);

            row = _AppendTypedValue(row, bag, i);

            if (includeCustomRepr)
            {
                row = _AppendNullableString(row, bag.CustomRepresentations[i]);
            }
            if (includeCustomText)
            {
                row = _AppendNullableString(row, bag.CustomTexts[i]);
            }

            row.EndRow();
        }
    }

    private static IDuckDBAppenderRow _AppendTypedValue(IDuckDBAppenderRow row, FieldColumnBag bag, int i) => bag.FieldType switch
    {
        FieldType.Bool => row.AppendValue(bag.BoolValues[i]),
        FieldType.I64 => row.AppendValue(bag.I64Values[i]),
        FieldType.U64 => row.AppendValue(bag.U64Values[i]),
        FieldType.F64 => row.AppendValue(bag.F64Values[i]),
        FieldType.Timestamp => row.AppendValue(bag.TimestampValues[i]),
        FieldType.String => _AppendNullableString(row, bag.StringValues[i]),
        _ => bag.BytesValues[i] is byte[] bytes
            ? row.AppendValue(bytes)
            : row.AppendNullValue(),
    };

    private static IDuckDBAppenderRow _AppendNullableString(IDuckDBAppenderRow row, string? value) =>
        value is null
            ? row.AppendNullValue()
            : row.AppendValue(value);

    #endregion

    #region Private Helpers — Catalog

    private void _WriteCatalog()
    {
        if (_Catalog.Count == 0)
        {
            return;
        }

        using DuckDBAppender appender = _Connection.CreateAppender("catalog");
        foreach (FieldCatalogEntry entry in _Catalog.Values)
        {
            appender.CreateRow()
                .AppendValue(entry.FieldIdValue)
                .AppendValue(entry.Name)
                .AppendValue(entry.UiName)
                .AppendValue((int)entry.FieldType)
                .AppendValue(entry.ProtocolIdValue)
                .AppendValue(entry.TableName)
                .EndRow();
        }
    }

    /// <summary>
    /// Deletes a prior DuckDB database at <paramref name="path"/> (and its WAL sidecar) so the
    /// export starts from an empty file. No-op when the path does not exist.
    /// </summary>
    private static void _DeleteExistingDatabase(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        string walPath = path + ".wal";
        if (File.Exists(walPath))
        {
            File.Delete(walPath);
        }
    }

    #endregion
}

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
///     protocol_id INTEGER, table_name VARCHAR, description VARCHAR)</c> — one row per distinct field ID observed,
///     written once in <see cref="Complete"/>. <c>description</c> is <c>NULL</c> when the field has none.</item>
///   <item><c>protocols(protocol_id INTEGER, name VARCHAR, ui_name VARCHAR)</c> — one row per protocol registered
///     in the parsing stack, written once when the writer is created.</item>
///   <item><c>field_{id}(packet_id INTEGER, node_id INTEGER, value &lt;type&gt;
///     [, custom_repr VARCHAR] [, custom_text VARCHAR])</c>
///     — one row per field occurrence. <c>value</c> is <c>VARCHAR</c> for
///     <see cref="FieldType.String"/>.</item>
/// </list>
/// </para>
/// <para>
/// <see cref="FieldType.U64"/> values are stored as DuckDB's native <c>UBIGINT</c>, matching
/// Parquet's <c>DataField&lt;ulong&gt;</c> / <c>UINT_64</c> unsigned semantics.
/// <see cref="FieldType.Timestamp"/> values (and <c>packets.timestamp_ns</c>) are plain <c>BIGINT</c> nanoseconds since
/// the Unix epoch, not a DuckDB <c>TIMESTAMP</c>; bytes and all fixed-size address types are <c>BLOB</c>.
/// </para>
/// <para>
/// <b>Write performance:</b> every <see cref="WriteBatch"/> call wraps its appends in a single
/// explicit transaction (<c>BEGIN</c>/<c>COMMIT</c>) and uses <see cref="DuckDBAppender"/> — never
/// row-at-a-time <c>INSERT</c> — for all data tables. <c>CHECKPOINT</c> is never issued mid-batch;
/// it only runs once, at the very end of <see cref="Complete"/>, so the WAL is not fsynced on
/// every flush. The appender is used because it feeds DuckDB's columnar storage directly in large chunks; a
/// parameterized <c>INSERT</c> per row would pay statement execution cost for each of the many field rows. One
/// transaction per batch also means a failed batch is rolled back as a whole, so a batch is either fully in the
/// database or not at all.
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

    // Field IDs whose field_{id} table exists in the database, so the CREATE TABLE statement runs once per field per
    // export. An ID enters this set only after the transaction that created the table committed: the table is created
    // inside the batch's transaction, and a failure later in the batch rolls the table back with it. Remembering the ID
    // earlier would make every later batch skip the CREATE and then fail on a table that does not exist.
    private readonly HashSet<int> _CreatedFieldTables = new();

    // Field IDs whose table was created by the batch being written but is not committed yet; moved to
    // _CreatedFieldTables after the commit and dropped when the batch fails.
    private readonly HashSet<int> _PendingFieldTables = new();

    // Catalog rows collected across batches and written once in Complete, so a field first seen in a late batch is
    // still included and the catalog table is written in a single pass.
    private readonly Dictionary<int, FieldCatalogEntry> _Catalog = new(64);

    private bool _Disposed;

    #endregion

    #region Constructor

    /// <summary>
    /// Opens a fresh DuckDB file at <paramref name="path"/>, applies the optional resource settings, creates the base
    /// tables, and writes the <c>protocols</c> table (the protocol set is fixed by the stack for the whole export).
    /// Any pre-existing file (and its <c>.wal</c> sidecar) at the same path is deleted first so
    /// re-exports overwrite rather than appending duplicate rows into an existing database.
    /// </summary>
    /// <param name="path">Target <c>.duckdb</c> file path.</param>
    /// <param name="flags">Detail flags the batches were configured with; controls optional columns.</param>
    /// <param name="protocols">Protocols of the parsing stack, written to the <c>protocols</c> table.</param>
    /// <param name="memoryLimitBytes">DuckDB <c>memory_limit</c> in bytes; <c>0</c> keeps DuckDB's default.</param>
    /// <param name="threads">DuckDB <c>threads</c>; <c>0</c> keeps DuckDB's default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="protocols"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="memoryLimitBytes"/> or <paramref name="threads"/> is negative.</exception>
    /// <remarks>
    /// <paramref name="path"/> is not validated here: <see cref="DuckDbExporter"/> passes the full path it normalized in
    /// its builder. Security: the constructor deletes the file at <paramref name="path"/> and its <c>.wal</c> sidecar
    /// (threat: data loss in a caller-chosen location; mitigation: only these two exact paths are deleted, never a
    /// directory or pattern; rejected alternative: appending into an existing database, which duplicates rows;
    /// residual risk: an existing, unrelated file at the target path is lost, the same as any overwrite on export).
    /// The path is passed through <see cref="DuckDBConnectionStringBuilder"/>, so characters such as <c>;</c> and quotes
    /// stay part of the file name and cannot add connection options.
    /// </remarks>
    internal DuckDbBulkWriter(
        string path,
        ColumnarDetailFlags flags,
        IReadOnlyList<ProtocolCatalogEntry> protocols,
        long memoryLimitBytes,
        int threads)
    {
        // Validate both arguments before any file is touched: building the settings SQL checks the numeric limits,
        // so an invalid limit cannot delete the previous database and then fail.
        ArgumentNullException.ThrowIfNull(protocols);
        string settingsSql = BuildSettingsSql(memoryLimitBytes, threads);

        _Flags = flags;
        _DeleteExistingDatabase(path);

        // Security: the path is data, not connection-string syntax. Interpolating it into "Data Source={path}" would let a
        // ';' end the data source and turn the rest of the path into connection options (for example access_mode=...),
        // and a quote would break the parsing. The builder quotes and escapes the value, so any path stays one value.
        // Rejected alternative: refusing paths that contain ';' or quotes, which would turn valid file names into errors.
        // No residual risk beyond the caller choosing which file is overwritten (see the remarks).
        DuckDBConnectionStringBuilder connectionString = new() { DataSource = path };
        _Connection = new DuckDBConnection(connectionString.ConnectionString);
        _Connection.Open();

        // The caller never receives this instance when the constructor throws, so close the connection here
        // instead of leaking the open database file.
        try
        {
            _InitializeDatabase(settingsSql, protocols);
        }
        catch
        {
            _Connection.Dispose();
            throw;
        }
    }

    /// <summary>Applies the resource settings, creates the base tables, and writes the <c>protocols</c> table.</summary>
    /// <param name="settingsSql">Output of <see cref="BuildSettingsSql"/>; empty when DuckDB's defaults apply.</param>
    /// <param name="protocols">Protocols of the parsing stack.</param>
    private void _InitializeDatabase(string settingsSql, IReadOnlyList<ProtocolCatalogEntry> protocols)
    {
        // Resource settings are the caller's decision; DuckDB's own defaults apply when the builder left them at 0.
        if (settingsSql.Length > 0)
        {
            using DuckDBCommand tuning = _Connection.CreateCommand();
            tuning.CommandText = settingsSql;
            tuning.ExecuteNonQuery();
        }

        using (DuckDBCommand ddl = _Connection.CreateCommand())
        {
            // New catalog columns are appended only, so positional readers of older exports keep working.
            ddl.CommandText =
                "CREATE TABLE IF NOT EXISTS packets(packet_id INTEGER, timestamp_ns BIGINT"
                + ((_Flags & ColumnarDetailFlags.IncludeInfo) != 0 ? ", info VARCHAR" : string.Empty)
                + ((_Flags & ColumnarDetailFlags.IncludeFrameBytes) != 0 ? ", frame_bytes BLOB" : string.Empty)
                + ");"
                + "CREATE TABLE IF NOT EXISTS topology(packet_id INTEGER, node_id INTEGER, field_id INTEGER, parent_node_id INTEGER);"
                + "CREATE TABLE IF NOT EXISTS catalog(field_id INTEGER, name VARCHAR, ui_name VARCHAR, field_type INTEGER, "
                + "protocol_id INTEGER, table_name VARCHAR, description VARCHAR);"
                + "CREATE TABLE IF NOT EXISTS protocols(protocol_id INTEGER, name VARCHAR, ui_name VARCHAR);";
            ddl.ExecuteNonQuery();
        }

        _WriteProtocols(protocols);
    }

    #endregion

    #region Settings

    /// <summary>
    /// Builds the <c>SET</c> statements for the optional DuckDB resource settings. A value of <c>0</c> emits nothing for
    /// that setting so DuckDB's own default applies; both <c>0</c> yields an empty string.
    /// </summary>
    /// <param name="memoryLimitBytes">DuckDB <c>memory_limit</c> in bytes, or <c>0</c> for DuckDB's default.</param>
    /// <param name="threads">DuckDB <c>threads</c>, or <c>0</c> for DuckDB's default.</param>
    /// <returns>The SQL to execute, or <see cref="string.Empty"/> when no setting is requested.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is negative.</exception>
    /// <remarks>
    /// Limits: <c>memory_limit</c> 1 to <see cref="long.MaxValue"/> bytes and <c>threads</c> 1 to
    /// <see cref="int.MaxValue"/>; <c>0</c> means "not set". Threat: DuckDB's default may use most of the host's memory
    /// and cores. Configurable by the library caller through the exporter builder, not by an end user. Posture: open by
    /// default (DuckDB's own defaults apply until the caller tightens them).
    /// </remarks>
    internal static string BuildSettingsSql(long memoryLimitBytes, int threads)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(memoryLimitBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(threads);

        // Both values are validated non-negative integers formatted with the invariant culture, never user strings,
        // so interpolating them into SQL is injection-safe.
        StringBuilder sql = new();
        if (memoryLimitBytes > 0)
        {
            sql.Append(CultureInfo.InvariantCulture, $"SET memory_limit = '{memoryLimitBytes}B';");
        }
        if (threads > 0)
        {
            sql.Append(CultureInfo.InvariantCulture, $"SET threads = {threads};");
        }
        return sql.ToString();
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

        // One transaction for the whole batch (see the type remarks): all tables of the batch commit together, and if
        // anything throws the transaction is disposed without Commit, which rolls the batch back.
        try
        {
            using DuckDBTransaction transaction = _Connection.BeginTransaction();

            _WritePackets(batch);

            if ((_Flags & ColumnarDetailFlags.IncludeTopology) != 0 && batch.Topology.Count > 0)
            {
                _WriteTopology(batch);
            }

            // The batch keeps its field bags across Reset, so bags without rows in this batch are skipped (no empty
            // appender, and no table is created for a field that has not produced a row yet).
            foreach (KeyValuePair<int, FieldColumnBag> entry in batch.FieldBags)
            {
                if (entry.Value.RowCount == 0)
                {
                    continue;
                }

                _EnsureFieldTables(entry.Key, entry.Value.FieldType);
                _WriteFieldRows(entry.Key, entry.Value);
            }

            transaction.Commit();

            // Only now do the tables created by this batch exist for good.
            _CreatedFieldTables.UnionWith(_PendingFieldTables);

            // Keep the catalog rows for Complete; overwriting by id is harmless because the batch never changes an entry
            // once created. Done after the commit so a rolled-back batch leaves no catalog row for a table it never created.
            foreach (KeyValuePair<int, FieldCatalogEntry> entry in batch.Catalog)
            {
                _Catalog[entry.Key] = entry.Value;
            }
        }
        finally
        {
            // After a failure the tables created by the rolled-back batch are gone, so they are forgotten again.
            _PendingFieldTables.Clear();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Writes <c>catalog</c> in its own transaction, then runs the only <c>CHECKPOINT</c> of the export, which folds the
    /// write-ahead log into the database file so the result is a single self-contained <c>.duckdb</c> file. Without
    /// <see cref="Complete"/> (only <see cref="Dispose"/>) the catalog table stays empty.
    /// </remarks>
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

    /// <summary>
    /// Appends one row per packet to <c>packets</c>. Optional columns are appended in the same order as in the
    /// table definition (info, then frame bytes); an appender row must supply every column of the table in order.
    /// </summary>
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

    /// <summary>Appends one row per field-tree node to <c>topology</c> (<c>parent_node_id</c> is -1 for top-level fields).</summary>
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

    /// <summary>
    /// Creates the <c>field_{id}</c> table on the first batch that has rows for the field, with a value column type
    /// chosen from the field type (mirrors the Parquet mapping: unsigned 64-bit as <c>UBIGINT</c>, timestamps as
    /// <c>BIGINT</c> nanoseconds, everything else that is not numeric as <c>BLOB</c>).
    /// </summary>
    private void _EnsureFieldTables(int fieldIdValue, FieldType fieldType)
    {
        // Known from an earlier committed batch, or already created by the batch being written (a field can appear once
        // per batch, but the check is cheap and keeps the method safe to call twice).
        if (_CreatedFieldTables.Contains(fieldIdValue) || !_PendingFieldTables.Add(fieldIdValue))
        {
            return;
        }

        // Security: the table name is built from an int through the invariant culture and the column types from a
        // fixed switch, so no caller-supplied text reaches the SQL string (injection-safe by construction). Identifiers
        // cannot be passed as SQL parameters, which is why string building is used at all.
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

    /// <summary>
    /// Appends the bag's rows to its field table. Column order must match the table definition: packet id, node id,
    /// value, then the optional custom representation and custom text.
    /// </summary>
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

    /// <summary>
    /// Appends the value of row <paramref name="i"/> from the bag list that matches the field type. The default case
    /// covers bytes and the fixed-size address types: reading <see cref="FieldColumnBag.BytesValues"/> materializes the
    /// deferred address payloads into <c>byte[]</c> on first access (once per flush), and a missing value is written
    /// as SQL NULL.
    /// </summary>
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

    /// <summary>Appends a string, or SQL NULL for <see langword="null"/> (unlike the PBF output, nulls are preserved).</summary>
    private static IDuckDBAppenderRow _AppendNullableString(IDuckDBAppenderRow row, string? value) =>
        value is null
            ? row.AppendNullValue()
            : row.AppendValue(value);

    #endregion

    #region Private Helpers — Catalog

    /// <summary>Appends one <c>catalog</c> row per distinct field ID observed; skipped when no field was observed.</summary>
    private void _WriteCatalog()
    {
        if (_Catalog.Count == 0)
        {
            return;
        }

        using DuckDBAppender appender = _Connection.CreateAppender("catalog");
        foreach (FieldCatalogEntry entry in _Catalog.Values)
        {
            IDuckDBAppenderRow row = appender.CreateRow()
                .AppendValue(entry.FieldIdValue)
                .AppendValue(entry.Name)
                .AppendValue(entry.UiName)
                .AppendValue((int)entry.FieldType)
                .AppendValue(entry.ProtocolIdValue)
                .AppendValue(entry.TableName);
            _AppendNullableString(row, entry.Description).EndRow();
        }
    }

    /// <summary>
    /// Appends one <c>protocols</c> row per registered protocol in its own transaction. Runs once at construction.
    /// </summary>
    /// <param name="protocols">Protocols of the parsing stack.</param>
    private void _WriteProtocols(IReadOnlyList<ProtocolCatalogEntry> protocols)
    {
        if (protocols.Count == 0)
        {
            return;
        }

        using DuckDBTransaction transaction = _Connection.BeginTransaction();
        using (DuckDBAppender appender = _Connection.CreateAppender("protocols"))
        {
            foreach (ProtocolCatalogEntry entry in protocols)
            {
                appender.CreateRow()
                    .AppendValue(entry.ProtocolIdValue)
                    .AppendValue(entry.Name)
                    .AppendValue(entry.UiName)
                    .EndRow();
            }
        }
        transaction.Commit();
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

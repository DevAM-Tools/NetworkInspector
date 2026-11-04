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
///   <item><c>packet_buffers(packet_id INTEGER, buffer_index UTINYINT, bytes BLOB)</c> — only with
///     <see cref="ExportContent.Buffers"/>: one row per additional packet buffer (index 1 to 255: reassembled datagrams, TCP PDUs,
///     decoded bodies), ascending by index within a packet; a packet without additional buffers has no row. A bound empty buffer is
///     an empty BLOB, never NULL. Sync: README § Table Layout.</item>
///   <item><c>topology(packet_id INTEGER, node_id INTEGER, field_id INTEGER, parent_node_id INTEGER
///     [, custom_text VARCHAR])</c> — one row per field-tree node, only in the <see cref="ExportShape.Hierarchy"/> shape
///     (the flat shapes have no node ids to relate and no topology table). <c>custom_text</c> (only with
///     <see cref="ExportContent.CustomText"/>) holds the custom text of a node without a value (a protocol layer, a group) and
///     is <c>NULL</c> for value nodes, whose text is in their field table, and for a node whose dissector set no text.</item>
///   <item><c>catalog(field_id INTEGER, name VARCHAR, ui_name VARCHAR, field_type INTEGER,
///     protocol_id INTEGER, table_name VARCHAR, description VARCHAR)</c> — one row per distinct field ID observed,
///     written once in <see cref="Complete"/>. <c>table_name</c> is <c>NULL</c> for a structure node (protocol, group)
///     that only appears in the topology. <c>description</c> is <c>NULL</c> when the field has none.</item>
///   <item><c>protocols(protocol_id INTEGER, name VARCHAR, ui_name VARCHAR)</c> — one row per protocol registered
///     in the parsing stack, written once when the writer is created.</item>
///   <item><c>export_info(key VARCHAR, value VARCHAR)</c> — the shape marker a reader checks first (<c>shape</c>, <c>fields</c>,
///     <c>occurrences</c>, <c>layout_version</c>; see <see cref="ExportInfo"/>), written once when the writer is created.</item>
///   <item><c>field_{id}(packet_id INTEGER, node_id|occurrence INTEGER, value &lt;type&gt;
///     [, custom_repr VARCHAR] [, custom_text VARCHAR])</c>
///     — one row per field occurrence; the second column is <c>node_id</c> in the hierarchy shape and <c>occurrence</c> (0 for the
///     first value of the field in the packet) in the flat shapes. <c>value</c> is <c>VARCHAR</c> for
///     <see cref="FieldType.String"/>. <c>custom_repr</c> and <c>custom_text</c> are <c>NULL</c> where the dissector set none and
///     <c>''</c> where it set an empty text.</item>
/// </list>
/// </para>
/// <para>
/// <see cref="FieldType.U64"/> values are stored as DuckDB's native <c>UBIGINT</c>, matching
/// Parquet's <c>DataField&lt;ulong&gt;</c> / <c>UINT_64</c> unsigned semantics.
/// <see cref="FieldType.Timestamp"/> values (and <c>packets.timestamp_ns</c>) are plain <c>BIGINT</c> nanoseconds since
/// the Unix epoch, not a DuckDB <c>TIMESTAMP</c>; bytes are <c>BLOB</c>.
/// </para>
/// <para>
/// <b>Addresses are integers</b> whose numeric order is the address order, so a subnet is a range query: IPv4 is <c>UINTEGER</c>
/// (<see cref="IPv4Address.RawValue"/>), MAC and EUI-64 are <c>UBIGINT</c> (the MAC address uses the low 48 bits), IPv6 is
/// <c>UHUGEINT</c> (<c>high &lt;&lt; 64 | low</c>), and a UUID is DuckDB's native <c>UUID</c>. Sync: README § Table Layout and
/// EXPORTER_GUIDE.md § Rebuilding the display line.
/// </para>
/// <para>
/// <b>IPv6 staging:</b> DuckDB.NET 1.5.6.1 appends a 128-bit integer only as a <see cref="System.Numerics.BigInteger"/>, which allocates
/// per value. So the rows of an IPv6 field go through a per-table <c>TEMP</c> table <c>stage_field_{id}</c> with two
/// <c>UBIGINT</c> halves, and one set-based <c>INSERT … SELECT</c> per batch combines the halves into the <c>UHUGEINT</c> column of
/// <c>field_{id}</c>. A staging table created by a batch is created and emptied inside that batch's transaction, so a failed
/// batch rolls back both; the tables of the selected fields are created up front, committed at once, and empty (the invariant
/// "empty between batches" holds from the start). Rejected: the <see cref="System.Numerics.BigInteger"/> append (a heap allocation per
/// value on the flush path), two permanent columns (breaks the one <c>value</c> column of every field table), and a BLOB (not an
/// integer). Decision record:
/// <c>docs/decisions/export-integer-addresses.md</c>.
/// </para>
/// <para>
/// <b>Write performance:</b> every <see cref="WriteBatch"/> call wraps its appends in a single
/// explicit transaction (<c>BEGIN</c>/<c>COMMIT</c>) and uses <see cref="DuckDBAppender"/> — never
/// row-at-a-time <c>INSERT</c> — for all data tables. Each <c>COMMIT</c> makes the batch durable in the write-ahead log,
/// and DuckDB checkpoints on its own when the log exceeds <c>checkpoint_threshold</c>. The writer issues one explicit
/// <c>CHECKPOINT</c> in <see cref="Complete"/> so the finished export is a single self-contained file without a <c>.wal</c>
/// sidecar. The appender is used because it feeds DuckDB's columnar storage directly in large chunks; a
/// parameterized <c>INSERT</c> per row would pay statement execution cost for each of the many field rows. One
/// transaction per batch also means a failed batch is rolled back as a whole, so a batch is either fully in the
/// database or not at all.
/// </para>
/// <para>
/// <b>Storage version:</b> the file format is chosen by the constructor argument <c>storageVersionSetting</c> (DuckDB's
/// <c>storage_compatibility_version</c>) and the export runs no compaction or copy pass afterwards: <see cref="Complete"/> writes
/// the catalog and ends with the checkpoint. Sync: README § Storage Version.
/// </para>
/// <para>
/// <b>Flush loops:</b> a flush reads the batch through spans and appends byte payloads as spans and addresses as plain integers,
/// so it allocates nothing per row; the type switch runs once per field table, not per row.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="WriteBatch"/> and <see cref="Complete"/>
/// must be called sequentially; the calls may come from different threads (the exporter's writer thread writes batches and
/// the finishing thread completes after joining it) as long as each call happens after the previous one finished.
/// </para>
/// </summary>
internal sealed class DuckDbBulkWriter : IColumnarBatchSink, IDisposable
{
    #region Database setup

    /// <summary>The connection to the database file; owned by this writer and closed by <see cref="Dispose"/>.</summary>
    private readonly DuckDBConnection _Connection;

    /// <summary>What the database contains; decides the shape-dependent tables and the optional columns.</summary>
    private readonly ExportContent _Content;

    /// <summary>Name of the second key column of the field tables: <c>node_id</c> (hierarchy shape) or <c>occurrence</c> (flat shapes).</summary>
    private readonly string _PositionColumn;

    // The optional-column flags are tested once per row in the flush loops, so they are read from the flags only once.

    /// <summary>Whether the field tables carry the <c>custom_repr</c> column.</summary>
    private readonly bool _IncludeCustomRepresentation;

    /// <summary>Whether the field tables and the topology carry the <c>custom_text</c> column.</summary>
    private readonly bool _IncludeCustomText;

    // Field IDs whose field_{id} table exists in the database, so the CREATE TABLE statement runs once per field per
    // export. An ID enters this set only after the transaction that created the table committed: the table is created
    // inside the batch's transaction, and a failure later in the batch rolls the table back with it. Remembering the ID
    // earlier would make every later batch skip the CREATE and then fail on a table that does not exist.

    /// <summary>Field ids whose table was created by a committed transaction.</summary>
    private readonly HashSet<int> _CreatedFieldTables = new();

    // Field IDs whose table was created by the batch being written but is not committed yet; moved to
    // _CreatedFieldTables after the commit and dropped when the batch fails.

    /// <summary>Field ids whose table the batch being written created and that is not committed yet.</summary>
    private readonly HashSet<int> _PendingFieldTables = new();

    // Catalog rows collected across batches and written once in Complete, so a field first seen in a late batch is
    // still included and the catalog table is written in a single pass.

    /// <summary>The catalog rows of all committed batches, by field id; written by <see cref="Complete"/>.</summary>
    private readonly Dictionary<int, FieldCatalogEntry> _Catalog = new(64);

    /// <summary>Set by the first <see cref="Dispose"/>; makes a second call a no-op.</summary>
    private bool _Disposed;

    /// <summary>
    /// Opens a fresh DuckDB file at <paramref name="path"/>, applies the optional resource settings, creates the base
    /// tables, and writes the <c>protocols</c> table (the protocol set is fixed by the stack for the whole export).
    /// Any pre-existing file (and its <c>.wal</c> sidecar) at the same path is deleted first so
    /// re-exports overwrite rather than appending duplicate rows into an existing database.
    /// </summary>
    /// <param name="path">Target <c>.duckdb</c> file path.</param>
    /// <param name="content">Content the batches were configured with; controls the shape and the optional columns.</param>
    /// <param name="protocols">Protocols of the parsing stack, written to the <c>protocols</c> table.</param>
    /// <param name="memoryLimitBytes">DuckDB <c>memory_limit</c> in bytes; <c>0</c> keeps DuckDB's default.</param>
    /// <param name="threads">DuckDB <c>threads</c>; <c>0</c> keeps DuckDB's default.</param>
    /// <param name="storageVersionSetting">
    /// The value of DuckDB's <c>storage_compatibility_version</c> (<c>v1.0.0</c> to <c>v1.5.0</c>), set on the connection before the
    /// file is created. Only non-emptiness is checked here: the builder maps and validates the value
    /// (<see cref="DuckDbStorageVersions.TryGetSettingValue"/>), and a text DuckDB does not know makes the open fail with DuckDB's own
    /// message instead of writing a file in another format.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="protocols"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="storageVersionSetting"/> is <see langword="null"/> or empty.</exception>
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
        ExportContent content,
        IReadOnlyList<ProtocolCatalogEntry> protocols,
        long memoryLimitBytes,
        int threads,
        string storageVersionSetting)
    {
        // Validate the arguments before any file is touched: building the settings SQL checks the numeric limits,
        // so an invalid limit cannot delete the previous database and then fail.
        ArgumentNullException.ThrowIfNull(protocols);
        ArgumentException.ThrowIfNullOrEmpty(storageVersionSetting);
        string settingsSql = BuildSettingsSql(memoryLimitBytes, threads);

        ArgumentNullException.ThrowIfNull(content);
        _Content = content;
        _PositionColumn = content.Shape == ExportShape.Hierarchy
            ? "node_id"
            : "occurrence";
        _IncludeCustomRepresentation = content.CustomRepresentation;
        _IncludeCustomText = content.CustomText;
        _DeleteExistingDatabase(path);

        // Security: the path is data, not connection-string syntax. Interpolating it into "Data Source={path}" would let a
        // ';' end the data source and turn the rest of the path into connection options (for example access_mode=...),
        // and a quote would break the parsing. The builder quotes and escapes the value, so any path stays one value.
        // Rejected alternative: refusing paths that contain ';' or quotes, which would turn valid file names into errors.
        // No residual risk beyond the caller choosing which file is overwritten (see the remarks).
        DuckDBConnectionStringBuilder connectionString = new() { DataSource = path };

        // The storage version is set explicitly so the file format never follows a library default that changes with a DuckDB.NET
        // upgrade; it decides which DuckDB releases can open the file, and DuckDB fixes it when the database file is created, so
        // it is a connection setting and cannot be applied later with SET. The value is the builder's validated mapping and goes in
        // through the builder's indexer, so it is quoted like the path and cannot add options.
        connectionString["storage_compatibility_version"] = storageVersionSetting;
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

    /// <summary>
    /// Deletes a prior DuckDB database at <paramref name="path"/> (and its WAL sidecar) so the
    /// export starts from an empty file. No-op when the path does not exist.
    /// </summary>
    /// <param name="path">The database file path; the sidecar is <c>path + ".wal"</c>.</param>
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

        // Only the hierarchy shape has a topology: the flat shapes carry occurrence numbers instead of node ids, so there is no
        // tree to relate. A missing topology table is part of the shape contract (export_info says which shape the file has).
        string topologySql = string.Empty;
        if (_Content.Shape == ExportShape.Hierarchy)
        {
            topologySql =
                "CREATE TABLE IF NOT EXISTS topology(packet_id INTEGER, node_id INTEGER, field_id INTEGER, parent_node_id INTEGER"
                + (_IncludeCustomText ? ", custom_text VARCHAR" : string.Empty)
                + ");";
        }

        // The buffer table exists exactly when the content asks for buffers (also with no row, so a reader sees "none were
        // present" instead of a missing table), and it is a table of its own: a buffer row belongs to a packet, not to a field, so
        // it never touches the field tables (the field tree leads).
        string bufferSql = string.Empty;
        if (_Content.Buffers)
        {
            bufferSql = "CREATE TABLE IF NOT EXISTS packet_buffers(packet_id INTEGER, buffer_index UTINYINT, bytes BLOB);";
        }

        using (DuckDBCommand ddl = _Connection.CreateCommand())
        {
            // New catalog columns are appended only, so positional readers of older exports keep working.
            ddl.CommandText =
                "CREATE TABLE IF NOT EXISTS packets(packet_id INTEGER, timestamp_ns BIGINT"
                + (_Content.Info ? ", info VARCHAR" : string.Empty)
                + (_Content.FrameBytes ? ", frame_bytes BLOB" : string.Empty)
                + ");"
                + bufferSql
                + topologySql
                + "CREATE TABLE IF NOT EXISTS catalog(field_id INTEGER, name VARCHAR, ui_name VARCHAR, field_type INTEGER, "
                + "protocol_id INTEGER, table_name VARCHAR, description VARCHAR);"
                + "CREATE TABLE IF NOT EXISTS protocols(protocol_id INTEGER, name VARCHAR, ui_name VARCHAR);"
                + "CREATE TABLE IF NOT EXISTS export_info(key VARCHAR, value VARCHAR);";
            ddl.ExecuteNonQuery();
        }

        _WriteProtocols(protocols);
        _WriteExportInfo();
        _CreateSelectedFieldTables();
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
    /// Writes the rows of <c>export_info</c> (see <see cref="ExportInfo"/>) in one transaction: the shape marker is complete
    /// before the first packet, so even an export without packets says what it would have contained.
    /// </summary>
    private void _WriteExportInfo()
    {
        IReadOnlyList<KeyValuePair<string, string>> rows = ExportInfo.CreateRows(_Content);
        using DuckDBTransaction transaction = _Connection.BeginTransaction();
        using (DuckDBAppender appender = _Connection.CreateAppender("export_info"))
        {
            foreach (KeyValuePair<string, string> row in rows)
            {
                appender.CreateRow().AppendValue(row.Key).AppendValue(row.Value).EndRow();
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// Creates the field table of every selected field up front, with the declared type, so an empty table means "the field never
    /// occurred" instead of "the table is missing" (a reader can tell a misspelled name from an absent field).
    /// </summary>
    private void _CreateSelectedFieldTables()
    {
        if (_Content.Fields is null)
        {
            return;
        }

        foreach (SelectedField field in _Content.Fields.Fields)
        {
            _EnsureFieldTables(field.Id.Value, field.FieldType);
        }

        // DDL outside a batch transaction is committed at once, so the tables are created for good.
        _CreatedFieldTables.UnionWith(_PendingFieldTables);
        _PendingFieldTables.Clear();
    }

    #endregion

    #region Resource settings

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

    #region Batch transaction

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

            // After the packets, inside the same transaction: a failed batch rolls the buffer rows back with its packet rows.
            if (_Content.Buffers && batch.BufferRowCount > 0)
            {
                _WriteBuffers(batch);
            }

            if (_Content.Shape == ExportShape.Hierarchy && batch.Topology.Count > 0)
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

            // Keep the catalog rows for Complete. A value row (with a table) wins over a structure row of the same id: two
            // batches (the exporter alternates between two) can see the same id once as a node without a value and once
            // with one, and the table must stay listed. Done after the commit so a rolled-back batch leaves no catalog row
            // for a table it never created. Sync: ParquetBatchSink.WriteBatch applies the same rule.
            foreach (KeyValuePair<int, FieldCatalogEntry> entry in batch.Catalog)
            {
                if (!_Catalog.TryGetValue(entry.Key, out FieldCatalogEntry? existing) || existing.TableName is null)
                {
                    _Catalog[entry.Key] = entry.Value;
                }
            }
        }
        finally
        {
            // After a failure the tables created by the rolled-back batch are gone, so they are forgotten again.
            _PendingFieldTables.Clear();
        }
    }

    #endregion

    #region Packet and topology rows

    /// <summary>
    /// Appends one row per packet to <c>packets</c>. Optional columns are appended in the same order as in the
    /// table definition (info, then frame bytes); an appender row must supply every column of the table in order.
    /// </summary>
    /// <param name="batch">The batch being written; its packet columns are read through spans.</param>
    private void _WritePackets(ColumnarPacketBatch batch)
    {
        bool includeInfo = _Content.Info;
        bool includeFrameBytes = _Content.FrameBytes;

        // Spans are taken here, inside WriteBatch: the owning thread does not call AddPacket while a batch is written, so
        // no list grows and moves its array while a span is alive. A span indexer is a direct load; the list interface
        // behind PacketIds/Timestamps/Infos would cost an interface call per cell.
        ReadOnlySpan<int> packetIds = batch.PacketIdSpan;
        ReadOnlySpan<long> timestamps = batch.TimestampSpan;
        ReadOnlySpan<string> infos = batch.InfoSpan;

        using DuckDBAppender appender = _Connection.CreateAppender("packets");
        for (int i = 0; i < packetIds.Length; i++)
        {
            IDuckDBAppenderRow row = appender.CreateRow()
                .AppendValue(packetIds[i])
                .AppendValue(timestamps[i]);
            if (includeInfo)
            {
                row = row.AppendValue(infos[i]);
            }
            if (includeFrameBytes)
            {
                // The appender takes the slab bytes as a span, so no byte[] per frame is created.
                row = _AppendBlob(row, batch.GetFrameBytes(i));
            }
            row.EndRow();
        }
    }

    /// <summary>
    /// Appends one row per additional packet buffer to <c>packet_buffers</c> (packet id, buffer index, bytes), in the order the batch
    /// holds them: packet order, then ascending buffer index.
    /// </summary>
    /// <param name="batch">The batch being written; its buffer rows are read through spans.</param>
    /// <remarks>
    /// Perf: the bytes come from the batch arena as a span, so no <c>byte[]</c> is created per buffer, and the loop reads the ids and
    /// indexes through spans like <see cref="_WritePackets"/>.
    /// </remarks>
    private void _WriteBuffers(ColumnarPacketBatch batch)
    {
        // Taken inside WriteBatch, where no list of the batch grows (see _WritePackets).
        ReadOnlySpan<int> packetIds = batch.BufferPacketIdSpan;
        ReadOnlySpan<byte> indexes = batch.BufferIndexSpan;

        using DuckDBAppender appender = _Connection.CreateAppender("packet_buffers");
        for (int i = 0; i < packetIds.Length; i++)
        {
            // _AppendBlob writes an empty buffer as an empty BLOB: a bound empty buffer must stay distinguishable from NULL.
            IDuckDBAppenderRow row = appender.CreateRow()
                .AppendValue(packetIds[i])
                .AppendValue(indexes[i]);
            _AppendBlob(row, batch.GetBufferBytes(i)).EndRow();
        }
    }

    /// <summary>
    /// Appends one row per field-tree node to <c>topology</c> (<c>parent_node_id</c> is -1 for top-level fields). The
    /// optional last column <c>custom_text</c> follows the table definition.
    /// </summary>
    /// <param name="batch">The batch being written; its topology nodes are read through a span.</param>
    private void _WriteTopology(ColumnarPacketBatch batch)
    {
        // Taken inside WriteBatch, where no list of the batch grows (see _WritePackets).
        ReadOnlySpan<TopologyNode> topology = batch.TopologySpan;

        using DuckDBAppender appender = _Connection.CreateAppender("topology");
        foreach (ref readonly TopologyNode node in topology)
        {
            IDuckDBAppenderRow row = appender.CreateRow()
                .AppendValue(node.PacketId)
                .AppendValue(node.NodeId)
                .AppendValue(node.FieldId)
                .AppendValue(node.ParentNodeId);
            if (_IncludeCustomText)
            {
                row = _AppendNullableString(row, node.CustomText);
            }
            row.EndRow();
        }
    }

    #endregion

    #region Field tables and rows

    /// <summary>
    /// Creates the <c>field_{id}</c> table on the first batch that has rows for the field, with a value column type
    /// chosen from the field type (mirrors the Parquet mapping: unsigned 64-bit as <c>UBIGINT</c>, timestamps as
    /// <c>BIGINT</c> nanoseconds, the addresses as unsigned integers, bytes as <c>BLOB</c>). For an IPv6 field it also creates the
    /// <c>TEMP</c> staging table <c>stage_field_{id}</c> (see the type remarks).
    /// </summary>
    /// <param name="fieldIdValue">The field id; names the table <c>field_{id}</c>.</param>
    /// <param name="fieldType">The declared field type; chooses the DuckDB type of the <c>value</c> column.</param>
    /// <remarks>
    /// The staging table is created in the same statement batch as the field table. When a batch creates them, both are created
    /// inside the batch transaction and rolled back together, and <see cref="_PendingFieldTables"/> covers both. The tables of the
    /// selected fields are created up front, outside any transaction, so they are committed at once (and empty). A TEMP table lives
    /// only in this connection and is never stored in the file.
    /// </remarks>
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

        // Sync: README § Table Layout (types) and EXPORTER_GUIDE.md § Rebuilding the display line (integer address forms).
        // The unsigned integer types keep the numeric order equal to the address order (a signed type would sort the upper half of
        // the address space before the lower half), and a UUID is DuckDB's native type, which a reader casts to text with ::VARCHAR.
        string valueType = fieldType switch
        {
            FieldType.Bool => "BOOLEAN",
            FieldType.I64 => "BIGINT",
            FieldType.U64 => "UBIGINT",
            FieldType.F64 => "DOUBLE",
            FieldType.Timestamp => "BIGINT",
            FieldType.String => "VARCHAR",
            FieldType.IPv4Address => "UINTEGER",
            FieldType.MacAddress => "UBIGINT",
            FieldType.Eui64 => "UBIGINT",
            FieldType.IPv6Address => "UHUGEINT",
            FieldType.Uuid => "UUID",
            _ => "BLOB",
        };

        // The optional text columns, written once so the field table and its staging table cannot differ.
        string textColumns = string.Empty;
        if (_IncludeCustomRepresentation)
        {
            textColumns += ", custom_repr VARCHAR";
        }
        if (_IncludeCustomText)
        {
            textColumns += ", custom_text VARCHAR";
        }

        StringBuilder sql = new();
        sql.Append(CultureInfo.InvariantCulture, $"CREATE TABLE IF NOT EXISTS {tableName}(packet_id INTEGER, {_PositionColumn} INTEGER, ");
        sql.Append(CultureInfo.InvariantCulture, $"value {valueType}{textColumns});");

        // Perf: DuckDB.NET 1.5.6.1 appends 128-bit integers only as BigInteger, which allocates per value; two UBIGINT halves and
        // one set-based INSERT per batch (_MoveStagedRows) keep the per-value path allocation-free. The TEMP table lives in memory
        // and is emptied in the same transaction, so a failed batch rolls back both.
        if (fieldType == FieldType.IPv6Address)
        {
            sql.Append(CultureInfo.InvariantCulture, $"CREATE TEMP TABLE IF NOT EXISTS stage_{tableName}(packet_id INTEGER, ");
            sql.Append(CultureInfo.InvariantCulture, $"{_PositionColumn} INTEGER, value_hi UBIGINT, value_lo UBIGINT{textColumns});");
        }

        using DuckDBCommand command = _Connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Appends the bag's rows to its field table. Column order must match the table definition: packet id, node id,
    /// value, then the optional custom representation and custom text.
    /// </summary>
    /// <param name="fieldIdValue">The field id; names the table <c>field_{id}</c>.</param>
    /// <param name="bag">The batch's rows of the field; its value type picks the append loop.</param>
    /// <exception cref="InvalidOperationException">An address row does not hold the bag's type (a broken invariant).</exception>
    /// <remarks>
    /// The rows of an IPv6 field are appended to its staging table and moved into the field table before the method returns
    /// (see <see cref="_MoveStagedRows"/>); every other type is appended to the field table directly. The caller's transaction
    /// covers both steps.
    /// </remarks>
    private void _WriteFieldRows(int fieldIdValue, FieldColumnBag bag)
    {
        string tableName = FormattableString.Invariant($"field_{fieldIdValue}");
        bool staged = bag.FieldType == FieldType.IPv6Address;
        string appendTable = tableName;
        if (staged)
        {
            appendTable = "stage_" + tableName;
        }

        // The appender is disposed (flushed) before the staging rows are moved: an INSERT ... SELECT must see every appended row.
        using (DuckDBAppender appender = _Connection.CreateAppender(appendTable))
        {
            // The type switch runs once per field table; each case then runs a loop over spans with concrete work per row, so
            // the flush pays neither an interface call per cell nor a switch per row.
            switch (bag.FieldType)
            {
                case FieldType.Bool:
                    _AppendRows(appender, bag, bag.BoolSpan);
                    break;
                case FieldType.I64:
                    _AppendRows(appender, bag, bag.I64Span);
                    break;
                case FieldType.U64:
                    _AppendRows(appender, bag, bag.U64Span);
                    break;
                case FieldType.F64:
                    _AppendRows(appender, bag, bag.F64Span);
                    break;
                case FieldType.Timestamp:
                    _AppendRows(appender, bag, bag.TimestampSpan);
                    break;
                case FieldType.String:
                    _AppendRows(appender, bag, bag.StringSpan);
                    break;
                case FieldType.IPv4Address:
                    _AppendIPv4Rows(appender, bag);
                    break;
                case FieldType.MacAddress:
                    _AppendMacRows(appender, bag);
                    break;
                case FieldType.Eui64:
                    _AppendEui64Rows(appender, bag);
                    break;
                case FieldType.IPv6Address:
                    _AppendIPv6HalfRows(appender, bag);
                    break;
                case FieldType.Uuid:
                    _AppendUuidRows(appender, bag);
                    break;
                default:
                    // Bytes: BLOB.
                    _AppendBlobRows(appender, bag);
                    break;
            }
        }

        if (staged)
        {
            _MoveStagedRows(tableName);
        }
    }

    /// <summary>
    /// Moves the rows of one batch from the staging table <c>stage_field_{id}</c> into <c>field_{id}</c>, combining the two
    /// <c>UBIGINT</c> halves into the <c>UHUGEINT</c> value, and empties the staging table.
    /// </summary>
    /// <param name="tableName">The field table name, <c>field_{id}</c>; the staging table is <c>stage_</c> plus this name.</param>
    /// <remarks>
    /// Runs inside the batch transaction, after the appender of the staging table was disposed. Invariant: the staging table is empty
    /// between batches (the same transaction fills and empties it, and a rolled-back batch rolls the fill back too), so no row of an
    /// earlier batch is inserted twice. The row order of the field table is the append order: DuckDB keeps insertion order
    /// (<c>preserve_insertion_order</c> is on by default) and the statement reads one table into one table.
    /// Security: the table name is built from an int through the invariant culture, and the column names are constants, so no
    /// caller text reaches the SQL (see <see cref="_EnsureFieldTables"/>).
    /// </remarks>
    private void _MoveStagedRows(string tableName)
    {
        StringBuilder sql = new();
        sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO {tableName} SELECT packet_id, {_PositionColumn}, ");
        sql.Append("(value_hi::UHUGEINT << 64) | value_lo::UHUGEINT");
        if (_IncludeCustomRepresentation)
        {
            sql.Append(", custom_repr");
        }
        if (_IncludeCustomText)
        {
            sql.Append(", custom_text");
        }

        sql.Append(CultureInfo.InvariantCulture, $" FROM stage_{tableName}; DELETE FROM stage_{tableName};");

        using DuckDBCommand command = _Connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.ExecuteNonQuery();
    }

    /// <summary>Appends the rows of a <see cref="FieldType.Bool"/> bag (packet id, position, value, optional texts).</summary>
    /// <param name="appender">Appender of the field's table; its column order matches the table definition.</param>
    /// <param name="bag">The batch's rows of the field.</param>
    /// <param name="values">The bag's value span; one entry per row.</param>
    /// <remarks>
    /// Perf: one overload per value type because <c>AppendValue</c> is overloaded per type; a generic loop would box or dispatch per
    /// cell. The spans are taken inside <see cref="WriteBatch"/>, so no list grows while one is alive.
    /// </remarks>
    private void _AppendRows(DuckDBAppender appender, FieldColumnBag bag, ReadOnlySpan<bool> values)
    {
        ReadOnlySpan<int> packetIds = bag.PacketIdSpan;
        ReadOnlySpan<int> positions = bag.PositionSpan;
        ReadOnlySpan<string?> customRepresentations = bag.CustomRepresentationSpan;
        ReadOnlySpan<string?> customTexts = bag.CustomTextSpan;
        for (int i = 0; i < values.Length; i++)
        {
            IDuckDBAppenderRow row = appender.CreateRow().AppendValue(packetIds[i]).AppendValue(positions[i]).AppendValue(values[i]);
            _EndRow(row, customRepresentations, customTexts, i);
        }
    }

    /// <summary>
    /// Appends the rows of a <see cref="FieldType.I64"/> or <see cref="FieldType.Timestamp"/> bag (packet id, position, value,
    /// optional texts).
    /// </summary>
    /// <param name="appender">Appender of the field's table; its column order matches the table definition.</param>
    /// <param name="bag">The batch's rows of the field.</param>
    /// <param name="values">The bag's value span (signed 64-bit, or nanoseconds for timestamps); one entry per row.</param>
    /// <remarks>Perf: see the <see cref="bool"/> overload.</remarks>
    private void _AppendRows(DuckDBAppender appender, FieldColumnBag bag, ReadOnlySpan<long> values)
    {
        ReadOnlySpan<int> packetIds = bag.PacketIdSpan;
        ReadOnlySpan<int> positions = bag.PositionSpan;
        ReadOnlySpan<string?> customRepresentations = bag.CustomRepresentationSpan;
        ReadOnlySpan<string?> customTexts = bag.CustomTextSpan;
        for (int i = 0; i < values.Length; i++)
        {
            IDuckDBAppenderRow row = appender.CreateRow().AppendValue(packetIds[i]).AppendValue(positions[i]).AppendValue(values[i]);
            _EndRow(row, customRepresentations, customTexts, i);
        }
    }

    /// <summary>Appends the rows of a <see cref="FieldType.U64"/> bag (packet id, position, value, optional texts).</summary>
    /// <param name="appender">Appender of the field's table; its column order matches the table definition.</param>
    /// <param name="bag">The batch's rows of the field.</param>
    /// <param name="values">The bag's value span (stored as <c>UBIGINT</c>); one entry per row.</param>
    /// <remarks>Perf: see the <see cref="bool"/> overload.</remarks>
    private void _AppendRows(DuckDBAppender appender, FieldColumnBag bag, ReadOnlySpan<ulong> values)
    {
        ReadOnlySpan<int> packetIds = bag.PacketIdSpan;
        ReadOnlySpan<int> positions = bag.PositionSpan;
        ReadOnlySpan<string?> customRepresentations = bag.CustomRepresentationSpan;
        ReadOnlySpan<string?> customTexts = bag.CustomTextSpan;
        for (int i = 0; i < values.Length; i++)
        {
            IDuckDBAppenderRow row = appender.CreateRow().AppendValue(packetIds[i]).AppendValue(positions[i]).AppendValue(values[i]);
            _EndRow(row, customRepresentations, customTexts, i);
        }
    }

    /// <summary>Appends the rows of a <see cref="FieldType.F64"/> bag (packet id, position, value, optional texts).</summary>
    /// <param name="appender">Appender of the field's table; its column order matches the table definition.</param>
    /// <param name="bag">The batch's rows of the field.</param>
    /// <param name="values">The bag's value span; one entry per row.</param>
    /// <remarks>Perf: see the <see cref="bool"/> overload.</remarks>
    private void _AppendRows(DuckDBAppender appender, FieldColumnBag bag, ReadOnlySpan<double> values)
    {
        ReadOnlySpan<int> packetIds = bag.PacketIdSpan;
        ReadOnlySpan<int> positions = bag.PositionSpan;
        ReadOnlySpan<string?> customRepresentations = bag.CustomRepresentationSpan;
        ReadOnlySpan<string?> customTexts = bag.CustomTextSpan;
        for (int i = 0; i < values.Length; i++)
        {
            IDuckDBAppenderRow row = appender.CreateRow().AppendValue(packetIds[i]).AppendValue(positions[i]).AppendValue(values[i]);
            _EndRow(row, customRepresentations, customTexts, i);
        }
    }

    /// <summary>Appends the rows of a <see cref="FieldType.String"/> bag (packet id, position, value, optional texts).</summary>
    /// <param name="appender">Appender of the field's table; its column order matches the table definition.</param>
    /// <param name="bag">The batch's rows of the field.</param>
    /// <param name="values">The bag's value span; one entry per row; a <see langword="null"/> entry is written as SQL NULL.</param>
    /// <remarks>Perf: see the <see cref="bool"/> overload.</remarks>
    private void _AppendRows(DuckDBAppender appender, FieldColumnBag bag, ReadOnlySpan<string?> values)
    {
        ReadOnlySpan<int> packetIds = bag.PacketIdSpan;
        ReadOnlySpan<int> positions = bag.PositionSpan;
        ReadOnlySpan<string?> customRepresentations = bag.CustomRepresentationSpan;
        ReadOnlySpan<string?> customTexts = bag.CustomTextSpan;
        for (int i = 0; i < values.Length; i++)
        {
            IDuckDBAppenderRow row = appender.CreateRow().AppendValue(packetIds[i]).AppendValue(positions[i]);
            row = _AppendNullableString(row, values[i]);
            _EndRow(row, customRepresentations, customTexts, i);
        }
    }

    /// <summary>
    /// Appends the rows of a <see cref="FieldType.Bytes"/> bag as BLOB. The appender takes the bytes as a span: a value is a view of
    /// the batch's slab, so no array is created per row.
    /// </summary>
    /// <param name="appender">Appender of the field's table; its column order matches the table definition.</param>
    /// <param name="bag">The batch's rows of the field; the value of row <c>i</c> is <see cref="FieldColumnBag.GetBlob"/>.</param>
    private void _AppendBlobRows(DuckDBAppender appender, FieldColumnBag bag)
    {
        ReadOnlySpan<int> packetIds = bag.PacketIdSpan;
        ReadOnlySpan<int> positions = bag.PositionSpan;
        ReadOnlySpan<string?> customRepresentations = bag.CustomRepresentationSpan;
        ReadOnlySpan<string?> customTexts = bag.CustomTextSpan;
        for (int i = 0; i < packetIds.Length; i++)
        {
            IDuckDBAppenderRow row = appender.CreateRow()
                .AppendValue(packetIds[i])
                .AppendValue(positions[i]);
            row = _AppendBlob(row, bag.GetBlob(i));
            _EndRow(row, customRepresentations, customTexts, i);
        }
    }

    // The five address loops below share one shape: the bag keeps the inline FieldValueData of each row, and the loop reads the
    // integer of its own type with the typed accessor. Invariant: FieldColumnBag.Add accepted only values of the bag's type, so the
    // accessor cannot fail; a false is a broken invariant, reported from a cold helper so the loop has no throw site. One loop per
    // type because AppendValue is overloaded per type and the accessor differs; a generic loop would box or dispatch per row.
    // Sync: README § Table Layout (types) and EXPORTER_GUIDE.md § Rebuilding the display line (integer address forms).

    /// <summary>Appends the rows of an <see cref="FieldType.IPv4Address"/> bag as <c>UINTEGER</c> (<see cref="IPv4Address.RawValue"/>).</summary>
    /// <param name="appender">Appender of the field's table; its column order matches the table definition.</param>
    /// <param name="bag">The batch's rows of the field.</param>
    /// <exception cref="InvalidOperationException">A row is no IPv4 value (a broken invariant).</exception>
    /// <remarks>Perf: no allocation per row; the address is read in place and appended as a plain integer.</remarks>
    private void _AppendIPv4Rows(DuckDBAppender appender, FieldColumnBag bag)
    {
        ReadOnlySpan<int> packetIds = bag.PacketIdSpan;
        ReadOnlySpan<int> positions = bag.PositionSpan;
        ReadOnlySpan<string?> customRepresentations = bag.CustomRepresentationSpan;
        ReadOnlySpan<string?> customTexts = bag.CustomTextSpan;
        ReadOnlySpan<FieldValueData> values = bag.FixedValueSpan;
        for (int i = 0; i < values.Length; i++)
        {
            if (!values[i].TryGetAsIPv4(out IPv4Address address))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.IPv4Address, values[i].Type);
            }

            IDuckDBAppenderRow row = appender.CreateRow()
                .AppendValue(packetIds[i])
                .AppendValue(positions[i])
                .AppendValue((uint?)address.RawValue);
            _EndRow(row, customRepresentations, customTexts, i);
        }
    }

    /// <summary>Appends the rows of a <see cref="FieldType.MacAddress"/> bag as <c>UBIGINT</c> (the 48-bit <see cref="MacAddress.RawValue"/>).</summary>
    /// <param name="appender">Appender of the field's table; its column order matches the table definition.</param>
    /// <param name="bag">The batch's rows of the field.</param>
    /// <exception cref="InvalidOperationException">A row is no MAC value (a broken invariant).</exception>
    /// <remarks>Perf: see <see cref="_AppendIPv4Rows"/>.</remarks>
    private void _AppendMacRows(DuckDBAppender appender, FieldColumnBag bag)
    {
        ReadOnlySpan<int> packetIds = bag.PacketIdSpan;
        ReadOnlySpan<int> positions = bag.PositionSpan;
        ReadOnlySpan<string?> customRepresentations = bag.CustomRepresentationSpan;
        ReadOnlySpan<string?> customTexts = bag.CustomTextSpan;
        ReadOnlySpan<FieldValueData> values = bag.FixedValueSpan;
        for (int i = 0; i < values.Length; i++)
        {
            if (!values[i].TryGetAsMacAddress(out MacAddress address))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.MacAddress, values[i].Type);
            }

            IDuckDBAppenderRow row = appender.CreateRow()
                .AppendValue(packetIds[i])
                .AppendValue(positions[i])
                .AppendValue((ulong?)address.RawValue);
            _EndRow(row, customRepresentations, customTexts, i);
        }
    }

    /// <summary>Appends the rows of a <see cref="FieldType.Eui64"/> bag as <c>UBIGINT</c> (<see cref="Eui64.RawValue"/>).</summary>
    /// <param name="appender">Appender of the field's table; its column order matches the table definition.</param>
    /// <param name="bag">The batch's rows of the field.</param>
    /// <exception cref="InvalidOperationException">A row is no EUI-64 value (a broken invariant).</exception>
    /// <remarks>Perf: see <see cref="_AppendIPv4Rows"/>.</remarks>
    private void _AppendEui64Rows(DuckDBAppender appender, FieldColumnBag bag)
    {
        ReadOnlySpan<int> packetIds = bag.PacketIdSpan;
        ReadOnlySpan<int> positions = bag.PositionSpan;
        ReadOnlySpan<string?> customRepresentations = bag.CustomRepresentationSpan;
        ReadOnlySpan<string?> customTexts = bag.CustomTextSpan;
        ReadOnlySpan<FieldValueData> values = bag.FixedValueSpan;
        for (int i = 0; i < values.Length; i++)
        {
            if (!values[i].TryGetAsEui64(out Eui64 identifier))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.Eui64, values[i].Type);
            }

            IDuckDBAppenderRow row = appender.CreateRow()
                .AppendValue(packetIds[i])
                .AppendValue(positions[i])
                .AppendValue((ulong?)identifier.RawValue);
            _EndRow(row, customRepresentations, customTexts, i);
        }
    }

    /// <summary>
    /// Appends the rows of an <see cref="FieldType.IPv6Address"/> bag to the staging table as the two <c>UBIGINT</c> halves
    /// (<see cref="IPv6Address.High"/>, <see cref="IPv6Address.Low"/>); <see cref="_MoveStagedRows"/> combines them afterwards.
    /// </summary>
    /// <param name="appender">Appender of the staging table <c>stage_field_{id}</c>; its columns are packet id, position, high, low, then the texts.</param>
    /// <param name="bag">The batch's rows of the field.</param>
    /// <exception cref="InvalidOperationException">A row is no IPv6 value (a broken invariant).</exception>
    /// <remarks>Perf: two plain 64-bit appends per row instead of a <see cref="System.Numerics.BigInteger"/> (a heap allocation per row).</remarks>
    private void _AppendIPv6HalfRows(DuckDBAppender appender, FieldColumnBag bag)
    {
        ReadOnlySpan<int> packetIds = bag.PacketIdSpan;
        ReadOnlySpan<int> positions = bag.PositionSpan;
        ReadOnlySpan<string?> customRepresentations = bag.CustomRepresentationSpan;
        ReadOnlySpan<string?> customTexts = bag.CustomTextSpan;
        ReadOnlySpan<FieldValueData> values = bag.FixedValueSpan;
        for (int i = 0; i < values.Length; i++)
        {
            if (!values[i].TryGetAsIPv6(out IPv6Address address))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.IPv6Address, values[i].Type);
            }

            IDuckDBAppenderRow row = appender.CreateRow()
                .AppendValue(packetIds[i])
                .AppendValue(positions[i])
                .AppendValue((ulong?)address.High)
                .AppendValue((ulong?)address.Low);
            _EndRow(row, customRepresentations, customTexts, i);
        }
    }

    /// <summary>
    /// Appends the rows of a <see cref="FieldType.Uuid"/> bag as DuckDB's native <c>UUID</c>. The 16 big-endian bytes of the value
    /// (<see cref="Uuid.ToBytes"/>) are read as a <see cref="Guid"/> in big-endian order, which is the order of the text form, so
    /// DuckDB shows the same UUID text as the exporter's own formatting.
    /// </summary>
    /// <param name="appender">Appender of the field's table; its column order matches the table definition.</param>
    /// <param name="bag">The batch's rows of the field.</param>
    /// <exception cref="InvalidOperationException">A row is no UUID value (a broken invariant).</exception>
    /// <remarks>
    /// Perf: one stack buffer for the whole loop; <see cref="Guid"/> is a value type, so no allocation per row. The byte order is the
    /// trap: the <see cref="Guid"/> constructor without <c>bigEndian: true</c> would swap the first three groups.
    /// </remarks>
    private void _AppendUuidRows(DuckDBAppender appender, FieldColumnBag bag)
    {
        ReadOnlySpan<int> packetIds = bag.PacketIdSpan;
        ReadOnlySpan<int> positions = bag.PositionSpan;
        ReadOnlySpan<string?> customRepresentations = bag.CustomRepresentationSpan;
        ReadOnlySpan<string?> customTexts = bag.CustomTextSpan;
        ReadOnlySpan<FieldValueData> values = bag.FixedValueSpan;
        Span<byte> uuidBytes = stackalloc byte[16];
        for (int i = 0; i < values.Length; i++)
        {
            if (!values[i].TryGetAsUuid(out Uuid uuid))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.Uuid, values[i].Type);
            }

            uuid.ToBytes(uuidBytes);
            IDuckDBAppenderRow row = appender.CreateRow()
                .AppendValue(packetIds[i])
                .AppendValue(positions[i])
                .AppendValue((Guid?)new Guid(uuidBytes, bigEndian: true));
            _EndRow(row, customRepresentations, customTexts, i);
        }
    }

    /// <summary>Appends the optional custom columns of row <paramref name="i"/> (table definition order) and ends the row.</summary>
    /// <param name="row">The row with packet id, position, and value already appended.</param>
    /// <param name="customRepresentations">The bag's custom representation per row; used when the table has <c>custom_repr</c>.</param>
    /// <param name="customTexts">The bag's custom text per row; used when the table has <c>custom_text</c>.</param>
    /// <param name="i">The index of the row in the bag.</param>
    private void _EndRow(
        IDuckDBAppenderRow row, ReadOnlySpan<string?> customRepresentations, ReadOnlySpan<string?> customTexts, int i)
    {
        if (_IncludeCustomRepresentation)
        {
            row = _AppendNullableString(row, customRepresentations[i]);
        }
        if (_IncludeCustomText)
        {
            row = _AppendNullableString(row, customTexts[i]);
        }

        row.EndRow();
    }

    /// <summary>
    /// Appends a byte payload as BLOB. DuckDB.NET appends an empty span as NULL but an empty array as an empty BLOB, and an
    /// empty value must stay distinguishable from an absent one, so the empty case goes through the shared empty array (no
    /// allocation).
    /// </summary>
    /// <param name="row">The row to append to.</param>
    /// <param name="blob">The payload; empty is written as an empty BLOB, never as NULL.</param>
    /// <returns>The row after the append.</returns>
    private static IDuckDBAppenderRow _AppendBlob(IDuckDBAppenderRow row, Span<byte> blob) =>
        blob.IsEmpty
            ? row.AppendValue(Array.Empty<byte>())
            : row.AppendValue(blob);

    /// <summary>
    /// Appends a custom text, a custom representation, or a string value: SQL NULL for <see langword="null"/> (absent), the text
    /// itself for any string, including the empty one (<c>''</c>). A reader tells "no dissector set a text" (NULL) from "a text
    /// that is empty" (<c>''</c>) by this difference. Sync: <see cref="OptionalTextReader"/> (null = absent) and
    /// EXPORTER_GUIDE.md § Rebuilding the display line (presence table).
    /// </summary>
    /// <param name="row">The row to append to.</param>
    /// <param name="value">The text; <see langword="null"/> is written as SQL NULL, the empty string as an empty VARCHAR.</param>
    /// <returns>The row after the append.</returns>
    private static IDuckDBAppenderRow _AppendNullableString(IDuckDBAppenderRow row, string? value) =>
        value is null
            ? row.AppendNullValue()
            : row.AppendValue(value);

    #endregion

    #region Catalog and completion

    /// <inheritdoc/>
    /// <remarks>
    /// Writes <c>catalog</c> in its own transaction, then runs the explicit <c>CHECKPOINT</c> of the export, which folds the
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
                .AppendValue(entry.ProtocolIdValue);

            // A structure node has no field table (table_name NULL); the column order stays unchanged.
            row = _AppendNullableString(row, entry.TableName);
            _AppendNullableString(row, entry.Description).EndRow();
        }
    }

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
}

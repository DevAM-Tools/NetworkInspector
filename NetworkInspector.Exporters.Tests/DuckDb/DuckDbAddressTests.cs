// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using AddressValues = NetworkInspector.Exporters.Tests.Helpers.AddressProbePacketFactory.Values;

namespace NetworkInspector.Exporters.Tests.DuckDb;

/// <summary>
/// The integer address columns of the DuckDB exporter: IPv4 as <c>UINTEGER</c>, MAC and EUI-64 as <c>UBIGINT</c>, IPv6 as
/// <c>UHUGEINT</c> (written through the per-table staging table), and UUID as DuckDB's native <c>UUID</c>. The tests read the
/// file back with SQL, the way an export reader would: range queries, the high and low halves of an IPv6 value, the UUID text, and
/// the sort order of the boundary values.
/// Slow: every test writes and reads a DuckDB file.
/// </summary>
/// <remarks>Sync: NetworkInspector.Exporters/EXPORTER_GUIDE.md § Rebuilding the display line (integer address forms).</remarks>
[Category("Slow")]
internal sealed class DuckDbAddressTests
{
    #region Exporting and reading back files

    /// <summary>Exports the packets of a factory to a DuckDB file.</summary>
    /// <param name="path">The target file.</param>
    /// <param name="factory">The factory that built the packets (and owns their stack).</param>
    /// <param name="values">The address values of each packet, in packet order.</param>
    /// <param name="shape">The export shape.</param>
    /// <param name="mode">The write mode.</param>
    /// <param name="maxPacketsPerBlock">The batch size in packets.</param>
    private static void _Export(
        string path,
        AddressProbePacketFactory factory,
        IReadOnlyList<AddressValues> values,
        ExportShape shape = ExportShape.Hierarchy,
        ExportWriteMode mode = ExportWriteMode.Synchronous,
        int maxPacketsPerBlock = 50_000)
    {
        using DuckDbExporter exporter = DuckDbExporter.CreateBuilder()
            .ToFile(path)
            .WithContent(factory.Content(shape))
            .WithWriteMode(mode)
            .WithMaxPacketsPerBlock(maxPacketsPerBlock)
            .Build();
        foreach (Packet packet in factory.CreateTreePackets(values))
        {
            _ = exporter.OnPacket(packet);
        }

        exporter.OnFinish();
    }

    /// <summary>Opens a read connection on a finished file.</summary>
    /// <param name="path">The <c>.duckdb</c> file.</param>
    /// <returns>The open connection.</returns>
    private static DuckDBConnection _Open(string path)
    {
        DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();
        return connection;
    }

    /// <summary>
    /// A packet's values with a distinct IPv4 and IPv6 value per index, so the rows of a multi-batch export are told apart by their
    /// value and their order is checked by it.
    /// </summary>
    /// <param name="index">The packet index.</param>
    /// <returns>The values.</returns>
    private static AddressValues _Distinct(int index) =>
        new(
            new IPv4Address(0x0A00_0000u + checked((uint)index)),
            new IPv6Address(0x2001_0DB8_0000_0000UL + checked((ulong)index), 0xFFFF_0000_0000_0000UL + checked((ulong)index)),
            new MacAddress(0x0200_0000_0000UL + checked((ulong)index)),
            new Eui64(0x0200_0000_FFFE_0000UL + checked((ulong)index)),
            new Uuid(0x1000_0000_0000_0000UL + checked((ulong)index), 0x8000_0000_0000_0000UL + checked((ulong)index)),
            new IPv6Address(0x2001_0DB8_0001_0000UL + checked((ulong)index), checked((ulong)index)));

    #endregion

    #region Column types and values

    /// <summary>
    /// The field tables of the address types have the documented column types: <c>UINTEGER</c> for IPv4, <c>UHUGEINT</c> for
    /// IPv6, <c>UBIGINT</c> for MAC and EUI-64, and the native <c>UUID</c> for UUID.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AddressFieldTablesHaveTheDocumentedIntegerTypes()
    {
        using TestDir dir = new("duckdb_address_types");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("addresses.duckdb");

        _Export(path, factory, [AddressProbePacketFactory.Readable]);

        using DuckDBConnection connection = _Open(path);
        await Assert.That(DuckDbReadBack.Text(connection, Invariant($"SELECT typeof(value) FROM {factory.TableOf("adp.ipv4")}"))).IsEqualTo("UINTEGER");
        await Assert.That(DuckDbReadBack.Text(connection, Invariant($"SELECT typeof(value) FROM {factory.TableOf("adp.ipv6")}"))).IsEqualTo("UHUGEINT");
        await Assert.That(DuckDbReadBack.Text(connection, Invariant($"SELECT typeof(value) FROM {factory.TableOf("adp.ipv6dst")}"))).IsEqualTo("UHUGEINT");
        await Assert.That(DuckDbReadBack.Text(connection, Invariant($"SELECT typeof(value) FROM {factory.TableOf("adp.mac")}"))).IsEqualTo("UBIGINT");
        await Assert.That(DuckDbReadBack.Text(connection, Invariant($"SELECT typeof(value) FROM {factory.TableOf("adp.eui64")}"))).IsEqualTo("UBIGINT");
        await Assert.That(DuckDbReadBack.Text(connection, Invariant($"SELECT typeof(value) FROM {factory.TableOf("adp.uuid")}"))).IsEqualTo("UUID");
    }

    /// <summary>IPv4, MAC and EUI-64 values are stored as the integer that their bytes form.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task EveryAddressTypeStoresTheIntegerOfItsBytes()
    {
        using TestDir dir = new("duckdb_address_values");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("addresses.duckdb");
        AddressValues values = AddressProbePacketFactory.Readable;

        _Export(path, factory, [values]);

        using DuckDBConnection connection = _Open(path);
        await Assert.That(DuckDbReadBack.Text(connection, Invariant($"SELECT value FROM {factory.TableOf("adp.ipv4")}"))).IsEqualTo("3232235777"); // 0xC0A80101
        await Assert.That(DuckDbReadBack.Text(connection, Invariant($"SELECT value FROM {factory.TableOf("adp.mac")}")))
            .IsEqualTo(values.Mac.RawValue.ToString(CultureInfo.InvariantCulture));
        await Assert.That(DuckDbReadBack.Text(connection, Invariant($"SELECT value FROM {factory.TableOf("adp.eui64")}")))
            .IsEqualTo(values.Eui64.RawValue.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A range query over the stored IPv4 integers returns exactly the addresses of a subnet and none next to its boundaries.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AnIPv4RangeQueryReturnsExactlyTheSubnet()
    {
        using TestDir dir = new("duckdb_address_range");
        string path = dir.FilePath("sources.duckdb");

        // The sources around the 192.168.1.0/24 boundaries: the last address below, both ends, a middle one, the first above.
        byte[][] sources = [[192, 168, 0, 255], [192, 168, 1, 0], [192, 168, 1, 77], [192, 168, 1, 255], [192, 168, 2, 0]];
        using (DuckDbExporter exporter = DuckDbExporter.CreateBuilder().ToFile(path)
            .WithContent(new ExportContent { Shape = ExportShape.Hierarchy, Info = false, FrameBytes = false })
            .WithWriteMode(ExportWriteMode.Synchronous).Build())
        {
            for (int i = 0; i < sources.Length; i++)
            {
                byte[] frame = FrameGenerators.BuildEthernetFrame(
                    [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF],
                    [0x00, 0x11, 0x22, 0x33, 0x44, 0x55],
                    0x0800,
                    FrameGenerators.BuildIpv4Packet(sources[i], [10, 0, 0, 1], 17, FrameGenerators.BuildUdpDatagram(1000, 53, [1, 2, 3, 4])));
                _ = exporter.OnPacket(PacketGenerators.CreateParsedPacket(i, frame));
            }

            exporter.OnFinish();
        }

        using DuckDBConnection connection = _Open(path);
        string table = Invariant($"field_{TestHarness.GetStack().GetFieldId("ip.src")!.Value.Value}");

        // 192.168.1.0 is 3232235776 and 192.168.1.255 is 3232236031. The packet ids are not asserted: the test harness numbers
        // the packets per thread, so they depend on which tests ran before.
        List<string> inside = DuckDbReadBack.Texts(
            connection,
            Invariant($"SELECT value FROM {table} WHERE value BETWEEN 3232235776 AND 3232236031 ORDER BY value"));
        List<string> all = DuckDbReadBack.Texts(connection, Invariant($"SELECT value FROM {table} ORDER BY value"));

        // The five rows exist (so the two outside the range were written and filtered out), and the three inside are exactly the
        // addresses .1.0, .1.77, and .1.255.
        await Assert.That(all.Count).IsEqualTo(5);
        await Assert.That(inside).IsEquivalentTo(new List<string> { "3232235776", "3232235853", "3232236031" }, CollectionOrdering.Matching);
    }

    /// <summary>An IPv6 value is split into its high and low 64-bit halves by shifting and masking in SQL.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AnIPv6ValueSplitsIntoItsHighAndLowHalvesWithSql()
    {
        using TestDir dir = new("duckdb_address_halves");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("addresses.duckdb");
        IPv6Address address = AddressProbePacketFactory.Readable.IPv6;

        _Export(path, factory, [AddressProbePacketFactory.Readable]);

        using DuckDBConnection connection = _Open(path);
        string table = factory.TableOf("adp.ipv6");
        await Assert.That(DuckDbReadBack.Text(connection, Invariant($"SELECT CAST(value >> 64 AS UBIGINT) FROM {table}")))
            .IsEqualTo(address.High.ToString(CultureInfo.InvariantCulture));
        await Assert.That(DuckDbReadBack.Text(connection, Invariant($"SELECT CAST(value & 18446744073709551615::UHUGEINT AS UBIGINT) FROM {table}")))
            .IsEqualTo(address.Low.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// UUIDs, including the all-zero, all-one and top-bit boundary values, are stored as DuckDB's native type and print in the
    /// lower-case text form.
    /// </summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task AUuidIsStoredAsTheNativeTypeWithTheSameText()
    {
        using TestDir dir = new("duckdb_address_uuid");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("uuids.duckdb");

        // The readable one, all zero, all one, and two with the top bit of the first byte clear and set (DuckDB keeps a UUID as a
        // signed 128-bit number with the top bit flipped, so those two are where a wrong conversion shows).
        Uuid[] uuids =
        [
            AddressProbePacketFactory.Readable.Uuid,
            new Uuid(0, 0),
            new Uuid(ulong.MaxValue, ulong.MaxValue),
            new Uuid(0x7FFF_FFFF_FFFF_FFFFUL, 0x0123_4567_89AB_CDEFUL),
            new Uuid(0x8000_0000_0000_0000UL, 0xFEDC_BA98_7654_3210UL),
        ];
        AddressValues[] packets = [.. uuids.Select(static uuid => AddressProbePacketFactory.Readable with { Uuid = uuid })];

        _Export(path, factory, packets);

        using DuckDBConnection connection = _Open(path);
        List<string> stored = DuckDbReadBack.Texts(connection, Invariant($"SELECT value::VARCHAR FROM {factory.TableOf("adp.uuid")} ORDER BY packet_id"));

        // DuckDB prints a UUID in lower case with dashes, which is the lower-case form of the value's own text.
        await Assert.That(stored).IsEquivalentTo(uuids.Select(static uuid => uuid.Format(HexCase.Lower)).ToList(), CollectionOrdering.Matching);
    }

    #endregion

    #region Order of the boundary values

    /// <summary>Boundary addresses come back unchanged and, sorted by the stored column, give the ascending address order.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task BoundaryAddressesRoundTripAndSortInAddressOrder()
    {
        using TestDir dir = new("duckdb_address_order");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("boundaries.duckdb");
        AddressValues[] ascending = AddressProbePacketFactory.AscendingBoundaries();

        _Export(path, factory, AddressProbePacketFactory.ShuffledBoundaries());

        using DuckDBConnection connection = _Open(path);
        List<string> ipv4 = DuckDbReadBack.Texts(connection, Invariant($"SELECT value FROM {factory.TableOf("adp.ipv4")} ORDER BY value"));
        List<string> mac = DuckDbReadBack.Texts(connection, Invariant($"SELECT value FROM {factory.TableOf("adp.mac")} ORDER BY value"));
        List<string> eui64 = DuckDbReadBack.Texts(connection, Invariant($"SELECT value FROM {factory.TableOf("adp.eui64")} ORDER BY value"));
        List<IPv6Address> ipv6 = _IPv6Rows(connection, factory.TableOf("adp.ipv6"), "value");

        // A signed column would sort the upper half of the address space first; a byte-order swap would not give these integers.
        await Assert.That(ipv4).IsEquivalentTo(AddressProbePacketFactory.DecimalOf(ascending, static v => v.IPv4.RawValue), CollectionOrdering.Matching);
        await Assert.That(mac).IsEquivalentTo(AddressProbePacketFactory.DecimalOf(ascending, static v => v.Mac.RawValue), CollectionOrdering.Matching);
        await Assert.That(eui64).IsEquivalentTo(AddressProbePacketFactory.DecimalOf(ascending, static v => v.Eui64.RawValue), CollectionOrdering.Matching);
        await Assert.That(ipv6).IsEquivalentTo(ascending.Select(static v => v.IPv6).ToList(), CollectionOrdering.Matching);
    }

    /// <summary>The ascending boundary list is already in the comparison order of each address value type.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task TheBoundaryOrderIsTheOrderOfTheValueTypesOwnComparison()
    {
        // The documented order of the stored integers is the comparison order of the value types; this ties the two together so a
        // change of either shows here. The ascending list is the oracle of the sort test above.
        AddressValues[] ascending = AddressProbePacketFactory.AscendingBoundaries();

        await Assert.That(ascending.Select(static v => v.IPv4).Order().SequenceEqual(ascending.Select(static v => v.IPv4))).IsTrue();
        await Assert.That(ascending.Select(static v => v.Mac).Order().SequenceEqual(ascending.Select(static v => v.Mac))).IsTrue();
        await Assert.That(ascending.Select(static v => v.Eui64).Order().SequenceEqual(ascending.Select(static v => v.Eui64))).IsTrue();
        await Assert.That(ascending.Select(static v => v.IPv6).Order().SequenceEqual(ascending.Select(static v => v.IPv6))).IsTrue();
        await Assert.That(ascending.Select(static v => v.Uuid).Order().SequenceEqual(ascending.Select(static v => v.Uuid))).IsTrue();
    }

    #endregion

    #region IPv6 staging

    /// <summary>
    /// Five IPv6 rows written in three batches keep their packet order in both write modes, and no staging table remains in the file.
    /// </summary>
    /// <param name="mode">The write mode of the exporter.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Arguments(ExportWriteMode.Synchronous)]
    [Arguments(ExportWriteMode.Background)]
    public async Task FiveIPv6RowsInThreeBatchesKeepTheirOrderAndLeaveNoStagingTable(ExportWriteMode mode)
    {
        using TestDir dir = new("duckdb_address_staging");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("staged.duckdb");
        AddressValues[] packets = [.. Enumerable.Range(0, 5).Select(_Distinct)];

        _Export(path, factory, packets, mode: mode, maxPacketsPerBlock: 2);

        using DuckDBConnection connection = _Open(path);
        List<IPv6Address> source = _IPv6Rows(connection, factory.TableOf("adp.ipv6"), "rowid");
        List<IPv6Address> destination = _IPv6Rows(connection, factory.TableOf("adp.ipv6dst"), "rowid");

        // Exactly five rows each, in packet order: a staging table that kept the rows of the first batch would repeat them in the
        // next one, and a missing DELETE would show as extra rows.
        await Assert.That(source).IsEquivalentTo(packets.Select(static v => v.IPv6).ToList(), CollectionOrdering.Matching);
        await Assert.That(destination).IsEquivalentTo(packets.Select(static v => v.IPv6Dst).ToList(), CollectionOrdering.Matching);
        await Assert.That(DuckDbReadBack.Text(connection, "SELECT COUNT(*) FROM information_schema.tables WHERE table_name LIKE 'stage_field_%'"))
            .IsEqualTo("0");
    }

    /// <summary>
    /// The flat shapes stage IPv6 rows through a table with the occurrence column, keep the packet order, and leave no staging
    /// table in the file.
    /// </summary>
    /// <param name="shape">The flat export shape.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Arguments(ExportShape.AllFields)]
    [Arguments(ExportShape.SelectedFields)]
    public async Task TheFlatShapesStageIPv6RowsWithTheOccurrenceColumn(ExportShape shape)
    {
        using TestDir dir = new("duckdb_address_flat");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("flat.duckdb");
        AddressValues[] packets = [.. Enumerable.Range(0, 3).Select(_Distinct)];

        // The selected-fields shape creates its field tables (and the staging tables) before the first packet, outside any batch
        // transaction; the all-fields shape creates them in the first batch.
        _Export(path, factory, packets, shape: shape);

        using DuckDBConnection connection = _Open(path);
        string table = factory.TableOf("adp.ipv6");
        List<IPv6Address> rows = _IPv6Rows(connection, table, "rowid");
        string columns = string.Join(
            ',',
            DuckDbReadBack.Texts(
                connection,
                Invariant($"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}' ORDER BY ordinal_position")));

        await Assert.That(rows).IsEquivalentTo(packets.Select(static v => v.IPv6).ToList(), CollectionOrdering.Matching);
        await Assert.That(columns).IsEqualTo("packet_id,occurrence,value,custom_repr,custom_text");
        await Assert.That(DuckDbReadBack.Text(connection, "SELECT COUNT(*) FROM information_schema.tables WHERE table_name LIKE 'stage_field_%'"))
            .IsEqualTo("0");
    }

    /// <summary>
    /// The representation and text columns of an IPv6 field keep their own values through the staging: set, absent (NULL), and
    /// empty stay distinct.
    /// </summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task TextsOfAnIPv6FieldSurviveTheStagingInTheirOwnColumns()
    {
        using TestDir dir = new("duckdb_address_texts");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("texts.duckdb");
        AddressValues readable = AddressProbePacketFactory.Readable;

        // A representation and a text, none, and both set but empty: the staging copies the columns by name, so a swapped pair or a
        // NULL turned into an empty string shows here.
        AddressValues[] packets =
        [
            readable with { IPv6Representation = "R", IPv6Text = "T" },
            readable with { IPv6Representation = null, IPv6Text = null },
            readable with { IPv6Representation = string.Empty, IPv6Text = string.Empty },
        ];

        _Export(path, factory, packets);

        using DuckDBConnection connection = _Open(path);
        List<string> texts = DuckDbReadBack.Texts(
            connection,
            Invariant($"SELECT coalesce(custom_repr, '<null>') || '|' || coalesce(custom_text, '<null>') FROM {factory.TableOf("adp.ipv6")} ORDER BY rowid"));

        await Assert.That(texts).IsEquivalentTo(new List<string> { "R|T", "<null>|<null>", "|" }, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A batch that fails after it created the IPv6 field tables and their staging table is rolled back completely: no rows, tables
    /// or staging table remain, and the retry of the same batch writes every row once.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AFailingBatchThatCreatedTheTablesLeavesNoRowsAndTheRetryWritesThemOnce()
    {
        using TestDir dir = new("duckdb_address_rollback_new");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("rollback.duckdb");
        AddressValues[] packets = [.. Enumerable.Range(0, 3).Select(_Distinct)];

        using ColumnarPacketBatch batch = _Batch(factory, packets);
        using DuckDbBulkWriter writer = new(path, factory.Content(ExportShape.Hierarchy), [], 0, 0, DuckDbStorageVersions.DefaultSettingValue);
        using DuckDBConnection other = _Open(path);

        // The UUID field is written after both IPv6 fields; a table of the wrong shape makes its append fail after their staging
        // rows were moved, inside the same transaction.
        DuckDbReadBack.Execute(other, Invariant($"CREATE TABLE {factory.TableOf("adp.uuid")}(unrelated INTEGER)"));
        IndexOutOfRangeException failure = Assert.Throws<IndexOutOfRangeException>(() => writer.WriteBatch(batch));
        DuckDbReadBack.Execute(other, Invariant($"DROP TABLE {factory.TableOf("adp.uuid")}"));
        long rowsAfterFailure = Convert.ToInt64(DuckDbReadBack.Text(other, "SELECT COUNT(*) FROM packets"), CultureInfo.InvariantCulture);

        // The failure came after the work of the IPv6 fields (their tables were created and filled inside the same transaction): the
        // rollback leaves neither their tables nor their staging table behind.
        long ipv6Tables = Convert.ToInt64(
            DuckDbReadBack.Text(other, Invariant($"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{factory.TableOf("adp.ipv6")}'")),
            CultureInfo.InvariantCulture);

        writer.WriteBatch(batch);

        await Assert.That(failure.Message).Contains(Invariant($"The table {factory.TableOf("adp.uuid")} has 1 columns"));
        await Assert.That(ipv6Tables).IsEqualTo(0L);
        await Assert.That(rowsAfterFailure).IsEqualTo(0L);
        List<IPv6Address> rows = _IPv6Rows(other, factory.TableOf("adp.ipv6"), "rowid");

        await Assert.That(rows).IsEquivalentTo(packets.Select(static v => v.IPv6).ToList(), CollectionOrdering.Matching);
    }

    /// <summary>
    /// A batch that fails after an earlier batch was committed rolls back its own IPv6 rows and leaves the staging table empty,
    /// so the retry writes the rows of the failed batch once and the file holds all four rows in packet order.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AFailingBatchAfterACommittedOneLeavesTheStagingEmptyForTheRetry()
    {
        using TestDir dir = new("duckdb_address_rollback_old");
        using AddressProbePacketFactory factory = new();
        string path = dir.FilePath("rollback.duckdb");
        AddressValues[] first = [.. Enumerable.Range(0, 2).Select(_Distinct)];
        AddressValues[] second = [.. Enumerable.Range(2, 2).Select(_Distinct)];

        using DuckDbBulkWriter writer = new(path, factory.Content(ExportShape.Hierarchy), [], 0, 0, DuckDbStorageVersions.DefaultSettingValue);
        using DuckDBConnection other = _Open(path);
        using (ColumnarPacketBatch firstBatch = _Batch(factory, first))
        {
            writer.WriteBatch(firstBatch);
        }

        // Replace the UUID table behind the writer's back: it believes the table exists, so the second batch appends into the
        // wrong shape and fails after the IPv6 staging INSERT and DELETE ran in its transaction. The staging table of the first
        // batch already exists, so this is the rollback of rows in a table that survives (the first test covers a created one).
        string uuidTable = factory.TableOf("adp.uuid");
        DuckDbReadBack.Execute(other, Invariant($"ALTER TABLE {uuidTable} RENAME TO {uuidTable}_saved"));
        DuckDbReadBack.Execute(other, Invariant($"CREATE TABLE {uuidTable}(unrelated INTEGER)"));
        using ColumnarPacketBatch secondBatch = _Batch(factory, second);
        IndexOutOfRangeException failure = Assert.Throws<IndexOutOfRangeException>(() => writer.WriteBatch(secondBatch));
        DuckDbReadBack.Execute(other, Invariant($"DROP TABLE {uuidTable}"));
        DuckDbReadBack.Execute(other, Invariant($"ALTER TABLE {uuidTable}_saved RENAME TO {uuidTable}"));

        // The IPv6 rows of the failed batch were moved into their table before the failure and rolled back with it: only the
        // committed first batch is there, and the staging table is empty.
        await Assert.That(_IPv6Rows(other, factory.TableOf("adp.ipv6"), "rowid"))
            .IsEquivalentTo(first.Select(static v => v.IPv6).ToList(), CollectionOrdering.Matching);
        writer.WriteBatch(secondBatch);

        // Four rows in packet order and no duplicate: staged rows of the failed batch that were not rolled back would be inserted
        // again by the retry.
        await Assert.That(failure.Message).Contains(Invariant($"The table {factory.TableOf("adp.uuid")} has 1 columns"));
        await Assert.That(_IPv6Rows(other, factory.TableOf("adp.ipv6"), "rowid"))
            .IsEquivalentTo(first.Concat(second).Select(static v => v.IPv6).ToList(), CollectionOrdering.Matching);
    }

    /// <summary>
    /// A warm flush with six address fields (50 000 more values than a flush with one) allocates less than 64 KiB more, so the
    /// address loops create no object for each value.
    /// </summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task AWarmFlushOfFiftyThousandAddressValuesAllocatesNothingPerValue()
    {
        using TestDir dir = new("duckdb_address_alloc");

        // The flush of any batch allocates a base amount outside the address loops (the packet rows, the appender, the topology).
        // So the cost of the address loops is the difference between a flush that has one address field and a flush that has all six
        // (50 000 more values, 20 000 of them IPv6 through the staging table). Per-value garbage (a BigInteger, a byte array, a boxed
        // value) would show as at least 20 bytes per value, which is 1 MB here; the loops add none.
        long oneField = _WarmFlushAllocation(dir, ["adp.ipv4"]);
        long sixFields = _WarmFlushAllocation(dir, AddressProbePacketFactory.FieldNames);

        await Assert.That(sixFields - oneField).IsLessThan(64 * 1024).Because(Invariant($"one field {oneField} bytes, six fields {sixFields} bytes"));
    }

    /// <summary>
    /// Flushes a batch of 10 000 packets with the named fields once to warm the writer, then once more, and returns the bytes the second
    /// flush allocated on the calling thread (the exporter's inline write mode runs the flush on the caller's thread).
    /// </summary>
    /// <param name="dir">The directory for the database file.</param>
    /// <param name="names">The selected address fields.</param>
    /// <returns>The allocated bytes of the second flush.</returns>
    /// <exception cref="InvalidOperationException">The field selection cannot be created for the names.</exception>
    private static long _WarmFlushAllocation(TestDir dir, IReadOnlyList<string> names)
    {
        using AddressProbePacketFactory factory = new();
        FieldRequest[] requests = [.. names.Select(static name => new FieldRequest(name))];
        if (!FieldSelection.TryCreate(factory.Stack, requests, Occurrences.All, out FieldSelection? selection, out FieldSelectionError error))
        {
            throw new InvalidOperationException(error.Message);
        }

        ExportContent content = new() { Shape = ExportShape.SelectedFields, Fields = selection, Info = false, FrameBytes = false };
        using ColumnarPacketBatch batch = new(content, 100_000, 1L << 30, true);
        foreach (Packet packet in factory.CreateTreePackets([.. Enumerable.Range(0, 10_000).Select(_Distinct)]))
        {
            batch.AddPacket(packet);
        }

        using DuckDbBulkWriter writer = new(
            dir.FilePath(Invariant($"alloc_{names.Count}.duckdb")), content, [], 0, 0, DuckDbStorageVersions.DefaultSettingValue);

        // The first flush creates the tables and the appender state; the same batch is flushed again, so the second flush measures
        // the loops (and the base amount) only.
        writer.WriteBatch(batch);
        long before = GC.GetAllocatedBytesForCurrentThread();
        writer.WriteBatch(batch);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Fills a batch with the tree packets of the values.</summary>
    /// <param name="factory">The factory that builds the packets.</param>
    /// <param name="values">The address values of each packet.</param>
    /// <returns>The batch (hierarchy shape); the caller disposes it.</returns>
    private static ColumnarPacketBatch _Batch(AddressProbePacketFactory factory, IReadOnlyList<AddressValues> values)
    {
        ColumnarPacketBatch batch = new(factory.Content(ExportShape.Hierarchy), 100, 1024 * 1024, true);
        foreach (Packet packet in factory.CreateTreePackets(values))
        {
            batch.AddPacket(packet);
        }

        return batch;
    }

    /// <summary>
    /// Reads the IPv6 values of a field table the way a reader does: the high half is <c>value &gt;&gt; 64</c>, the low half the low 64 bits.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="table">The IPv6 field table.</param>
    /// <param name="orderBy">
    /// The sort expression: <c>value</c> for the address order, <c>rowid</c> for the order the rows are stored (the append order).
    /// </param>
    /// <returns>The values in that order.</returns>
    private static List<IPv6Address> _IPv6Rows(DuckDBConnection connection, string table, string orderBy)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText =
            Invariant($"SELECT CAST(value >> 64 AS UBIGINT), CAST(value & 18446744073709551615::UHUGEINT AS UBIGINT) FROM {table} ORDER BY {orderBy}");
        using DuckDBDataReader reader = command.ExecuteReader();
        List<IPv6Address> rows = [];
        while (reader.Read())
        {
            ulong high = Convert.ToUInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            ulong low = Convert.ToUInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            rows.Add(new IPv6Address(high, low));
        }

        return rows;
    }

    #endregion
}

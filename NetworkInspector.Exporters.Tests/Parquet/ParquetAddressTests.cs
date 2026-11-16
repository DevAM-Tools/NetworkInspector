// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using AddressValues = NetworkInspector.Exporters.Tests.Helpers.AddressProbePacketFactory.Values;

namespace NetworkInspector.Exporters.Tests.Parquet;

/// <summary>
/// The integer address columns of the Parquet exporter: IPv4 as UINT32, MAC and EUI-64 as UINT64, and IPv6 and UUID as the two UINT64
/// columns <c>value_hi</c> and <c>value_lo</c> (Parquet has no 128-bit integer). The schema is read with Parquet.Net; the values
/// are read back through DuckDB's <c>read_parquet</c>, the way an export reader would.
/// Slow: every test writes a Parquet dataset.
/// </summary>
/// <remarks>Sync: NetworkInspector.Exporters/EXPORTER_GUIDE.md § Rebuilding the display line (integer address forms).</remarks>
[Category("Slow")]
internal sealed class ParquetAddressTests
{
    #region Writing and reading back a dataset

    /// <summary>Exports the packets of a factory to a Parquet dataset.</summary>
    /// <param name="output">The builder with its output already set (a directory or a target).</param>
    /// <param name="factory">The factory that built the packets (and owns their stack).</param>
    /// <param name="values">The address values of each packet, in packet order.</param>
    /// <param name="maxPacketsPerBlock">The batch size in packets (one row group per batch).</param>
    private static void _Export(
        ParquetExporter.Builder output, AddressProbePacketFactory factory, IReadOnlyList<AddressValues> values, int maxPacketsPerBlock = 50_000)
    {
        using ParquetExporter exporter = output
            .WithContent(factory.Content(ExportShape.Hierarchy))
            .WithWriteMode(ExportWriteMode.Synchronous)
            .WithMaxPacketsPerBlock(maxPacketsPerBlock)
            .Build();
        foreach (Packet packet in factory.CreateTreePackets(values))
        {
            _ = exporter.OnPacket(packet);
        }

        exporter.OnFinish();
    }

    /// <summary>Exports the packets of a factory to a Parquet dataset in a directory.</summary>
    /// <param name="directory">The target directory.</param>
    /// <param name="factory">The factory that built the packets (and owns their stack).</param>
    /// <param name="values">The address values of each packet, in packet order.</param>
    /// <param name="maxPacketsPerBlock">The batch size in packets (one row group per batch).</param>
    private static void _Export(string directory, AddressProbePacketFactory factory, IReadOnlyList<AddressValues> values, int maxPacketsPerBlock = 50_000) =>
        _Export(ParquetExporter.CreateBuilder().ToDirectory(directory), factory, values, maxPacketsPerBlock);

    /// <summary>The path of the Parquet file of an address field.</summary>
    /// <param name="directory">The dataset directory.</param>
    /// <param name="factory">The factory that names the field ids.</param>
    /// <param name="name">The field name, for example <c>adp.mac</c>.</param>
    /// <returns>The file path.</returns>
    private static string _File(string directory, AddressProbePacketFactory factory, string name) =>
        Path.Combine(directory, "fields", factory.TableOf(name) + ".parquet");

    /// <summary>The columns of the Parquet file of an address field in memory, with the CLR type Parquet.Net maps each to.</summary>
    /// <param name="target">The target after the export.</param>
    /// <param name="factory">The factory that names the field ids.</param>
    /// <param name="name">The field name, for example <c>adp.mac</c>.</param>
    /// <returns>Column name to CLR type.</returns>
    private static async Task<Dictionary<string, Type>> _Columns(MemoryExportTarget target, AddressProbePacketFactory factory, string name)
    {
        using MemoryStream stream = new(target.Bytes("fields/" + factory.TableOf(name) + ".parquet"));
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        return reader.Schema.GetDataFields().ToDictionary(static field => field.Name, static field => field.ClrType);
    }

    /// <summary>Runs a query over a Parquet file in an in-memory DuckDB and returns the first column of every row as text.</summary>
    /// <param name="select">The select list and clauses after <c>FROM</c>, with <c>{file}</c> standing for the <c>read_parquet</c> call.</param>
    /// <param name="path">The Parquet file.</param>
    /// <returns>The first column of each row as text, in result order.</returns>
    private static List<string> _Query(string select, string path)
    {
        using DuckDBConnection connection = new("Data Source=:memory:");
        connection.Open();
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = select.Replace("{file}", Invariant($"read_parquet('{path}')"), StringComparison.Ordinal);
        using DuckDBDataReader reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            rows.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
        }

        return rows;
    }

    #endregion

    #region Schema of the address files

    /// <summary>
    /// The files of the address fields have the documented integer columns: a single <c>value</c> column for IPv4, MAC and EUI-64,
    /// and the two UINT64 columns <c>value_hi</c> and <c>value_lo</c> without a <c>value</c> column for IPv6 and UUID.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AddressFilesHaveTheDocumentedIntegerColumns()
    {
        using AddressProbePacketFactory factory = new();
        MemoryExportTarget target = new(seekable: true);

        _Export(ParquetExporter.CreateBuilder().ToTarget(target), factory, [AddressProbePacketFactory.Readable]);

        Dictionary<string, Type> ipv4 = await _Columns(target, factory, "adp.ipv4");
        Dictionary<string, Type> mac = await _Columns(target, factory, "adp.mac");
        Dictionary<string, Type> eui64 = await _Columns(target, factory, "adp.eui64");
        Dictionary<string, Type> ipv6 = await _Columns(target, factory, "adp.ipv6");
        Dictionary<string, Type> uuid = await _Columns(target, factory, "adp.uuid");

        await Assert.That(ipv4["value"]).IsEqualTo(typeof(uint));
        await Assert.That(mac["value"]).IsEqualTo(typeof(ulong));
        await Assert.That(eui64["value"]).IsEqualTo(typeof(ulong));

        // The 128-bit types have two UINT64 columns and no value column; a reader that looked for "value" finds nothing instead of half an address.
        await Assert.That(ipv6.ContainsKey("value")).IsFalse();
        await Assert.That(ipv6["value_hi"]).IsEqualTo(typeof(ulong));
        await Assert.That(ipv6["value_lo"]).IsEqualTo(typeof(ulong));
        await Assert.That(uuid.ContainsKey("value")).IsFalse();
        await Assert.That(uuid["value_hi"]).IsEqualTo(typeof(ulong));
        await Assert.That(uuid["value_lo"]).IsEqualTo(typeof(ulong));
    }

    /// <summary>DuckDB reads the address columns as unsigned integers (UINTEGER for IPv4, UBIGINT for the 64-bit columns).</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task DuckDbReadsTheAddressColumnsAsUnsignedIntegers()
    {
        using TestDir dir = new("parquet_address_types");
        using AddressProbePacketFactory factory = new();

        _Export(dir.DirectoryPath, factory, [AddressProbePacketFactory.Readable]);

        await Assert.That(_Query("SELECT typeof(value) FROM {file}", _File(dir.DirectoryPath, factory, "adp.ipv4"))[0]).IsEqualTo("UINTEGER");
        await Assert.That(_Query("SELECT typeof(value) FROM {file}", _File(dir.DirectoryPath, factory, "adp.mac"))[0]).IsEqualTo("UBIGINT");
        await Assert.That(_Query("SELECT typeof(value) FROM {file}", _File(dir.DirectoryPath, factory, "adp.eui64"))[0]).IsEqualTo("UBIGINT");
        await Assert.That(_Query("SELECT typeof(value_hi) FROM {file}", _File(dir.DirectoryPath, factory, "adp.ipv6"))[0]).IsEqualTo("UBIGINT");
        await Assert.That(_Query("SELECT typeof(value_lo) FROM {file}", _File(dir.DirectoryPath, factory, "adp.uuid"))[0]).IsEqualTo("UBIGINT");
    }

    #endregion

    #region Stored values

    /// <summary>
    /// Each address type stores the integer that its bytes form: the 32-bit value for IPv4, the raw value for MAC and EUI-64,
    /// and the high and low halves for IPv6 and UUID.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task EveryAddressTypeStoresTheIntegerOfItsBytes()
    {
        using TestDir dir = new("parquet_address_values");
        using AddressProbePacketFactory factory = new();
        AddressValues values = AddressProbePacketFactory.Readable;

        _Export(dir.DirectoryPath, factory, [values]);

        await Assert.That(_Query("SELECT value FROM {file}", _File(dir.DirectoryPath, factory, "adp.ipv4"))[0]).IsEqualTo("3232235777"); // 0xC0A80101
        await Assert.That(_Query("SELECT value FROM {file}", _File(dir.DirectoryPath, factory, "adp.mac"))[0])
            .IsEqualTo(values.Mac.RawValue.ToString(CultureInfo.InvariantCulture));
        await Assert.That(_Query("SELECT value FROM {file}", _File(dir.DirectoryPath, factory, "adp.eui64"))[0])
            .IsEqualTo(values.Eui64.RawValue.ToString(CultureInfo.InvariantCulture));
        await Assert.That(_Query("SELECT value_hi FROM {file}", _File(dir.DirectoryPath, factory, "adp.ipv6"))[0])
            .IsEqualTo(values.IPv6.High.ToString(CultureInfo.InvariantCulture));
        await Assert.That(_Query("SELECT value_lo FROM {file}", _File(dir.DirectoryPath, factory, "adp.ipv6"))[0])
            .IsEqualTo(values.IPv6.Low.ToString(CultureInfo.InvariantCulture));
        await Assert.That(_Query("SELECT value_hi FROM {file}", _File(dir.DirectoryPath, factory, "adp.uuid"))[0])
            .IsEqualTo(values.Uuid.High.ToString(CultureInfo.InvariantCulture));
        await Assert.That(_Query("SELECT value_lo FROM {file}", _File(dir.DirectoryPath, factory, "adp.uuid"))[0])
            .IsEqualTo(values.Uuid.Low.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Boundary addresses written across several row groups come back unchanged, sort in the order of the address value types
    /// when read by the stored integers, and keep the packet order of the feed in the file.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task BoundaryAddressesRoundTripAndSortInAddressOrderAcrossRowGroups()
    {
        using TestDir dir = new("parquet_address_order");
        using AddressProbePacketFactory factory = new();
        AddressValues[] packets = AddressProbePacketFactory.ShuffledBoundaries();

        // Two packets per batch gives three row groups per file, so the pooled columns of the address types are used more than once.
        _Export(dir.DirectoryPath, factory, packets, maxPacketsPerBlock: 2);

        List<string> ipv4 = _Query("SELECT value FROM {file} ORDER BY value", _File(dir.DirectoryPath, factory, "adp.ipv4"));
        List<string> mac = _Query("SELECT value FROM {file} ORDER BY value", _File(dir.DirectoryPath, factory, "adp.mac"));
        List<string> eui64 = _Query("SELECT value FROM {file} ORDER BY value", _File(dir.DirectoryPath, factory, "adp.eui64"));
        List<string> ipv6High = _Query("SELECT value_hi FROM {file} ORDER BY value_hi, value_lo", _File(dir.DirectoryPath, factory, "adp.ipv6"));
        List<string> ipv6Low = _Query("SELECT value_lo FROM {file} ORDER BY value_hi, value_lo", _File(dir.DirectoryPath, factory, "adp.ipv6"));
        List<string> inFileOrder = _Query("SELECT value FROM {file}", _File(dir.DirectoryPath, factory, "adp.ipv4"));

        // No value is lost or changed, and the unsigned order puts 0x80000000 and above after 0x7FFFFFFF (a signed column would not).
        AddressValues[] byIPv4 = [.. packets.OrderBy(static v => v.IPv4)];
        AddressValues[] byMac = [.. packets.OrderBy(static v => v.Mac)];
        AddressValues[] byEui64 = [.. packets.OrderBy(static v => v.Eui64)];
        await Assert.That(ipv4).IsEquivalentTo(AddressProbePacketFactory.DecimalOf(byIPv4, static v => v.IPv4.RawValue), CollectionOrdering.Matching);
        await Assert.That(mac).IsEquivalentTo(AddressProbePacketFactory.DecimalOf(byMac, static v => v.Mac.RawValue), CollectionOrdering.Matching);
        await Assert.That(eui64).IsEquivalentTo(AddressProbePacketFactory.DecimalOf(byEui64, static v => v.Eui64.RawValue), CollectionOrdering.Matching);

        // The oracle of the 128-bit types is the comparison of the value types themselves (the address order), not a sort by the
        // stored halves: ORDER BY value_hi, value_lo has to give the order that IPv6Address and Uuid define.
        await Assert.That(ipv6High.Zip(ipv6Low, static (high, low) => high + ":" + low))
            .IsEquivalentTo(
                packets.OrderBy(static v => v.IPv6)
                    .Select(static v => v.IPv6.High.ToString(CultureInfo.InvariantCulture) + ":" + v.IPv6.Low.ToString(CultureInfo.InvariantCulture)),
                CollectionOrdering.Matching);
        List<string> uuidHigh = _Query("SELECT value_hi FROM {file} ORDER BY value_hi, value_lo", _File(dir.DirectoryPath, factory, "adp.uuid"));
        List<string> uuidLow = _Query("SELECT value_lo FROM {file} ORDER BY value_hi, value_lo", _File(dir.DirectoryPath, factory, "adp.uuid"));
        await Assert.That(uuidHigh.Zip(uuidLow, static (high, low) => high + ":" + low))
            .IsEquivalentTo(
                packets.OrderBy(static v => v.Uuid)
                    .Select(static v => v.Uuid.High.ToString(CultureInfo.InvariantCulture) + ":" + v.Uuid.Low.ToString(CultureInfo.InvariantCulture)),
                CollectionOrdering.Matching);

        // The boundary UUIDs themselves come back: all zeros, all ones, and the top bit alone (not changed by a signed column).
        await Assert.That(uuidHigh[0]).IsEqualTo("0");
        await Assert.That(uuidLow[0]).IsEqualTo("0");
        await Assert.That(uuidHigh[^1]).IsEqualTo(ulong.MaxValue.ToString(CultureInfo.InvariantCulture));
        await Assert.That(uuidLow[^1]).IsEqualTo(ulong.MaxValue.ToString(CultureInfo.InvariantCulture));
        await Assert.That(uuidHigh).Contains(0x8000_0000_0000_0000UL.ToString(CultureInfo.InvariantCulture));

        // The file keeps the packet order of the feed.
        await Assert.That(inFileOrder).IsEquivalentTo(AddressProbePacketFactory.DecimalOf(packets, static v => v.IPv4.RawValue), CollectionOrdering.Matching);
    }

    #endregion
}

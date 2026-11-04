// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using CompressionLevel = System.IO.Compression.CompressionLevel;
using ParquetCompressionMethod = Parquet.CompressionMethod;

namespace NetworkInspector.Exporters.Tests.Parquet;

/// <summary>
/// The Parquet compression of the exporter: the Try mappings of the codec and the level, the default, the refusal of an undefined
/// value, and the files a codec produces, read back the way a reader sees them (<c>parquet_metadata()</c> of DuckDB reports the codec
/// of every column chunk of every file). Tests that write a dataset carry the slow marker; the mapping and builder tests are plain
/// unit tests.
/// </summary>
internal sealed class ParquetCompressionTests
{
    #region Writing and reading back a dataset

    /// <summary>The codec names DuckDB reports, by exporter codec.</summary>
    /// <param name="compression">The exporter codec.</param>
    /// <returns>The codec name that DuckDB reports for column chunks written with the codec.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a codec of the exporter.</exception>
    private static string _Reported(ParquetCompression compression) =>
        compression switch
        {
            ParquetCompression.None => "UNCOMPRESSED",
            ParquetCompression.Snappy => "SNAPPY",
            ParquetCompression.Gzip => "GZIP",
            ParquetCompression.Brotli => "BROTLI",
            ParquetCompression.Lz4 => "LZ4_RAW",
            ParquetCompression.Zstd => "ZSTD",
            _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "Not a codec of the test."),
        };

    /// <summary>
    /// Writes a dataset that has every file kind: packets with frames, buffers (a packet with two additional buffers), topology
    /// (the hierarchy shape), catalog, protocols, export info, and at least one field file.
    /// </summary>
    /// <param name="directory">The dataset directory.</param>
    /// <param name="configure">Applies the options under test to the builder; <see langword="null"/> keeps the defaults.</param>
    private static void _WriteDataset(string directory, Func<ParquetExporter.Builder, ParquetExporter.Builder>? configure)
    {
        using BufferProbePacketFactory factory = new();
        ParquetExporter.Builder builder = ParquetExporter.CreateBuilder()
            .ToDirectory(directory)
            .WithWriteMode(ExportWriteMode.Synchronous)
            .WithContent(new ExportContent { FrameBytes = true, Buffers = true });
        using ParquetExporter exporter = (configure?.Invoke(builder) ?? builder).Build();
        foreach (Packet packet in new[]
        {
            factory.CreatePacket([1, 2, 3], [4, 5]),
            factory.CreatePacket(),
            factory.CreatePacket([6]),
        })
        {
            _ = exporter.OnPacket(packet);
        }

        exporter.OnFinish();
    }

    /// <summary>
    /// Reads the codec of every column chunk of every Parquet file below the directory through DuckDB.
    /// </summary>
    /// <param name="directory">The dataset directory.</param>
    /// <returns>The distinct codecs per file name (relative to the directory, with forward slashes).</returns>
    private static Dictionary<string, SortedSet<string>> _Codecs(string directory)
    {
        string[] files = Directory.GetFiles(directory, "*.parquet", SearchOption.AllDirectories);
        string list = string.Join(", ", files.Select(static file => "'" + file.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal) + "'"));
        Dictionary<string, SortedSet<string>> codecs = new(StringComparer.Ordinal);
        using DuckDBConnection connection = new("Data Source=:memory:");
        connection.Open();
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = Invariant($"SELECT file_name, compression FROM parquet_metadata([{list}])");
        using DuckDBDataReader reader = command.ExecuteReader();
        string root = directory.Replace('\\', '/').TrimEnd('/') + "/";
        while (reader.Read())
        {
            string name = reader.GetString(0).Replace('\\', '/');
            name = name.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? name[root.Length..]
                : name;
            if (!codecs.TryGetValue(name, out SortedSet<string>? set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                codecs[name] = set;
            }

            _ = set.Add(reader.GetString(1));
        }

        return codecs;
    }

    /// <summary>Counts the rows of the packets file through DuckDB (proves the dataset is readable).</summary>
    /// <param name="directory">The dataset directory.</param>
    /// <returns>The number of rows in <c>packets.parquet</c>.</returns>
    private static long _PacketRows(string directory)
    {
        using DuckDBConnection connection = new("Data Source=:memory:");
        connection.Open();
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = Invariant($"SELECT COUNT(*) FROM read_parquet('{Path.Combine(directory, "packets.parquet").Replace('\\', '/')}')");
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Asserts that the dataset has every file kind the test promises and that every column chunk of every file has the codec.
    /// </summary>
    /// <param name="directory">The dataset directory.</param>
    /// <param name="expected">The codec DuckDB must report for every column chunk.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    private static async Task _AssertEveryFileHas(string directory, string expected)
    {
        Dictionary<string, SortedSet<string>> codecs = _Codecs(directory);

        // Every writer of the dataset is in the check: the table files, the buffer file, the three one-shot files, and a field file.
        string[] requiredFiles =
        [
            "packets.parquet", "packet_buffers.parquet", "topology.parquet", "catalog.parquet", "protocols.parquet", "export_info.parquet",
        ];
        foreach (string required in requiredFiles)
        {
            await Assert.That(codecs.ContainsKey(required)).IsTrue().Because(Invariant($"{required} is part of the dataset"));
        }

        await Assert.That(codecs.Keys.Any(static name => name.StartsWith("fields/field_", StringComparison.Ordinal))).IsTrue();
        foreach (KeyValuePair<string, SortedSet<string>> file in codecs)
        {
            await Assert.That(string.Join(",", file.Value)).IsEqualTo(expected).Because(Invariant($"{file.Key} must have only the codec {expected}"));
        }
    }

    #endregion

    #region Choosing a codec and a level

    /// <summary>Each codec is written to every file of the dataset and DuckDB reports it on every column chunk.</summary>
    /// <param name="compression">The codec the builder is set to.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Category("Slow")]
    [Arguments(ParquetCompression.None)]
    [Arguments(ParquetCompression.Snappy)]
    [Arguments(ParquetCompression.Gzip)]
    [Arguments(ParquetCompression.Brotli)]
    [Arguments(ParquetCompression.Lz4)]
    [Arguments(ParquetCompression.Zstd)]
    public async Task EveryCodecIsWrittenToEveryFileOfTheDataset(ParquetCompression compression)
    {
        using TestDir dir = new("parquet_compression_select");
        string directory = dir.FilePath("dataset");

        _WriteDataset(directory, builder => builder.WithCompression(compression));

        await _AssertEveryFileHas(directory, _Reported(compression));
        await Assert.That(_PacketRows(directory)).IsEqualTo(3);
    }

    /// <summary>Each level is accepted with zstd and gives a readable dataset whose files all report zstd.</summary>
    /// <param name="level">The compression level the builder is set to.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Category("Slow")]
    [Arguments(ParquetCompressionLevel.Fastest)]
    [Arguments(ParquetCompressionLevel.Optimal)]
    [Arguments(ParquetCompressionLevel.Smallest)]
    public async Task EveryLevelGivesAReadableZstdDataset(ParquetCompressionLevel level)
    {
        using TestDir dir = new("parquet_compression_level");
        string directory = dir.FilePath("dataset");

        _WriteDataset(directory, builder => builder.WithCompression(ParquetCompression.Zstd, level));

        await _AssertEveryFileHas(directory, "ZSTD");
        await Assert.That(_PacketRows(directory)).IsEqualTo(3);
    }

    /// <summary>The codec mapping gives the Parquet.Net codec; LZ4 maps to the raw LZ4 codec.</summary>
    /// <param name="compression">The exporter codec.</param>
    /// <param name="expected">The Parquet.Net codec the mapping must give.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Arguments(ParquetCompression.None, ParquetCompressionMethod.None)]
    [Arguments(ParquetCompression.Snappy, ParquetCompressionMethod.Snappy)]
    [Arguments(ParquetCompression.Gzip, ParquetCompressionMethod.Gzip)]
    [Arguments(ParquetCompression.Brotli, ParquetCompressionMethod.Brotli)]
    [Arguments(ParquetCompression.Lz4, ParquetCompressionMethod.Lz4Raw)]
    [Arguments(ParquetCompression.Zstd, ParquetCompressionMethod.Zstd)]
    public async Task TheCodecMappingGivesTheParquetNetCodec(ParquetCompression compression, ParquetCompressionMethod expected)
    {
        bool mapped = compression.TryGetMethod(out ParquetCompressionMethod method);

        await Assert.That(mapped).IsTrue();
        await Assert.That(method).IsEqualTo(expected);
    }

    /// <summary>The level mapping gives the .NET levels; the smallest level maps to the smallest size.</summary>
    /// <param name="level">The exporter level.</param>
    /// <param name="expected">The .NET level the mapping must give.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Arguments(ParquetCompressionLevel.Fastest, CompressionLevel.Fastest)]
    [Arguments(ParquetCompressionLevel.Optimal, CompressionLevel.Optimal)]
    [Arguments(ParquetCompressionLevel.Smallest, CompressionLevel.SmallestSize)]
    public async Task TheLevelMappingGivesTheDotNetLevel(ParquetCompressionLevel level, CompressionLevel expected)
    {
        bool mapped = level.TryGetLevel(out CompressionLevel result);

        await Assert.That(mapped).IsTrue();
        await Assert.That(result).IsEqualTo(expected);
    }

    #endregion

    #region Defaults

    /// <summary>A builder without <c>WithCompression</c> writes zstd on every column chunk of every file (set, not the library default).</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Category("Slow")]
    public async Task ABuilderWithoutACodecWritesZstdInEveryFile()
    {
        using TestDir dir = new("parquet_compression_default");
        string directory = dir.FilePath("dataset");

        _WriteDataset(directory, null);

        await _AssertEveryFileHas(directory, "ZSTD");
    }

    /// <summary>The public default constants and the builder's initial mapping name the same codec and level.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task TheDefaultConstantsAndTheBuilderDefaultsAgree()
    {
        // Copies of the constants: the assertion library refuses a constant as its subject.
        ParquetCompression defaultCompression = ParquetExporter.DefaultCompression;
        ParquetCompressionLevel defaultLevel = ParquetExporter.DefaultCompressionLevel;

        await Assert.That(defaultCompression).IsEqualTo(ParquetCompression.Zstd);
        await Assert.That(defaultLevel).IsEqualTo(ParquetCompressionLevel.Optimal);
        await Assert.That(defaultCompression.TryGetMethod(out ParquetCompressionMethod method)).IsTrue();
        await Assert.That(method).IsEqualTo(ParquetCompressions.DefaultMethod);
        await Assert.That(defaultLevel.TryGetLevel(out CompressionLevel level)).IsTrue();
        await Assert.That(level).IsEqualTo(ParquetCompressions.DefaultLevel);
    }

    #endregion

    #region Undefined values and refusals

    /// <summary>The codec mapping accepts each defined value and refuses an undefined one.</summary>
    /// <param name="value">The numeric value that is cast to <see cref="ParquetCompression"/>.</param>
    /// <param name="defined">Whether the value is a defined member of the enum.</param>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    [Arguments(0, true)]
    [Arguments(5, true)]
    [Arguments(6, false)]
    [Arguments(99, false)]
    [Arguments(-1, false)]
    public async Task TheCodecMappingAcceptsDefinedValuesAndRefusesUndefinedOnes(int value, bool defined)
    {
        bool mapped = ((ParquetCompression)value).TryGetMethod(out ParquetCompressionMethod _);

        await Assert.That(mapped).IsEqualTo(defined);
    }

    /// <summary>The level mapping accepts each defined value and refuses an undefined one.</summary>
    /// <param name="value">The numeric value that is cast to <see cref="ParquetCompressionLevel"/>.</param>
    /// <param name="defined">Whether the value is a defined member of the enum.</param>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    [Arguments(0, true)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(99, false)]
    [Arguments(-1, false)]
    public async Task TheLevelMappingAcceptsDefinedValuesAndRefusesUndefinedOnes(int value, bool defined)
    {
        bool mapped = ((ParquetCompressionLevel)value).TryGetLevel(out CompressionLevel _);

        await Assert.That(mapped).IsEqualTo(defined);
    }

    /// <summary>The refusal texts name exactly the defined members, in enum order, in lower case as the plugin options spell them.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task TheAllowedTextsListTheDefinedMembers()
    {
        string codecs = string.Join(", ", Enum.GetValues<ParquetCompression>().Select(static value => value.ToString().ToLowerInvariant()));
        string levels = string.Join(", ", Enum.GetValues<ParquetCompressionLevel>().Select(static value => value.ToString().ToLowerInvariant()));

        await Assert.That(ParquetCompressions.AllowedCompressionText).IsEqualTo(codecs);
        await Assert.That(ParquetCompressions.AllowedLevelText).IsEqualTo(levels);
    }

    /// <summary>
    /// An undefined codec or level is a bug of the caller of the public builder: it throws <see cref="ArgumentOutOfRangeException"/>
    /// that names the parameter and lists the allowed values, and the refused call changes nothing (the dataset is not created).
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task AnUndefinedCodecOrLevelThrowsAndListsTheAllowedValues()
    {
        using TestDir dir = new("parquet_compression_invalid");
        string directory = dir.FilePath("dataset");
        ParquetExporter.Builder builder = ParquetExporter.CreateBuilder().ToDirectory(directory);

        ArgumentOutOfRangeException codec = Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.WithCompression((ParquetCompression)99));
        ArgumentOutOfRangeException level = Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.WithCompression(ParquetCompression.Gzip, (ParquetCompressionLevel)99));

        await Assert.That(codec.ParamName).IsEqualTo("compression");
        await Assert.That(codec.Message).Contains("none, snappy, gzip, brotli, lz4, zstd");
        await Assert.That(level.ParamName).IsEqualTo("level");
        await Assert.That(level.Message).Contains("fastest, optimal, smallest");

        // No dataset is created by building or by a refused call; the builder builds normally.
        using ParquetExporter exporter = builder.Build();
        await Assert.That(Directory.Exists(directory)).IsFalse();
    }

    /// <summary>
    /// A refused level does not leave the codec of the same call behind: the call is all or nothing, so an export after the
    /// refusal writes the default zstd.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Category("Slow")]
    public async Task ARefusedCallKeepsThePreviousChoice()
    {
        using TestDir dir = new("parquet_compression_atomic");
        string directory = dir.FilePath("dataset");

        _WriteDataset(directory, builder =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithCompression(ParquetCompression.Gzip, (ParquetCompressionLevel)99));
            return builder;
        });

        await _AssertEveryFileHas(directory, "ZSTD");
    }

    #endregion
}

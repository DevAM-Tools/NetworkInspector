// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.DuckDb;

/// <summary>
/// The DuckDB storage version of the exporter: the mapping of <see cref="DuckDbStorageVersion"/> to DuckDB's
/// <c>storage_compatibility_version</c> text, the default, the refusal of an undefined value, and the file a version produces,
/// read back the way a reader sees it (the storage version tag DuckDB reports for the attached database). Tests that write a file
/// carry the slow marker; the mapping and builder tests are plain unit tests.
/// </summary>
internal sealed class DuckDbStorageVersionTests
{
    #region Writing and reading back a file

    /// <summary>The six setting texts in enum order.</summary>
    private static readonly string[] _SettingTexts = ["v1.0.0", "v1.1.0", "v1.2.0", "v1.3.0", "v1.4.0", "v1.5.0"];

    /// <summary>Writes the packets to a file in synchronous mode (no writer thread, the file is complete when the call returns).</summary>
    /// <param name="path">The target file.</param>
    /// <param name="configure">Applies the options under test to the builder; <see langword="null"/> keeps the defaults.</param>
    /// <param name="packetCount">The number of packets to export.</param>
    /// <param name="maxPacketsPerBlock">The batch size in packets.</param>
    private static void _Export(string path, Func<DuckDbExporter.Builder, DuckDbExporter.Builder>? configure, int packetCount = 1,
        int maxPacketsPerBlock = 50_000)
    {
        DuckDbExporter.Builder builder = DuckDbExporter.CreateBuilder()
            .ToFile(path)
            .WithWriteMode(ExportWriteMode.Synchronous)
            .WithMaxPacketsPerBlock(maxPacketsPerBlock);
        using DuckDbExporter exporter = (configure?.Invoke(builder) ?? builder).Build();
        foreach (Packet packet in PacketGenerators.CreateEthernetUdpPackets(packetCount))
        {
            if (!exporter.OnPacket(packet))
            {
                throw new InvalidOperationException("The DuckDB exporter refused a packet.");
            }
        }

        exporter.OnFinish();
    }

    /// <summary>
    /// Reads the storage version DuckDB reports for the file: the <c>storage_version</c> tag of the attached database, which is
    /// the oldest storage version that has the format of the file, followed by <c>+</c> (DuckDB 1.5: <c>v1.2.0+</c> means "the
    /// format of v1.2.0 or a newer one").
    /// </summary>
    /// <param name="path">The finished <c>.duckdb</c> file.</param>
    /// <returns>The reported tag text.</returns>
    private static string _ReportedVersion(string path)
    {
        using DuckDBConnection connection = new(Invariant($"Data Source={path};access_mode=READ_ONLY"));
        connection.Open();
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = "SELECT tags['storage_version'] FROM duckdb_databases() WHERE NOT internal";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!; // Invariant: the file is the only user database.
    }

    /// <summary>Counts the rows of a table of a finished file, opened read-only.</summary>
    /// <param name="path">The finished <c>.duckdb</c> file.</param>
    /// <param name="table">The table name.</param>
    /// <returns>The number of rows in the table.</returns>
    private static long _Count(string path, string table)
    {
        using DuckDBConnection connection = new(Invariant($"Data Source={path};access_mode=READ_ONLY"));
        connection.Open();
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = Invariant($"SELECT COUNT(*) FROM {table}");
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    #endregion

    #region Choosing a version

    /// <summary>
    /// Each version is written and DuckDB reports it for the file. DuckDB names the oldest version that has the same file format:
    /// v1.0.0 and v1.1.0 share one format (the report is <c>v1.0.0+</c> for both), every later version has a format of its own.
    /// A connection keyword that DuckDB ignored would give the same report for every version, so this test fails then.
    /// </summary>
    /// <param name="version">The storage version the builder is set to.</param>
    /// <param name="reported">The tag text DuckDB reports for the written file.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Category("Slow")]
    [Arguments(DuckDbStorageVersion.V1Dot0, "v1.0.0+")]
    [Arguments(DuckDbStorageVersion.V1Dot1, "v1.0.0+")]
    [Arguments(DuckDbStorageVersion.V1Dot2, "v1.2.0+")]
    [Arguments(DuckDbStorageVersion.V1Dot3, "v1.3.0+")]
    [Arguments(DuckDbStorageVersion.V1Dot4, "v1.4.0+")]
    [Arguments(DuckDbStorageVersion.V1Dot5, "v1.5.0+")]
    public async Task EveryVersionWritesAFileThatDuckDbReportsWithItsFormat(DuckDbStorageVersion version, string reported)
    {
        using TestDir dir = new("duckdb_storage_select");
        string path = dir.FilePath("capture.duckdb");

        _Export(path, builder => builder.WithStorageVersion(version));

        await Assert.That(_ReportedVersion(path)).IsEqualTo(reported);
        await Assert.That(_Count(path, "packets")).IsEqualTo(1);
    }

    /// <summary>The setting texts of the six versions are the documented ones, one per member, in enum order.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task TheSettingTextOfEveryVersionIsTheDocumentedOne()
    {
        DuckDbStorageVersion[] versions = Enum.GetValues<DuckDbStorageVersion>();

        await Assert.That(versions.Length).IsEqualTo(_SettingTexts.Length);
        for (int index = 0; index < versions.Length; index++)
        {
            await Assert.That(versions[index].TryGetSettingValue(out string text)).IsTrue();
            await Assert.That(text).IsEqualTo(_SettingTexts[index]);
        }

        // The refusal messages name exactly these six texts, in the same order.
        await Assert.That(DuckDbStorageVersions.AllowedText).IsEqualTo(string.Join(", ", _SettingTexts));
    }

    #endregion

    #region Default version

    /// <summary>A builder without <c>WithStorageVersion</c> writes <c>v1.5.0</c>, set explicitly and not taken from the library.</summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    [Category("Slow")]
    public async Task ABuilderWithoutAVersionWritesV150()
    {
        using TestDir dir = new("duckdb_storage_default");
        string path = dir.FilePath("capture.duckdb");

        _Export(path, null);

        await Assert.That(_ReportedVersion(path)).IsEqualTo("v1.5.0+");
    }

    /// <summary>The public default constant and the builder's initial text name the same version.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task TheDefaultConstantAndTheBuilderDefaultTextAgree()
    {
        // A copy of the constant: the assertion library refuses a constant as its subject.
        DuckDbStorageVersion defaultVersion = DuckDbExporter.DefaultStorageVersion;

        await Assert.That(defaultVersion).IsEqualTo(DuckDbStorageVersion.V1Dot5);
        await Assert.That(defaultVersion.TryGetSettingValue(out string text)).IsTrue();
        await Assert.That(text).IsEqualTo(DuckDbStorageVersions.DefaultSettingValue);
        await Assert.That(text).IsEqualTo("v1.5.0");
    }

    #endregion

    #region Undefined versions

    /// <summary>The Try mapping accepts each defined value and refuses an undefined one with an empty text.</summary>
    /// <param name="value">The numeric value that is cast to <see cref="DuckDbStorageVersion"/>.</param>
    /// <param name="defined">Whether the value is a defined member of the enum.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Arguments(0, true)]
    [Arguments(5, true)]
    [Arguments(6, false)]
    [Arguments(99, false)]
    [Arguments(-1, false)]
    public async Task TheTryMappingAcceptsDefinedValuesAndRefusesUndefinedOnes(int value, bool defined)
    {
        bool mapped = ((DuckDbStorageVersion)value).TryGetSettingValue(out string text);

        await Assert.That(mapped).IsEqualTo(defined);
        await Assert.That(text.Length > 0).IsEqualTo(defined);
    }

    /// <summary>
    /// An undefined enum value is a bug of the caller of the public builder: it throws <see cref="ArgumentOutOfRangeException"/>
    /// whose message lists the six versions, and the builder keeps its previous choice.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Category("Slow")]
    public async Task AnUndefinedVersionThrowsAndListsTheSixVersions()
    {
        using TestDir dir = new("duckdb_storage_invalid");
        string path = dir.FilePath("never-written.duckdb");
        DuckDbExporter.Builder builder = DuckDbExporter.CreateBuilder().ToFile(path);

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.WithStorageVersion((DuckDbStorageVersion)99));

        await Assert.That(exception.ParamName).IsEqualTo("version");
        foreach (string text in _SettingTexts)
        {
            await Assert.That(exception.Message).Contains(text);
        }

        // The refused call changed nothing: the builder keeps its previous choice (the default), so the exporter builds without
        // creating a file before the first packet and the file it then writes reports the default storage version.
        using (DuckDbExporter exporter = builder.Build())
        {
            await Assert.That(File.Exists(path)).IsFalse();
            await Assert.That(exporter.OnPacket(PacketGenerators.CreateEthernetUdpPackets(1)[0])).IsTrue();
            exporter.OnFinish();
        }

        await Assert.That(_ReportedVersion(path)).IsEqualTo("v1.5.0+");
    }

    #endregion

    #region Finished file without a copy pass

    /// <summary>
    /// The export of two batches ends with a complete file and no second pass: the file opens read-only (which DuckDB refuses while a
    /// write-ahead log is pending, so the final checkpoint ran), the directory holds nothing but the target file (no <c>.wal</c>
    /// sidecar and no temporary copy that a copy-and-replace compaction would leave or rename), and the tables hold every row. The
    /// check is on what the exporter leaves behind, not on a file time: a creation time survives a delete and create under the same
    /// name on NTFS, and file systems without a birth time report a time that changes with every write.
    /// </summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Category("Slow")]
    public async Task TheExportEndsAfterTheCheckpointWithoutACopyPass()
    {
        using TestDir dir = new("duckdb_storage_nocompact");
        string path = dir.FilePath("capture.duckdb");

        using (DuckDbExporter exporter = DuckDbExporter.CreateBuilder()
            .ToFile(path)
            .WithWriteMode(ExportWriteMode.Synchronous)
            .WithMaxPacketsPerBlock(2)
            .Build())
        {
            foreach (Packet packet in PacketGenerators.CreateEthernetUdpPackets(5))
            {
                await Assert.That(exporter.OnPacket(packet)).IsTrue();
            }

            exporter.OnFinish();
        }

        string[] leftBehind = [.. Directory.GetFiles(dir.DirectoryPath).Select(static file => Path.GetFileName(file))];

        await Assert.That(leftBehind).IsEquivalentTo(["capture.duckdb"], CollectionOrdering.Matching);
        await Assert.That(_Count(path, "packets")).IsEqualTo(5);
        await Assert.That(_Count(path, "topology")).IsGreaterThan(0);
    }

    #endregion
}

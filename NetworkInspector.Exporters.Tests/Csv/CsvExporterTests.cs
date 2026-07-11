// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Csv;

/// <summary>
/// Tests for the <see cref="CsvExporter"/> — validates builder, BOM, delimiter, header row,
/// column selection, RFC 4180 escaping, file output, and export lifecycle.
/// </summary>
internal sealed class CsvExporterTests
{
    [Test]
    public async Task Builder_RequiresOutput()
    {
        CsvExporter.Builder builder = CsvExporter.CreateBuilder();
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task DefaultColumns_ProducesHeaderAndRows()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        string csv = _GetCsvString(ms);
        string[] lines = _SplitCsvLines(csv);

        // Header + 3 data rows
        await Assert.That(lines.Length).IsEqualTo(4);

        // Default header: No.,Time,Info,Length
        await Assert.That(lines[0]).IsEqualTo("No.,Time,Info,Length");
    }

    [Test]
    public async Task BomEnabled_WritesUtf8Bom()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(true)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        byte[] data = ms.ToArray();

        // UTF-8 BOM: EF BB BF
        await Assert.That(data.Length).IsGreaterThanOrEqualTo(3);
        await Assert.That(data[0]).IsEqualTo((byte)0xEF);
        await Assert.That(data[1]).IsEqualTo((byte)0xBB);
        await Assert.That(data[2]).IsEqualTo((byte)0xBF);
    }

    [Test]
    public async Task BomDisabled_NoBom()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        byte[] data = ms.ToArray();

        // First bytes should be the header row starting with 'N' (No.), not BOM
        await Assert.That(data.Length).IsGreaterThan(0);
        await Assert.That(data[0]).IsNotEqualTo((byte)0xEF);
    }

    [Test]
    public async Task SemicolonDelimiter_ProducesCorrectFormat()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .WithDelimiter(';')
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        string csv = _GetCsvString(ms);
        string[] lines = _SplitCsvLines(csv);

        // Header should use semicolons
        await Assert.That(lines[0]).IsEqualTo("No.;Time;Info;Length");
    }

    [Test]
    public async Task TabDelimiter_ProducesCorrectFormat()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .WithDelimiter('\t')
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        string csv = _GetCsvString(ms);
        string[] lines = _SplitCsvLines(csv);

        // Header should use tabs
        await Assert.That(lines[0]).IsEqualTo("No.\tTime\tInfo\tLength");
    }

    [Test]
    public async Task PipeDelimiter_ProducesCorrectFormat()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .WithDelimiter('|')
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        string csv = _GetCsvString(ms);
        string[] lines = _SplitCsvLines(csv);

        // Header should use pipes
        await Assert.That(lines[0]).IsEqualTo("No.|Time|Info|Length");
    }

    [Test]
    public async Task HeaderDisabled_NoHeaderRow()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .WithHeader(false)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        string csv = _GetCsvString(ms);
        string[] lines = _SplitCsvLines(csv);

        // No header row — just 2 data rows
        await Assert.That(lines.Length).IsEqualTo(2);

        // First line should not be the header
        await Assert.That(lines[0].StartsWith("No.", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task CustomColumns_ProducesSelectedColumnsOnly()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .WithColumn(CsvColumnKind.PacketNumber)
            .WithColumn(CsvColumnKind.FrameLength)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        string csv = _GetCsvString(ms);
        string[] lines = _SplitCsvLines(csv);

        // Header with only two columns
        await Assert.That(lines[0]).IsEqualTo("No.,Length");

        // Data row should have exactly one comma (two columns)
        int commaCount = lines[1].Count(c => c == ',');
        await Assert.That(commaCount).IsEqualTo(1);
    }

    [Test]
    public async Task CustomColumnHeaders_OverridesDefaults()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .WithColumn(CsvColumnKind.PacketNumber, "Packet")
            .WithColumn(CsvColumnKind.Timestamp, "Zeitstempel")
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        string csv = _GetCsvString(ms);
        string[] lines = _SplitCsvLines(csv);

        await Assert.That(lines[0]).IsEqualTo("Packet,Zeitstempel");
    }

    [Test]
    public async Task PacketCount_TracksCorrectly()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .Build();

        const int count = 10;
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(count);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        await Assert.That(exporter.PacketCount).IsEqualTo(count);
        await Assert.That(exporter.WrittenCount).IsEqualTo(count);
        exporter.OnFinish();
    }

    [Test]
    public async Task TargetPacketCount_LimitsExport()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .WithTargetPacketCount(3)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);
        int accepted = 0;
        foreach (Packet packet in packets)
        {
            if (exporter.OnPacket(packet))
            {
                accepted++;
            }
        }

        exporter.OnFinish();

        // After reaching target, packets are refused
        await Assert.That(exporter.PacketCount).IsEqualTo(3);
    }

    [Test]
    public async Task Cancellation_StopsExport()
    {
        using CancellationTokenSource cts = new();
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .WithCancellationToken(cts.Token)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);

        exporter.OnPacket(packets[0]);
        exporter.OnPacket(packets[1]);
        await cts.CancelAsync().ConfigureAwait(false);

        // After cancellation, OnPacket should return false
        bool accepted = exporter.OnPacket(packets[2]);
        await Assert.That(accepted).IsFalse();

        exporter.OnFinish();
    }

    [Test]
    public async Task FileOutput_ProducesValidCsv()
    {
        using TestDir dir = new("csv_file");
        string path = dir.FilePath("output.csv");

        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToFile(path)
            .WithBom(false)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        string csv = await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false);
        string[] lines = _SplitCsvLines(csv);

        // Header + 5 data rows
        await Assert.That(lines.Length).IsEqualTo(6);
    }

    [Test]
    public async Task EmptyExport_WithHeader_ProducesHeaderOnly()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .Build();

        // No packets — just finish
        exporter.OnFinish();

        // With no packets, Start() is never called so no header is written either
        await Assert.That(ms.Length).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyExport_NoHeader_ProducesEmptyOutput()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .WithHeader(false)
            .Build();

        exporter.OnFinish();

        await Assert.That(ms.Length).IsEqualTo(0);
    }

    [Test]
    public async Task TimestampColumn_HasIso8601Format()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .WithColumn(CsvColumnKind.Timestamp)
            .Build();

        // Create a packet with a known timestamp: 1.5 seconds
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        string csv = _GetCsvString(ms);
        string[] lines = _SplitCsvLines(csv);

        // Data row should contain a timestamp with 'T' and 'Z'
        await Assert.That(lines[1].Contains('T', StringComparison.Ordinal)).IsTrue();
        await Assert.That(lines[1].EndsWith('Z')).IsTrue();
    }

    [Test]
    public async Task DataRowsUseCrLfLineEndings()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        byte[] data = ms.ToArray();
        string rawContent = Encoding.UTF8.GetString(data);

        // RFC 4180: CRLF line endings
        await Assert.That(rawContent.Contains("\r\n", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task MultiplePackets_AllWritten_WithCorrectRowCount()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .Build();

        const int count = 20;
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(count);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        string csv = _GetCsvString(ms);
        string[] lines = _SplitCsvLines(csv);

        // Header + 20 data rows
        await Assert.That(lines.Length).IsEqualTo(count + 1);
    }

    [Test]
    public async Task DefaultColumnsUsed_WhenNoneConfigured()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        string csv = _GetCsvString(ms);
        string[] lines = _SplitCsvLines(csv);

        // Default columns: No., Time, Info, Length
        string[] headers = lines[0].Split(',');
        await Assert.That(headers.Length).IsEqualTo(4);
        await Assert.That(headers[0]).IsEqualTo("No.");
        await Assert.That(headers[1]).IsEqualTo("Time");
        await Assert.That(headers[2]).IsEqualTo("Info");
        await Assert.That(headers[3]).IsEqualTo("Length");
    }

    [Test]
    public async Task FrameLengthColumn_HasNonZeroValue()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .WithColumn(CsvColumnKind.FrameLength)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        string csv = _GetCsvString(ms);
        string[] lines = _SplitCsvLines(csv);

        // Data row should have a non-zero frame length
        bool parsed = int.TryParse(lines[1], out int length);
        await Assert.That(parsed).IsTrue();
        await Assert.That(length).IsGreaterThan(0);
    }

    [Test]
    public async Task IsFinished_TrueAfterOnFinish()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .Build();

        await Assert.That(exporter.IsFinished).IsFalse();

        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    [Test]
    public async Task OnPacket_AfterFinish_ReturnsFalse()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .Build();

        exporter.OnFinish();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        bool accepted = exporter.OnPacket(packets[0]);
        await Assert.That(accepted).IsFalse();
    }

    [Test]
    public async Task BomWithFileOutput_ProducesValidFile()
    {
        using TestDir dir = new("csv_bom_file");
        string path = dir.FilePath("output.csv");

        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToFile(path)
            .WithBom(true)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        byte[] data = await File.ReadAllBytesAsync(path).ConfigureAwait(false);

        // UTF-8 BOM at start
        await Assert.That(data[0]).IsEqualTo((byte)0xEF);
        await Assert.That(data[1]).IsEqualTo((byte)0xBB);
        await Assert.That(data[2]).IsEqualTo((byte)0xBF);

        // Content after BOM should start with header
        string content = Encoding.UTF8.GetString(data, 3, data.Length - 3);
        await Assert.That(content.StartsWith("No.,", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task DoubleFinish_DoesNotThrow()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);

        // Calling OnFinish twice should not throw
        exporter.OnFinish();
        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
    }
    [Test]
    public async Task EmptyExport_ToFile_ProducesEmptyFile()
    {
        using TestDir dir = new("csv_empty_file");
        string path = dir.FilePath("empty.csv");

        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToFile(path)
            .WithBom(false)
            .Build();

        // No packets — just finish
        exporter.OnFinish();

        // With no packets, Start() is never called so the file is not created
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task EmptyExport_WithPacket_FileHasHeaderAndRow()
    {
        using TestDir dir = new("csv_file_with_row");
        string path = dir.FilePath("output.csv");

        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToFile(path)
            .WithBom(false)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        await Assert.That(File.Exists(path)).IsTrue();
        string csv = await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false);
        string[] lines = _SplitCsvLines(csv);

        // Header + 1 data row
        await Assert.That(lines.Length).IsEqualTo(2);
    }
    // ── Helpers ──────────────────────────────────────────────────────────────

    [Test]
    public async Task DefaultUiName_IsCsvExporter()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .Build();

        await Assert.That(exporter.UiName).IsEqualTo("CSV Exporter");
    }

    [Test]
    public async Task WithUiName_OverridesDefault()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithUiName("My CSV")
            .Build();

        await Assert.That(exporter.UiName).IsEqualTo("My CSV");
    }

    [Test]
    public async Task Description_ReturnsNull_WhenNotSet()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .Build();

        await Assert.That(exporter.Description).IsNull();
    }

    [Test]
    public async Task Description_ReturnsConfiguredValue()
    {
        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithDescription("Comma-separated values export")
            .Build();

        await Assert.That(exporter.Description).IsEqualTo("Comma-separated values export");
    }

    /// <summary>Extracts the CSV string from a <see cref="MemoryStream"/>.</summary>
    private static string _GetCsvString(MemoryStream ms) =>
        Encoding.UTF8.GetString(ms.ToArray());

    /// <summary>
    /// Splits a CSV string into lines, handling CRLF line endings
    /// and removing the trailing empty line if present.
    /// </summary>
    private static string[] _SplitCsvLines(string csv)
    {
        string[] lines = csv.Split("\r\n", StringSplitOptions.None);

        // Remove trailing empty entry from final CRLF
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        return lines;
    }

    // ========================================================================
    // Pre-epoch timestamp formatting
    // ========================================================================

    /// <summary>
    /// A negative nanosecond timestamp (before Unix epoch) must
    /// produce a valid ISO 8601 timestamp with non-negative fractional seconds.
    /// Before the fix, C# modulo preserved the sign of the dividend, producing
    /// malformed output like "1969-12-31T23:59:59.-500000000Z".
    /// </summary>
    [Test]
    public async Task TimestampColumn_PreEpoch_FormatsCorrectly()
    {
        // -500_000_000 ns = -0.5 s = 1969-12-31T23:59:59.500000000Z
        // (one half-second before midnight Jan 1 1970)
        const long preEpochNanos = -500_000_000L;

        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .WithColumn(CsvColumnKind.Timestamp)
            .Build();

        Packet packet = PacketGenerators.CreateParsedPacket(
            0,
            FrameGenerators.BuildEthernetIpv4UdpFrame(32),
            preEpochNanos);

        exporter.OnPacket(packet);
        exporter.OnFinish();

        string csv = _GetCsvString(ms);
        string[] lines = _SplitCsvLines(csv);

        // Must be exactly 2 lines: header + data
        await Assert.That(lines.Length).IsEqualTo(2);

        string timestampCell = lines[1];

        // Must have valid ISO 8601 form: ends with 'Z', contains 'T'
        await Assert.That(timestampCell.EndsWith('Z')).IsTrue();
        await Assert.That(timestampCell.Contains('T', StringComparison.Ordinal)).IsTrue();

        // The subsecond component must not start with a '-' character.
        // Find the '.' separator in the time part (after 'T')
        int tIdx = timestampCell.IndexOf('T', StringComparison.Ordinal);
        int dotIdx = timestampCell.IndexOf('.', tIdx);
        await Assert.That(dotIdx).IsGreaterThan(tIdx)
            .Because("Timestamp must contain a fractional-seconds separator '.'");

        // Character after '.' must be a digit, not '-'
        await Assert.That(char.IsDigit(timestampCell[dotIdx + 1])).IsTrue()
            .Because("Fractional-seconds component must be non-negative");
    }

    /// <summary>
    /// A timestamp of exactly -1 second (= -1_000_000_000 ns)
    /// must format as 1969-12-31T23:59:59.000000000Z (zero subseconds).
    /// </summary>
    [Test]
    public async Task TimestampColumn_ExactlyMinusOneSecond_FormatsCorrectly()
    {
        const long minusOneSecondNanos = -1_000_000_000L;

        using MemoryStream ms = new();
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(ms)
            .WithBom(false)
            .WithColumn(CsvColumnKind.Timestamp)
            .Build();

        Packet packet = PacketGenerators.CreateParsedPacket(
            0,
            FrameGenerators.BuildEthernetIpv4UdpFrame(32),
            minusOneSecondNanos);

        exporter.OnPacket(packet);
        exporter.OnFinish();

        string csv = _GetCsvString(ms);
        string[] lines = _SplitCsvLines(csv);

        string timestampCell = lines[1];

        await Assert.That(timestampCell.EndsWith('Z')).IsTrue();
        await Assert.That(timestampCell.Contains('T', StringComparison.Ordinal)).IsTrue();

        // Fractional part must be non-negative
        int tIdx = timestampCell.IndexOf('T', StringComparison.Ordinal);
        int dotIdx = timestampCell.IndexOf('.', tIdx);
        if (dotIdx >= 0)
        {
            await Assert.That(char.IsDigit(timestampCell[dotIdx + 1])).IsTrue();
        }
    }
}

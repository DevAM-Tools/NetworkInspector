// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using FailingStream = NetworkInspector.Exporters.Tests.Helpers.TestStreams.FailingStream;

namespace NetworkInspector.Exporters.Tests.Text;

/// <summary>
/// Tests for the <see cref="TextExporter"/> — validates builder configuration, detail levels,
/// output content, export lifecycle, statistics, and cancellation.
/// </summary>
internal sealed class TextExporterTests
{
    // ========================================================================
    // Builder
    // ========================================================================

    [Test]
    public async Task Builder_RequiresOutput()
    {
        TextExporter.Builder builder = TextExporter.CreateBuilder();
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task UiName_ReturnsDefault()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.UiName).IsEqualTo("Text Exporter");
    }

    [Test]
    public async Task UiName_ReturnsCustomValue()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithUiName("My Text Exporter")
            .Build();

        await Assert.That(exporter.UiName).IsEqualTo("My Text Exporter");
    }

    [Test]
    public async Task Description_ReturnsNull_WhenNotSet()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.Description).IsNull();
    }

    [Test]
    public async Task Description_ReturnsConfiguredValue()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithDescription("Human-readable packet dump")
            .Build();

        await Assert.That(exporter.Description).IsEqualTo("Human-readable packet dump");
    }

    // ========================================================================
    // Basic output
    // ========================================================================

    [Test]
    public async Task SinglePacket_ProducesOutput()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        await Assert.That(ms.Length).IsGreaterThan(0);

        string text = Encoding.UTF8.GetString(ms.ToArray());
        // Each packet starts with "Packet N  [timestamp]"
        await Assert.That(text.Contains("Packet 1", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task MultiplePackets_AllWritten()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        const int count = 5;
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(count);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        string text = Encoding.UTF8.GetString(ms.ToArray());

        // Each packet produces a numbered header line
        for (int i = 1; i <= count; i++)
        {
            await Assert.That(text.Contains($"Packet {i}", StringComparison.Ordinal)).IsTrue();
        }

        await Assert.That(exporter.PacketCount).IsEqualTo(count);
        await Assert.That(exporter.WrittenCount).IsEqualTo(count);
    }

    [Test]
    public async Task EmptyExport_WritesMinimalNewline()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        exporter.OnFinish();

        await Assert.That(ms.Length).IsEqualTo(1);
        await Assert.That(ms.ToArray()[0]).IsEqualTo((byte)'\n');
    }

    [Test]
    public async Task FileOutput_ProducesValidText()
    {
        using TestDir dir = new("text_file");
        string path = dir.FilePath("output.txt");

        using TextExporter exporter = TextExporter.CreateBuilder().ToFile(path).Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        string text = await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false);

        await Assert.That(text.Length).IsGreaterThan(0);
        await Assert.That(text.Contains("Packet 1", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains("Packet 3", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task StreamOutput_ProducesValidText()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        await Assert.That(ms.Length).IsGreaterThan(0);

        string text = Encoding.UTF8.GetString(ms.ToArray());
        await Assert.That(text.Contains("Packet 1", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains("Packet 2", StringComparison.Ordinal)).IsTrue();
    }

    // ========================================================================
    // Detail levels
    // ========================================================================

    [Test]
    public async Task DetailLevel_Standard_ContainsSeparatorBlankLine()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithDetailLevel(TextDetailLevel.Standard)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        string text = Encoding.UTF8.GetString(ms.ToArray());

        // A blank separator line is produced after the field tree (two consecutive newlines).
        await Assert.That(text.Contains("\n\n", StringComparison.Ordinal)).IsTrue();

        // Separator must appear AFTER at least some field content (not before the tree)
        int separatorIdx = text.IndexOf("\n\n", StringComparison.Ordinal);
        int headerIdx = text.IndexOf("Packet ", StringComparison.Ordinal);
        await Assert.That(separatorIdx).IsGreaterThan(headerIdx);
    }

    [Test]
    public async Task DetailLevel_Summary_ProducesLessOutputThanStandard()
    {
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);

        using MemoryStream msSummary = new();
        using TextExporter summaryExporter = TextExporter.CreateBuilder()
            .ToStream(msSummary)
            .WithDetailLevel(TextDetailLevel.Summary)
            .Build();
        summaryExporter.OnPacket(packets[0]);
        summaryExporter.OnFinish();

        using MemoryStream msStandard = new();
        using TextExporter standardExporter = TextExporter.CreateBuilder()
            .ToStream(msStandard)
            .WithDetailLevel(TextDetailLevel.Standard)
            .Build();
        standardExporter.OnPacket(packets[0]);
        standardExporter.OnFinish();

        // Summary mode suppresses sub-fields, so output is shorter
        await Assert.That(msSummary.Length).IsLessThan(msStandard.Length);
    }

    [Test]
    public async Task DetailLevel_Summary_OnlyShowsContainerFields()
    {
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);

        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithDetailLevel(TextDetailLevel.Summary)
            .Build();
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        string text = Encoding.UTF8.GetString(ms.ToArray());

        // Value fields produced by IPv4 and UDP protocols must be absent in Summary mode.
        await Assert.That(text.Contains("Source Address:", StringComparison.Ordinal)).IsFalse();
        await Assert.That(text.Contains("Source Port:", StringComparison.Ordinal)).IsFalse();
    }

    // ========================================================================
    // MaxTextLength
    // ========================================================================

    [Test]
    public async Task MaxTextLength_Zero_DoesNotTruncate()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithMaxTextLength(0)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        // Just verify it produces output without throwing
        await Assert.That(ms.Length).IsGreaterThan(0);
    }

    // ========================================================================
    // Lifecycle: IsFinished, Double-finish, OnPacket after finish
    // ========================================================================

    [Test]
    public async Task IsFinished_FalseBeforeOnFinish()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        await Assert.That(exporter.IsFinished).IsFalse();
    }

    [Test]
    public async Task IsFinished_TrueAfterOnFinish()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    [Test]
    public async Task OnPacket_AfterFinish_ReturnsFalse()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        exporter.OnFinish();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        bool accepted = exporter.OnPacket(packets[0]);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.PacketCount).IsEqualTo(0);
    }

    [Test]
    public async Task DoubleFinish_IsIdempotent()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        exporter.OnPacket(packets[0]);

        // Calling OnFinish twice must not throw
        exporter.OnFinish();
        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
        await Assert.That(exporter.PacketCount).IsEqualTo(1);
    }

    [Test]
    public async Task Dispose_IsIdempotent()
    {
        MemoryStream ms = new();
        TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);

        // Disposing twice must not throw
        exporter.Dispose();
        exporter.Dispose();

        await Assert.That(ms.Length).IsGreaterThan(0);

        await ms.DisposeAsync().ConfigureAwait(false);
    }

    // ========================================================================
    // Cancellation
    // ========================================================================

    [Test]
    public async Task Cancellation_StopsExport()
    {
        using CancellationTokenSource cts = new();
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithCancellationToken(cts.Token)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);

        exporter.OnPacket(packets[0]);
        exporter.OnPacket(packets[1]);
        await cts.CancelAsync().ConfigureAwait(false);

        // After cancellation, OnPacket must return false
        bool accepted = exporter.OnPacket(packets[2]);

        await Assert.That(accepted).IsFalse();

        exporter.OnFinish();
    }

    [Test]
    public async Task IsFinished_TrueAfterCancellation()
    {
        using CancellationTokenSource cts = new();
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithCancellationToken(cts.Token)
            .Build();

        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    // ========================================================================
    // Target packet count
    // ========================================================================

    [Test]
    public async Task TargetPacketCount_LimitsExport()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
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

        await Assert.That(exporter.PacketCount).IsEqualTo(3);
    }

    [Test]
    public async Task IsFinished_TrueAfterTargetReached()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(ms)
            .WithTargetPacketCount(2)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        await Assert.That(exporter.IsFinished).IsTrue();
        exporter.OnFinish();
    }

    // ========================================================================
    // Statistics counters
    // ========================================================================

    [Test]
    public async Task PacketCount_TracksCorrectly()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        const int count = 7;
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(count);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        await Assert.That(exporter.PacketCount).IsEqualTo(count);
        await Assert.That(exporter.WrittenCount).IsEqualTo(count);
        await Assert.That(exporter.SkippedCount).IsEqualTo(0);
        await Assert.That(exporter.ErrorCount).IsEqualTo(0);
        await Assert.That(exporter.HasErrors).IsFalse();

        exporter.OnFinish();
    }

    [Test]
    public async Task SkippedCount_IncreasesOnTolerantError()
    {
        // A stream that accepts zero bytes will cause each packet write to fail.
        using FailingStream stream = new()
        {
            ThrowAfterByte = 0
        };
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(stream)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        int skippedRaised = 0;
        exporter.ItemSkipped += (_, _) => Interlocked.Increment(ref skippedRaised);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        await Assert.That(skippedRaised).IsGreaterThanOrEqualTo(1);
        await Assert.That(exporter.SkippedCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.WrittenCount).IsEqualTo(0);
    }

    // ========================================================================
    // Packet content
    // ========================================================================

    [Test]
    public async Task PacketHeader_ContainsTimestamp()
    {
        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        string text = Encoding.UTF8.GetString(ms.ToArray());

        // Timestamp in ISO 8601 format: T and Z present
        await Assert.That(text.Contains('T', StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains('Z', StringComparison.Ordinal)).IsTrue();
    }

    // ========================================================================
    // WithMaxTextLength rejects negative values
    // ========================================================================

    /// <summary>
    /// <see cref="TextExporter.Builder.WithMaxTextLength"/> must throw
    /// <see cref="ArgumentOutOfRangeException"/> when a negative value is supplied.
    /// </summary>
    [Test]
    public async Task Builder_WithMaxTextLength_Negative_ThrowsArgumentOutOfRangeException()
    {
        TextExporter.Builder builder = TextExporter.CreateBuilder();

        await Assert.That(() => builder.WithMaxTextLength(-1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Builder_WithMaxTextLength_Zero_Accepted()
    {
        using MemoryStream ms = new();
        TextExporter.Builder builder = TextExporter.CreateBuilder().ToStream(ms);

        // Zero means "unlimited" — must not throw
        builder.WithMaxTextLength(0);
        using TextExporter exporter = builder.Build();

        await Assert.That(exporter).IsNotNull();
    }

    [Test]
    public async Task Builder_WithMaxTextLength_Positive_Accepted()
    {
        using MemoryStream ms = new();
        TextExporter.Builder builder = TextExporter.CreateBuilder().ToStream(ms);

        builder.WithMaxTextLength(512);
        using TextExporter exporter = builder.Build();

        await Assert.That(exporter).IsNotNull();
    }

    // ========================================================================
    // Builder.WithTargetPacketCount input validation
    // ========================================================================

    /// <summary>
    /// <see cref="TextExporter.Builder.WithTargetPacketCount"/> must reject
    /// negative values with <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    [Test]
    public async Task Builder_WithTargetPacketCount_Negative_ThrowsArgumentOutOfRangeException()
    {
        TextExporter.Builder builder = TextExporter.CreateBuilder();

        await Assert.That(() => builder.WithTargetPacketCount(-1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Builder_WithTargetPacketCount_Zero_Accepted()
    {
        using MemoryStream ms = new();
        TextExporter.Builder builder = TextExporter.CreateBuilder().ToStream(ms);

        // Zero means "unlimited" — must not throw
        builder.WithTargetPacketCount(0);
        using TextExporter exporter = builder.Build();

        await Assert.That(exporter).IsNotNull();
    }

    // ========================================================================
    // Pre-epoch timestamp formatting
    // ========================================================================

    /// <summary>
    /// A negative nanosecond timestamp (before Unix epoch) must produce a valid
    /// ISO 8601 timestamp with non-negative fractional seconds in the text output.
    /// </summary>
    [Test]
    public async Task Timestamp_PreEpoch_FormatsCorrectly()
    {
        // -500_000_000 ns = 1969-12-31T23:59:59.500000000Z
        const long preEpochNanos = -500_000_000L;

        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        Packet packet = PacketGenerators.CreateParsedPacket(
            0,
            FrameGenerators.BuildEthernetIpv4UdpFrame(32),
            preEpochNanos);

        exporter.OnPacket(packet);
        exporter.OnFinish();

        string text = Encoding.UTF8.GetString(ms.ToArray());

        // Must contain a valid ISO 8601 fragment: ends with 'Z', fractional part non-negative
        await Assert.That(text.Contains('Z', StringComparison.Ordinal)).IsTrue();

        // Find the '.' in the time portion and verify the next character is a digit
        int dotIdx = -1;
        int zIdx = text.LastIndexOf('Z');
        // The fractional seconds '.NNN...' must appear just before 'Z'
        for (int i = zIdx - 1; i >= 0; i--)
        {
            if (text[i] == '.')
            {
                dotIdx = i;
                break;
            }
            if (!char.IsDigit(text[i]))
            {
                break;
            }
        }

        await Assert.That(dotIdx).IsGreaterThan(0)
            .Because("Timestamp must contain fractional seconds separator '.'");
        await Assert.That(char.IsDigit(text[dotIdx + 1])).IsTrue()
            .Because("Fractional seconds must be non-negative (digit after '.')");
    }

    /// <summary>
    /// A timestamp of exactly -1_000_000_000 ns (-1 second)
    /// must format as ...T23:59:59.000000000Z, not ...T23:59:59.-000000000Z.
    /// </summary>
    [Test]
    public async Task Timestamp_ExactlyMinusOneSecond_FormatsCorrectly()
    {
        const long minusOneSecondNanos = -1_000_000_000L;

        using MemoryStream ms = new();
        using TextExporter exporter = TextExporter.CreateBuilder().ToStream(ms).Build();

        Packet packet = PacketGenerators.CreateParsedPacket(
            0,
            FrameGenerators.BuildEthernetIpv4UdpFrame(32),
            minusOneSecondNanos);

        exporter.OnPacket(packet);
        exporter.OnFinish();

        string text = Encoding.UTF8.GetString(ms.ToArray());

        // The text output must not contain a '.-' fragment (negative subsecond)
        await Assert.That(text.Contains(".-", StringComparison.Ordinal)).IsFalse()
            .Because("Negative fractional seconds must not appear in output");
    }
}

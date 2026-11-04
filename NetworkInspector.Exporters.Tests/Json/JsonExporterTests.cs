// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Json;

/// <summary>
/// Tests for the <see cref="JsonExporter"/> — validates all three formats (Compact, Pretty, Array),
/// JSON structural correctness, and export behavior.
/// </summary>
internal sealed class JsonExporterTests
{
    [Test]
    public async Task BuilderRequiresOutput()
    {
        JsonExporter.Builder builder = JsonExporter.CreateBuilder();
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task CompactFormatProducesValidJson()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Compact)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        JsonVerifier verifier = JsonVerifier.FromStream(ms);
        await Assert.That(verifier.IsArray).IsTrue();
        await Assert.That(verifier.PacketCount).IsEqualTo(3);
        verifier.Dispose();
    }

    [Test]
    public async Task PrettyFormatProducesValidJson()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Pretty)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        JsonVerifier verifier = JsonVerifier.FromStream(ms);
        await Assert.That(verifier.IsArray).IsTrue();
        await Assert.That(verifier.PacketCount).IsEqualTo(2);
        verifier.Dispose();
    }

    [Test]
    public async Task ArrayFormatProducesValidJson()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Array)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        JsonVerifier verifier = JsonVerifier.FromStream(ms);
        await Assert.That(verifier.IsArray).IsTrue();
        await Assert.That(verifier.PacketCount).IsEqualTo(3);
        verifier.Dispose();
    }

    [Test]
    public async Task OnPacketSkipPacketReportsSkippedAndWritesNoPacket()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Compact)
            .Build();

        byte[] frameData = FrameGenerators.BuildEthernetIpv4UdpFrame(32);
        Frame frame = TestHarness.CreateFrame(0, 0, frameData);
        ParseOptions options = new(FieldTreeMode.Skip);
        if (!Packet.TryParse(TestHarness.NextPacketId(), TestHarness.GetStack(), frame, in options, out Packet? skip, out ParseFailure failure)
            || skip is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);
        bool accepted = exporter.OnPacket(skip);
        exporter.OnFinish();

        await Assert.That(accepted).IsTrue();
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(exporter.PacketCount).IsEqualTo(0);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(events[0].Message).IsEqualTo("Skip packets cannot be exported (HasFieldTree is false).");
    }

    [Test]
    public async Task OnPacketSkipPacketStopsAStrictExportWithoutAnEvent()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(ms).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;
        ParseOptions options = new(FieldTreeMode.Skip);
        Packet skip = TestHarness.ParseFrame(TestHarness.CreateFrame(0, 0, FrameGenerators.BuildEthernetIpv4UdpFrame(32)), in options);
        int skippedEvents = 0;
        exporter.ItemSkipped += (_, _) => skippedEvents++;

        bool accepted = exporter.OnPacket(skip);

        await Assert.That(accepted).IsFalse();
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(skippedEvents).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyExportProducesEmptyJsonArray()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Compact)
            .Build();

        exporter.OnFinish();

        // An empty export must still produce a valid JSON array ("[\n]\n").
        await Assert.That(ms.Length).IsGreaterThan(0);
        JsonVerifier verifier = JsonVerifier.FromStream(ms);
        await Assert.That(verifier.IsArray).IsTrue();
        await Assert.That(verifier.PacketCount).IsEqualTo(0);
        verifier.Dispose();
    }

    [Test]
    public async Task FileOutputProducesValidJson()
    {
        using TestDir dir = new("json_file");
        string path = dir.FilePath("output.json");

        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToFile(path)
            .WithFormat(JsonExportFormat.Pretty)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        JsonVerifier verifier = JsonVerifier.Open(path);
        await Assert.That(verifier.IsArray).IsTrue();
        await Assert.That(verifier.PacketCount).IsEqualTo(5);
        verifier.Dispose();
    }

    [Test]
    [Category("Slow")]
    public async Task CancellationStopsExport()
    {
        using CancellationTokenSource cts = new();
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Compact)
            .WithCancellationToken(cts.Token)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);

        // Export a few packets then cancel
        exporter.OnPacket(packets[0]);
        exporter.OnPacket(packets[1]);
        await cts.CancelAsync().ConfigureAwait(false);

        // After cancellation, OnPacket should return false
        bool accepted = exporter.OnPacket(packets[2]);
        await Assert.That(accepted).IsFalse();

        exporter.OnFinish();
    }

    [Test]
    [Category("Slow")]
    public async Task TargetPacketCountLimitsExport()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Compact)
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

        // OnPacket returns true for every packet that is actually written; the
        // 4th call (after the 3-packet target was reached on call 3) returns
        // false at the up-front gate. So 3 packets are accepted and written.
        await Assert.That(accepted).IsEqualTo(3);
    }

    [Test]
    public async Task PrettyFormatContainsIndentation()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Pretty)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        ms.Position = 0;
        string content = Encoding.UTF8.GetString(ms.ToArray());

        // Pretty format should contain indentation (spaces at line start)
        await Assert.That(content.Contains("  ", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task DefaultUiNameIsJsonExporter()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .Build();

        await Assert.That(exporter.UiName).IsEqualTo("JSON Exporter");
    }

    [Test]
    public async Task WithUiNameOverridesDefault()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithUiName("My JSON")
            .Build();

        await Assert.That(exporter.UiName).IsEqualTo("My JSON");
    }

    // ========================================================================
    // Lifecycle: IsFinished, Double-finish
    // ========================================================================

    [Test]
    public async Task IsFinishedFalseBeforeOnFinish()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Compact)
            .Build();

        await Assert.That(exporter.IsFinished).IsFalse();
    }

    [Test]
    public async Task IsFinishedTrueAfterOnFinish()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Compact)
            .Build();

        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    [Test]
    public async Task OnPacketAfterFinishReturnsFalse()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Compact)
            .Build();

        exporter.OnFinish();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        bool accepted = exporter.OnPacket(packets[0]);

        await Assert.That(accepted).IsFalse();
    }

    [Test]
    public async Task DoubleFinishIsIdempotent()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Compact)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        exporter.OnPacket(packets[0]);

        // Calling OnFinish twice must not throw
        exporter.OnFinish();
        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
        await Assert.That(exporter.PacketCount).IsEqualTo(1);
    }

    // ========================================================================
    // Statistics counters
    // ========================================================================

    [Test]
    public async Task StatisticsTracksWrittenCount()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Compact)
            .Build();

        const int count = 5;
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

    // ========================================================================
    // Description
    // ========================================================================

    [Test]
    public async Task DescriptionReturnsNullWhenNotSet()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .Build();

        await Assert.That(exporter.Description).IsNull();
    }

    [Test]
    public async Task DescriptionReturnsConfiguredValue()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithDescription("JSON packet dump")
            .Build();

        await Assert.That(exporter.Description).IsEqualTo("JSON packet dump");
    }

    [Test]
    public async Task PrettyFormatFieldTypeIsTextualName()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Pretty)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        JsonVerifier verifier = JsonVerifier.FromStream(ms);
        JsonElement typeElement = _FindFieldTypeByName(verifier.Root[0], "eth.dst", "type");
        await Assert.That(typeElement.ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(typeElement.GetString()).IsEqualTo("MacAddress");
        verifier.Dispose();
    }

    [Test]
    public async Task ArrayFormatFieldTypeIsTextualName()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Array)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        JsonVerifier verifier = JsonVerifier.FromStream(ms);
        JsonElement typeElement = _FindFieldTypeByName(verifier.Root[0], "eth.dst", "type");
        await Assert.That(typeElement.ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(typeElement.GetString()).IsEqualTo("MacAddress");
        verifier.Dispose();
    }

    [Test]
    public async Task CompactFormatFieldTypeIsNumericEnumValue()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Compact)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        exporter.OnPacket(packets[0]);
        exporter.OnFinish();

        JsonVerifier verifier = JsonVerifier.FromStream(ms);
        JsonElement typeElement = _FindFieldTypeByName(verifier.Root[0], "eth.dst", "TY");
        await Assert.That(typeElement.ValueKind).IsEqualTo(JsonValueKind.Number);
        await Assert.That(typeElement.GetInt32()).IsEqualTo((int)FieldType.MacAddress);
        verifier.Dispose();
    }

    [Test]
    public async Task TheCompactFormatRefusesAPacketOfAnotherStackAndKeepsTheEarlierPacketsValid()
    {
        // The compact format names a field id once per document; the custom stack registers other fields than the standard
        // stack, so the same id names another field there and a reader would keep the first stack's name.
        using CustomFieldPacketFactory factory = new();
        (Packet standard, Packet custom) = _PacketOfEachStack(factory);
        using MemoryStream stream = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(stream).WithFormat(JsonExportFormat.Compact).Build();
        List<ExportErrorEventArgs> errors = [];
        exporter.ItemSkipped += (_, args) => errors.Add(args);

        bool first = exporter.OnPacket(standard);
        bool second = exporter.OnPacket(custom);
        exporter.OnFinish();

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(ExportErrorKind.StackMismatch);
        await Assert.That(errors[0].ItemIndex).IsEqualTo(1);
        await Assert.That(exporter.PacketCount).IsEqualTo(1);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
        await Assert.That(document.RootElement.GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task TheCompactFormatRefusesEveryLaterPacketOnceTheStackChanged()
    {
        // The refusal is fatal, so a later packet of the first stack is not written after a gap either.
        using CustomFieldPacketFactory factory = new();
        (Packet standard, Packet custom) = _PacketOfEachStack(factory);
        using MemoryStream stream = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(stream).WithFormat(JsonExportFormat.Compact).Build();

        _ = exporter.OnPacket(standard);
        _ = exporter.OnPacket(custom);
        bool later = exporter.OnPacket(standard);
        exporter.OnFinish();

        await Assert.That(later).IsFalse();
        await Assert.That(exporter.PacketCount).IsEqualTo(1);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(JsonExportFormat.Pretty, ExportShape.Hierarchy)]
    [Arguments(JsonExportFormat.Array, ExportShape.Hierarchy)]
    [Arguments(JsonExportFormat.Compact, ExportShape.AllFields)]
    public async Task TheFormatsThatNameEveryFieldAcceptPacketsOfTwoStacks(JsonExportFormat format, ExportShape shape)
    {
        // Pretty, Array, and the flat shapes write the name of every field, so a second stack is harmless there.
        using CustomFieldPacketFactory factory = new();
        (Packet standard, Packet custom) = _PacketOfEachStack(factory);
        using MemoryStream stream = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(format)
            .WithContent(JsonExporter.DefaultContent with { Shape = shape })
            .Build();

        bool first = exporter.OnPacket(standard);
        bool second = exporter.OnPacket(custom);
        exporter.OnFinish();

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        await Assert.That(first).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(exporter.ErrorCount).IsEqualTo(0);
        await Assert.That(document.RootElement.GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task ASkippedFirstPacketDoesNotPinTheStackOfACompactExport()
    {
        // Nothing of the skipped packet reached the document, so the document has no stack yet.
        using CustomFieldPacketFactory factory = new();
        Frame frame = TestHarness.CreateFrame(0, 0, FrameGenerators.BuildEthernetIpv4UdpFrame(32));
        ParseOptions skipOptions = new(FieldTreeMode.Skip);
        if (!Packet.TryParse(TestHarness.NextPacketId(), TestHarness.GetStack(), frame, in skipOptions, out Packet? skip, out ParseFailure failure)
            || skip is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        Packet custom = factory.Create(5, "text");
        using MemoryStream stream = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(stream).WithFormat(JsonExportFormat.Compact).Build();

        _ = exporter.OnPacket(skip);
        bool accepted = exporter.OnPacket(custom);
        exporter.OnFinish();

        await Assert.That(accepted).IsTrue();
        await Assert.That(exporter.PacketCount).IsEqualTo(1);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
    }

    /// <summary>
    /// One packet of the standard test stack and one of the factory's own stack. The ids differ, as in a session where every
    /// packet has a unique number.
    /// </summary>
    /// <param name="factory">The factory with its own stack.</param>
    /// <returns>A standard-stack packet and a packet of the factory's stack.</returns>
    private static (Packet Standard, Packet Custom) _PacketOfEachStack(CustomFieldPacketFactory factory)
    {
        Packet standard = TestHarness.ParseFrame(TestHarness.CreateFrame(1, 1_000_000, FrameGenerators.BuildEthernetIpv4UdpFrame(8)));
        Packet custom = factory.Create(77, "hello");
        while (custom.Id.Value == standard.Id.Value)
        {
            custom = factory.Create(77, "hello");
        }

        return (standard, custom);
    }

    /// <summary>
    /// Finds the type metadata element for a field identified by its protocol name.
    /// </summary>
    private static JsonElement _FindFieldTypeByName(JsonElement packet, string fieldName, string typeKey)
    {
        if (!packet.TryGetProperty("fields", out JsonElement fields)
            && !packet.TryGetProperty("CH", out fields))
        {
            throw new InvalidOperationException("Packet has no field array.");
        }

        if (!_TryFindFieldTypeByName(fields, fieldName, typeKey, out JsonElement typeElement))
        {
            throw new InvalidOperationException($"Field '{fieldName}' with key '{typeKey}' not found.");
        }

        return typeElement;
    }

    private static bool _TryFindFieldTypeByName(
        JsonElement fields,
        string fieldName,
        string typeKey,
        out JsonElement typeElement)
    {
        foreach (JsonElement field in fields.EnumerateArray())
        {
            if (field.TryGetProperty("name", out JsonElement nameElement)
                || field.TryGetProperty("NA", out nameElement))
            {
                if (string.Equals(nameElement.GetString(), fieldName, StringComparison.Ordinal)
                    && field.TryGetProperty(typeKey, out typeElement))
                {
                    return true;
                }
            }

            if (field.TryGetProperty("children", out JsonElement children)
                || field.TryGetProperty("CH", out children))
            {
                if (_TryFindFieldTypeByName(children, fieldName, typeKey, out typeElement))
                {
                    return true;
                }
            }
        }

        typeElement = default;
        return false;
    }
}

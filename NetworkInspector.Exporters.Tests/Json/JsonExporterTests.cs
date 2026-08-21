// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Json;

/// <summary>
/// Tests for the <see cref="JsonExporter"/> — validates all three formats (Compact, Pretty, Array),
/// JSON structural correctness, and export behavior.
/// </summary>
internal sealed class JsonExporterTests
{
    [Test]
    public async Task Builder_RequiresOutput()
    {
        JsonExporter.Builder builder = JsonExporter.CreateBuilder();
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task CompactFormat_ProducesValidJson()
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
    public async Task PrettyFormat_ProducesValidJson()
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
    public async Task ArrayFormat_ProducesValidJson()
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
    public async Task EmptyExport_ProducesEmptyJsonArray()
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
    public async Task FileOutput_ProducesValidJson()
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
    public async Task Cancellation_StopsExport()
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
    public async Task TargetPacketCount_LimitsExport()
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
    public async Task PrettyFormat_ContainsIndentation()
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
    public async Task DefaultUiName_IsJsonExporter()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .Build();

        await Assert.That(exporter.UiName).IsEqualTo("JSON Exporter");
    }

    [Test]
    public async Task WithUiName_OverridesDefault()
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
    public async Task IsFinished_FalseBeforeOnFinish()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(JsonExportFormat.Compact)
            .Build();

        await Assert.That(exporter.IsFinished).IsFalse();
    }

    [Test]
    public async Task IsFinished_TrueAfterOnFinish()
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
    public async Task OnPacket_AfterFinish_ReturnsFalse()
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
    public async Task DoubleFinish_IsIdempotent()
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
    public async Task Statistics_TracksWrittenCount()
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
    public async Task Description_ReturnsNull_WhenNotSet()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .Build();

        await Assert.That(exporter.Description).IsNull();
    }

    [Test]
    public async Task Description_ReturnsConfiguredValue()
    {
        using MemoryStream ms = new();
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(ms)
            .WithDescription("JSON packet dump")
            .Build();

        await Assert.That(exporter.Description).IsEqualTo("JSON packet dump");
    }

    [Test]
    public async Task PrettyFormat_FieldType_IsTextualName()
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
    public async Task ArrayFormat_FieldType_IsTextualName()
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
    public async Task CompactFormat_FieldType_IsNumericEnumValue()
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

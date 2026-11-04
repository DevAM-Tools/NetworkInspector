// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Json;

/// <summary>
/// The flat export shapes of the JSON and Text exporters: selected fields and all fields, fed by a tree-free parse with the
/// exporter's own observer and by tree packets. JSON output is parsed with <see cref="System.Text.Json"/> in every
/// <see cref="JsonExportFormat"/>; Text output is read line by line.
/// </summary>
internal sealed class JsonTextShapeTests
{
    #region Selections and feeds

    /// <summary>The selected-fields content of the tests: <c>ip.src</c> with the given occurrences and the first <c>udp.dstport</c>.</summary>
    /// <param name="ipSrc">Which occurrences of <c>ip.src</c> to select.</param>
    /// <param name="texts">Whether custom texts and representations are written.</param>
    /// <returns>The content, without info and frame bytes.</returns>
    private static ExportContent _Selected(Occurrences ipSrc = Occurrences.All, bool texts = false)
    {
        FieldRequest[] requests = [new FieldRequest("ip.src", ipSrc), new FieldRequest("udp.dstport", Occurrences.First)];
        return new ExportContent
        {
            Shape = ExportShape.SelectedFields,
            Fields = ExportContents.Selected(requests).Fields,
            Info = false,
            FrameBytes = false,
            CustomText = texts,
            CustomRepresentation = texts,
        };
    }

    /// <summary>Exports the tunnel, UDP, and CAN frames through a tree-free parse with a JSON exporter.</summary>
    /// <param name="format">The JSON style.</param>
    /// <param name="content">The content of the export.</param>
    /// <returns>The JSON text.</returns>
    private static string _RunJson(JsonExportFormat format, ExportContent content)
    {
        using MemoryStream stream = new();
        using (JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(stream).WithFormat(format).WithContent(content).Build())
        {
            _ = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
            exporter.OnFinish();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Exports the tunnel, UDP, and CAN frames through a tree-free parse with a Text exporter.</summary>
    /// <param name="content">The content of the export.</param>
    /// <param name="maxTextLength">The longest text in characters before it is cut with an ellipsis.</param>
    /// <returns>The text.</returns>
    private static string _RunText(ExportContent content, int maxTextLength = 256)
    {
        using MemoryStream stream = new();
        using (TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).WithContent(content).WithMaxTextLength(maxTextLength).Build())
        {
            _ = TreeFreeFeed.Run(exporter, ShapeFrames.TunnelUdpCan());
            exporter.OnFinish();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    #endregion

    #region JSON

    /// <summary>
    /// Proves that selected fields are a map in which "all occurrences" is always an array and "first occurrence" a scalar, and that
    /// a packet without the fields keeps its object with the field keys omitted.
    /// </summary>
    /// <param name="format">The JSON style under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(JsonExportFormat.Compact)]
    [Arguments(JsonExportFormat.Pretty)]
    [Arguments(JsonExportFormat.Array)]
    public async Task SelectedFieldsAreAMapWithArraysForAllAndScalarsForFirstInEveryFormat(JsonExportFormat format)
    {
        string json = _RunJson(format, _Selected());

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement[] packets = [.. document.RootElement.EnumerateArray()];

        await Assert.That(packets.Length).IsEqualTo(3);

        // The tunnel packet: All is an array of both addresses, First a scalar port.
        JsonElement tunnel = packets[0].GetProperty("fields");
        await Assert.That(tunnel.GetProperty("ip.src").ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(tunnel.GetProperty("ip.src").GetArrayLength()).IsEqualTo(2);
        await Assert.That(tunnel.GetProperty("udp.dstport").GetInt64()).IsEqualTo(53L);

        // A plain packet: still an array for All (one value), so a consumer sees one JSON type per key.
        await Assert.That(packets[1].GetProperty("fields").GetProperty("ip.src").GetArrayLength()).IsEqualTo(1);

        // The CAN packet has neither field: the keys are omitted, and the packet object is still there.
        await Assert.That(packets[2].GetProperty("fields").EnumerateObject().Count()).IsEqualTo(0);
        await Assert.That(packets[2].TryGetProperty("id", out _)).IsTrue();
        await Assert.That(packets[2].TryGetProperty("timestamp", out _)).IsTrue();
    }

    /// <summary>The tree styles write the packet info key only when the content asks for it (<c>Info</c>, on by default).</summary>
    /// <param name="format">The JSON style.</param>
    /// <param name="key">The spelling of the info key in that style.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(JsonExportFormat.Compact, "IN")]
    [Arguments(JsonExportFormat.Pretty, "info")]
    [Arguments(JsonExportFormat.Array, "info")]
    public async Task JsonTreeWithInfoOffWritesNoInfoKey(JsonExportFormat format, string key)
    {
        Packet packet = PacketGenerators.CreateEthernetUdpPackets(1)[0];
        bool[] present = new bool[2];
        for (int i = 0; i < 2; i++)
        {
            using MemoryStream stream = new();
            ExportContent content = new() { Shape = ExportShape.Hierarchy, Info = i == 0, FrameBytes = false };
            using (JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(stream).WithFormat(format).WithContent(content).Build())
            {
                await Assert.That(exporter.OnPacket(packet)).IsTrue();
                exporter.OnFinish();
            }

            using JsonDocument document = JsonDocument.Parse(stream.ToArray());
            present[i] = document.RootElement[0].TryGetProperty(key, out _);
        }

        // The packet has an info text, so the key is there with the member on (the positive control) and gone with it off.
        await Assert.That(packet.Info.Length).IsGreaterThan(0);
        await Assert.That(present[0]).IsTrue();
        await Assert.That(present[1]).IsFalse();
    }

    /// <summary>Proves that selecting the first or the last occurrence of a field writes a scalar string with that occurrence's value.</summary>
    /// <param name="choice">The occurrence that is selected.</param>
    /// <param name="expected">The value that occurrence has in the first packet.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(Occurrences.First, "10.0.0.1")]
    [Arguments(Occurrences.Last, "192.168.0.1")]
    public async Task FirstAndLastAreScalarsInJson(Occurrences choice, string expected)
    {
        string json = _RunJson(JsonExportFormat.Array, _Selected(choice));

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement value = document.RootElement[0].GetProperty("fields").GetProperty("ip.src");

        await Assert.That(value.ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(value.GetString()).IsEqualTo(expected);
    }

    /// <summary>Proves that the all-fields shape is an array of named occurrences, with a text key only where a dissector set a text.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AllFieldsAreAnArrayOfNamedOccurrencesWithTheTextKeyOnlyWhereADissectorSetOne()
    {
        string json = _RunJson(JsonExportFormat.Pretty, new ExportContent { Shape = ExportShape.AllFields, FrameBytes = false });

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement fields = document.RootElement[0].GetProperty("fields");
        JsonElement[] ipSrc = [.. fields.EnumerateArray().Where(static entry => entry.GetProperty("name").GetString() == "ip.src")];

        await Assert.That(fields.GetArrayLength()).IsGreaterThan(10);
        await Assert.That(ipSrc.Length).IsEqualTo(2);
        await Assert.That(ipSrc[1].GetProperty("occurrence").GetInt64()).IsEqualTo(1L);

        // Every occurrence is an object that carries its value.
        await Assert.That(ipSrc[0].TryGetProperty("value", out _)).IsTrue();

        // Texts are on by default (the content default) and written only where a dissector set one: the checksum of the UDP header
        // has a custom text, the source port has none, so its key is left out.
        JsonElement[] checksums = [.. fields.EnumerateArray().Where(static entry => entry.GetProperty("name").GetString() == "udp.checksum")];
        JsonElement[] ports = [.. fields.EnumerateArray().Where(static entry => entry.GetProperty("name").GetString() == "udp.srcport")];
        await Assert.That(checksums.Length).IsGreaterThan(0);
        await Assert.That(checksums.All(static entry => entry.TryGetProperty("custom_text", out _))).IsTrue();
        await Assert.That(ports.Length).IsGreaterThan(0);
        await Assert.That(ports.Any(static entry => entry.TryGetProperty("custom_text", out _))).IsFalse();
    }

    /// <summary>Proves that with custom texts on, a selected field is an object that carries its value instead of a bare scalar.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task CustomTextsMakeEveryOccurrenceAnObjectWithTheValue()
    {
        string json = _RunJson(JsonExportFormat.Array, _Selected(texts: true));

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement port = document.RootElement[0].GetProperty("fields").GetProperty("udp.dstport");

        await Assert.That(port.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(port.GetProperty("value").GetInt64()).IsEqualTo(53L);
    }

    /// <summary>Proves that feeding tree packets and feeding a tree-free parse write the same selected-field JSON.</summary>
    /// <param name="format">The JSON style under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(JsonExportFormat.Compact)]
    [Arguments(JsonExportFormat.Pretty)]
    [Arguments(JsonExportFormat.Array)]
    public async Task TheTreeFeedWritesTheSameJsonAsTheTreeFreeFeed(JsonExportFormat format)
    {
        using MemoryStream tree = new();
        using (JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(tree).WithFormat(format).WithContent(_Selected()).Build())
        {
            foreach (Frame frame in ShapeFrames.TunnelUdpCan())
            {
                _ = exporter.OnPacket(TestHarness.ParseFrame(frame));
            }

            exporter.OnFinish();
        }

        // The ids differ between the runs (dense ids per thread); the fields must not.
        static string FieldsOf(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return string.Join("|", document.RootElement.EnumerateArray().Select(static packet => packet.GetProperty("fields").GetRawText()));
        }

        await Assert.That(FieldsOf(Encoding.UTF8.GetString(tree.ToArray()))).IsEqualTo(FieldsOf(_RunJson(format, _Selected())));
    }

    /// <summary>Proves that the JSON hierarchy shape writes the packet's fields as a tree: an array of named field objects.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheJsonHierarchyShapeWritesTheTree()
    {
        using MemoryStream stream = new();
        using (JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(stream).WithFormat(JsonExportFormat.Array).Build())
        {
            _ = TreeFreeFeed.Run(exporter, [ShapeFrames.TunnelUdpCan()[1]]);
            exporter.OnFinish();
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());

        await Assert.That(document.RootElement[0].GetProperty("fields").ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(document.RootElement[0].GetProperty("fields")[0].TryGetProperty("name", out _)).IsTrue();
    }

    /// <summary>Proves that the JSON builder accepts a content with frame bytes and refuses a content whose shape does not fit.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task JsonAcceptsFrameBytesAndRefusesAMismatchedShape()
    {
        JsonExporter.Builder builder = JsonExporter.CreateBuilder();

        await Assert.That(() => builder.WithContent(JsonExporter.DefaultContent with { FrameBytes = true })).ThrowsNothing();
        await Assert.That(() => builder.WithContent(JsonExporter.DefaultContent with { Shape = ExportShape.SelectedFields })).Throws<ArgumentException>();
    }

    #endregion

    #region Text

    /// <summary>Proves that a selected-fields text line joins the occurrences of a field with a bar and leaves out fields a packet lacks.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheSelectedTextLineJoinsOccurrencesAndSkipsAbsentFields()
    {
        string text = _RunText(_Selected());
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        await Assert.That(lines.Length).IsEqualTo(3);

        // Each occurrence is the display line of the field (UI name, ": ", default text), the same line the tree shows.
        await Assert.That(lines[0]).EndsWith("  ip.src=Source Address: 10.0.0.1|Source Address: 192.168.0.1  udp.dstport=Destination Port: 53");
        await Assert.That(lines[0]).StartsWith("Packet 1  [");
        await Assert.That(lines[1]).EndsWith("  ip.src=Source Address: 192.168.1.1  udp.dstport=Destination Port: 53");

        // The CAN packet has neither field: the line is the header only.
        await Assert.That(lines[2]).StartsWith("Packet 3  [");
        await Assert.That(lines[2]).DoesNotContain("ip.src");
    }

    /// <summary>Proves that the all-fields text has one indexed line per occurrence of a field.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheAllFieldsTextHasOneLinePerOccurrence()
    {
        string text = _RunText(new ExportContent { Shape = ExportShape.AllFields, FrameBytes = false, CustomText = false });
        string[] lines = text.Split('\n');

        await Assert.That(lines.Count(static line => line.StartsWith("ip.src[0]=", StringComparison.Ordinal))).IsEqualTo(2);
        await Assert.That(lines.Contains("ip.src[1]=Source Address: 192.168.0.1")).IsTrue();
        await Assert.That(lines.Contains("udp.dstport[0]=Destination Port: 53")).IsTrue();
    }

    /// <summary>Proves that with custom texts on, a field that has a display text shows it instead of the plain value.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheCustomTextReplacesTheValueWhenTheContentAsksForIt()
    {
        string withTexts = _RunText(new ExportContent { Shape = ExportShape.AllFields, FrameBytes = false, CustomText = true });

        // With texts on, a field that has a display text shows it instead of the plain value (the rule of the tree lines).
        await Assert.That(withTexts).Contains("udp.length[0]=");
        bool hasEthernetType = withTexts.Split('\n').Any(
            static line => line.StartsWith("eth.type[0]=", StringComparison.Ordinal) && line.Contains("IPv4", StringComparison.Ordinal));
        await Assert.That(hasEthernetType).IsTrue();
    }

    /// <summary>Proves that a bytes field longer than the maximum text length is cut and ends with an ellipsis.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task LongBytesAreCutAtTheMaximumTextLength()
    {
        string text = _RunText(new ExportContent { Shape = ExportShape.AllFields, FrameBytes = false, CustomText = false }, maxTextLength: 8);

        // The raw frame is a bytes field of the all-fields shape: it is hex, cut at 8 characters, with the ellipsis.
        await Assert.That(text.Split('\n').Any(static line => line.StartsWith("frame[0]=", StringComparison.Ordinal) && line.EndsWith('…'))).IsTrue();
    }

    /// <summary>Proves that feeding tree packets and feeding a tree-free parse write the same selected-field text lines.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheTextTreeFeedWritesTheSameLinesAsTheTreeFreeFeed()
    {
        using MemoryStream tree = new();
        using (TextExporter exporter = TextExporter.CreateBuilder().ToStream(tree).WithContent(_Selected()).Build())
        {
            foreach (Frame frame in ShapeFrames.TunnelUdpCan())
            {
                _ = exporter.OnPacket(TestHarness.ParseFrame(frame));
            }

            exporter.OnFinish();
        }

        // Compare the field part of each line (the packet number and the timestamp differ between the runs).
        static string[] FieldParts(string text) =>
            [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => line[(line.IndexOf(']', StringComparison.Ordinal) + 1)..])];

        await Assert.That(FieldParts(Encoding.UTF8.GetString(tree.ToArray()))).IsEquivalentTo(FieldParts(_RunText(_Selected())));
    }

    /// <summary>Proves that the Text hierarchy shape writes an indented tree and that its builder accepts a content with frame bytes.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheTextHierarchyShapeWritesTheTreeAndAcceptsFrameBytes()
    {
        using MemoryStream stream = new();
        using (TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).Build())
        {
            _ = TreeFreeFeed.Run(exporter, [ShapeFrames.TunnelUdpCan()[1]]);
            exporter.OnFinish();
        }

        TextExporter.Builder builder = TextExporter.CreateBuilder();

        // The tree format indents the children of a protocol; the flat lines never start with spaces.
        string[] lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
        await Assert.That(lines.Any(static line => line.StartsWith("  ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(() => builder.WithContent(TextExporter.DefaultContent with { FrameBytes = true })).ThrowsNothing();
    }

    /// <summary>Proves that both the JSON and the Text exporter skip a packet that has neither a field tree nor staged fields and report why.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task APacketWithNeitherTreeNorStagedFieldsIsSkippedWithTheNamedMessageByBothFormats()
    {
        List<ExportErrorEventArgs> errors = [];
        ParseOptions noObserver = new(FieldTreeMode.Skip);
        using MemoryStream jsonStream = new();
        using MemoryStream textStream = new();

        using (JsonExporter json = JsonExporter.CreateBuilder().ToStream(jsonStream).WithContent(_Selected()).Build())
        using (TextExporter text = TextExporter.CreateBuilder().ToStream(textStream).WithContent(_Selected()).Build())
        {
            json.ItemSkipped += (_, error) => errors.Add(error);
            text.ItemSkipped += (_, error) => errors.Add(error);
            _ = TreeFreeFeed.Run(json, [ShapeFrames.TunnelUdpCan()[1]], in noObserver);
            _ = TreeFreeFeed.Run(text, [ShapeFrames.TunnelUdpCan()[1]], in noObserver);

            await Assert.That(json.PacketCount).IsEqualTo(0);
            await Assert.That(text.PacketCount).IsEqualTo(0);
        }

        await Assert.That(errors.Count).IsEqualTo(2);
        await Assert.That(errors.TrueForAll(static error => error.Message.Contains("neither a field tree nor staged fields", StringComparison.Ordinal)))
            .IsTrue();
    }

    #endregion
}

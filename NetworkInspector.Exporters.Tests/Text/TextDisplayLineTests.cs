// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using CoreField = NetworkInspector.Core.Fields.Field;

namespace NetworkInspector.Exporters.Tests.Text;

/// <summary>
/// The Text export writes display lines: a set custom text is the whole line (never prefixed with the UI name), a field without a
/// value shows its UI name, every other field shows UI name, <c>": "</c>, and the value's default string (upper-case hex for bytes).
/// The lines equal <c>Field.ToString()</c>, the same in the tree and the flat shapes, and a switched-off text or representation
/// is left out of the line.
/// </summary>
internal sealed class TextDisplayLineTests
{
    #region Exporting and expected lines

    /// <summary>Exports a packet with the Text exporter and returns the lines (without line ends).</summary>
    /// <param name="packet">The packet to export.</param>
    /// <param name="content">The content, or <see langword="null"/> for the tree at <paramref name="level"/>.</param>
    /// <param name="level">The detail level of the tree.</param>
    /// <param name="maxTextLength">The text limit; 0 is unlimited.</param>
    /// <returns>The lines of the output.</returns>
    /// <exception cref="InvalidOperationException">The exporter refused the packet.</exception>
    private static string[] _Export(
        Packet packet, ExportContent? content = null, TextDetailLevel level = TextDetailLevel.Full, int maxTextLength = 256)
    {
        using MemoryStream stream = new();
        TextExporter.Builder builder = TextExporter.CreateBuilder().ToStream(stream).WithDetailLevel(level).WithMaxTextLength(maxTextLength);
        if (content is not null)
        {
            builder = builder.WithContent(content);
        }

        using (TextExporter exporter = builder.Build())
        {
            bool accepted = exporter.OnPacket(packet);
            exporter.OnFinish();
            if (!accepted)
            {
                throw new InvalidOperationException("The Text exporter refused the packet.");
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
    }

    /// <summary>The expected tree lines of a packet: indentation, then <c>Field.ToString()</c>, for every field in depth-first order.</summary>
    /// <param name="packet">The packet.</param>
    /// <returns>One line per field.</returns>
    private static List<string> _FieldLines(Packet packet)
    {
        List<string> lines = [];
        _AddLines(packet.RootField(), 0, lines);
        return lines;
    }

    /// <summary>Appends the lines of the children of <paramref name="parent"/> and their descendants.</summary>
    /// <param name="parent">The field whose descendants are listed.</param>
    /// <param name="depth">The depth of the children; each level indents by two spaces.</param>
    /// <param name="lines">Receives one line per descendant in depth-first order.</param>
    private static void _AddLines(CoreField parent, int depth, List<string> lines)
    {
        foreach (CoreField child in parent.Children(materialize: true))
        {
            lines.Add(new string(' ', depth * 2) + child.ToString(null, CultureInfo.InvariantCulture));
            _AddLines(child, depth + 1, lines);
        }
    }

    #endregion

    #region The rule in the tree

    /// <summary>Proves that a TCP SYN with an MSS option shows the option's custom text once as the whole line, without the field label before it.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ATcpSynWithAnMssOptionShowsTheCustomTextOnceAndWithoutAPrefix()
    {
        Packet packet = PacketGenerators.CreateParsedPacket(0, FrameGenerators.BuildEthernetIpv4TcpSynFrame());

        string[] lines = _Export(packet);

        // The text of the option is the whole line: "Maximum Segment Size: 1460 bytes" once, not after a second prefix with the label.
        await Assert.That(lines.Count(static line => line.Trim() == "Maximum Segment Size: 1460 bytes")).IsEqualTo(1);
        await Assert.That(lines.Any(static line => line.Contains("Maximum Segment Size: Maximum Segment Size", StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>Proves that a protocol layer's line is its custom text alone, not prefixed with the layer's UI name.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AProtocolLayerShowsItsCustomTextAloneAsItsLine()
    {
        Packet packet = PacketGenerators.CreateEthernetUdpPackets(1)[0];

        string[] lines = _Export(packet, level: TextDetailLevel.Standard);

        await Assert.That(lines.Any(static line => line.StartsWith("Ethernet II, Src: ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(lines.Any(static line => line.StartsWith("Ethernet: ", StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>Proves that every tree line of a real packet equals the default string of its field, with no line dropped or added.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task EveryLineOfARealPacketEqualsTheDefaultStringOfItsField()
    {
        // The exporter and Core state the same rule; this compares them on every field of a real packet, at the unlimited text
        // length, so a drift of either side fails here. Bytes fields are in the list at the Full level.
        foreach (Packet packet in new[]
        {
            PacketGenerators.CreateEthernetUdpPackets(1)[0],
            PacketGenerators.CreateParsedPacket(0, FrameGenerators.BuildEthernetIpv4TcpSynFrame()),
        })
        {
            List<string> expected = _FieldLines(packet);
            string[] lines = _Export(packet, maxTextLength: 0);

            // Skip the "Packet N  [timestamp]" header; the lines of the tree follow up to the blank separator line. The whole range is
            // compared, so a line that the export drops or adds fails the count, not only a different text.
            int end = Array.IndexOf(lines, string.Empty, 1);
            string[] tree = lines[1..end];

            await Assert.That(tree).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    /// <summary>Proves that each probe field shows the line of the display rule at every detail level that lists it; the level only selects lines.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheProbeFieldsShowTheLineOfTheRuleAtEveryDetailLevelThatListsThem()
    {
        using TextProbePacketFactory probe = new();
        Packet packet = probe.CreateTreePacket();

        string[] full = _Export(packet, level: TextDetailLevel.Full);
        string[] standard = _Export(packet, level: TextDetailLevel.Standard);
        string[] summary = _Export(packet, level: TextDetailLevel.Summary);

        // custom text alone | no value: UI name | UI name, ": ", custom representation | empty text: the empty line |
        // UI name and upper-case hex | text wins over the representation.
        await Assert.That(full).Contains("Probe: a layer");
        await Assert.That(full).Contains("  Group");
        await Assert.That(full).Contains("  Plain Value: 5");
        await Assert.That(full).Contains("  Length: 8 bytes");
        await Assert.That(full).Contains("  Echo Field: Echo");
        await Assert.That(full).Contains("  ");
        await Assert.That(full).Contains("  Raw: 0A FF 00");
        await Assert.That(full).Contains("  Nine: nine");

        // The detail level selects lines, it never changes one: Standard leaves the raw byte leaf out, Summary keeps only containers.
        await Assert.That(standard).Contains("  Plain Value: 5");
        await Assert.That(standard.Contains("  Raw: 0A FF 00")).IsFalse();
        await Assert.That(standard).Contains("  Group");
        await Assert.That(summary).Contains("Probe: a layer");
        await Assert.That(summary.Contains("  Group")).IsFalse();
        await Assert.That(summary.Contains("  Plain Value: 5")).IsFalse();
    }

    /// <summary>Proves that a layer that holds bytes and has no custom text shows its UI name and the upper-case hex of its bytes.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ALayerThatHoldsBytesAndHasNoCustomTextShowsItsBytesLikeAnyBytesField()
    {
        using TextProbePacketFactory probe = new();
        Packet packet = probe.CreateTreePacket();

        string[] standard = _Export(packet, level: TextDetailLevel.Standard);
        string[] summary = _Export(packet, level: TextDetailLevel.Summary);

        // The detail level lists the container, the rule writes its line: UI name, ": ", and the upper-case hex of its bytes. A
        // childless bytes field is a raw buffer that Standard leaves out; a container with a child is listed.
        await Assert.That(standard).Contains("  Bytes Layer: 0A FF 00");
        await Assert.That(standard).Contains("    Leaf: 1");
        await Assert.That(standard.Contains("  Raw: 0A FF 00")).IsFalse();
        await Assert.That(summary.Contains("  Bytes Layer: 0A FF 00")).IsFalse();
    }

    /// <summary>Proves that a custom text longer than the limit is cut after the limit and ends with an ellipsis that the limit does not count.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ACustomTextLongerThanTheLimitIsCutAfterTheLimitWithTheEllipsis()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "text", containerText: "abcdefghijklmnopqrst");

        string[] lines = _Export(packet, level: TextDetailLevel.Standard, maxTextLength: 10);

        // A container line is its custom text alone: cut after ten characters, the ellipsis is not counted.
        await Assert.That(lines).Contains("abcdefghij…");
    }

    /// <summary>Proves that control characters in a custom text are written as escape sequences, so no raw control character reaches the output.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ACustomTextWithControlCharactersIsWrittenEscapedAsTheWholeLine()
    {
        using CustomFieldPacketFactory factory = new();
        Packet packet = factory.Create(1, "text", containerText: "a\r\nb\u001bc");

        string[] lines = _Export(packet, level: TextDetailLevel.Standard);

        await Assert.That(lines).Contains("a\\r\\nb\\x1bc");
        await Assert.That(lines.Any(static line => line.Contains('\u001b', StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>
    /// Proves that at every detail level each line is the default string of a field of the packet, at the depth of that field, and that
    /// the levels only select lines: Summary is a subset of Standard, Standard a subset of Full, and Full lists every field once.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task EveryLineAtEveryDetailLevelIsTheStringOfAFieldAndTheLevelsOnlySelect()
    {
        using TextProbePacketFactory probe = new();
        Packet packet = probe.CreateTreePacket();
        List<string> everyField = _FieldLines(packet);

        string[] full = _TreeLines(_Export(packet, level: TextDetailLevel.Full, maxTextLength: 0));
        string[] standard = _TreeLines(_Export(packet, level: TextDetailLevel.Standard, maxTextLength: 0));
        string[] summary = _TreeLines(_Export(packet, level: TextDetailLevel.Summary, maxTextLength: 0));

        // Full lists every field once, in depth-first order, with exactly the line of Field.ToString().
        await Assert.That(full).IsEquivalentTo(everyField, CollectionOrdering.Matching);

        // The lower levels select lines of that list; a level never changes a line (a changed line would be missing from the list).
        await Assert.That(_LinesMissingFrom(standard, full)).IsEmpty();
        await Assert.That(_LinesMissingFrom(summary, standard)).IsEmpty();
        await Assert.That(standard.Length).IsLessThan(full.Length);
        await Assert.That(summary.Length).IsLessThan(standard.Length);
    }

    /// <summary>
    /// Proves that a field id that the stack does not know has no label in the Text export: its line is the value alone (no label, no
    /// separator), the empty line without a value, and the custom text when one is set, each equal to <c>Field.ToString()</c>.
    /// </summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AFieldIdThatTheStackDoesNotKnowIsWrittenWithoutALabel()
    {
        using TextProbePacketFactory probe = new();
        Packet packet = probe.CreateTreePacket(withUnknownIds: true);
        List<string> everyField = _FieldLines(packet);

        string[] full = _TreeLines(_Export(packet, level: TextDetailLevel.Full, maxTextLength: 0));

        await Assert.That(full).IsEquivalentTo(everyField, CollectionOrdering.Matching);
        await Assert.That(everyField.TakeLast(TextProbePacketFactory.UnknownIdsLines.Count).Select(static line => line.Trim()))
            .IsEquivalentTo(TextProbePacketFactory.UnknownIdsLines, CollectionOrdering.Matching);
    }

    /// <summary>The tree lines of an export: the lines after the packet header up to the blank separator line.</summary>
    /// <param name="exportLines">All lines of the export of one packet.</param>
    /// <returns>The lines of the field tree.</returns>
    private static string[] _TreeLines(string[] exportLines)
    {
        int end = Array.IndexOf(exportLines, string.Empty, 1);
        return exportLines[1..end];
    }

    /// <summary>Lists the lines of <paramref name="lines"/> that <paramref name="allowed"/> does not contain, counting duplicates.</summary>
    /// <param name="lines">The lines to check.</param>
    /// <param name="allowed">The lines that may occur, each as often as it is listed.</param>
    /// <returns>The lines that have no counterpart in <paramref name="allowed"/>; empty when every line is allowed.</returns>
    private static List<string> _LinesMissingFrom(string[] lines, string[] allowed)
    {
        Dictionary<string, int> remaining = [];
        foreach (string line in allowed)
        {
            remaining[line] = remaining.GetValueOrDefault(line) + 1;
        }

        List<string> missing = [];
        foreach (string line in lines)
        {
            if (remaining.GetValueOrDefault(line) > 0)
            {
                remaining[line]--;
            }
            else
            {
                missing.Add(line);
            }
        }

        return missing;
    }

    #endregion

    #region Switches

    /// <summary>Proves that a switched-off custom text or custom representation is left out of the line and the default text shows instead.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ASwitchedOffTextOrRepresentationIsLeftOutOfTheLine()
    {
        using TextProbePacketFactory probe = new();
        Packet packet = probe.CreateTreePacket();
        ExportContent noText = TextExporter.DefaultContent with { CustomText = false };
        ExportContent noRepresentation = TextExporter.DefaultContent with { CustomRepresentation = false };
        ExportContent neither = TextExporter.DefaultContent with { CustomText = false, CustomRepresentation = false };

        string[] withoutText = _Export(packet, noText);
        string[] withoutRepresentation = _Export(packet, noRepresentation);
        string[] withoutBoth = _Export(packet, neither);

        // Text off: the line is UI name and the representation (or the default text when none) — the text is not used.
        await Assert.That(withoutText).Contains("  Length: 8");
        await Assert.That(withoutText).Contains("  Both: nine");
        await Assert.That(withoutText).Contains("  Empty Texts: ");
        await Assert.That(withoutText).Contains("Probe");
        await Assert.That(withoutText).Contains("  Echo Field: Echo");

        // Representation off: the custom text still wins where set; where only a representation was set, the default text shows.
        await Assert.That(withoutRepresentation).Contains("  Echo Field: e");
        await Assert.That(withoutRepresentation).Contains("  Length: 8 bytes");
        await Assert.That(withoutRepresentation).Contains("  Nine: nine");
        await Assert.That(withoutRepresentation).Contains("Probe: a layer");

        // Both off: every line is UI name and the default text of the value.
        await Assert.That(withoutBoth).Contains("  Plain Value: 5");
        await Assert.That(withoutBoth).Contains("  Length: 8");
        await Assert.That(withoutBoth).Contains("  Echo Field: e");
        await Assert.That(withoutBoth).Contains("  Empty Texts: 7");
        await Assert.That(withoutBoth).Contains("  Both: 9");
        await Assert.That(withoutBoth).Contains("  Raw: 0A FF 00");
    }

    #endregion

    #region Flat shapes

    /// <summary>Proves that the flat shapes write the same line for a field as the tree and as its default string, for both feeds.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheFlatShapesWriteTheSameLineForAFieldAsTheTreeAndAsFieldToString()
    {
        using TextProbePacketFactory probe = new();
        Packet treePacket = probe.CreateTreePacket();
        Dictionary<string, string> expected = [];
        foreach (CoreField field in treePacket.IterFieldsDfs(materialize: true))
        {
            string? name = field.FieldInfo?.Name;
            if (name is not null && TextProbePacketFactory.Expected.ContainsKey(name))
            {
                expected[name] = field.ToString(null, CultureInfo.InvariantCulture);
            }
        }

        foreach (bool treeFree in new[] { false, true })
        {
            string[] all;
            string[] selected;
            using (MemoryStream stream = new())
            {
                using (TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).WithContent(probe.FlatContent(ExportShape.AllFields)).Build())
                {
                    probe.Feed(exporter, treeFree);
                    exporter.OnFinish();
                }

                all = Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
            }

            using (MemoryStream stream = new())
            {
                using (TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).WithContent(probe.FlatContent(ExportShape.SelectedFields)).Build())
                {
                    probe.Feed(exporter, treeFree);
                    exporter.OnFinish();
                }

                selected = Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
            }

            foreach ((string name, string line) in expected)
            {
                await Assert.That(all).Contains(Invariant($"{name}[0]={line}"));
                await Assert.That(selected[0].Contains(Invariant($"  {name}={line}"), StringComparison.Ordinal)).IsTrue();
            }
        }
    }

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Direct tests for <see cref="HeuristicProtocolTable"/> metadata, lookup, and matching.
/// </summary>
internal sealed class HeuristicProtocolTableTests
{
    private static HeuristicProtocolTable _CreateTable(string? description = "Heuristic dispatch")
    {
        HeuristicProtocolTableId id = new(0);
        ProtocolId owner = new(1);
        HeuristicProtocolTableInfo info = new(id, "eth.heur", "Ethernet Heuristic", description, owner);
        return new HeuristicProtocolTable(info);
    }

    [Test]
    public async Task Properties_ExposeInfoMetadata()
    {
        HeuristicProtocolTable table = _CreateTable("Layer-2 heuristics");

        await Assert.That(table.Info.Name).IsEqualTo("eth.heur");
        await Assert.That(table.Id.Value).IsEqualTo(0);
        await Assert.That(table.Name).IsEqualTo("eth.heur");
        await Assert.That(table.UiName).IsEqualTo("Ethernet Heuristic");
        await Assert.That(table.Description).IsEqualTo("Layer-2 heuristics");
        await Assert.That(table.IsEmpty).IsTrue();
        await Assert.That(table.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FindByName_Miss_ReturnsNull()
    {
        HeuristicProtocolTable table = _CreateTable();
        table.AddEntry(new HeuristicParserEntry(new NamedHeuristicParser("known", ProtocolId.Invalid, match: false)));

        await Assert.That(table.FindByName("missing")).IsNull();
    }

    [Test]
    public async Task FindByName_Hit_ReturnsEntry()
    {
        HeuristicProtocolTable table = _CreateTable();
        HeuristicParserEntry entry = new(new NamedHeuristicParser("known", new ProtocolId(3), match: false));
        table.AddEntry(entry);

        HeuristicParserEntry? found = table.FindByName("known");
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Name).IsEqualTo("known");
    }

    [Test]
    public async Task TryMatchWithName_NoMatch_ReturnsNull()
    {
        HeuristicProtocolTable table = _CreateTable();
        table.AddEntry(new HeuristicParserEntry(new NamedHeuristicParser("miss", ProtocolId.Invalid, match: false)));

        (ProtocolId Id, string Name)? result = table.TryMatchWithName(new byte[] { 0x01 });
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task TryMatchWithName_FirstMatch_ReturnsIdAndName()
    {
        ProtocolId matched = new(9);
        HeuristicProtocolTable table = _CreateTable();
        table.AddEntry(new HeuristicParserEntry(new NamedHeuristicParser("first", ProtocolId.Invalid, match: false)));
        table.AddEntry(new HeuristicParserEntry(new NamedHeuristicParser("winner", matched, match: true)));

        (ProtocolId Id, string Name)? result = table.TryMatchWithName(new byte[] { 0xAA });
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.Id).IsEqualTo(matched);
        await Assert.That(result.Value.Name).IsEqualTo("winner");
    }

    private sealed class NamedHeuristicParser(string name, ProtocolId protocolId, bool match) : IHeuristicParser
    {
        public ProtocolId ProtocolId => protocolId;
        public string Name => name;
        public string UiName => name;
        public string? Description => null;

        public bool Test(ReadOnlyMemory<byte> data) => match;
    }
}

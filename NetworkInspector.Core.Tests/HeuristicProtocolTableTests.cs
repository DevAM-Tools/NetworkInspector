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
    public async Task TryMatch_NoMatch_ReturnsFalseAndInvalid()
    {
        HeuristicProtocolTable table = _CreateTable();
        table.AddEntry(new HeuristicParserEntry(new NamedHeuristicParser("miss", new ProtocolId(3), match: false)));

        bool matched = table.TryMatch(new byte[] { 0x01 }, out ProtocolId protocolId);

        await Assert.That(matched).IsFalse();
        await Assert.That(protocolId).IsEqualTo(ProtocolId.Invalid);
    }

    [Test]
    public async Task TryMatch_EmptyTable_ReturnsFalse()
    {
        HeuristicProtocolTable table = _CreateTable();

        bool matched = table.TryMatch(new byte[] { 0x01 }, out ProtocolId protocolId);

        await Assert.That(matched).IsFalse();
        await Assert.That(protocolId).IsEqualTo(ProtocolId.Invalid);
    }

    [Test]
    public async Task TryMatch_FirstMatchWinsInRegistrationOrder()
    {
        ProtocolId winner = new(9);
        HeuristicProtocolTable table = _CreateTable();
        table.AddEntry(new HeuristicParserEntry(new NamedHeuristicParser("first", new ProtocolId(4), match: false)));
        table.AddEntry(new HeuristicParserEntry(new NamedHeuristicParser("winner", winner, match: true)));
        table.AddEntry(new HeuristicParserEntry(new NamedHeuristicParser("later", new ProtocolId(10), match: true)));

        bool matched = table.TryMatch(new byte[] { 0xAA }, out ProtocolId protocolId);

        await Assert.That(matched).IsTrue();
        await Assert.That(protocolId).IsEqualTo(winner);
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

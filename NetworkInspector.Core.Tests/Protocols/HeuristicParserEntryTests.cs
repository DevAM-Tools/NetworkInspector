// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests.Protocols;

/// <summary>
/// Exit-point coverage for <see cref="HeuristicParserEntry"/> and <see cref="IHeuristicParser"/>.
/// </summary>
internal sealed class HeuristicParserEntryTests
{
    [Test]
    public async Task HeuristicParserEntry_DefaultDescription_IsNull()
    {
        IHeuristicParser parser = new DefaultDescriptionHeuristicParser(new ProtocolId(1));
        await Assert.That(parser.Description).IsNull();
    }

    [Test]
    public async Task HeuristicParserEntry_ExposesParserMetadata()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubFrameProto frame = new();
        ProtocolId frameId = builder.RegisterProtocol(frame);
        HeuristicProtocolTableId tableId = builder.RegisterHeuristicProtocolTable(
            frameId, "heur.test", "Heuristic Test", description: "table desc");

        DescribedHeuristicParser parser = new(frameId);
        builder.RegisterHeuristicParser(tableId, parser);

        using Stack stack = builder.Build();
        HeuristicProtocolTable? table = stack.GetHeuristicProtocolTable(tableId);
        await Assert.That(table).IsNotNull();

        HeuristicParserEntry? entry = table!.FindByName("heur.described");
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.UiName).IsEqualTo("Described Heuristic");
        await Assert.That(entry.Description).IsEqualTo("parser description");
        await Assert.That(entry.Test(new byte[] { 0x01 })).IsTrue();
    }

    private sealed class StubFrameProto : IProtocol
    {
        public string Name => "frame";
        public string UiName => "Frame";
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;
    }

    private sealed class DefaultDescriptionHeuristicParser(ProtocolId protocolId) : IHeuristicParser
    {
        public ProtocolId ProtocolId => protocolId;
        public string Name => "heur.default";
        public string UiName => "Default Description";
        public bool Test(ReadOnlyMemory<byte> data) => data.Length == 0;
    }

    private sealed class DescribedHeuristicParser(ProtocolId protocolId) : IHeuristicParser
    {
        public ProtocolId ProtocolId => protocolId;
        public string Name => "heur.described";
        public string UiName => "Described Heuristic";
        public string? Description => "parser description";

        public bool Test(ReadOnlyMemory<byte> data) => data.Length > 0;
    }
}

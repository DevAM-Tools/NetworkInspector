// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests.Protocols;

/// <summary>
/// Exit-point coverage for <see cref="RootProtocol"/>.
/// </summary>
internal sealed class RootProtocolTests
{
    [Test]
    public async Task Parse_ReturnsZero()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        using Stack stack = builder.Build();

        RootProtocol root = new();
        byte[] data = [0x01];
        Frame frame = Frame.Create(
            new FrameId(1), Timestamp.FromSecs(0), data,
            LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        Packet packet = new(new PacketId(1), stack, frame);

        bool consumedOk;
        int consumed;
        {
            MutField rootField = packet.RootFieldMut();
            ParseResult result = root.Parse(in rootField, data, new ParseContext(stack));
            consumedOk = result.TryGetConsumed(out consumed);
        }

        await Assert.That(consumedOk).IsTrue();
        await Assert.That(consumed).IsEqualTo(0);
    }
}

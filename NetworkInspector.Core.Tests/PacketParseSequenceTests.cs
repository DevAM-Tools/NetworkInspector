// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// First parses on a <see cref="Stack"/> must use dense packet ids starting at 0.
/// A jump is a caller contract violation; a later parse of an already first-parsed id is a replay.
/// </summary>
internal sealed class PacketParseSequenceTests
{
    [Test]
    public async Task ParseFrame_FirstIdMustBeZero()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack);

        InvalidOperationException? thrown = null;
        try
        {
            Packet.ParseFrame(new PacketId(1), stack, frame);
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message).Contains("next expected 0", StringComparison.Ordinal);
    }

    [Test]
    public async Task ParseFrame_JumpAfterZero_Throws()
    {
        using Stack stack = _BuildStack();
        Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack, frameId: 0));

        InvalidOperationException? thrown = null;
        try
        {
            Packet.ParseFrame(new PacketId(2), stack, _MakeFrame(stack, frameId: 2));
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message).Contains("next expected 1", StringComparison.Ordinal);
    }

    [Test]
    public async Task ParseFrame_DenseThenReplay_DoesNotThrow()
    {
        using Stack stack = _BuildStack();
        Packet first = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack, frameId: 0));
        Packet second = Packet.ParseFrame(new PacketId(1), stack, _MakeFrame(stack, frameId: 1));
        Packet replay = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack, frameId: 0));

        await Assert.That(first.Id).IsEqualTo(new PacketId(0));
        await Assert.That(second.Id).IsEqualTo(new PacketId(1));
        await Assert.That(replay.Id).IsEqualTo(new PacketId(0));
        await Assert.That(replay.IsFinalized).IsTrue();
    }

    [Test]
    public async Task ParseFrameIndexed_SecondCallForSameId_DoesNotGrowIndex()
    {
        using Stack stack = _BuildStack();
        PacketIndex index = new(stack);
        Frame frame = _MakeFrame(stack, frameId: 0);
        ProtocolId? ethId = stack.GetProtocolId("eth");

        Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);
        long afterFirst = index.ProtocolCardinality(ethId!.Value);

        Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);
        long afterReplay = index.ProtocolCardinality(ethId.Value);

        await Assert.That(afterReplay).IsEqualTo(afterFirst);
        await Assert.That(afterFirst).IsGreaterThan(0L);
    }

    [Test]
    public async Task ParseFrameIndexed_ConcurrentReplay_LeavesIndexUnchanged()
    {
        using Stack stack = _BuildStack();
        PacketIndex index = new(stack);
        Frame frame = _MakeFrame(stack, frameId: 0);
        ProtocolId? ethId = stack.GetProtocolId("eth");

        Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);
        long afterFirst = index.ProtocolCardinality(ethId!.Value);

        await Parallel.ForAsync(0, 8, (_, _) =>
        {
            Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);
            return ValueTask.CompletedTask;
        });

        await Assert.That(index.ProtocolCardinality(ethId.Value)).IsEqualTo(afterFirst);
    }

    private static Stack _BuildStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static Frame _MakeFrame(Stack stack, int frameId = 0)
    {
        byte[] data = new byte[64];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), 0x0800);
        return Frame.Create(
            new FrameId(frameId),
            Timestamp.FromSecs(frameId),
            data,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
    }
}

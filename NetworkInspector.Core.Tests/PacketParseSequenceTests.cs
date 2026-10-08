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
        ParseOptions options = new();

        bool parsed = Packet.TryParse(
            new PacketId(1),
            stack,
            frame,
            in options,
            out Packet? _,
            out ParseFailure failure);

        await Assert.That(parsed).IsFalse();
        await Assert.That(failure).IsEqualTo(ParseFailure.ParseIdGap);
    }

    [Test]
    public async Task ParseFrame_JumpAfterZero_Throws()
    {
        using Stack stack = _BuildStack();
        {
            ParseOptions options = new();
            if (!Packet.TryParse(
                new PacketId(0),
                stack,
                _MakeFrame(stack, frameId: 0),
                in options,
                out Packet? _,
                out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }
        }

        ParseOptions jumpOptions = new();
        bool jumped = Packet.TryParse(
            new PacketId(2),
            stack,
            _MakeFrame(stack, frameId: 2),
            in jumpOptions,
            out Packet? _,
            out ParseFailure jumpFailure);

        await Assert.That(jumped).IsFalse();
        await Assert.That(jumpFailure).IsEqualTo(ParseFailure.ParseIdGap);
    }

    [Test]
    public async Task ParseFrame_DenseThenReplay_DoesNotThrow()
    {
        using Stack stack = _BuildStack();
        Packet first;
        {
            ParseOptions options = new();
            if (!Packet.TryParse(
                new PacketId(0),
                stack,
                _MakeFrame(stack, frameId: 0),
                in options,
                out Packet? parsed,
                out ParseFailure failure) || parsed is null)
            {
                throw new InvalidOperationException(failure.ToString());
            }

            first = parsed;
        }
        Packet second;
        {
            ParseOptions options = new();
            if (!Packet.TryParse(
                new PacketId(1),
                stack,
                _MakeFrame(stack, frameId: 1),
                in options,
                out Packet? parsed,
                out ParseFailure failure) || parsed is null)
            {
                throw new InvalidOperationException(failure.ToString());
            }

            second = parsed;
        }
        Packet replay;
        {
            ParseOptions options = new();
            if (!Packet.TryParse(
                new PacketId(0),
                stack,
                _MakeFrame(stack, frameId: 0),
                in options,
                out Packet? parsed,
                out ParseFailure failure) || parsed is null)
            {
                throw new InvalidOperationException(failure.ToString());
            }

            replay = parsed;
        }

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

        {
            ParseOptions options = new(index: index);
            if (!Packet.TryParse(
                new PacketId(0),
                stack,
                frame,
                in options,
                out Packet? _,
                out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }
        }
        long afterFirst = index.ProtocolCardinality(ethId!.Value);

        {
            ParseOptions options = new(index: index);
            if (!Packet.TryParse(
                new PacketId(0),
                stack,
                frame,
                in options,
                out Packet? _,
                out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }
        }
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

        {
            ParseOptions options = new(index: index);
            if (!Packet.TryParse(
                new PacketId(0),
                stack,
                frame,
                in options,
                out Packet? _,
                out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }
        }
        long afterFirst = index.ProtocolCardinality(ethId!.Value);

        await Parallel.ForAsync(0, 8, (_, _) =>
        {
            {
                ParseOptions options = new(index: index);
                if (!Packet.TryParse(
                    new PacketId(0),
                    stack,
                    frame,
                    in options,
                    out Packet? _,
                    out ParseFailure failure))
                {
                    throw new InvalidOperationException(failure.ToString());
                }
            }
            return ValueTask.CompletedTask;
        });

        await Assert.That(index.ProtocolCardinality(ethId.Value)).IsEqualTo(afterFirst);
    }

    [Test]
    public async Task ParseFrameInvalidPacketIdReturnsInvalidPacketId()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack);
        ParseOptions options = new();

        bool parsed = Packet.TryParse(
            PacketId.Invalid,
            stack,
            frame,
            in options,
            out Packet? packet,
            out ParseFailure failure);

        await Assert.That(parsed).IsFalse();
        await Assert.That(packet).IsNull();
        await Assert.That(failure).IsEqualTo(ParseFailure.InvalidPacketId);

        bool next = Packet.TryParse(
            new PacketId(0),
            stack,
            frame,
            in options,
            out Packet? nextPacket,
            out ParseFailure nextFailure);

        await Assert.That(next).IsTrue();
        await Assert.That(nextPacket).IsNotNull();
        await Assert.That(nextFailure).IsEqualTo(ParseFailure.None);
    }

    [Test]
    public async Task ParseFrameRecycleInvalidPacketIdLeavesPacketUnchanged()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, frameId: 0);
        ParseOptions options = new();
        Packet packet;
        {
            if (!Packet.TryParse(
                new PacketId(0),
                stack,
                frame,
                in options,
                out Packet? parsed,
                out ParseFailure firstFailure) || parsed is null)
            {
                throw new InvalidOperationException(firstFailure.ToString());
            }

            packet = parsed;
        }

        bool recycled = Packet.TryParse(
            packet,
            PacketId.Invalid,
            stack,
            frame,
            in options,
            out ParseFailure failure);

        await Assert.That(recycled).IsFalse();
        await Assert.That(failure).IsEqualTo(ParseFailure.InvalidPacketId);
        await Assert.That(packet.Id).IsEqualTo(new PacketId(0));
        await Assert.That(packet.IsFinalized).IsTrue();
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

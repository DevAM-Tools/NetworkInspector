// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// The two <see cref="Packet.TryParse(PacketId, Stack, Frame, in ParseOptions, out Packet?, out ParseFailure)"/>
/// calls: preconditions return a code, and cache and index travel with the options.
/// </summary>
internal sealed class PacketTryParseTests
{
    #region Helpers

    private static Stack _BuildStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static Frame _Frame(Stack stack, int id)
    {
        return Frame.Create(
            new FrameId(id),
            Timestamp.FromSecs(id),
            FrameBuilders.GenerateStaticUdpIpv6Frame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
    }

    #endregion

    #region Parse calls

    [Test]
    public async Task TryParse_ReturnsPacketOnThePassedStack()
    {
        using Stack stack = _BuildStack();
        ParseOptions options = new();

        bool parsed = Packet.TryParse(
            new PacketId(0),
            stack,
            _Frame(stack, 0),
            in options,
            out Packet? packet,
            out ParseFailure failure);

        await Assert.That(parsed).IsTrue();
        await Assert.That(failure).IsEqualTo(ParseFailure.None);
        await Assert.That(packet).IsNotNull();
        await Assert.That(packet!.Stack).IsSameReferenceAs(stack);
        await Assert.That(packet.HasFieldTree).IsTrue();
    }

    [Test]
    public async Task TryParse_IdJump_ReturnsGapWithoutThrowing()
    {
        using Stack stack = _BuildStack();
        ParseOptions options = new();
        Frame first = _Frame(stack, 0);
        bool started = Packet.TryParse(new PacketId(0), stack, first, in options, out Packet? _, out ParseFailure _);
        await Assert.That(started).IsTrue();

        bool jumped = false;
        ParseFailure failure = ParseFailure.None;
        Exception? thrown = null;
        try
        {
            jumped = Packet.TryParse(
                new PacketId(2),
                stack,
                _Frame(stack, 2),
                in options,
                out Packet? _,
                out failure);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNull();
        await Assert.That(jumped).IsFalse();
        await Assert.That(failure).IsEqualTo(ParseFailure.ParseIdGap);
    }

    [Test]
    public async Task TryParse_InvalidFrame_ReturnsInvalidFrame()
    {
        using Stack stack = _BuildStack();
        ParseOptions options = new();

        bool parsed = Packet.TryParse(
            new PacketId(0),
            stack,
            default,
            in options,
            out Packet? packet,
            out ParseFailure failure);

        await Assert.That(parsed).IsFalse();
        await Assert.That(failure).IsEqualTo(ParseFailure.InvalidFrame);
        await Assert.That(packet).IsNull();
    }

    [Test]
    public async Task TryParse_UnsealedRecycle_LeavesIdUnchanged()
    {
        using Stack stack = _BuildStack();
        ParseOptions options = new();
        bool started = Packet.TryParse(
            new PacketId(0),
            stack,
            _Frame(stack, 0),
            in options,
            out Packet? packet,
            out ParseFailure _);
        await Assert.That(started).IsTrue();

        // Force the unsealed state the recycle gate rejects. The id must stay 0.
        packet!.GetType().GetField("_Finalized", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(packet, 0);

        bool recycled = Packet.TryParse(
            packet,
            new PacketId(1),
            stack,
            _Frame(stack, 1),
            in options,
            out ParseFailure failure);

        await Assert.That(recycled).IsFalse();
        await Assert.That(failure).IsEqualTo(ParseFailure.NotFinalized);
        await Assert.That(packet.Id).IsEqualTo(new PacketId(0));
    }

    [Test]
    public async Task TryParse_DissectorError_ReturnsTrueAndStoresTheError()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        RejectProtocol reject = new();
        ProtocolId protocolId = builder.RegisterProtocol(reject);
        using Stack stack = builder.Build();
        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            new byte[16],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new(firstProtocol: protocolId);

        bool parsed = Packet.TryParse(
            new PacketId(0),
            stack,
            frame,
            in options,
            out Packet? packet,
            out ParseFailure failure);

        await Assert.That(parsed).IsTrue();
        await Assert.That(failure).IsEqualTo(ParseFailure.None);
        bool hasError = packet!.TryGetFieldValue(stack.PacketErrorFieldId, out _, materialize: false);
        await Assert.That(hasError).IsTrue();
    }

    [Test]
    public async Task TryParse_CacheAndIndexChangePerFrame()
    {
        using Stack stack = _BuildStack();
        FieldId port = stack.GetFieldId("udp.srcport")!.Value;
        ValueCache cacheA = new(stack, [new ValueCacheFieldConfig(port)]);
        ValueCache cacheB = new(stack, [new ValueCacheFieldConfig(port)]);
        PacketIndex indexA = new(stack);
        ParseOptions shared = new(FieldTreeMode.Build);
        ParseOptions first = shared with { Cache = cacheA, Index = indexA };
        ParseOptions second = shared with { Cache = cacheB, Index = null };
        ProtocolId? eth = stack.GetProtocolId("eth");

        bool parsed0 = Packet.TryParse(new PacketId(0), stack, _Frame(stack, 0), in first, out _, out ParseFailure failure0);
        long afterFirst = indexA.ProtocolCardinality(eth!.Value);
        bool parsed1 = Packet.TryParse(new PacketId(1), stack, _Frame(stack, 1), in second, out _, out ParseFailure failure1);
        long afterSecond = indexA.ProtocolCardinality(eth.Value);

        await Assert.That(parsed0).IsTrue();
        await Assert.That(parsed1).IsTrue();
        await Assert.That(failure0).IsEqualTo(ParseFailure.None);
        await Assert.That(failure1).IsEqualTo(ParseFailure.None);
        await Assert.That(cacheA.GetSeries<ulong>(port).Count).IsEqualTo(1);
        await Assert.That(cacheB.GetSeries<ulong>(port).Count).IsEqualTo(1);
        await Assert.That(afterSecond).IsEqualTo(afterFirst);
        await Assert.That(afterFirst).IsGreaterThan(0L);
    }

    [Test]
    public async Task TryParse_Recycle_ReusesTheSameInstance()
    {
        using Stack stack = _BuildStack();
        ParseOptions options = new(FieldTreeMode.Skip);
        bool started = Packet.TryParse(
            new PacketId(0),
            stack,
            _Frame(stack, 0),
            in options,
            out Packet? packet,
            out ParseFailure _);
        await Assert.That(started).IsTrue();

        bool again = Packet.TryParse(
            packet!,
            new PacketId(1),
            stack,
            _Frame(stack, 1),
            in options,
            out ParseFailure failure);

        await Assert.That(again).IsTrue();
        await Assert.That(failure).IsEqualTo(ParseFailure.None);
        await Assert.That(packet!.Id).IsEqualTo(new PacketId(1));
        await Assert.That(packet.HasFieldTree).IsFalse();
    }

    #endregion
}

/// <summary>Dissector that records a protocol error and still lets the parse return.</summary>
internal sealed class RejectProtocol : IProtocol
{
    private int _Resets;

    /// <inheritdoc />
    public string Name => "reject";

    /// <inheritdoc />
    public string UiName => "Reject";

    /// <inheritdoc />
    public void ResetParseState()
    {
        _Resets++;
    }

    /// <inheritdoc />
    public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
    {
        _ = protocolId;
        _Resets++;
    }

    /// <inheritdoc />
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        return ParseError.Custom(Name, "rejected");
    }
}

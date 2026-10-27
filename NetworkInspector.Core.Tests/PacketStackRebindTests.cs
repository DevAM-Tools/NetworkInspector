// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// A recycled packet that meets a stack of the same <see cref="FrameInterfaceRegistry"/> is rebound to it (a session restart
/// replaces the stack and keeps the registry), and a stack of another registry is still refused with the packet untouched.
/// </summary>
internal sealed class PacketStackRebindTests
{
    #region Fixtures

    /// <summary>
    /// Ethernet-level protocol that registers its own fields, so that a stack built around it assigns other field ids than the
    /// standard stack does (ids are dense counters per stack).
    /// </summary>
    private sealed class MarkerProtocol : IProtocol
    {
        private FieldId _MarkerFieldId;

        public string Name => "marker";

        public string UiName => "Marker";

        internal void RegisterFields(StackBuilder builder, ProtocolId protocolId)
            => _MarkerFieldId = builder.RegisterField(protocolId, "marker.length", "Length", FieldType.U64);

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            _ = parentField.Append(_MarkerFieldId, FieldValue.NewU64((ulong)data.Length));
            return data.Length;
        }

        public void OnShutdown(Stack stack)
        {
        }
    }

    /// <summary>Builds the standard stack on <paramref name="registry"/>.</summary>
    private static Stack _BuildStandard(FrameInterfaceRegistry registry)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, registry);
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    /// <summary>Builds a stack around <see cref="MarkerProtocol"/> on <paramref name="registry"/>; its field ids differ from the standard stack's.</summary>
    private static Stack _BuildMarker(FrameInterfaceRegistry registry)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, registry);
        FrameProtocol frame = new();
        ProtocolId frameId = builder.RegisterProtocol(frame);
        frame.RegisterFields(builder, frameId);
        MarkerProtocol marker = new();
        ProtocolId markerId = builder.RegisterProtocol(marker);
        marker.RegisterFields(builder, markerId);
        builder.RegisterParserInU64TableByName(FrameProtocol.LinkTypeTableName, (ulong)LinkType.Ethernet, markerId);
        return builder.Build();
    }

    private static Frame _MakeFrame(Stack stack, int frameId = 1) =>
        Frame.Create(
            new FrameId(frameId),
            Timestamp.FromSecs(frameId),
            FrameBuilders.GenerateStaticUdpFrame(64),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    private static Packet _Parse(PacketId id, Stack stack, Frame frame, in ParseOptions options)
    {
        if (!Packet.TryParse(id, stack, frame, in options, out Packet? packet, out ParseFailure failure) || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet;
    }

    private static string[] _FieldNames(Packet packet, Stack stack)
    {
        List<string> names = [];
        foreach (Field field in packet.IterFieldsFlat(materialize: true))
        {
            names.Add(stack.GetField(field.FieldId)?.Name ?? "?");
        }

        return [.. names];
    }

    #endregion

    #region Rebind

    [Test]
    public async Task RecycleOntoAStackOfTheSameRegistryRebindsThePacketAndShowsTheNewStacksFields()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stackA = _BuildStandard(registry);
        using Stack stackB = _BuildMarker(registry);
        ParseOptions firstOptions = new();
        _ = _Parse(new PacketId(0), stackA, _MakeFrame(stackA, 1), in firstOptions); // first parses on a stack are dense: 0, 1, ...
        Packet packet = _Parse(new PacketId(1), stackA, _MakeFrame(stackA, 2), in firstOptions);
        string[] before = _FieldNames(packet, stackA);

        ParseOptions options = new();
        bool recycled = Packet.TryParse(packet, new PacketId(0), stackB, _MakeFrame(stackB, 3), in options, out ParseFailure failure);
        string[] after = _FieldNames(packet, stackB);

        await Assert.That(recycled).IsTrue();
        await Assert.That(failure).IsEqualTo(ParseFailure.None);
        await Assert.That(ReferenceEquals(packet.Stack, stackB)).IsTrue();
        await Assert.That(packet.Id.Value).IsEqualTo(0);
        await Assert.That(before).Contains("udp.dstport");
        await Assert.That(after).Contains("marker.length");
        await Assert.That(after.Contains("udp.dstport")).IsFalse();
        await Assert.That(packet.FirstProtocolOverride).IsEqualTo(ProtocolId.Invalid);
    }

    [Test]
    public async Task RecycleOntoAnotherStackAllocatesLessThanAFreshParseOfThatStack()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stackA = _BuildStandard(registry);
        using Stack stackB = _BuildMarker(registry);
        ParseOptions options = new();
        Packet recycle = _Parse(new PacketId(0), stackA, _MakeFrame(stackA, 1), in options);
        Frame frameB = _MakeFrame(stackB, 2);

        // Warm both paths once (JIT, first-use tables); the packet goes back and forth between the two stacks.
        _ = Packet.TryParse(recycle, new PacketId(0), stackB, frameB, in options, out _);
        _ = _Parse(new PacketId(0), stackB, frameB, in options);

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = _Parse(new PacketId(0), stackB, frameB, in options);
        long freshBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        _ = Packet.TryParse(recycle, new PacketId(0), stackA, _MakeFrame(stackA, 3), in options, out _);
        before = GC.GetAllocatedBytesForCurrentThread();
        bool recycled = Packet.TryParse(recycle, new PacketId(0), stackB, frameB, in options, out _);
        long recycledBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(recycled).IsTrue();
        await Assert.That(recycledBytes).IsLessThan(freshBytes);
    }

    [Test]
    public async Task RecycleOntoAStackOfAnotherRegistryIsRefusedAndLeavesThePacketUntouched()
    {
        using Stack stackA = _BuildStandard(new());
        using Stack otherRegistry = _BuildStandard(new());
        ParseOptions options = new();
        _ = _Parse(new PacketId(0), stackA, _MakeFrame(stackA, 1), in options);
        Packet packet = _Parse(new PacketId(1), stackA, _MakeFrame(stackA, 2), in options);
        string[] before = _FieldNames(packet, stackA);

        bool recycled = Packet.TryParse(
            packet, new PacketId(0), otherRegistry, _MakeFrame(otherRegistry, 3), in options, out ParseFailure failure);

        await Assert.That(recycled).IsFalse();
        await Assert.That(failure).IsEqualTo(ParseFailure.StackMismatch);
        await Assert.That(ReferenceEquals(packet.Stack, stackA)).IsTrue();
        await Assert.That(packet.Id.Value).IsEqualTo(1);
        await Assert.That(_FieldNames(packet, stackA)).IsEquivalentTo(before);
    }

    [Test]
    public async Task AFrameOfAnotherRegistryStillGivesRegistryMismatchWhenTheStackMatchesThePacket()
    {
        using Stack stackA = _BuildStandard(new());
        using Stack other = _BuildStandard(new());
        ParseOptions options = new();
        Packet packet = _Parse(new PacketId(0), stackA, _MakeFrame(stackA, 1), in options);

        bool recycled = Packet.TryParse(packet, new PacketId(1), stackA, _MakeFrame(other, 2), in options, out ParseFailure failure);

        await Assert.That(recycled).IsFalse();
        await Assert.That(failure).IsEqualTo(ParseFailure.RegistryMismatch);
        await Assert.That(ReferenceEquals(packet.Stack, stackA)).IsTrue();
    }

    #endregion
}

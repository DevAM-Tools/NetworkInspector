// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="Packet"/> buffer management, info strings, field access helpers,
/// and prepare-for-reuse behavior beyond recycle integration tests.
/// </summary>
internal sealed class PacketBufferTests
{
    private static Stack _BuildStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static Frame _MakeFrame(Stack stack, byte[] data, int frameId = 1) =>
        Frame.Create(
            new FrameId(frameId),
            Timestamp.FromSecs(frameId),
            data,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    [Test]
    public async Task Buffer_AdditionalBuffers_AreIndexed()
    {
        using Stack stack = _BuildStack();
        byte[] frameData = FrameBuilders.GenerateStaticUdpFrame(128);
        Frame frame = _MakeFrame(stack, frameData);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);

        byte[] extra1 = [0x01, 0x02];
        byte[] extra2 = [0x03, 0x04, 0x05];
        int idx1 = packet.AddBuffer(extra1);
        int idx2 = packet.AddBuffer(extra2);
        ReadOnlyMemory<byte>? buf1 = packet.Buffer(1);
        ReadOnlyMemory<byte>? buf2 = packet.Buffer(2);

        await Assert.That(idx1).IsEqualTo(1);
        await Assert.That(idx2).IsEqualTo(2);
        await Assert.That(packet.BufferCount).IsEqualTo(3);
        await Assert.That(packet.Buffer(0)!.Value.Length).IsEqualTo(frameData.Length);
        await Assert.That(buf1).IsNotNull();
        await Assert.That(buf2).IsNotNull();
        await Assert.That(buf1!.Value.ToArray()).IsEqualTo(extra1);
        await Assert.That(buf2!.Value.ToArray()).IsEqualTo(extra2);
        await Assert.That(packet.Buffer(99)).IsNull();
    }

    [Test]
    public async Task Info_AppendAndPrepend()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);

        LazyString prefix = new("prefix: ");
        LazyString suffix = new(" suffix");
        packet.PrependToInfo(prefix);
        packet.AppendToInfo(suffix);

        string info = packet.InfoLazy.AsString;
        await Assert.That(info.StartsWith("prefix: ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(info.EndsWith(" suffix", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task FieldCount_WithAndWithoutMaterialize()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(128));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);

        int eager = packet.FieldCount(materialize: false);
        int materialized = packet.FieldCount(materialize: true);
        await Assert.That(eager).IsGreaterThan(0);
        await Assert.That(materialized).IsGreaterThanOrEqualTo(eager);
    }

    [Test]
    public async Task TryGetFieldMutAt_ReturnsFieldOrFalse()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);

        bool foundRoot = packet.TryGetFieldMutAt(0, out MutField root);
        FieldId rootFieldId = root.FieldId;
        bool missing = packet.TryGetFieldMutAt(9999, out _);

        await Assert.That(foundRoot).IsTrue();
        await Assert.That(rootFieldId).IsEqualTo(packet.RootField().FieldId);
        await Assert.That(missing).IsFalse();
    }

    [Test]
    public async Task PrepareForReuse_ResetsIdAndBuffers()
    {
        using Stack stack = _BuildStack();
        byte[] data = FrameBuilders.GenerateStaticUdpFrame(64);
        Frame frame1 = _MakeFrame(stack, data, 1);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame1, stack.GetProtocolId("eth")!.Value);
        packet.AddBuffer(new byte[] { 0xFF });

        Frame frame2 = _MakeFrame(stack, data, 2);
        RecycleError? err = packet.PrepareForReuse(new PacketId(99), frame2);
        await Assert.That(err).IsNull();

        await Assert.That(packet.Id.Value).IsEqualTo(99);
        await Assert.That(packet.BufferCount).IsEqualTo(1);
        await Assert.That(packet.Buffer(1)).IsNull();
    }

    [Test]
    public async Task TryGetNextFieldValue_FindsEthTypeField()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(128));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);

        FieldId ethType = stack.GetFieldId("eth.type")!.Value;
        FieldLookupCookie cookie = default;
        bool found = packet.TryGetNextFieldValue(ethType, ref cookie, out FieldValue value, materialize: true);
        await Assert.That(found).IsTrue();
        await Assert.That(value.Type).IsNotEqualTo(FieldType.None);
    }

    [Test]
    public async Task SetError_OnPacket_AddsErrorField()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);
        packet.SetError("parse failed");

        FieldLookupCookie cookie = default;
        bool found = packet.TryGetNextFieldValue(
            stack.PacketErrorFieldId, ref cookie, out FieldValue err, materialize: true);
        await Assert.That(found).IsTrue();
        err.Data.TryGetAsString(out string msg);
        await Assert.That(msg.Contains("parse failed", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task TryParseFrame_Success_ReturnsNull()
    {
        using Stack stack = _BuildStack();
        byte[] data = FrameBuilders.GenerateStaticUdpFrame(128);
        Frame frame1 = _MakeFrame(stack, data, 1);
        Packet seed = Packet.ParseFrame(new PacketId(1), stack, frame1, stack.GetProtocolId("eth")!.Value);
        Frame frame2 = _MakeFrame(stack, data, 2);
        RecycleError? err = Packet.TryParseFrame(seed, new PacketId(2), stack, frame2);
        await Assert.That(err).IsNull();
        await Assert.That(seed.Id).IsEqualTo(new PacketId(2));
    }

    [Test]
    public async Task TryParseFrameIndexed_WithProtocolOverride_Succeeds()
    {
        using Stack stack = _BuildStack();
        byte[] data = FrameBuilders.GenerateStaticUdpFrame(128);
        Frame frame1 = _MakeFrame(stack, data, 1);
        Packet seed = Packet.ParseFrame(new PacketId(1), stack, frame1, stack.GetProtocolId("eth")!.Value);
        PacketIndex index = new(stack);
        Frame frame2 = _MakeFrame(stack, data, 2);
        ProtocolId eth = stack.GetProtocolId("eth")!.Value;
        RecycleError? err = Packet.TryParseFrameIndexed(seed, new PacketId(2), stack, frame2, index, eth);
        await Assert.That(err).IsNull();
    }

    [Test]
    public async Task ParseFrameIndexed_StaticOverload_ReturnsPacket()
    {
        using Stack stack = _BuildStack();
        byte[] data = FrameBuilders.GenerateStaticUdpFrame(128);
        Frame frame = _MakeFrame(stack, data);
        PacketIndex index = new(stack);
        ProtocolId eth = stack.GetProtocolId("eth")!.Value;
        Packet packet = Packet.ParseFrameIndexed(new PacketId(5), stack, frame, index, eth);
        await Assert.That(packet.Id).IsEqualTo(new PacketId(5));
        await Assert.That(packet.IsFinalized).IsTrue();
    }

    [Test]
    public async Task SetFieldError_AttachesErrorUnderParent()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);
        packet.SetFieldError(0, "field error");
        await Assert.That(packet.FieldCount(materialize: true)).IsGreaterThan(1);
    }

    [Test]
    public async Task TryParseFrameIndexed_WithoutProtocolOverride_Succeeds()
    {
        using Stack stack = _BuildStack();
        byte[] data = FrameBuilders.GenerateStaticUdpFrame(128);
        Frame frame1 = _MakeFrame(stack, data, 1);
        Packet seed = Packet.ParseFrame(new PacketId(1), stack, frame1, stack.GetProtocolId("eth")!.Value);
        PacketIndex index = new(stack);
        Frame frame2 = _MakeFrame(stack, data, 2);
        RecycleError? err = Packet.TryParseFrameIndexed(seed, new PacketId(2), stack, frame2, index);
        await Assert.That(err).IsNull();
    }

    [Test]
    public async Task FieldCount_AfterSeal_UsesVolatilePath()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(128));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);
        int count = packet.FieldCount();
        await Assert.That(count).IsGreaterThan(0);
        await Assert.That(packet.Info.Length).IsGreaterThan(0);
    }
}

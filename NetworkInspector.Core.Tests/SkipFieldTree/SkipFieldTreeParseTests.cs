// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Skip-field-tree parse contract: empty tree, index/cache record, recycle Skip↔Build, invalid mode.
/// </summary>
internal sealed class SkipFieldTreeParseTests
{
    #region Helpers

    private static Stack _BuildStandardStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static Frame _Ipv6UdpFrame(Stack stack, int frameId = 1) =>
        Frame.Create(
            new FrameId(frameId),
            Timestamp.FromSecs(frameId),
            FrameBuilders.GenerateStaticUdpIpv6Frame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    private static Frame _Ipv4UdpFrame(Stack stack, int frameId = 1) =>
        Frame.Create(
            new FrameId(frameId),
            Timestamp.FromSecs(frameId),
            FrameBuilders.GenerateStaticUdpFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    private static (Stack Stack, SkipCursorProbeProtocol Proto, ProtocolId ProtoId) _BuildProbeStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        SkipCursorProbeProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        return (builder.Build(), proto, protoId);
    }

    private static Frame _EmptyFrame(Stack stack) =>
        Frame.Create(
            new FrameId(1),
            Timestamp.FromSecs(1),
            new byte[8],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    #endregion

    #region Empty tree contract

    [Test]
    public async Task ParseFrame_Skip_HasNoFieldTreeAndOnlySyntheticRoot()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Ipv6UdpFrame(stack);

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Skip);

        await Assert.That(packet.HasFieldTree).IsFalse();
        await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(1);
        await Assert.That(packet.RootField().HasFieldTree).IsFalse();
        await Assert.That(packet.RootField().TryGetFirstChild(out _, materialize: false)).IsFalse();
        await Assert.That(packet.RootField().Value.Type).IsEqualTo(FieldType.None);
        await Assert.That(packet.RootField().TryGetValue(out FieldValue rootValue)).IsTrue();
        await Assert.That(rootValue.Type).IsEqualTo(FieldType.None);
        Field ghost = new(packet, 1, FieldId.Invalid);
        await Assert.That(ghost.TryGetValue(out _)).IsFalse();
        await Assert.That(ghost.TryGetCustomText(out _)).IsFalse();
    }

    [Test]
    public async Task ParseFrame_Default_StillBuildsTreeOnIpv6Udp()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Ipv6UdpFrame(stack);

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);

        await Assert.That(packet.HasFieldTree).IsTrue();
        await Assert.That(packet.FieldCount(materialize: false)).IsGreaterThan(1);
    }

    [Test]
    public async Task ParseFrame_ExplicitBuild_EqualsDefaultFieldCount()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Ipv6UdpFrame(stack);

        Packet defaultPacket = Packet.ParseFrame(new PacketId(0), stack, frame);
        using Stack stack2 = _BuildStandardStack();
        Frame frame2 = _Ipv6UdpFrame(stack2);
        Packet explicitBuild = Packet.ParseFrame(new PacketId(0), stack2, frame2, FieldTreeMode.Build);

        await Assert.That(explicitBuild.HasFieldTree).IsTrue();
        await Assert.That(explicitBuild.FieldCount(materialize: false))
            .IsEqualTo(defaultPacket.FieldCount(materialize: false));
    }

    [Test]
    public async Task ParseFrame_Skip_ChildCursorValue_ThrowsInvalidOperation()
    {
        (Stack? stack, SkipCursorProbeProtocol proto, ProtocolId protoId) = _BuildProbeStack();
        using (stack)
        {
            _ = Packet.ParseFrame(new PacketId(0), stack, _EmptyFrame(stack), protoId, FieldTreeMode.Skip);

            await Assert.That(proto.ContextHasFieldTree).IsFalse();
            await Assert.That(proto.MutFieldHasFieldTree).IsFalse();
            await Assert.That(proto.ChildValueException).IsNotNull();
            await Assert.That(proto.ChildValueException).IsTypeOf<InvalidOperationException>();
        }
    }

    [Test]
    public async Task ParseFrame_Skip_Ipv6Udp_DoesNotThrow_FieldCountStaysOne()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Ipv6UdpFrame(stack);

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Skip);

        await Assert.That(packet.IsFinalized).IsTrue();
        await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(1);
        await Assert.That(packet.HasFieldTree).IsFalse();
    }

    #endregion

    #region Index and value cache

    [Test]
    public async Task ParseFrameIndexed_Skip_RecordsSameEagerUdpGroupsAsBuild()
    {
        using Stack buildStack = _BuildStandardStack();
        using Stack skipStack = _BuildStandardStack();
        Frame buildFrame = _Ipv4UdpFrame(buildStack);
        Frame skipFrame = _Ipv4UdpFrame(skipStack);
        PacketIndex buildIndex = new(buildStack);
        PacketIndex skipIndex = new(skipStack);

        _ = Packet.ParseFrameIndexed(new PacketId(0), buildStack, buildFrame, buildIndex);
        _ = Packet.ParseFrameIndexed(new PacketId(0), skipStack, skipFrame, skipIndex, FieldTreeMode.Skip);

        FieldId srcPortId = skipStack.GetFieldId("udp.srcport")!.Value;
        ProtocolId udpId = skipStack.GetProtocolId("udp")!.Value;
        await Assert.That(skipIndex.GetFieldBitmap(srcPortId).Contains(0)).IsTrue();
        await Assert.That(skipIndex.GetProtocolBitmap(udpId).Contains(0)).IsTrue();
        await Assert.That(skipIndex.GetFieldBitmap(srcPortId).Contains(0))
            .IsEqualTo(buildIndex.GetFieldBitmap(buildStack.GetFieldId("udp.srcport")!.Value).Contains(0));
        await Assert.That(skipIndex.GetProtocolBitmap(udpId).Contains(0))
            .IsEqualTo(buildIndex.GetProtocolBitmap(buildStack.GetProtocolId("udp")!.Value).Contains(0));
    }

    [Test]
    public async Task ParseFrame_Skip_EagerUdpSrcPort_MatchesBuild()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Ipv4UdpFrame(stack);
        FieldId portId = stack.GetFieldId("udp.srcport")!.Value;

        ValueCache buildCache = new(stack, [new ValueCacheFieldConfig(portId)]);
        _ = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Build, buildCache);

        ValueCache skipCache = new(stack, [new ValueCacheFieldConfig(portId)]);
        _ = Packet.ParseFrame(new PacketId(1), stack, frame, FieldTreeMode.Skip, skipCache);

        await Assert.That(skipCache.GetSeries<ulong>(portId).Count).IsEqualTo(1);
        await Assert.That(skipCache.GetSeries<ulong>(portId).Count)
            .IsEqualTo(buildCache.GetSeries<ulong>(portId).Count);
        await Assert.That(skipCache.GetSeries<ulong>(portId)[0].Value)
            .IsEqualTo(buildCache.GetSeries<ulong>(portId)[0].Value);
    }

    [Test]
    public async Task ParseFrame_Skip_ReplayWithoutRecordOnReplay_DoesNotFill()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Ipv4UdpFrame(stack);
        FieldId portId = stack.GetFieldId("udp.srcport")!.Value;

        ValueCache first = new(stack, [new ValueCacheFieldConfig(portId)]);
        _ = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Skip, first);

        ValueCache replay = new(stack, [new ValueCacheFieldConfig(portId)]);
        _ = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Skip, replay);

        await Assert.That(replay.GetSeries<ulong>(portId).Count).IsEqualTo(0);
    }

    [Test]
    public async Task ParseFrame_Skip_ReplayWithRecordOnReplay_Fills()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Ipv4UdpFrame(stack);
        FieldId portId = stack.GetFieldId("udp.srcport")!.Value;

        ValueCache first = new(stack, [new ValueCacheFieldConfig(portId)]);
        _ = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Skip, first);

        ValueCache replay = new(stack, [new ValueCacheFieldConfig(portId)]);
        _ = Packet.ParseFrame(
            new PacketId(0), stack, frame, FieldTreeMode.Skip, replay, recordOnReplay: true);

        await Assert.That(replay.GetSeries<ulong>(portId).Count).IsEqualTo(1);
        await Assert.That(replay.GetSeries<ulong>(portId)[0].Value)
            .IsEqualTo(first.GetSeries<ulong>(portId)[0].Value);
    }

    #endregion

    #region Errors and recycle

    [Test]
    public async Task ParseFrame_InvalidFieldTreeMode_ThrowsArgumentOutOfRange()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Ipv6UdpFrame(stack);

        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Packet.ParseFrame(new PacketId(0), stack, frame, (FieldTreeMode)2));

        await Assert.That(ex.ParamName).IsEqualTo("fieldTree");
    }

    [Test]
    public async Task Recycle_SkipThenBuild_ProducesWalkableTree()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Ipv6UdpFrame(stack);

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Skip);
        Packet recycled = Packet.ParseFrame(packet, new PacketId(1), stack, frame, FieldTreeMode.Build);

        await Assert.That(ReferenceEquals(packet, recycled)).IsTrue();
        await Assert.That(recycled.HasFieldTree).IsTrue();
        await Assert.That(recycled.FieldCount(materialize: false)).IsGreaterThan(1);
        await Assert.That(recycled.RootField().TryGetFirstChild(out _, materialize: false)).IsTrue();
    }

    [Test]
    public async Task Recycle_BuildThenSkip_YieldsEmptyTree()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Ipv6UdpFrame(stack);

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Build);
        Packet recycled = Packet.ParseFrame(packet, new PacketId(1), stack, frame, FieldTreeMode.Skip);

        await Assert.That(recycled.HasFieldTree).IsFalse();
        await Assert.That(recycled.FieldCount(materialize: false)).IsEqualTo(1);
        await Assert.That(recycled.RootField().TryGetFirstChild(out _, materialize: false)).IsFalse();
    }

    [Test]
    public async Task ParseFrame_Skip_DfsAndFlatIteratorsYieldNoFields()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Ipv6UdpFrame(stack);

        Packet skip = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Skip);
        int dfsSkip = 0;
        foreach (Field _ in skip.IterFieldsDfs(materialize: false))
        {
            dfsSkip++;
        }

        int flatSkip = 0;
        foreach (Field _ in skip.IterFieldsFlat(materialize: false))
        {
            flatSkip++;
        }

        await Assert.That(dfsSkip).IsEqualTo(0);
        await Assert.That(flatSkip).IsEqualTo(0);

        Packet build = Packet.ParseFrame(new PacketId(1), stack, frame, FieldTreeMode.Build);
        int dfsBuild = 0;
        foreach (Field _ in build.IterFieldsDfs(materialize: false))
        {
            dfsBuild++;
        }

        await Assert.That(dfsBuild).IsGreaterThan(0);
    }

    #endregion
}

/// <summary>Records skip-tree cursor behavior during Parse for <see cref="SkipFieldTreeParseTests"/>.</summary>
internal sealed class SkipCursorProbeProtocol : IProtocol
{
    public FieldId NumberId;
    public bool ContextHasFieldTree = true;
    public bool MutFieldHasFieldTree = true;
    public Exception? ChildValueException;

    public string Name => "skipprobe";
    public string UiName => "Skip Cursor Probe";

    public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
    {
        NumberId = builder.RegisterField(protocolId, "skipprobe.num", "Number", FieldType.U64);
    }

    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        ContextHasFieldTree = context.HasFieldTree;
        MutFieldHasFieldTree = parentField.HasFieldTree;
        MutField child = parentField.Append(NumberId, FieldValue.NewU64(1));
        try
        {
            _ = child.Value;
        }
        catch (Exception ex)
        {
            ChildValueException = ex;
        }

        return data.Length;
    }
}

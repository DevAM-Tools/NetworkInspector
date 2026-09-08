// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Skip-tree display-text skip: payload-only cache must not pay <c>ZA.Lazy</c> / generic FormatLazy.
/// </summary>
internal sealed class SkipFieldTreeZeroAllocTests
{
    #region Helpers

    private static Stack _BuildStandardStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static Frame _Ipv6UdpFrame(Stack stack, int id) =>
        Frame.Create(
            new FrameId(id),
            Timestamp.FromSecs(id),
            FrameBuilders.GenerateStaticUdpIpv6Frame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    private static (Stack Stack, SkipGenericAppendProtocol Proto, ProtocolId ProtoId) _BuildGenericStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        SkipGenericAppendProtocol proto = new();
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

    [Test]
    [NotInParallel]
    public async Task TryParseFrame_Skip_PayloadOnlyUdpSrcPort_AllocatesLessThanBuild()
    {
        using Stack skipStack = _BuildStandardStack();
        using Stack buildStack = _BuildStandardStack();
        FieldId skipPortId = skipStack.GetFieldId("udp.srcport")!.Value;
        FieldId buildPortId = buildStack.GetFieldId("udp.srcport")!.Value;
        ValueCache skipCache = new(skipStack, [new ValueCacheFieldConfig(skipPortId)]);
        ValueCache buildCache = new(buildStack, [new ValueCacheFieldConfig(buildPortId)]);
        Frame skipWarmup = _Ipv6UdpFrame(skipStack, 0);
        Frame buildWarmup = _Ipv6UdpFrame(buildStack, 0);
        Packet skipPacket = Packet.ParseFrame(new PacketId(0), skipStack, skipWarmup, FieldTreeMode.Skip, skipCache);
        Packet buildPacket = Packet.ParseFrame(new PacketId(0), buildStack, buildWarmup, FieldTreeMode.Build, buildCache);

        const int warmup = 8;
        for (int i = 0; i < warmup; i++)
        {
            Frame skipFrame = _Ipv6UdpFrame(skipStack, i + 1);
            Frame buildFrame = _Ipv6UdpFrame(buildStack, i + 1);
            RecycleError? skipErr = Packet.TryParseFrame(
                skipPacket, new PacketId(i + 1), skipStack, skipFrame, FieldTreeMode.Skip, skipCache);
            RecycleError? buildErr = Packet.TryParseFrame(
                buildPacket, new PacketId(i + 1), buildStack, buildFrame, FieldTreeMode.Build, buildCache);
            await Assert.That(skipErr).IsNull();
            await Assert.That(buildErr).IsNull();
        }

        Frame skipMeasure = _Ipv6UdpFrame(skipStack, warmup + 1);
        Frame buildMeasure = _Ipv6UdpFrame(buildStack, warmup + 1);
        long beforeSkip = GC.GetAllocatedBytesForCurrentThread();
        RecycleError? measuredSkip = Packet.TryParseFrame(
            skipPacket, new PacketId(warmup + 1), skipStack, skipMeasure, FieldTreeMode.Skip, skipCache);
        long skipAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeSkip;

        long beforeBuild = GC.GetAllocatedBytesForCurrentThread();
        RecycleError? measuredBuild = Packet.TryParseFrame(
            buildPacket, new PacketId(warmup + 1), buildStack, buildMeasure, FieldTreeMode.Build, buildCache);
        long buildAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeBuild;

        await Assert.That(measuredSkip).IsNull();
        await Assert.That(measuredBuild).IsNull();
        await Assert.That(skipAlloc).IsLessThan(buildAlloc);
    }

    [Test]
    public async Task ParseFrame_Skip_GenericArity1And8_DoesNotThrow_FieldCountStaysOne()
    {
        (Stack stack, SkipGenericAppendProtocol proto, ProtocolId protoId) = _BuildGenericStack();
        using (stack)
        {
            Frame frame = _EmptyFrame(stack);
            Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, protoId, FieldTreeMode.Skip);

            await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(1);
            await Assert.That(proto.AppendCalls).IsEqualTo(2);
        }
    }
}

/// <summary>Calls generic <see cref="MutField.AppendWithCustomText"/> arities 1 and 8 on skip parse.</summary>
internal sealed class SkipGenericAppendProtocol : IProtocol
{
    public FieldId OneId;
    public FieldId EightId;
    public int AppendCalls;

    public string Name => "skipgen";
    public string UiName => "Skip Generic Append";

    public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
    {
        OneId = builder.RegisterFieldInGroup(protocolId, "skipgen.one", "One", FieldType.U64, "skipgen");
        EightId = builder.RegisterFieldInGroup(protocolId, "skipgen.eight", "Eight", FieldType.U64, "skipgen");
    }

    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        parentField.AppendWithCustomText(OneId, FieldValue.NewU64(1), "one");
        parentField.AppendWithCustomText(
            EightId, FieldValue.NewU64(8),
            "a", "b", "c", "d", "e", "f", "g", "h");
        Interlocked.Add(ref AppendCalls, 2);
        return data.Length;
    }
}

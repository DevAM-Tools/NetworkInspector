// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Parse-time record, <see cref="Packet.ParseFrame(PacketId, Stack, Frame, FieldTreeMode, ValueCache, Boolean)"/>, recycle, and custom-text mutation.</summary>
internal sealed class ValueCacheParseTests
{
    #region Helpers

    private static (Stack Stack, Packet Packet, FieldId PortId) _ParseUdp(ValueCache? cache = null, PacketIndex? index = null)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();
        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(1),
            FrameBuilders.GenerateStaticUdpFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        FieldId portId = stack.GetFieldId("udp.srcport")!.Value;
        Packet packet = cache is null
            ? Packet.ParseFrame(new PacketId(0), stack, frame)
            : index is null
                ? Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Build, cache)
                : Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index, FieldTreeMode.Build, cache);
        return (stack, packet, portId);
    }

    private static (Stack Stack, ValueCacheExerciseProtocol Proto, ProtocolId ProtoId) _BuildExercise()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ValueCacheExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        return (builder.Build(), proto, protoId);
    }

    private static Frame _Frame(Stack stack, int id = 1) =>
        Frame.Create(
            new FrameId(id),
            Timestamp.FromSecs(id),
            new byte[16],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    #endregion

    #region Parity and order

    [Test]
    public async Task ParseFrame_EagerUdpPort_MatchesRecordPacket()
    {
        (Stack? stack, Packet parsed, FieldId portId) = _ParseUdp();
        using (stack)
        {
            ValueCache recorded = new(stack, [new ValueCacheFieldConfig(portId)]);
            _ = Packet.ParseFrame(new PacketId(1), stack, parsed.Frame, FieldTreeMode.Build, recorded);

            ValueCache pulled = new(stack, [new ValueCacheFieldConfig(portId)]);
            pulled.RecordPacket(parsed);

            ulong recordedValue = recorded.GetSeries<ulong>(portId)[0].Value;
            ulong pulledValue = pulled.GetSeries<ulong>(portId)[0].Value;
            await Assert.That(recorded.GetSeries<ulong>(portId).Count).IsEqualTo(1);
            await Assert.That(recordedValue).IsEqualTo(pulledValue);
        }
    }

    [Test]
    public async Task ParseFrame_SingleField_MissesUnrecordedSibling()
    {
        (Stack? stack, Packet parsed, FieldId portId) = _ParseUdp();
        using (stack)
        {
            FieldId dstId = stack.GetFieldId("udp.dstport")!.Value;
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(portId)]);
            _ = Packet.ParseFrame(new PacketId(1), stack, parsed.Frame, FieldTreeMode.Build, cache);
            await Assert.That(cache.TryGetSeries<ulong>(portId, out _)).IsTrue();
            await Assert.That(cache.TryGetSeries<ulong>(dstId, out _)).IsFalse();
            await Assert.That(cache.GetSeries<ulong>(portId).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ParseFrame_Prepend_MatchesRecordPacketStorageOrder()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        PrependExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        Frame frame = _Frame(stack);
        ValueCache recorded = new(stack, [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.AllOccurrences)]);
        Packet recordedPacket = Packet.ParseFrame(new PacketId(0), stack, frame, protoId, FieldTreeMode.Build, recorded);
        ValueCache pulled = new(stack, [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.AllOccurrences)]);
        pulled.RecordPacket(recordedPacket);
        ulong recorded0 = recorded.GetSeries<ulong>(proto.NumberId)[0].Value;
        ulong recorded1 = recorded.GetSeries<ulong>(proto.NumberId)[1].Value;
        ulong pull0 = pulled.GetSeries<ulong>(proto.NumberId)[0].Value;
        ulong pull1 = pulled.GetSeries<ulong>(proto.NumberId)[1].Value;
        await Assert.That(recorded.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(2);
        await Assert.That((recorded0, recorded1)).IsEqualTo((pull0, pull1));
        await Assert.That(recorded0).IsEqualTo(1UL);
        await Assert.That(recorded1).IsEqualTo(2UL);
    }

    #endregion

    #region Lifecycle

    [Test]
    public async Task ParseFrame_ProtocolThrow_KeepsRowsAlreadyRecorded_NextPacketRecords()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ThrowingExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
        proto.ThrowAfterAppend = true;
        Packet failed = Packet.ParseFrame(new PacketId(0), stack, _Frame(stack, 0), protoId, FieldTreeMode.Build, cache);
        bool hasError = failed.TryGetFieldValue(stack.PacketErrorFieldId, out _, materialize: true);
        await Assert.That(hasError).IsTrue();
        await Assert.That(cache.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(1);
        await Assert.That(cache.GetSeries<ulong>(proto.NumberId)[0].Value).IsEqualTo(1UL);

        proto.ThrowAfterAppend = false;
        _ = Packet.ParseFrame(new PacketId(1), stack, _Frame(stack, 1), protoId, FieldTreeMode.Build, cache);
        await Assert.That(cache.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(2);
    }

    [Test]
    public async Task ParseFrameIndexed_PopulatesBitmapAndSeries()
    {
        (Stack? stack, Packet _, FieldId portId) = _ParseUdp();
        using (stack)
        {
            PacketIndex index = new(stack);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(portId)]);
            Frame frame = Frame.Create(
                new FrameId(1),
                Timestamp.FromSecs(2),
                FrameBuilders.GenerateStaticUdpFrame(),
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            _ = Packet.ParseFrameIndexed(new PacketId(1), stack, frame, index, FieldTreeMode.Build, cache);
            await Assert.That(cache.GetSeries<ulong>(portId).Count).IsEqualTo(1);
            await Assert.That(index.GetFieldBitmap(portId).Contains(1)).IsTrue();
        }
    }

    [Test]
    public async Task TryParseFrame_RecycleLoop_SeriesMatchesPacketCount()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        using Stack stack = builder.Build();
        FieldId portId = stack.GetFieldId("udp.srcport")!.Value;
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(portId)]);
        Frame frame0 = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(1),
            FrameBuilders.GenerateStaticUdpFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame0, FieldTreeMode.Build, cache);
        for (int i = 1; i < 100; i++)
        {
            Frame frame = Frame.Create(
                new FrameId(i),
                Timestamp.FromSecs(i + 1),
                FrameBuilders.GenerateStaticUdpFrame(),
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            RecycleError? err = Packet.TryParseFrame(packet, new PacketId(i), stack, frame, FieldTreeMode.Build, cache);
            await Assert.That(err).IsNull();
        }

        await Assert.That(cache.GetSeries<ulong>(portId).Count).IsEqualTo(100);
    }

    [Test]
    public async Task ParseFrame_LazyTtl_ProducesRow()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        using Stack stack = builder.Build();
        FieldId ttlId = stack.GetFieldId("ip.ttl")!.Value;
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(ttlId)]);
        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(1),
            FrameBuilders.GenerateStaticUdpFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Build, cache);
        await Assert.That(cache.GetSeries<ulong>(ttlId).Count).IsEqualTo(1);
        await Assert.That(packet.HasUnpopulatedLazyFields).IsTrue();
    }

    [Test]
    public async Task ParseFrame_RecordAllFields_Completes()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        using Stack stack = builder.Build();
        ValueCache cache = new(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true });
        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(1),
            FrameBuilders.GenerateStaticUdpFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        _ = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Build, cache);
        FieldId? portId = stack.GetFieldId("udp.srcport");
        await Assert.That(cache.GetSeries<ulong>(portId!.Value).Count).IsEqualTo(1);
    }

    /// <summary>Stresses live Count vs parse-time record writer.</summary>
    [Test]
    [NotInParallel]
    public async Task ParseFrame_ConcurrentReaders_SeeOnlyCommittedRows()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        using Stack stack = builder.Build();
        FieldId portId = stack.GetFieldId("udp.srcport")!.Value;
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(portId)]);
        using CancellationTokenSource cts = new();
        Task reader = Task.Run(() =>
        {
            ValueCacheSeries<ulong> series = cache.GetSeries<ulong>(portId);
            while (!cts.Token.IsCancellationRequested)
            {
                int count = series.Count;
                for (int i = 0; i < count; i++)
                {
                    _ = series[i];
                }
            }
        }, cts.Token);

        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(1),
            FrameBuilders.GenerateStaticUdpFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Build, cache);
        for (int i = 1; i < 32; i++)
        {
            Frame next = Frame.Create(
                new FrameId(i),
                Timestamp.FromSecs(i + 1),
                FrameBuilders.GenerateStaticUdpFrame(),
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            RecycleError? err = Packet.TryParseFrame(packet, new PacketId(i), stack, next, FieldTreeMode.Build, cache);
            await Assert.That(err).IsNull();
        }

        await cts.CancelAsync();
        try
        {
            await reader;
        }
        catch (OperationCanceledException)
        {
        }

        await Assert.That(cache.GetSeries<ulong>(portId).Count).IsEqualTo(32);
    }

    #endregion

    #region Arguments and overloads

    [Test]
    public async Task ParseFrame_OtherStack_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol _, ProtocolId _) = _BuildExercise();
        (Stack? other, ValueCacheExerciseProtocol protoOther, ProtocolId _) = _BuildExercise();
        using (stack)
        using (other)
        {
            ValueCache cache = new(other, [new ValueCacheFieldConfig(protoOther.NumberId)]);
            await Assert.That(() => Packet.ParseFrame(new PacketId(0), stack, _Frame(stack), FieldTreeMode.Build, cache))
                .Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task ParseFrame_Replay_DoesNotRecord()
    {
        (Stack? stack, Packet _, FieldId portId) = _ParseUdp();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(portId)]);
            Frame frame = Frame.Create(
                new FrameId(1),
                Timestamp.FromSecs(2),
                FrameBuilders.GenerateStaticUdpFrame(),
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            _ = Packet.ParseFrame(new PacketId(1), stack, frame, FieldTreeMode.Build, cache);
            _ = Packet.ParseFrame(new PacketId(1), stack, frame, FieldTreeMode.Build, cache);
            await Assert.That(cache.GetSeries<ulong>(portId).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ParseFrame_Overloads_AndRecycleErrors()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        using Stack stack = builder.Build();
        using SettingsManager otherSettings = new();
        StackBuilder otherBuilder = new(otherSettings, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(otherBuilder);
        using Stack other = otherBuilder.Build();
        FieldId portId = stack.GetFieldId("udp.srcport")!.Value;
        ProtocolId eth = stack.GetProtocolId("eth")!.Value;
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(portId)]);
        PacketIndex index = new(stack);
        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(1),
            FrameBuilders.GenerateStaticUdpFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        Packet a = Packet.ParseFrame(new PacketId(0), stack, frame, eth, FieldTreeMode.Build, cache);
        Packet b = Packet.ParseFrameIndexed(new PacketId(1), stack, frame, index, eth, FieldTreeMode.Build, cache);
        RecycleError? ok = Packet.TryParseFrame(a, new PacketId(2), stack, frame, eth, FieldTreeMode.Build, cache);
        RecycleError? okIndex = Packet.TryParseFrameIndexed(b, new PacketId(3), stack, frame, index, FieldTreeMode.Build, cache);
        RecycleError? okBoth = Packet.TryParseFrameIndexed(a, new PacketId(4), stack, frame, index, eth, FieldTreeMode.Build, cache);
        Packet thrown = Packet.ParseFrame(a, new PacketId(5), stack, frame, FieldTreeMode.Build, cache);
        _ = Packet.ParseFrame(a, new PacketId(6), stack, frame, eth, FieldTreeMode.Build, cache);
        _ = Packet.ParseFrameIndexed(a, new PacketId(7), stack, frame, index, FieldTreeMode.Build, cache);
        _ = Packet.ParseFrameIndexed(a, new PacketId(8), stack, frame, index, eth, FieldTreeMode.Build, cache);
        RecycleError? mismatch = Packet.TryParseFrame(a, new PacketId(9), other, frame, FieldTreeMode.Build, cache);
        await Assert.That(ok).IsNull();
        await Assert.That(okIndex).IsNull();
        await Assert.That(okBoth).IsNull();
        await Assert.That(thrown).IsSameReferenceAs(a);
        await Assert.That(mismatch).IsEqualTo(RecycleError.StackMismatch);
        await Assert.That(cache.GetSeries<ulong>(portId).Count).IsEqualTo(9);
    }

    #endregion

    #region Custom text

    [Test]
    public async Task ParseFrame_CustomText_FirstOccurrence_SkipsLaterMutation()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        CustomTextExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        ValueCache first = new(
            stack,
            [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.FirstOccurrence, RecordValue: false, RecordCustomText: true)]);
        proto.OverwriteCustomText = true;
        _ = Packet.ParseFrame(new PacketId(0), stack, _Frame(stack, 0), protoId, FieldTreeMode.Build, first);
        ValueCacheSeries<string> series = first.GetCustomTextSeries(proto.NumberId);
        await Assert.That(series.Count).IsEqualTo(1);
        await Assert.That(series[0].Value).IsEqualTo("first");
    }

    [Test]
    public async Task ParseFrame_CustomRepresentation_IsStored()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExercise();
        using (stack)
        {
            proto.WithCustomRep = true;
            ValueCache cache = new(
                stack,
                [new ValueCacheFieldConfig(proto.NumberId, RecordValue: true, RecordCustomRepresentation: true)]);
            _ = Packet.ParseFrame(new PacketId(0), stack, _Frame(stack), protoId, FieldTreeMode.Build, cache);
            ValueCacheSeries<string> series = cache.GetCustomRepresentationSeries(proto.NumberId);
            await Assert.That(series.Count).IsEqualTo(1);
            await Assert.That(series[0].Value).IsEqualTo("custom-rep");
        }
    }

    [Test]
    public async Task ParseFrame_InsertAfter_Records()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        InsertAfterExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.AllOccurrences)]);
        _ = Packet.ParseFrame(new PacketId(0), stack, _Frame(stack), protoId, FieldTreeMode.Build, cache);
        await Assert.That(cache.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(2);
        await Assert.That(cache.GetSeries<ulong>(proto.NumberId)[1].Value).IsEqualTo(2UL);
    }

    #endregion
}

internal sealed class PrependExerciseProtocol : IProtocol
{
    public FieldId NumberId;
    private int _Resets;

    public string Name => "vcxpre";
    public string UiName => "Prepend Exercise";

    public void ResetParseState() => _Resets++;

    public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
    {
        NumberId = builder.RegisterField(protocolId, "vcx.pre", "Number", FieldType.U64);
    }

    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        parentField.Append(NumberId, FieldValue.NewU64(1));
        parentField.Prepend(NumberId, FieldValue.NewU64(2));
        return data.Length;
    }
}

internal sealed class ThrowingExerciseProtocol : IProtocol
{
    public FieldId NumberId;
    public bool ThrowAfterAppend;
    private int _Resets;

    public string Name => "vcxthrow";
    public string UiName => "Throw Exercise";

    public void ResetParseState() => _Resets++;

    public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
    {
        NumberId = builder.RegisterField(protocolId, "vcx.throw", "Number", FieldType.U64);
    }

    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        parentField.Append(NumberId, FieldValue.NewU64(1));
        if (ThrowAfterAppend)
        {
            throw new InvalidOperationException("protocol boom");
        }

        return data.Length;
    }
}

internal sealed class CustomTextExerciseProtocol : IProtocol
{
    public FieldId NumberId;
    public bool OverwriteCustomText;
    private int _Resets;

    public string Name => "vcxtext";
    public string UiName => "CustomText Exercise";

    public void ResetParseState() => _Resets++;

    public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
    {
        NumberId = builder.RegisterField(protocolId, "vcx.text", "Number", FieldType.U64);
    }

    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        MutField child = parentField.AppendWithCustomText(NumberId, FieldValue.NewU64(1), new LazyString("first"));
        if (OverwriteCustomText)
        {
            child.SetCustomText(new LazyString("second"));
        }
        else
        {
            child.ClearCustomText();
        }
        return data.Length;
    }
}

internal sealed class InsertAfterExerciseProtocol : IProtocol
{
    public FieldId NumberId;
    private int _Resets;

    public string Name => "vcxins";
    public string UiName => "InsertAfter Exercise";

    public void ResetParseState() => _Resets++;

    public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
    {
        NumberId = builder.RegisterField(protocolId, "vcx.ins", "Number", FieldType.U64);
    }

    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        MutField first = parentField.Append(NumberId, FieldValue.NewU64(1));
        first.InsertAfter(NumberId, FieldValue.NewU64(2));
        return data.Length;
    }
}

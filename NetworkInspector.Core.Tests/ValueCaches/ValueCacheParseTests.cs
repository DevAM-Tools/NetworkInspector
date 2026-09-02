// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Parse-time tee, <see cref="Packet.ParseFrameRecorded(PacketId, Stack, Frame, ValueCache)"/>, recycle, and custom-text mutation.</summary>
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
                ? Packet.ParseFrameRecorded(new PacketId(0), stack, frame, cache)
                : Packet.ParseFrameRecorded(new PacketId(0), stack, frame, cache, index);
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
    public async Task ParseFrameRecorded_EagerUdpPort_MatchesRecordPacket()
    {
        (Stack? stack, Packet parsed, FieldId portId) = _ParseUdp();
        using (stack)
        {
            ValueCache recorded = new(stack, [new ValueCacheFieldConfig(portId)]);
            _ = Packet.ParseFrameRecorded(new PacketId(1), stack, parsed.Frame, recorded);

            ValueCache pulled = new(stack, [new ValueCacheFieldConfig(portId)]);
            pulled.RecordPacket(parsed);

            ulong recordedValue = recorded.GetSeries<ulong>(portId)[0].Value;
            ulong pulledValue = pulled.GetSeries<ulong>(portId)[0].Value;
            await Assert.That(recorded.GetSeries<ulong>(portId).Count).IsEqualTo(1);
            await Assert.That(recordedValue).IsEqualTo(pulledValue);
        }
    }

    [Test]
    public async Task ParseFrameRecorded_SingleField_MissesUnrecordedSibling()
    {
        (Stack? stack, Packet parsed, FieldId portId) = _ParseUdp();
        using (stack)
        {
            FieldId dstId = stack.GetFieldId("udp.dstport")!.Value;
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(portId)]);
            _ = Packet.ParseFrameRecorded(new PacketId(1), stack, parsed.Frame, cache);
            await Assert.That(cache.TryGetSeries<ulong>(portId, out _)).IsTrue();
            await Assert.That(cache.TryGetSeries<ulong>(dstId, out _)).IsFalse();
            await Assert.That(cache.GetSeries<ulong>(portId).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ParseFrameRecorded_Prepend_MatchesRecordPacketStorageOrder()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        PrependExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        Frame frame = _Frame(stack);
        ValueCache teed = new(stack, [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.AllOccurrences)]);
        Packet teedPacket = Packet.ParseFrameRecorded(new PacketId(0), stack, frame, teed, protoId);
        ValueCache pulled = new(stack, [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.AllOccurrences)]);
        pulled.RecordPacket(teedPacket);
        ulong tee0 = teed.GetSeries<ulong>(proto.NumberId)[0].Value;
        ulong tee1 = teed.GetSeries<ulong>(proto.NumberId)[1].Value;
        ulong pull0 = pulled.GetSeries<ulong>(proto.NumberId)[0].Value;
        ulong pull1 = pulled.GetSeries<ulong>(proto.NumberId)[1].Value;
        await Assert.That(teed.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(2);
        await Assert.That((tee0, tee1)).IsEqualTo((pull0, pull1));
        await Assert.That(tee0).IsEqualTo(1UL);
        await Assert.That(tee1).IsEqualTo(2UL);
    }

    #endregion

    #region Lifecycle

    [Test]
    public async Task ParseFrameRecorded_ProtocolThrow_KeepsRowsAlreadyTeed_NextPacketRecords()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ThrowingExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
        proto.ThrowAfterAppend = true;
        Packet failed = Packet.ParseFrameRecorded(new PacketId(0), stack, _Frame(stack, 0), cache, protoId);
        bool hasError = failed.TryGetFieldValue(stack.PacketErrorFieldId, out _, materialize: true);
        await Assert.That(hasError).IsTrue();
        await Assert.That(cache.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(1);
        await Assert.That(cache.GetSeries<ulong>(proto.NumberId)[0].Value).IsEqualTo(1UL);

        proto.ThrowAfterAppend = false;
        _ = Packet.ParseFrameRecorded(new PacketId(1), stack, _Frame(stack, 1), cache, protoId);
        await Assert.That(cache.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(2);
    }

    [Test]
    public async Task ParseFrameRecorded_WithIndex_PopulatesBitmapAndSeries()
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
            _ = Packet.ParseFrameRecorded(new PacketId(1), stack, frame, cache, index);
            await Assert.That(cache.GetSeries<ulong>(portId).Count).IsEqualTo(1);
            await Assert.That(index.GetFieldBitmap(portId).Contains(1)).IsTrue();
        }
    }

    [Test]
    public async Task TryParseFrameRecorded_RecycleLoop_SeriesMatchesPacketCount()
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
        Packet packet = Packet.ParseFrameRecorded(new PacketId(0), stack, frame0, cache);
        for (int i = 1; i < 100; i++)
        {
            Frame frame = Frame.Create(
                new FrameId(i),
                Timestamp.FromSecs(i + 1),
                FrameBuilders.GenerateStaticUdpFrame(),
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            RecycleError? err = Packet.TryParseFrameRecorded(packet, new PacketId(i), stack, frame, cache);
            await Assert.That(err).IsNull();
        }

        await Assert.That(cache.GetSeries<ulong>(portId).Count).IsEqualTo(100);
    }

    [Test]
    public async Task ParseFrameRecorded_LazyTtl_ProducesRow()
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
        Packet packet = Packet.ParseFrameRecorded(new PacketId(0), stack, frame, cache);
        await Assert.That(cache.GetSeries<ulong>(ttlId).Count).IsEqualTo(1);
        await Assert.That(packet.HasUnpopulatedLazyFields).IsTrue();
    }

    [Test]
    public async Task ParseFrameRecorded_RecordAllFields_Completes()
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
        _ = Packet.ParseFrameRecorded(new PacketId(0), stack, frame, cache);
        FieldId? portId = stack.GetFieldId("udp.srcport");
        await Assert.That(cache.GetSeries<ulong>(portId!.Value).Count).IsEqualTo(1);
    }

    /// <summary>Stresses live Count vs ParseFrameRecorded writer.</summary>
    [Test]
    [NotInParallel]
    public async Task ParseFrameRecorded_ConcurrentReaders_SeeOnlyCommittedRows()
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
        Packet packet = Packet.ParseFrameRecorded(new PacketId(0), stack, frame, cache);
        for (int i = 1; i < 32; i++)
        {
            Frame next = Frame.Create(
                new FrameId(i),
                Timestamp.FromSecs(i + 1),
                FrameBuilders.GenerateStaticUdpFrame(),
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            RecycleError? err = Packet.TryParseFrameRecorded(packet, new PacketId(i), stack, next, cache);
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
    public async Task ParseFrameRecorded_NullCache_Throws()
    {
        (Stack? stack, Packet _, FieldId _) = _ParseUdp();
        using (stack)
        {
            Frame frame = _Frame(stack);
            await Assert.That(() => Packet.ParseFrameRecorded(new PacketId(1), stack, frame, null!))
                .Throws<ArgumentNullException>();
        }
    }

    [Test]
    public async Task ParseFrameRecorded_OtherStack_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol _, ProtocolId _) = _BuildExercise();
        (Stack? other, ValueCacheExerciseProtocol protoOther, ProtocolId _) = _BuildExercise();
        using (stack)
        using (other)
        {
            ValueCache cache = new(other, [new ValueCacheFieldConfig(protoOther.NumberId)]);
            await Assert.That(() => Packet.ParseFrameRecorded(new PacketId(0), stack, _Frame(stack), cache))
                .Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task ParseFrameRecorded_Replay_DoesNotRecord()
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
            _ = Packet.ParseFrameRecorded(new PacketId(1), stack, frame, cache);
            _ = Packet.ParseFrameRecorded(new PacketId(1), stack, frame, cache);
            await Assert.That(cache.GetSeries<ulong>(portId).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ParseFrameRecorded_Overloads_AndRecycleErrors()
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
        Packet a = Packet.ParseFrameRecorded(new PacketId(0), stack, frame, cache, eth);
        Packet b = Packet.ParseFrameRecorded(new PacketId(1), stack, frame, cache, index, eth);
        RecycleError? ok = Packet.TryParseFrameRecorded(a, new PacketId(2), stack, frame, cache, eth);
        RecycleError? okIndex = Packet.TryParseFrameRecorded(b, new PacketId(3), stack, frame, cache, index);
        RecycleError? okBoth = Packet.TryParseFrameRecorded(a, new PacketId(4), stack, frame, cache, index, eth);
        Packet thrown = Packet.ParseFrameRecorded(a, new PacketId(5), stack, frame, cache);
        _ = Packet.ParseFrameRecorded(a, new PacketId(6), stack, frame, cache, eth);
        _ = Packet.ParseFrameRecorded(a, new PacketId(7), stack, frame, cache, index);
        _ = Packet.ParseFrameRecorded(a, new PacketId(8), stack, frame, cache, index, eth);
        RecycleError? mismatch = Packet.TryParseFrameRecorded(a, new PacketId(9), other, frame, cache);
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
    public async Task ParseFrameRecorded_CustomText_LastOccurrenceOverwrite_AndFirstSkips()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        CustomTextExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        ValueCache last = new(
            stack,
            [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.LastOccurrence, RecordValue: false, RecordCustomText: true)]);
        proto.OverwriteCustomText = true;
        _ = Packet.ParseFrameRecorded(new PacketId(0), stack, _Frame(stack, 0), last, protoId);
        _ = last.GetCustomTextSeries(proto.NumberId).TryGetAsString(0, out string lastText);

        proto.OverwriteCustomText = false;
        ValueCache first = new(
            stack,
            [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.FirstOccurrence, RecordValue: false, RecordCustomText: true)]);
        _ = Packet.ParseFrameRecorded(new PacketId(1), stack, _Frame(stack, 1), first, protoId);
        _ = first.GetCustomTextSeries(proto.NumberId).TryGetAsString(0, out string firstText);
        await Assert.That(lastText).IsEqualTo("second");
        await Assert.That(firstText).IsEqualTo("first");
        await Assert.That(first.GetCustomTextSeries(proto.NumberId).Count).IsEqualTo(1);
    }

    [Test]
    public async Task ParseFrameRecorded_CustomRepresentation_IsStored()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExercise();
        using (stack)
        {
            proto.WithCustomRep = true;
            ValueCache cache = new(
                stack,
                [new ValueCacheFieldConfig(proto.NumberId, RecordValue: true, RecordCustomRepresentation: true)]);
            _ = Packet.ParseFrameRecorded(new PacketId(0), stack, _Frame(stack), cache, protoId);
            _ = cache.GetCustomRepresentationSeries(proto.NumberId).TryGetAsString(0, out string text);
            await Assert.That(text).IsEqualTo("custom-rep");
        }
    }

    [Test]
    public async Task ParseFrameRecorded_InsertAfter_Tees()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        InsertAfterExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.AllOccurrences)]);
        _ = Packet.ParseFrameRecorded(new PacketId(0), stack, _Frame(stack), cache, protoId);
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

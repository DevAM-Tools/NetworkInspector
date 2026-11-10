// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Parse-time record, <see cref="Packet.TryParse(PacketId, Stack, Frame, in ParseOptions, out Packet?, out ParseFailure)"/>,
/// recycle, and custom-text mutation.</summary>
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
            0,
            Timestamp.FromSecs(1),
            FrameBuilders.GenerateStaticUdpFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        FieldId portId = stack.GetFieldId("udp.srcport")!.Value;
        Packet packet;
        if (cache is null)
        {
            ParseOptions options = new();
            if (!Packet.TryParse(
                0,
                stack,
                frame,
                in options,
                out Packet? parsed,
                out ParseFailure failure) || parsed is null)
            {
                throw new InvalidOperationException(failure.ToString());
            }

            packet = parsed;
        }
        else if (index is null)
        {
            ParseOptions options = new(FieldTreeMode.Build, cache);
            if (!Packet.TryParse(
                0,
                stack,
                frame,
                in options,
                out Packet? parsed,
                out ParseFailure failure) || parsed is null)
            {
                throw new InvalidOperationException(failure.ToString());
            }

            packet = parsed;
        }
        else
        {
            ParseOptions options = new(FieldTreeMode.Build, cache, index: index);
            if (!Packet.TryParse(
                0,
                stack,
                frame,
                in options,
                out Packet? parsed,
                out ParseFailure failure) || parsed is null)
            {
                throw new InvalidOperationException(failure.ToString());
            }

            packet = parsed;
        }

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
            id,
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
            {
                ParseOptions options = new(FieldTreeMode.Build, recorded);
                if (!Packet.TryParse(
                    1,
                    stack,
                    parsed.Frame,
                    in options,
                    out Packet? _,
                    out ParseFailure failure))
                {
                    throw new InvalidOperationException(failure.ToString());
                }
            }

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
            {
                ParseOptions options = new(FieldTreeMode.Build, cache);
                if (!Packet.TryParse(
                    1,
                    stack,
                    parsed.Frame,
                    in options,
                    out Packet? _,
                    out ParseFailure failure))
                {
                    throw new InvalidOperationException(failure.ToString());
                }
            }
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
        Packet recordedPacket;
        {
            ParseOptions options = new(FieldTreeMode.Build, recorded, firstProtocol: protoId);
            if (!Packet.TryParse(
                0,
                stack,
                frame,
                in options,
                out Packet? parsed,
                out ParseFailure failure) || parsed is null)
            {
                throw new InvalidOperationException(failure.ToString());
            }

            recordedPacket = parsed;
        }
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
        Packet failed;
        {
            ParseOptions options = new(FieldTreeMode.Build, cache, firstProtocol: protoId);
            if (!Packet.TryParse(
                0,
                stack,
                _Frame(stack, 0),
                in options,
                out Packet? parsed,
                out ParseFailure failure) || parsed is null)
            {
                throw new InvalidOperationException(failure.ToString());
            }

            failed = parsed;
        }
        bool hasError = failed.TryGetFieldValue(stack.PacketErrorFieldId, out _, materialize: true);
        await Assert.That(hasError).IsTrue();
        await Assert.That(cache.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(1);
        await Assert.That(cache.GetSeries<ulong>(proto.NumberId)[0].Value).IsEqualTo(1UL);

        proto.ThrowAfterAppend = false;
        {
            ParseOptions options = new(FieldTreeMode.Build, cache, firstProtocol: protoId);
            if (!Packet.TryParse(
                1,
                stack,
                _Frame(stack, 1),
                in options,
                out Packet? _,
                out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }
        }
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
                1,
                Timestamp.FromSecs(2),
                FrameBuilders.GenerateStaticUdpFrame(),
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            {
                ParseOptions options = new(FieldTreeMode.Build, cache, index: index);
                if (!Packet.TryParse(
                    1,
                    stack,
                    frame,
                    in options,
                    out Packet? _,
                    out ParseFailure failure))
                {
                    throw new InvalidOperationException(failure.ToString());
                }
            }
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
            0,
            Timestamp.FromSecs(1),
            FrameBuilders.GenerateStaticUdpFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        Packet packet;
        {
            ParseOptions options = new(FieldTreeMode.Build, cache);
            if (!Packet.TryParse(
                0,
                stack,
                frame0,
                in options,
                out Packet? parsed,
                out ParseFailure failure) || parsed is null)
            {
                throw new InvalidOperationException(failure.ToString());
            }

            packet = parsed;
        }
        for (int i = 1; i < 100; i++)
        {
            Frame frame = Frame.Create(
                i,
                Timestamp.FromSecs(i + 1),
                FrameBuilders.GenerateStaticUdpFrame(),
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            bool errParsed;
            {
                ParseOptions options = new(FieldTreeMode.Build, cache);
                errParsed = Packet.TryParse(
                    packet,
                    i,
                    stack,
                    frame,
                    in options,
                    out ParseFailure _);
            }
            await Assert.That(errParsed).IsTrue();
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
            0,
            Timestamp.FromSecs(1),
            FrameBuilders.GenerateStaticUdpFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        Packet packet;
        {
            ParseOptions options = new(FieldTreeMode.Build, cache);
            if (!Packet.TryParse(
                0,
                stack,
                frame,
                in options,
                out Packet? parsed,
                out ParseFailure failure) || parsed is null)
            {
                throw new InvalidOperationException(failure.ToString());
            }

            packet = parsed;
        }
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
            0,
            Timestamp.FromSecs(1),
            FrameBuilders.GenerateStaticUdpFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        {
            ParseOptions options = new(FieldTreeMode.Build, cache);
            if (!Packet.TryParse(
                0,
                stack,
                frame,
                in options,
                out Packet? _,
                out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }
        }
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
            0,
            Timestamp.FromSecs(1),
            FrameBuilders.GenerateStaticUdpFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        Packet packet;
        {
            ParseOptions options = new(FieldTreeMode.Build, cache);
            if (!Packet.TryParse(
                0,
                stack,
                frame,
                in options,
                out Packet? parsed,
                out ParseFailure failure) || parsed is null)
            {
                throw new InvalidOperationException(failure.ToString());
            }

            packet = parsed;
        }
        for (int i = 1; i < 32; i++)
        {
            Frame next = Frame.Create(
                i,
                Timestamp.FromSecs(i + 1),
                FrameBuilders.GenerateStaticUdpFrame(),
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            bool errParsed;
            {
                ParseOptions options = new(FieldTreeMode.Build, cache);
                errParsed = Packet.TryParse(
                    packet,
                    i,
                    stack,
                    next,
                    in options,
                    out ParseFailure _);
            }
            await Assert.That(errParsed).IsTrue();
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
            ParseOptions options = new(FieldTreeMode.Build, cache);
            bool parsed = Packet.TryParse(
                0,
                stack,
                _Frame(stack),
                in options,
                out Packet? _,
                out ParseFailure failure);
            await Assert.That(parsed).IsFalse();
            await Assert.That(failure).IsEqualTo(ParseFailure.CacheStackMismatch);
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
                1,
                Timestamp.FromSecs(2),
                FrameBuilders.GenerateStaticUdpFrame(),
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            {
                ParseOptions options = new(FieldTreeMode.Build, cache);
                if (!Packet.TryParse(
                    1,
                    stack,
                    frame,
                    in options,
                    out Packet? _,
                    out ParseFailure failure))
                {
                    throw new InvalidOperationException(failure.ToString());
                }
            }
            {
                ParseOptions options = new(FieldTreeMode.Build, cache);
                if (!Packet.TryParse(
                    1,
                    stack,
                    frame,
                    in options,
                    out Packet? _,
                    out ParseFailure failure))
                {
                    throw new InvalidOperationException(failure.ToString());
                }
            }
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
            0,
            Timestamp.FromSecs(1),
            FrameBuilders.GenerateStaticUdpFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        Packet a;
        {
            ParseOptions options = new(FieldTreeMode.Build, cache, firstProtocol: eth);
            if (!Packet.TryParse(
                0,
                stack,
                frame,
                in options,
                out Packet? parsed,
                out ParseFailure failure) || parsed is null)
            {
                throw new InvalidOperationException(failure.ToString());
            }

            a = parsed;
        }
        Packet b;
        {
            ParseOptions options = new(FieldTreeMode.Build, cache, index: index, firstProtocol: eth);
            if (!Packet.TryParse(
                1,
                stack,
                frame,
                in options,
                out Packet? parsed,
                out ParseFailure failure) || parsed is null)
            {
                throw new InvalidOperationException(failure.ToString());
            }

            b = parsed;
        }
        bool okParsed;
        {
            ParseOptions options = new(FieldTreeMode.Build, cache, firstProtocol: eth);
            okParsed = Packet.TryParse(
                a,
                2,
                stack,
                frame,
                in options,
                out ParseFailure _);
        }
        bool okIndexParsed;
        {
            ParseOptions options = new(FieldTreeMode.Build, cache, index: index);
            okIndexParsed = Packet.TryParse(
                b,
                3,
                stack,
                frame,
                in options,
                out ParseFailure _);
        }
        bool okBothParsed;
        {
            ParseOptions options = new(FieldTreeMode.Build, cache, index: index, firstProtocol: eth);
            okBothParsed = Packet.TryParse(
                a,
                4,
                stack,
                frame,
                in options,
                out ParseFailure _);
        }
        Packet thrown;
        {
            ParseOptions options = new(FieldTreeMode.Build, cache);
            if (!Packet.TryParse(
                a,
                5,
                stack,
                frame,
                in options,
                out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }

            thrown = a;
        }
        {
            ParseOptions options = new(FieldTreeMode.Build, cache, firstProtocol: eth);
            if (!Packet.TryParse(
                a,
                6,
                stack,
                frame,
                in options,
                out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }
        }
        {
            ParseOptions options = new(FieldTreeMode.Build, cache, index: index);
            if (!Packet.TryParse(
                a,
                7,
                stack,
                frame,
                in options,
                out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }
        }
        {
            ParseOptions options = new(FieldTreeMode.Build, cache, index: index, firstProtocol: eth);
            if (!Packet.TryParse(
                a,
                8,
                stack,
                frame,
                in options,
                out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }
        }
        bool mismatchParsed;
        ParseFailure mismatchFailure;
        {
            ParseOptions options = new(FieldTreeMode.Build, cache);
            mismatchParsed = Packet.TryParse(
                a,
                9,
                other,
                frame,
                in options,
                out mismatchFailure);
        }
        await Assert.That(okParsed).IsTrue();
        await Assert.That(okIndexParsed).IsTrue();
        await Assert.That(okBothParsed).IsTrue();
        await Assert.That(thrown).IsSameReferenceAs(a);
        await Assert.That(mismatchParsed).IsFalse();
        await Assert.That(mismatchFailure).IsEqualTo(ParseFailure.StackMismatch);
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
        {
            ParseOptions options = new(FieldTreeMode.Build, first, firstProtocol: protoId);
            if (!Packet.TryParse(
                0,
                stack,
                _Frame(stack, 0),
                in options,
                out Packet? _,
                out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }
        }
        ValueCacheSeries<string> series = first.GetCustomTextSeries(proto.NumberId);
        await Assert.That(series.Count).IsEqualTo(1);
        await Assert.That(series[0].Value).IsEqualTo("first");
    }

    [Test]
    [Arguments(FieldTreeMode.Build)]
    [Arguments(FieldTreeMode.Skip)]
    public async Task ParseFrame_CustomText_CompletedAfterAppend_IsRecordedFinal(FieldTreeMode tree)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        CustomTextExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        ValueCache cache = new(
            stack,
            [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.FirstOccurrence, RecordValue: false, RecordCustomText: true)]);
        proto.DeferredText = true;
        ParseOptions options = new(tree, cache, firstProtocol: protoId);
        if (!Packet.TryParse(0, stack, _Frame(stack, 0), in options, out Packet? _, out ParseFailure failure))
        {
            throw new InvalidOperationException(failure.ToString());
        }

        ValueCacheSeries<string> series = cache.GetCustomTextSeries(proto.NumberId);

        // The cache must hold the text the dissector completed, not the placeholder it had at the append.
        await Assert.That(series.Count).IsEqualTo(1);
        await Assert.That(series[0].Value).IsEqualTo("final");
    }

    [Test]
    [Arguments(FieldTreeMode.Build)]
    [Arguments(FieldTreeMode.Skip)]
    public async Task MixedTextsOfOneFieldKeepTheirAppendOrder(FieldTreeMode tree)
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
        ValueCache all = new(
            stack,
            [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.AllOccurrences, RecordValue: false, RecordCustomText: true)]);
        proto.MixedOccurrences = true;
        ParseOptions firstOptions = new(tree, first, firstProtocol: protoId);
        ParseOptions allOptions = new(tree, all, firstProtocol: protoId);
        bool firstParsed = Packet.TryParse(0, stack, _Frame(stack, 0), in firstOptions, out Packet? _, out _);
        bool allParsed = Packet.TryParse(1, stack, _Frame(stack, 1), in allOptions, out Packet? _, out _);

        ValueCacheSeries<string> firstSeries = first.GetCustomTextSeries(proto.NumberId);
        ValueCacheSeries<string> allSeries = all.GetCustomTextSeries(proto.NumberId);

        // The deferred text of occurrence 1 is recorded at the end of the parse; the plain text of occurrence 2 must wait behind
        // it, or FirstOccurrence keeps occurrence 2 and AllOccurrences swaps the rows.
        await Assert.That(firstParsed && allParsed).IsTrue();
        await Assert.That(firstSeries.Count).IsEqualTo(1);
        await Assert.That(firstSeries[0].Value).IsEqualTo("final");
        await Assert.That(allSeries.Count).IsEqualTo(2);
        await Assert.That(allSeries[0].Value).IsEqualTo("final");
        await Assert.That(allSeries[1].Value).IsEqualTo("plain");
    }

    [Test]
    public async Task AReplacedDeferredTextIsRecordedLikeAReplacedPlainText()
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
        ValueCache all = new(
            stack,
            [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.AllOccurrences, RecordValue: false, RecordCustomText: true)]);
        proto.DeferredThenOverwrite = true;
        ParseOptions firstOptions = new(FieldTreeMode.Build, first, firstProtocol: protoId);
        ParseOptions allOptions = new(FieldTreeMode.Build, all, firstProtocol: protoId);
        bool firstParsed = Packet.TryParse(0, stack, _Frame(stack, 0), in firstOptions, out Packet? _, out _);
        bool allParsed = Packet.TryParse(1, stack, _Frame(stack, 1), in allOptions, out Packet? _, out _);

        ValueCacheSeries<string> firstSeries = first.GetCustomTextSeries(proto.NumberId);
        ValueCacheSeries<string> allSeries = all.GetCustomTextSeries(proto.NumberId);

        // Same rule as ParseFrame_CustomText_FirstOccurrence_SkipsLaterMutation for plain text: the text of the append (here
        // completed to "final"), then the change; never the changed text twice.
        await Assert.That(firstParsed && allParsed).IsTrue();
        await Assert.That(firstSeries.Count).IsEqualTo(1);
        await Assert.That(firstSeries[0].Value).IsEqualTo("final");
        await Assert.That(allSeries.Count).IsEqualTo(2);
        await Assert.That(allSeries[0].Value).IsEqualTo("final");
        await Assert.That(allSeries[1].Value).IsEqualTo("second");
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
            {
                ParseOptions options = new(FieldTreeMode.Build, cache, firstProtocol: protoId);
                if (!Packet.TryParse(
                    0,
                    stack,
                    _Frame(stack),
                    in options,
                    out Packet? _,
                    out ParseFailure failure))
                {
                    throw new InvalidOperationException(failure.ToString());
                }
            }
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
        {
            ParseOptions options = new(FieldTreeMode.Build, cache, firstProtocol: protoId);
            if (!Packet.TryParse(
                0,
                stack,
                _Frame(stack),
                in options,
                out Packet? _,
                out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }
        }
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

    /// <summary>Appends a deferred text and completes it after the append, still inside the parse (like a SOME/IP variable array).</summary>
    public bool DeferredText;

    /// <summary>Appends the field twice: first with a deferred text completed to "final", then with the plain text "plain".</summary>
    public bool MixedOccurrences;

    /// <summary>Appends a deferred text, completes it to "final", then replaces it with "second" before the parse ends.</summary>
    public bool DeferredThenOverwrite;
    private int _Resets;

    /// <summary>State of a deferred text; the text is read when it is first needed, not when it is appended.</summary>
    private sealed class CompletedLater
    {
        internal string Value = "placeholder";

        public override string ToString() => Value;
    }

    public string Name => "vcxtext";
    public string UiName => "CustomText Exercise";

    public void ResetParseState() => _Resets++;

    public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
    {
        NumberId = builder.RegisterField(protocolId, "vcx.text", "Number", FieldType.U64);
    }

    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (DeferredText)
        {
            CompletedLater state = new();
            _ = parentField.AppendWithCustomText(NumberId, FieldValue.NewU64(1), state);
            state.Value = "final";
            return data.Length;
        }

        if (MixedOccurrences)
        {
            CompletedLater state = new();
            _ = parentField.AppendWithCustomText(NumberId, FieldValue.NewU64(1), state);
            state.Value = "final";
            _ = parentField.AppendWithCustomText(NumberId, FieldValue.NewU64(2), new LazyString("plain"));
            return data.Length;
        }

        if (DeferredThenOverwrite)
        {
            CompletedLater state = new();
            MutField deferred = parentField.AppendWithCustomText(NumberId, FieldValue.NewU64(1), state);
            state.Value = "final";
            deferred.SetCustomText(new LazyString("second"));
            return data.Length;
        }

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

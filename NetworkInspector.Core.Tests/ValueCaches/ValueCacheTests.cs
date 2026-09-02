// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Construction, capture modes, limits, flags, materialization, and RecordPacket for <see cref="ValueCache"/>.</summary>
internal sealed class ValueCacheTests
{
    #region Helpers

    private static (Stack Stack, ValueCacheExerciseProtocol Proto, ProtocolId ProtoId) _BuildExerciseStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ValueCacheExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        Stack stack = builder.Build();
        return (stack, proto, protoId);
    }

    private static Packet _Parse(Stack stack, ProtocolId firstProtocolId, ValueCacheExerciseProtocol proto)
    {
        Frame frame = Frame.Create(
            new FrameId(1),
            Timestamp.FromSecs(1000),
            new byte[16],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        proto.ResetParseState();
        return Packet.ParseFrame(new PacketId(0), stack, frame, firstProtocolId);
    }

    private static Packet _ParseId(Stack stack, ProtocolId firstProtocolId, ValueCacheExerciseProtocol proto, int packetId, long timestampSecs)
    {
        Frame frame = Frame.Create(
            new FrameId(packetId),
            Timestamp.FromSecs(timestampSecs),
            new byte[16],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        proto.ResetParseState();
        return Packet.ParseFrame(new PacketId(packetId), stack, frame, firstProtocolId);
    }

    internal static long ExpectedUnmanagedByteSize(int rowCount, int sizeofT)
    {
        if (rowCount <= 0)
        {
            return 0;
        }

        long perRow = 4 + 8 + sizeofT;
        int chunkAllocs = ((rowCount - 1) / ValueCacheColumnState.ChunkSize) + 1;
        long chunkBytes = chunkAllocs * (
            (long)ValueCacheColumnState.ChunkSize * 4
            + (long)ValueCacheColumnState.ChunkSize * 8
            + (long)ValueCacheColumnState.ChunkSize * sizeofT);
        return (perRow * rowCount) + chunkBytes;
    }

    private static (Stack Stack, Packet Packet) _BuildStandardUdp()
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
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);
        return (stack, packet);
    }

    #endregion

    #region Construction

    [Test]
    public async Task Ctor_NullStack_Throws()
    {
        await Assert.That(() => new ValueCache(null!, [])).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Ctor_UnknownField_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol _, ProtocolId _) = _BuildExerciseStack();
        using (stack)
        {
            await Assert.That(() => new ValueCache(stack, [new ValueCacheFieldConfig(new FieldId(50_000))]))
                .Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task Ctor_UnknownGroup_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol _, ProtocolId _) = _BuildExerciseStack();
        using (stack)
        {
            await Assert.That(() => new ValueCache(stack, [], [new ValueCacheGroupConfig(new IndexGroupId(50_000))]))
                .Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task Ctor_EmptyConfigWithoutRecordAllFields_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol _, ProtocolId _) = _BuildExerciseStack();
        using (stack)
        {
            await Assert.That(() => new ValueCache(stack, []))
                .Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task Ctor_MaxRowCountZero_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _) = _BuildExerciseStack();
        using (stack)
        {
            ValueCacheBuildOptions options = new()
            {
                Limits = new ValueCacheLimits(0, null),
            };
            await Assert.That(() => new ValueCache(stack, [new ValueCacheFieldConfig(proto.NumberId)], options: options))
                .Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task Ctor_AllRecordFlagsFalse_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _) = _BuildExerciseStack();
        using (stack)
        {
            await Assert.That(() => new ValueCache(
                    stack,
                    [new ValueCacheFieldConfig(proto.NumberId, RecordValue: false)]))
                .Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task Ctor_DuplicatePayloadField_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _) = _BuildExerciseStack();
        using (stack)
        {
            await Assert.That(() => new ValueCache(
                    stack,
                    [new ValueCacheFieldConfig(proto.NumberId), new ValueCacheFieldConfig(proto.NumberId)]))
                .Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task Ctor_ExplicitFieldOverridesGroupMode()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            proto.AppendTwice = true;
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(
                stack,
                [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.LastOccurrence)],
                [new ValueCacheGroupConfig(proto.NumberGroupId, ValueCaptureMode.AllOccurrences)]);
            cache.RecordPacket(packet);

            ValueCacheSeries<ulong> series = cache.GetSeries<ulong>(proto.NumberId);
            await Assert.That(series.CaptureMode).IsEqualTo(ValueCaptureMode.LastOccurrence);
            await Assert.That(series.Count).IsEqualTo(1);
            await Assert.That(series[0].Value).IsEqualTo(2UL);
        }
    }

    [Test]
    public async Task Ctor_RecordAllFields_CreatesPayloadPerField_NoCustomText()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _) = _BuildExerciseStack();
        using (stack)
        {
            ValueCache cache = new(
                stack,
                [],
                options: new ValueCacheBuildOptions { RecordAllFields = true });

            await Assert.That(cache.TryGetSeries<byte>(stack.RootFieldId, out _)).IsTrue();
            await Assert.That(cache.TryGetCustomTextSeries(proto.NumberId, out _)).IsFalse();
            await Assert.That(cache.Series.Count).IsEqualTo(stack.FieldCount);
        }
    }

    #endregion

    #region Capture modes

    [Test]
    public async Task RecordPacket_FirstOccurrence_StoresOneRow()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            proto.AppendTwice = true;
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.FirstOccurrence)]);
            cache.RecordPacket(packet);

            ValueCacheSeries<ulong> series = cache.GetSeries<ulong>(proto.NumberId);
            await Assert.That(series.Count).IsEqualTo(1);
            await Assert.That(series[0].Value).IsEqualTo(1UL);
        }
    }

    [Test]
    public async Task RecordPacket_LastOccurrence_StoresSecondValue()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            proto.AppendTwice = true;
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.LastOccurrence)]);
            cache.RecordPacket(packet);

            ValueCacheSeries<ulong> series = cache.GetSeries<ulong>(proto.NumberId);
            await Assert.That(series.Count).IsEqualTo(1);
            await Assert.That(series[0].Value).IsEqualTo(2UL);
        }
    }

    [Test]
    public async Task RecordPacket_AllOccurrences_StoresTwoRowsSamePacketId()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            proto.AppendTwice = true;
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.AllOccurrences)]);
            cache.RecordPacket(packet);

            ValueCacheSeries<ulong> series = cache.GetSeries<ulong>(proto.NumberId);
            await Assert.That(series.Count).IsEqualTo(2);
            await Assert.That(series[0].PacketId).IsEqualTo(series[1].PacketId);
            await Assert.That(series[0].Value).IsEqualTo(1UL);
            await Assert.That(series[1].Value).IsEqualTo(2UL);
        }
    }

    #endregion

    #region Custom text / representation

    [Test]
    public async Task RecordPacket_CustomTextAndRepresentation_SameField()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            proto.WithCustomText = true;
            proto.WithCustomRep = true;
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(
                stack,
                [new ValueCacheFieldConfig(
                    proto.NumberId,
                    RecordValue: true,
                    RecordCustomText: true,
                    RecordCustomRepresentation: true)]);
            cache.RecordPacket(packet);

            ValueCacheSeries<ulong> payload = cache.GetSeries<ulong>(proto.NumberId);
            ValueCacheStringSeries text = cache.GetCustomTextSeries(proto.NumberId);
            ValueCacheStringSeries rep = cache.GetCustomRepresentationSeries(proto.NumberId);
            await Assert.That(payload.Count).IsEqualTo(1);
            await Assert.That(text.TryGetAsString(0, out string? textValue)).IsTrue();
            await Assert.That(textValue).IsEqualTo("custom-text");
            await Assert.That(rep.TryGetAsString(0, out string? repValue)).IsTrue();
            await Assert.That(repValue).IsEqualTo("custom-rep");
        }
    }

    [Test]
    public async Task RecordPacket_SecondPacketWithoutCustomText_DoesNotAddTextRow()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            proto.WithCustomText = true;
            Packet first = _ParseId(stack, protoId, proto, 0, 1);
            proto.WithCustomText = false;
            Packet second = _ParseId(stack, protoId, proto, 1, 2);
            ValueCache cache = new(
                stack,
                [new ValueCacheFieldConfig(proto.NumberId, RecordValue: true, RecordCustomText: true)]);
            cache.RecordPacket(first);
            cache.RecordPacket(second);

            await Assert.That(cache.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(2);
            await Assert.That(cache.GetCustomTextSeries(proto.NumberId).Count).IsEqualTo(1);
        }
    }

    #endregion

    #region Type mismatch / named series

    [Test]
    public async Task GetSeries_TypeMismatch_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);

            await Assert.That(() => cache.GetSeries<uint>(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(cache.TryGetSeries<uint>(proto.NumberId, out _)).IsFalse();
        }
    }

    [Test]
    public async Task RecordPacket_IPv6_HighLowChunks()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.Ipv6Id)]);
            cache.RecordPacket(packet);

            ValueCacheIPv6Series series = cache.GetIPv6Series(proto.Ipv6Id);
            bool gotHigh = series.TryGetHighChunk(0, series.Count, out ReadOnlySpan<ulong> high);
            ulong high0 = gotHigh ? high[0] : 0UL;
            await Assert.That(series.Count).IsEqualTo(1);
            await Assert.That(gotHigh).IsTrue();
            await Assert.That(high0).IsEqualTo(0x20010DB800000000UL);
        }
    }

    [Test]
    public async Task RecordPacket_Uuid_HighLowChunks()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.UuidId)]);
            cache.RecordPacket(packet);

            ValueCacheUuidSeries series = cache.GetUuidSeries(proto.UuidId);
            bool gotLow = series.TryGetLowChunk(0, series.Count, out ReadOnlySpan<ulong> low);
            ulong low0 = gotLow ? low[0] : 0UL;
            await Assert.That(series.Count).IsEqualTo(1);
            await Assert.That(gotLow).IsTrue();
            await Assert.That(low0).IsEqualTo(2UL);
        }
    }

    [Test]
    public async Task RecordPacket_Bytes_CopyOutlivesFrameMutation()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.BytesId)]);
            cache.RecordPacket(packet);
            proto.BytesBuffer[0] = 0xFF;

            ValueCacheBytesSeries series = cache.GetBytesSeries(proto.BytesId);
            bool got = series.TryGetAsBytes(0, out ReadOnlyMemory<byte> copy);
            byte first = got && copy.Length > 0 ? copy.Span[0] : (byte)0;
            await Assert.That(got).IsTrue();
            await Assert.That(first).IsEqualTo((byte)1);
        }
    }

    [Test]
    public async Task RecordPacket_LazyString_EvaluatesStable()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.StringId)]);
            cache.RecordPacket(packet);

            bool found = cache.Series[0] is ValueCacheStringSeries strings
                && strings.TryGetAsString(0, out string? text)
                && text == "lazy-string";
            await Assert.That(found).IsTrue();
        }
    }

    #endregion

    #region Lazy / all-fields

    [Test]
    public async Task RecordPacket_LazyTtl_ProducesRow_UnrelatedStayLazy()
    {
        (Stack? stack, Packet packet) = _BuildStandardUdp();
        using (stack)
        {
            FieldId? ttlId = stack.GetFieldId("ip.ttl");
            await Assert.That(ttlId).IsNotNull();
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(ttlId!.Value)]);
            cache.RecordPacket(packet);

            ValueCacheSeries<ulong> series = cache.GetSeries<ulong>(ttlId.Value);
            await Assert.That(series.Count).IsEqualTo(1);
            await Assert.That(packet.HasUnpopulatedLazyFields).IsTrue();
        }
    }

    [Test]
    public async Task RecordPacket_RecordAllFields_ContainerAndUdpPort()
    {
        (Stack? stack, Packet packet) = _BuildStandardUdp();
        using (stack)
        {
            ValueCache cache = new(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true });
            cache.RecordPacket(packet);

            FieldId? portId = stack.GetFieldId("udp.srcport");
            await Assert.That(portId).IsNotNull();
            await Assert.That(cache.GetSeries<ulong>(portId!.Value).Count).IsEqualTo(1);
            await Assert.That(cache.TryGetSeries<byte>(stack.RootFieldId, out ValueCacheSeries<byte>? root)).IsTrue();
            await Assert.That(root).IsNotNull();
            await Assert.That(root!.Count).IsEqualTo(1);
        }
    }

    #endregion

    #region Limits / flags / misuse

    [Test]
    public async Task Limits_MaxRowCountOne_SecondPacketUnpublished()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            Packet first = _ParseId(stack, protoId, proto, 0, 1);
            Packet second = _ParseId(stack, protoId, proto, 1, 2);
            ValueCache cache = new(
                stack,
                [new ValueCacheFieldConfig(proto.NumberId)],
                options: new ValueCacheBuildOptions { Limits = new ValueCacheLimits(1, null) });
            cache.RecordPacket(first);
            cache.RecordPacket(second);

            await Assert.That(cache.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(1);
            await Assert.That(cache.IsCapacityReached).IsTrue();
        }
    }

    [Test]
    public async Task Flags_IncreasingIdsAndTimestamps_StayTrue()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(_ParseId(stack, protoId, proto, 0, 10));
            cache.RecordPacket(_ParseId(stack, protoId, proto, 1, 11));
            cache.RecordPacket(_ParseId(stack, protoId, proto, 2, 12));

            await Assert.That(cache.PacketIdsStrictlyIncreasing).IsTrue();
            await Assert.That(cache.TimestampsStrictlyIncreasing).IsTrue();
        }
    }

    [Test]
    public async Task Flags_EqualTimestamps_ClearsTimestampFlagOnly()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(_ParseId(stack, protoId, proto, 0, 10));
            cache.RecordPacket(_ParseId(stack, protoId, proto, 1, 10));

            await Assert.That(cache.PacketIdsStrictlyIncreasing).IsTrue();
            await Assert.That(cache.TimestampsStrictlyIncreasing).IsFalse();
        }
    }

    [Test]
    public async Task Flags_DecreasingIds_ClearsPacketIdFlag_Sticky()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            Packet p0 = _ParseId(stack, protoId, proto, 0, 10);
            Packet p1 = _ParseId(stack, protoId, proto, 1, 30);
            Packet p2 = _ParseId(stack, protoId, proto, 2, 20);
            Packet p3 = _ParseId(stack, protoId, proto, 3, 40);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(p0);
            cache.RecordPacket(p2);
            cache.RecordPacket(p1);
            cache.RecordPacket(p3);

            await Assert.That(cache.PacketIdsStrictlyIncreasing).IsFalse();
            await Assert.That(cache.TimestampsStrictlyIncreasing).IsTrue();
        }
    }

    [Test]
    public async Task ByteSize_MatchesC13_ForUdpPortSeries()
    {
        (Stack? stack, Packet packet) = _BuildStandardUdp();
        using (stack)
        {
            FieldId? portId = stack.GetFieldId("udp.srcport");
            await Assert.That(portId).IsNotNull();
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(portId!.Value)]);
            cache.RecordPacket(packet);

            ValueCacheSeries<ulong> series = cache.GetSeries<ulong>(portId.Value);
            long expected = ExpectedUnmanagedByteSize(series.Count, sizeof(ulong));
            await Assert.That(series.ByteSize).IsEqualTo(expected);
            await Assert.That(cache.ByteSize).IsGreaterThanOrEqualTo(expected);
        }
    }

    [Test]
    public async Task BeginPacket_Nested_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _) = _BuildExerciseStack();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.BeginPacket(0, 1);
            await Assert.That(() => cache.BeginPacket(1, 2)).Throws<InvalidOperationException>();
            cache.EndPacket();
        }
    }

    [Test]
    public async Task EndPacket_WithoutBegin_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _) = _BuildExerciseStack();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            await Assert.That(() => cache.EndPacket()).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task RecordPacket_OtherStack_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        (Stack? other, ValueCacheExerciseProtocol otherProto, ProtocolId otherId) = _BuildExerciseStack();
        using (stack)
        using (other)
        {
            Packet packet = _Parse(other, otherId, otherProto);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            await Assert.That(() => cache.RecordPacket(packet)).Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task RecordPacket_Unsealed_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _) = _BuildExerciseStack();
        using (stack)
        {
            Frame frame = Frame.Create(
                new FrameId(1),
                Timestamp.FromSecs(1),
                new byte[16],
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            Packet unsealed = new(new PacketId(0), stack, frame);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            await Assert.That(() => cache.RecordPacket(unsealed)).Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task Abandon_BeginPacket_Throws_ReadsRemain()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);
            cache.Abandon();
            ValueCacheReaderView view = cache.AsReadOnlyView();

            await Assert.That(cache.IsAbandoned).IsTrue();
            await Assert.That(view.IsAbandoned).IsTrue();
            await Assert.That(() => cache.BeginPacket(1, 1)).Throws<InvalidOperationException>();
            await Assert.That(cache.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ReaderView_ForwardsSeries()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);
            ValueCacheReaderView view = cache.AsReadOnlyView();
            await Assert.That(view.IsAbandoned).IsFalse();
            await Assert.That(view.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(1);
        }
    }

    #endregion
}

/// <summary>Protocol used by <see cref="ValueCacheTests"/> to append configurable field trees.</summary>
internal sealed class ValueCacheExerciseProtocol : IProtocol
{
    public FieldId NumberId;
    public FieldId StringId;
    public FieldId BytesId;
    public FieldId Ipv6Id;
    public FieldId UuidId;
    public FieldId LazyTtlId;
    public FieldId LazyContainerId;
    public FieldId BoolId;
    public FieldId I64Id;
    public FieldId F64Id;
    public FieldId MacId;
    public FieldId Ipv4Id;
    public FieldId Eui64Id;
    public FieldId TimestampId;
    public FieldId NoneId;
    public FieldId LazyNoGroupId;
    public IndexGroupId NumberGroupId;
    public byte[] BytesBuffer = [1, 2, 3];
    public bool AppendTwice;
    public bool WithCustomText;
    public bool WithCustomRep;
    public bool NestedLazyOnMaterialize;

    public string Name => "vcx";
    public string UiName => "ValueCache Exercise";

    public void ResetParseState()
    {
        BytesBuffer = [1, 2, 3];
    }

    public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
    {
        NumberId = builder.RegisterFieldInGroup(protocolId, "vcx.num", "Number", FieldType.U64, "vcx.num");
        NumberGroupId = builder.GetOrCreateIndexGroup("vcx.num");
        StringId = builder.RegisterField(protocolId, "vcx.str", "String", FieldType.String);
        BytesId = builder.RegisterField(protocolId, "vcx.bytes", "Bytes", FieldType.Bytes);
        Ipv6Id = builder.RegisterField(protocolId, "vcx.ip6", "IPv6", FieldType.IPv6Address);
        UuidId = builder.RegisterField(protocolId, "vcx.uuid", "Uuid", FieldType.Uuid);
        LazyContainerId = builder.RegisterFieldInGroup(protocolId, "vcx.lazy", "Lazy", FieldType.None, "vcx.lazy");
        LazyTtlId = builder.RegisterFieldInGroup(protocolId, "vcx.ttl", "TTL", FieldType.U64, "vcx.lazy");
        BoolId = builder.RegisterField(protocolId, "vcx.bool", "Bool", FieldType.Bool);
        I64Id = builder.RegisterField(protocolId, "vcx.i64", "I64", FieldType.I64);
        F64Id = builder.RegisterField(protocolId, "vcx.f64", "F64", FieldType.F64);
        MacId = builder.RegisterField(protocolId, "vcx.mac", "Mac", FieldType.MacAddress);
        Ipv4Id = builder.RegisterField(protocolId, "vcx.ip4", "IPv4", FieldType.IPv4Address);
        Eui64Id = builder.RegisterField(protocolId, "vcx.eui", "Eui64", FieldType.Eui64);
        TimestampId = builder.RegisterField(protocolId, "vcx.ts", "Timestamp", FieldType.Timestamp);
        NoneId = builder.RegisterField(protocolId, "vcx.none", "None", FieldType.None);
        LazyNoGroupId = builder.RegisterField(protocolId, "vcx.lazynogroup", "LazyNoGroup", FieldType.None);
    }

    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        FieldValue number = FieldValue.NewU64(1);
        if (WithCustomRep)
        {
            number = number.WithCustomRepresentation(new LazyString("custom-rep"));
        }

        if (WithCustomText)
        {
            parentField.AppendWithCustomText(NumberId, number, new LazyString("custom-text"));
        }
        else
        {
            parentField.Append(NumberId, number);
        }

        if (AppendTwice)
        {
            parentField.Append(NumberId, FieldValue.NewU64(2));
        }

        parentField.Append(StringId, FieldValue.NewLazyString(new LazyString("lazy-string")));
        parentField.Append(BytesId, FieldValue.NewBytes(BytesBuffer));
        parentField.Append(Ipv6Id, FieldValue.NewIPv6(new IPv6Address(0x20010DB800000000UL, 1)));
        parentField.Append(UuidId, FieldValue.NewUuid(new Uuid(1, 2)));
        parentField.Append(BoolId, FieldValue.NewBool(true));
        parentField.Append(I64Id, FieldValue.NewI64(-7));
        parentField.Append(F64Id, FieldValue.NewF64(1.5));
        parentField.Append(MacId, FieldValue.NewMacAddress(new MacAddress(0xAABBCCDDEEFFUL)));
        parentField.Append(Ipv4Id, FieldValue.NewIPv4(new IPv4Address(0xC0A80101)));
        parentField.Append(Eui64Id, FieldValue.NewEui64(new Eui64(0x1122334455667788UL)));
        parentField.Append(TimestampId, FieldValue.NewTimestamp(new Timestamp(123)));
        parentField.Append(NoneId, FieldValue.None);

        FieldId ttlId = LazyTtlId;
        FieldId nestedId = LazyNoGroupId;
        bool nested = NestedLazyOnMaterialize;
        parentField.AppendLazy(LazyContainerId, FieldValue.None, (in MutField container) =>
        {
            container.Append(ttlId, FieldValue.NewU64(64));
            if (nested)
            {
                container.AppendLazy(nestedId, FieldValue.None, (in MutField _) => 0);
            }

            return 0;
        });

        parentField.AppendLazy(LazyNoGroupId, FieldValue.None, (in MutField _) => 0);

        return data.Length;
    }
}

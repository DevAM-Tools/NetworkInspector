// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Construction, capture modes, flags, materialization, and RecordPacket for <see cref="ValueCache"/>.</summary>
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

    private static bool _ContainsField(ValueCache cache, FieldId fieldId)
    {
        IReadOnlyList<ValueCacheSeries> series = cache.Series;
        for (int i = 0; i < series.Count; i++)
        {
            if (series[i].FieldId == fieldId)
            {
                return true;
            }
        }

        return false;
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
    public async Task Ctor_ObjectInitializerOmitsChunkShift_UsesDefault12()
    {
        (Stack? stack, ValueCacheExerciseProtocol _, ProtocolId _) = _BuildExerciseStack();
        using (stack)
        {
            ValueCache cache = new(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true });
            await Assert.That(cache.ChunkShift).IsEqualTo(12);
        }
    }

    [Test]
    public async Task Ctor_DefaultStructOptions_Throws()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _) = _BuildExerciseStack();
        using (stack)
        {
            await Assert.That(() => _ = new ValueCache(
                    stack,
                    [new ValueCacheFieldConfig(proto.NumberId)],
                    options: default(ValueCacheBuildOptions)))
                .Throws<ArgumentOutOfRangeException>();
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
                [new ValueCacheFieldConfig(proto.NumberId, ValueCaptureMode.FirstOccurrence)],
                [new ValueCacheGroupConfig(proto.NumberGroupId, ValueCaptureMode.AllOccurrences)]);
            cache.RecordPacket(packet);

            ValueCacheSeries<ulong> series = cache.GetSeries<ulong>(proto.NumberId);
            await Assert.That(series.CaptureMode).IsEqualTo(ValueCaptureMode.FirstOccurrence);
            await Assert.That(series.Count).IsEqualTo(1);
            await Assert.That(series[0].Value).IsEqualTo(1UL);
        }
    }

    [Test]
    public async Task Ctor_RecordAllFields_SeriesEmpty_NoCustomText()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _) = _BuildExerciseStack();
        using (stack)
        {
            ValueCache cache = new(
                stack,
                [],
                options: new ValueCacheBuildOptions { RecordAllFields = true });

            await Assert.That(cache.Series.Count).IsEqualTo(0);
            await Assert.That(cache.TryGetSeries<ulong>(proto.NumberId, out _)).IsFalse();
            await Assert.That(cache.TryGetSeries<byte>(stack.RootFieldId, out _)).IsFalse();
            await Assert.That(cache.TryGetCustomTextSeries(proto.NumberId, out _)).IsFalse();
            await Assert.That(() => cache.GetSeries<ulong>(proto.NumberId)).Throws<ArgumentException>();
            await Assert.That(() => cache.Series[0]).Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task Ctor_RecordAllFields_ExplicitPayload_PreCreatesOnlyThatSeries()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId _) = _BuildExerciseStack();
        using (stack)
        {
            ValueCache cache = new(
                stack,
                [new ValueCacheFieldConfig(proto.NumberId)],
                options: new ValueCacheBuildOptions { RecordAllFields = true });

            await Assert.That(cache.Series.Count).IsEqualTo(1);
            await Assert.That(cache.TryGetSeries<ulong>(proto.NumberId, out _)).IsTrue();
            await Assert.That(cache.TryGetSeries<ulong>(proto.LazyTtlId, out _)).IsFalse();
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
            ValueCacheSeries<string> text = cache.GetCustomTextSeries(proto.NumberId);
            ValueCacheSeries<string> rep = cache.GetCustomRepresentationSeries(proto.NumberId);
            await Assert.That(payload.Count).IsEqualTo(1);
            await Assert.That(text.Count).IsEqualTo(1);
            await Assert.That(text[0].Value).IsEqualTo("custom-text");
            await Assert.That(rep.Count).IsEqualTo(1);
            await Assert.That(rep[0].Value).IsEqualTo("custom-rep");
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

            ValueCacheSeries<IPv6Address> series = cache.GetSeries<IPv6Address>(proto.Ipv6Id);
            await Assert.That(series.Count).IsEqualTo(1);
            await Assert.That(series[0].Value.High).IsEqualTo(0x20010DB800000000UL);
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

            ValueCacheSeries<Uuid> series = cache.GetSeries<Uuid>(proto.UuidId);
            await Assert.That(series.Count).IsEqualTo(1);
            await Assert.That(series[0].Value.Low).IsEqualTo(2UL);
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

            ValueCacheSeries<byte[]> series = cache.GetSeries<byte[]>(proto.BytesId);
            byte[] copy = series[0].Value;
            byte first = copy.Length > 0 ? copy[0] : (byte)0;
            await Assert.That(series.Count).IsEqualTo(1);
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

            ValueCacheSeries<string> strings = cache.GetSeries<string>(proto.StringId);
            await Assert.That(strings.Count).IsEqualTo(1);
            await Assert.That(strings[0].Value).IsEqualTo("lazy-string");
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
    public async Task RecordPacket_RecordAllFields_AppearingFieldsOnly_NoContainerPresence()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true });
            IReadOnlyList<ValueCacheSeries> series = cache.Series;
            cache.RecordPacket(packet);

            await Assert.That(ReferenceEquals(series, cache.Series)).IsTrue();
            await Assert.That(series.Count).IsGreaterThan(0);
            await Assert.That(series.Count).IsLessThan(stack.FieldCount);
            await Assert.That(cache.TryGetSeries<ulong>(proto.NumberId, out _)).IsTrue();
            await Assert.That(cache.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(1);
            await Assert.That(cache.TryGetSeries<byte>(proto.NoneId, out _)).IsFalse();
            await Assert.That(cache.TryGetSeries<byte>(stack.RootFieldId, out _)).IsFalse();
            await Assert.That(cache.TryGetSeries<ulong>(proto.LazyTtlId, out _)).IsTrue();
            int enumerated = 0;
            foreach (ValueCacheSeries column in series)
            {
                enumerated++;
                _ = column.FieldId;
            }

            await Assert.That(enumerated).IsEqualTo(series.Count);
        }
    }

    [Test]
    public async Task RecordPacket_RecordAllFields_RecordContainerPresence_CreatesNoneSeries()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(
                stack,
                [],
                options: new ValueCacheBuildOptions { RecordAllFields = true, RecordContainerPresence = true });
            cache.RecordPacket(packet);

            await Assert.That(cache.TryGetSeries<byte>(proto.NoneId, out _)).IsTrue();
            await Assert.That(cache.GetSeries<byte>(proto.NoneId).Count).IsEqualTo(1);
            await Assert.That(cache.TryGetSeries<byte>(stack.RootFieldId, out _)).IsTrue();
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
            FieldId? tcpPortId = stack.GetFieldId("tcp.srcport");
            await Assert.That(portId).IsNotNull();
            await Assert.That(cache.GetSeries<ulong>(portId!.Value).Count).IsEqualTo(1);
            await Assert.That(cache.TryGetSeries<byte>(stack.RootFieldId, out _)).IsFalse();
            await Assert.That(cache.Series.Count).IsLessThan(stack.FieldCount);
            if (tcpPortId is { } tcp)
            {
                await Assert.That(_ContainsField(cache, tcp)).IsFalse();
            }
        }
    }

    #endregion

    #region Flags / misuse

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
    public async Task Abandon_RecordPacket_Throws_ReadsRemain()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);
            cache.Abandon();
            ReadOnlyValueCache view = cache.AsReadOnlyView();
            Packet later = _ParseId(stack, protoId, proto, 1, 2);

            await Assert.That(cache.IsAbandoned).IsTrue();
            await Assert.That(view.IsAbandoned).IsTrue();
            await Assert.That(() => cache.RecordPacket(later)).Throws<InvalidOperationException>();
            await Assert.That(cache.GetSeries<ulong>(proto.NumberId).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ReadOnlyValueCache_ForwardsSeries()
    {
        (Stack? stack, ValueCacheExerciseProtocol proto, ProtocolId protoId) = _BuildExerciseStack();
        using (stack)
        {
            Packet packet = _Parse(stack, protoId, proto);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(proto.NumberId)]);
            cache.RecordPacket(packet);
            ReadOnlyValueCache view = cache.AsReadOnlyView();
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
        NestedLazyOnMaterialize = false;
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

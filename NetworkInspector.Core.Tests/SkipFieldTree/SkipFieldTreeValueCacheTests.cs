// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Skip-tree ValueCache record, display-text probe, <see cref="ValueCache.RecordPacket"/> reject, and unused lazy drop.
/// </summary>
internal sealed class SkipFieldTreeValueCacheTests
{
    #region Helpers

    private static Stack _BuildStandardStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
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

    private static Frame _Frame(Stack stack, byte[] data, int id = 1) =>
        Frame.Create(
            new FrameId(id),
            Timestamp.FromSecs(id),
            data,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    #endregion

    [Test]
    public async Task ParseFrame_Skip_IpTtlAndUdpSrcPort_MatchBuild()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Frame(stack, FrameBuilders.GenerateStaticUdpFrame());
        FieldId ttlId = stack.GetFieldId("ip.ttl")!.Value;
        FieldId portId = stack.GetFieldId("udp.srcport")!.Value;
        ValueCacheFieldConfig[] configs = [new(ttlId), new(portId)];

        ValueCache build = new(stack, configs);
        _ = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Build, build);

        ValueCache skip = new(stack, configs);
        Packet skipPacket = Packet.ParseFrame(new PacketId(1), stack, frame, FieldTreeMode.Skip, skip);

        await Assert.That(skipPacket.FieldCount(materialize: false)).IsEqualTo(1);
        await Assert.That(skip.GetSeries<ulong>(ttlId).Count).IsEqualTo(build.GetSeries<ulong>(ttlId).Count);
        await Assert.That(skip.GetSeries<ulong>(ttlId)[0].Value).IsEqualTo(build.GetSeries<ulong>(ttlId)[0].Value);
        await Assert.That(skip.GetSeries<ulong>(portId).Count).IsEqualTo(build.GetSeries<ulong>(portId).Count);
        await Assert.That(skip.GetSeries<ulong>(portId)[0].Value).IsEqualTo(build.GetSeries<ulong>(portId)[0].Value);
    }

    [Test]
    public async Task ParseFrame_Skip_OnlyUdpSrcPort_DoesNotRequireDnsLazy()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Frame(stack, FrameBuilders.GenerateDnsQueryFrame());
        FieldId portId = stack.GetFieldId("udp.srcport")!.Value;
        FieldId? dnsNameId = stack.GetFieldId("dns.qry.name");

        ValueCache skip = new(stack, [new ValueCacheFieldConfig(portId)]);
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Skip, skip);

        await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(1);
        await Assert.That(skip.GetSeries<ulong>(portId).Count).IsEqualTo(1);
        if (dnsNameId is { } dnsId)
        {
            await Assert.That(skip.TryGetSeries<ulong>(dnsId, out _)).IsFalse();
            await Assert.That(skip.TryGetCustomTextSeries(dnsId, out _)).IsFalse();
        }
    }

    [Test]
    public async Task ParseFrame_Skip_RecordAllFields_MatchesBuildFieldIds()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Frame(stack, FrameBuilders.GenerateStaticUdpIpv6Frame());
        FieldId portId = stack.GetFieldId("udp.srcport")!.Value;
        FieldId ipv6SrcId = stack.GetFieldId("ipv6.src")!.Value;
        FieldId ethSrcId = stack.GetFieldId("eth.src")!.Value;

        ValueCache build = new(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true });
        _ = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Build, build);

        ValueCache skip = new(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true });
        _ = Packet.ParseFrame(new PacketId(1), stack, frame, FieldTreeMode.Skip, skip);

        await Assert.That(skip.GetSeries<ulong>(portId).Count).IsEqualTo(build.GetSeries<ulong>(portId).Count);
        await Assert.That(skip.GetSeries<IPv6Address>(ipv6SrcId).Count).IsEqualTo(build.GetSeries<IPv6Address>(ipv6SrcId).Count);
        await Assert.That(skip.GetSeries<ulong>(ethSrcId).Count).IsEqualTo(build.GetSeries<ulong>(ethSrcId).Count);
        await Assert.That(skip.GetSeries<ulong>(portId)[0].Value).IsEqualTo(build.GetSeries<ulong>(portId)[0].Value);
        await Assert.That(skip.Series.Count).IsEqualTo(build.Series.Count);
        await Assert.That(skip.Series.Count).IsLessThan(stack.FieldCount);
        await Assert.That(skip.Series.Count).IsGreaterThan(0);
        FieldId? tcpPortId = stack.GetFieldId("tcp.srcport");
        if (tcpPortId is { } tcp)
        {
            await Assert.That(_ContainsField(skip, tcp)).IsFalse();
            await Assert.That(_ContainsField(build, tcp)).IsFalse();
        }
    }

    [Test]
    public async Task Ctor_RecordAllFields_SeriesEmpty_IndexerThrows()
    {
        using Stack stack = _BuildStandardStack();
        ValueCache cache = new(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true });
        await Assert.That(cache.Series.Count).IsEqualTo(0);
        await Assert.That(() => cache.Series[0]).Throws<ArgumentOutOfRangeException>();
        int enumerated = 0;
        foreach (ValueCacheSeries _ in cache.Series)
        {
            enumerated++;
        }

        await Assert.That(enumerated).IsEqualTo(0);
        using IEnumerator<ValueCacheSeries> enumerator = cache.Series.GetEnumerator();
        await Assert.That(enumerator.MoveNext()).IsFalse();
        IEnumerator boxed = ((IEnumerable)cache.Series).GetEnumerator();
        try
        {
            await Assert.That(boxed.MoveNext()).IsFalse();
        }
        finally
        {
            (boxed as IDisposable)?.Dispose();
        }
    }

    [Test]
    public async Task RecordPacket_SkipPacket_ThrowsArgumentException()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Frame(stack, FrameBuilders.GenerateStaticUdpIpv6Frame());
        Packet skip = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Skip);
        FieldId portId = stack.GetFieldId("udp.srcport")!.Value;
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(portId)]);

        await Assert.That(() => cache.RecordPacket(skip)).Throws<ArgumentException>();
    }

    [Test]
    public async Task ParseFrame_Skip_NullCache_DoesNotInvokeLazyPopulator()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        SkipLazyProbeProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        Frame frame = _Frame(stack, new byte[8]);

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, protoId, FieldTreeMode.Skip);

        await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(1);
        await Assert.That(proto.PopulatorInvocations).IsEqualTo(0);
    }

    [Test]
    public async Task WantsDisplayText_PayloadOnly_False_CustomText_True_RecordAll_False()
    {
        using Stack stack = _BuildStandardStack();
        FieldId portId = stack.GetFieldId("udp.srcport")!.Value;

        ValueCache payload = new(stack, [new ValueCacheFieldConfig(portId)]);
        ValueCache withText = new(stack, [new ValueCacheFieldConfig(portId, RecordCustomText: true)]);
        ValueCache all = new(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true });

        await Assert.That(payload.WantsDisplayText(portId)).IsFalse();
        await Assert.That(withText.WantsDisplayText(portId)).IsTrue();
        await Assert.That(all.WantsDisplayText(portId)).IsFalse();
    }
}

/// <summary>Lazy container whose populator must not run on skip parse without a value cache.</summary>
internal sealed class SkipLazyProbeProtocol : IProtocol
{
    public FieldId ContainerId;
    public int PopulatorInvocations;

    public string Name => "skiplazy";
    public string UiName => "Skip Lazy Probe";

    public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
    {
        ContainerId = builder.RegisterFieldInGroup(protocolId, "skiplazy.box", "Box", FieldType.None, "skiplazy");
    }

    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        parentField.AppendLazy(ContainerId, FieldValue.None, (in MutField container) =>
        {
            Interlocked.Increment(ref PopulatorInvocations);
            _ = container;
            return 0;
        });
        return data.Length;
    }
}

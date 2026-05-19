// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Core.Ids;
using NetworkInspector.Core.Index;

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for RoaringBitmap: add, contains, cardinality, set operations, min/max.
/// </summary>
internal sealed class IndexTests
{
    // === Basic operations ===

    [Test]
    public async Task RoaringBitmap_EmptyBitmap()
    {
        RoaringBitmap bm = new();
        await Assert.That(bm.IsEmpty).IsTrue();
        await Assert.That(bm.Cardinality).IsEqualTo(0L);
    }

    [Test]
    public async Task RoaringBitmap_EmptyBitmap_MinMaxThrows()
    {
        RoaringBitmap bm = new();
        await Assert.That(() => _ = bm.Min).Throws<InvalidOperationException>();
        await Assert.That(() => _ = bm.Max).Throws<InvalidOperationException>();
        await Assert.That(bm.TryGetMin(out uint min)).IsFalse();
        await Assert.That(min).IsEqualTo(0u);
        await Assert.That(bm.TryGetMax(out uint max)).IsFalse();
        await Assert.That(max).IsEqualTo(0u);
    }

    [Test]
    public async Task RoaringBitmap_AddAndContains()
    {
        RoaringBitmap bm = new();
        bm.Add(42);

        await Assert.That(bm.Contains(42)).IsTrue();
        await Assert.That(bm.Contains(43)).IsFalse();
    }

    [Test]
    public async Task RoaringBitmap_AddMultiple()
    {
        RoaringBitmap bm = new();
        bm.Add(1);
        bm.Add(100);
        bm.Add(10000);

        await Assert.That(bm.Contains(1)).IsTrue();
        await Assert.That(bm.Contains(100)).IsTrue();
        await Assert.That(bm.Contains(10000)).IsTrue();
        await Assert.That(bm.Contains(50)).IsFalse();
    }

    [Test]
    public async Task RoaringBitmap_Cardinality()
    {
        RoaringBitmap bm = new();
        bm.Add(1);
        bm.Add(2);
        bm.Add(3);
        bm.Add(100);
        bm.Add(200);

        await Assert.That(bm.Cardinality).IsEqualTo(5L);
    }

    [Test]
    public async Task RoaringBitmap_DuplicateAdd_DoesNotIncreaseCardinality()
    {
        RoaringBitmap bm = new();
        bm.Add(42);
        bm.Add(42);
        bm.Add(42);

        await Assert.That(bm.Cardinality).IsEqualTo(1L);
    }

    // === Min / Max ===

    [Test]
    public async Task RoaringBitmap_Min()
    {
        RoaringBitmap bm = new();
        bm.Add(100);
        bm.Add(50);
        bm.Add(200);

        await Assert.That(bm.Min).IsEqualTo(50u);
    }

    [Test]
    public async Task RoaringBitmap_Max()
    {
        RoaringBitmap bm = new();
        bm.Add(100);
        bm.Add(50);
        bm.Add(200);

        await Assert.That(bm.Max).IsEqualTo(200u);
    }

    [Test]
    public async Task RoaringBitmap_MinMax_SingleElement()
    {
        RoaringBitmap bm = new();
        bm.Add(77);

        await Assert.That(bm.Min).IsEqualTo(77u);
        await Assert.That(bm.Max).IsEqualTo(77u);
    }

    // === Set operations: AND (intersection) ===

    [Test]
    public async Task RoaringBitmap_And_Intersection()
    {
        RoaringBitmap a = new();
        a.Add(1);
        a.Add(2);
        a.Add(3);

        RoaringBitmap b = new();
        b.Add(2);
        b.Add(3);
        b.Add(4);

        RoaringBitmap result = a.And(b);
        await Assert.That(result.Cardinality).IsEqualTo(2L);
        await Assert.That(result.Contains(2)).IsTrue();
        await Assert.That(result.Contains(3)).IsTrue();
        await Assert.That(result.Contains(1)).IsFalse();
        await Assert.That(result.Contains(4)).IsFalse();
    }

    [Test]
    public async Task RoaringBitmap_And_Disjoint()
    {
        RoaringBitmap a = new();
        a.Add(1);
        a.Add(2);

        RoaringBitmap b = new();
        b.Add(3);
        b.Add(4);

        RoaringBitmap result = a.And(b);
        await Assert.That(result.Cardinality).IsEqualTo(0L);
        await Assert.That(result.IsEmpty).IsTrue();
    }

    [Test]
    public async Task RoaringBitmap_And_WithEmpty()
    {
        RoaringBitmap a = new();
        a.Add(1);
        a.Add(2);
        a.Add(3);

        RoaringBitmap empty = new();

        RoaringBitmap result = a.And(empty);
        await Assert.That(result.Cardinality).IsEqualTo(0L);
    }

    // === Set operations: OR (union) ===

    [Test]
    public async Task RoaringBitmap_Or_Union()
    {
        RoaringBitmap a = new();
        a.Add(1);
        a.Add(2);
        a.Add(3);

        RoaringBitmap b = new();
        b.Add(3);
        b.Add(4);
        b.Add(5);

        RoaringBitmap result = a.Or(b);
        await Assert.That(result.Cardinality).IsEqualTo(5L);
        await Assert.That(result.Contains(1)).IsTrue();
        await Assert.That(result.Contains(2)).IsTrue();
        await Assert.That(result.Contains(3)).IsTrue();
        await Assert.That(result.Contains(4)).IsTrue();
        await Assert.That(result.Contains(5)).IsTrue();
    }

    [Test]
    public async Task RoaringBitmap_Or_WithEmpty()
    {
        RoaringBitmap a = new();
        a.Add(1);
        a.Add(2);

        RoaringBitmap empty = new();

        RoaringBitmap result = a.Or(empty);
        await Assert.That(result.Cardinality).IsEqualTo(2L);
        await Assert.That(result.Contains(1)).IsTrue();
        await Assert.That(result.Contains(2)).IsTrue();
    }

    [Test]
    public async Task RoaringBitmap_Or_Disjoint()
    {
        RoaringBitmap a = new();
        a.Add(1);

        RoaringBitmap b = new();
        b.Add(100);

        RoaringBitmap result = a.Or(b);
        await Assert.That(result.Cardinality).IsEqualTo(2L);
    }

    // === Large ranges ===

    [Test]
    public async Task RoaringBitmap_LargeSequentialRange()
    {
        RoaringBitmap bm = new();
        for (uint i = 0; i < 10000; i++)
        {
            bm.Add(i);
        }

        await Assert.That(bm.Cardinality).IsEqualTo(10000L);
        await Assert.That(bm.Min).IsEqualTo(0u);
        await Assert.That(bm.Max).IsEqualTo(9999u);
        await Assert.That(bm.Contains(0)).IsTrue();
        await Assert.That(bm.Contains(5000)).IsTrue();
        await Assert.That(bm.Contains(9999)).IsTrue();
        await Assert.That(bm.Contains(10000)).IsFalse();
    }

    [Test]
    public async Task RoaringBitmap_CrossChunkValues()
    {
        // Values that span different 16-bit high chunks
        RoaringBitmap bm = new();
        bm.Add(0);             // chunk 0
        bm.Add(65535);         // chunk 0, max low
        bm.Add(65536);         // chunk 1, min low
        bm.Add(131072);        // chunk 2

        await Assert.That(bm.Cardinality).IsEqualTo(4L);
        await Assert.That(bm.Contains(0)).IsTrue();
        await Assert.That(bm.Contains(65535)).IsTrue();
        await Assert.That(bm.Contains(65536)).IsTrue();
        await Assert.That(bm.Contains(131072)).IsTrue();
    }

    [Test]
    public async Task RoaringBitmap_And_CrossChunk()
    {
        RoaringBitmap a = new();
        a.Add(0);
        a.Add(65536);
        a.Add(131072);

        RoaringBitmap b = new();
        b.Add(65536);
        b.Add(131072);
        b.Add(196608);

        RoaringBitmap result = a.And(b);
        await Assert.That(result.Cardinality).IsEqualTo(2L);
        await Assert.That(result.Contains(65536)).IsTrue();
        await Assert.That(result.Contains(131072)).IsTrue();
    }

    [Test]
    public async Task RoaringBitmap_Or_CrossChunk()
    {
        RoaringBitmap a = new();
        a.Add(0);
        a.Add(65536);

        RoaringBitmap b = new();
        b.Add(131072);
        b.Add(196608);

        RoaringBitmap result = a.Or(b);
        await Assert.That(result.Cardinality).IsEqualTo(4L);
        await Assert.That(result.Contains(0)).IsTrue();
        await Assert.That(result.Contains(65536)).IsTrue();
        await Assert.That(result.Contains(131072)).IsTrue();
        await Assert.That(result.Contains(196608)).IsTrue();
    }

    [Test]
    public async Task RoaringBitmap_MaxUint32Value()
    {
        RoaringBitmap bm = new();
        bm.Add(uint.MaxValue);

        await Assert.That(bm.Cardinality).IsEqualTo(1L);
        await Assert.That(bm.Contains(uint.MaxValue)).IsTrue();
        await Assert.That(bm.Max).IsEqualTo(uint.MaxValue);
    }

    [Test]
    public async Task RoaringBitmap_ZeroValue()
    {
        RoaringBitmap bm = new();
        bm.Add(0);

        await Assert.That(bm.Cardinality).IsEqualTo(1L);
        await Assert.That(bm.Contains(0)).IsTrue();
        await Assert.That(bm.Min).IsEqualTo(0u);
    }

    [Test]
    public async Task RoaringBitmap_SparseValues()
    {
        RoaringBitmap bm = new();
        // Widely spaced values in different chunks
        bm.Add(0);
        bm.Add(1_000_000);
        bm.Add(2_000_000);
        bm.Add(3_000_000);

        await Assert.That(bm.Cardinality).IsEqualTo(4L);
        await Assert.That(bm.Min).IsEqualTo(0u);
        await Assert.That(bm.Max).IsEqualTo(3_000_000u);
    }
}

/// <summary>
/// Tests for PacketIndex: protocol-driven presence recording during parsing,
/// dedup, PresenceQuery, and integration with real protocols.
/// </summary>
internal sealed class PacketIndexTests
{
    private static (Stack Stack, Frame Frame) BuildStackAndFrame()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();

        byte[] data = FrameBuilders.GenerateStaticUdpFrame(512);
        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            data,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        return (stack, frame);
    }

    [Test]
    public async Task ParseFrameIndexed_RecordsProtocolPresence()
    {
        (Stack stack, Frame frame) = BuildStackAndFrame();
        PacketIndex index = new(stack);

        _ = Packet.ParseFrameIndexed(
            new PacketId(0), stack, frame, index);

        // eth, ip, udp protocols should be recorded
        ProtocolId? ethId = stack.GetProtocolId("eth");
        ProtocolId? ipId = stack.GetProtocolId("ip");
        ProtocolId? udpId = stack.GetProtocolId("udp");

        await Assert.That(ethId).IsNotNull();
        await Assert.That(ipId).IsNotNull();
        await Assert.That(udpId).IsNotNull();

        await Assert.That(index.GetProtocolBitmap(ethId!.Value).Contains(0)).IsTrue();
        await Assert.That(index.GetProtocolBitmap(ipId!.Value).Contains(0)).IsTrue();
        await Assert.That(index.GetProtocolBitmap(udpId!.Value).Contains(0)).IsTrue();
    }

    [Test]
    public async Task ParseFrameIndexed_RecordsGroupPresence()
    {
        (Stack stack, Frame frame) = BuildStackAndFrame();
        PacketIndex index = new(stack);

        Packet.ParseFrameIndexed(
            new PacketId(0), stack, frame, index);

        // Verify group presence via field lookup — udp.srcport is in "udp" group
        FieldId? srcPortId = stack.GetFieldId("udp.srcport");
        await Assert.That(srcPortId).IsNotNull();

        ReadOnlyRoaringBitmap srcPortBitmap = index.GetFieldBitmap(srcPortId!.Value);
        await Assert.That(srcPortBitmap.Contains(0)).IsTrue();
    }

    [Test]
    public async Task ParseFrameIndexed_OptionalGroupPresence()
    {
        (Stack stack, Frame frame) = BuildStackAndFrame();
        PacketIndex index = new(stack);

        // Frame has payload → udp.payload group should be recorded
        Packet.ParseFrameIndexed(
            new PacketId(0), stack, frame, index);

        FieldId? payloadId = stack.GetFieldId("udp.payload");
        await Assert.That(payloadId).IsNotNull();

        ReadOnlyRoaringBitmap payloadBitmap = index.GetFieldBitmap(payloadId!.Value);
        await Assert.That(payloadBitmap.Contains(0)).IsTrue();
    }

    [Test]
    public async Task ParseFrameIndexed_MultiplePackets()
    {
        (Stack stack, Frame frame) = BuildStackAndFrame();
        PacketIndex index = new(stack);

        // Parse 100 packets
        for (int i = 0; i < 100; i++)
        {
            Packet.ParseFrameIndexed(
                new PacketId(i), stack, frame, index);
        }

        ProtocolId? udpId = stack.GetProtocolId("udp");
        await Assert.That(udpId).IsNotNull();
        await Assert.That(index.ProtocolCardinality(udpId!.Value)).IsEqualTo(100L);
    }

    [Test]
    public async Task ParseFrameIndexed_DedupWithinPacket()
    {
        (Stack stack, Frame frame) = BuildStackAndFrame();
        PacketIndex index = new(stack);

        // Parse same packet — each protocol is recorded once per packet
        Packet.ParseFrameIndexed(
            new PacketId(0), stack, frame, index);

        ProtocolId? ethId = stack.GetProtocolId("eth");
        await Assert.That(ethId).IsNotNull();
        await Assert.That(index.ProtocolCardinality(ethId!.Value)).IsEqualTo(1L);
    }

    [Test]
    public async Task PresenceQuery_SelectProtocolAndGroup()
    {
        (Stack stack, Frame frame) = BuildStackAndFrame();
        PacketIndex index = new(stack);

        for (int i = 0; i < 10; i++)
        {
            Packet.ParseFrameIndexed(
                new PacketId(i), stack, frame, index);
        }

        ProtocolId? udpId = stack.GetProtocolId("udp");
        await Assert.That(udpId).IsNotNull();

        long count = index.Query()
            .SelectProtocol(udpId!.Value)
            .Count();

        await Assert.That(count).IsEqualTo(10L);
    }

    [Test]
    public async Task PresenceQuery_AndProtocol()
    {
        (Stack stack, Frame frame) = BuildStackAndFrame();
        PacketIndex index = new(stack);

        for (int i = 0; i < 10; i++)
        {
            Packet.ParseFrameIndexed(
                new PacketId(i), stack, frame, index);
        }

        ProtocolId? ethId = stack.GetProtocolId("eth");
        ProtocolId? udpId = stack.GetProtocolId("udp");

        // AND: packets with both eth AND udp — should be all 10
        long count = index.Query()
            .SelectProtocol(ethId!.Value)
            .AndProtocol(udpId!.Value)
            .Count();

        await Assert.That(count).IsEqualTo(10L);
    }

    [Test]
    public async Task ParseFrameIndexed_NonIndexedParse_Works()
    {
        // Ensure normal (non-indexed) parsing still works fine
        (Stack stack, Frame frame) = BuildStackAndFrame();

        Packet packet = Packet.ParseFrame(
            new PacketId(0), stack, frame);

        // Packet should parse correctly, no index side effects
        await Assert.That(packet.FieldCount()).IsGreaterThan(0);
    }

    [Test]
    public async Task PresenceQuery_OrProtocol()
    {
        (Stack stack, _) = BuildStackAndFrame();
        PacketIndex index = new(stack);

        // Parse IPv4 frame for packets 0-4
        byte[] ipv4Data = FrameBuilders.GenerateStaticUdpFrame(512);
        Frame ipv4Frame = Frame.Create(new FrameId(0), Timestamp.FromSecs(0), ipv4Data,
            LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;

        for (int i = 0; i < 5; i++)
        {
            Packet.ParseFrameIndexed(
                new PacketId(i), stack, ipv4Frame, index);
        }

        // Parse IPv6 frame for packets 5-9
        byte[] ipv6Data = FrameBuilders.GenerateStaticUdpIpv6Frame(512);
        Frame ipv6Frame = Frame.Create(new FrameId(0), Timestamp.FromSecs(0), ipv6Data,
            LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;

        for (int i = 5; i < 10; i++)
        {
            Packet.ParseFrameIndexed(
                new PacketId(i), stack, ipv6Frame, index);
        }

        ProtocolId? ipId = stack.GetProtocolId("ip");
        ProtocolId? ipv6Id = stack.GetProtocolId("ipv6");

        // OR: packets with ip OR ipv6 — should be all 10
        long count = index.Query()
            .SelectProtocol(ipId!.Value)
            .OrProtocol(ipv6Id!.Value)
            .Count();

        await Assert.That(count).IsEqualTo(10L);

        // AND: packets with ip AND ipv6 — should be 0 (mutually exclusive)
        long andCount = index.Query()
            .SelectProtocol(ipId!.Value)
            .AndProtocol(ipv6Id!.Value)
            .Count();

        await Assert.That(andCount).IsEqualTo(0L);
    }
}
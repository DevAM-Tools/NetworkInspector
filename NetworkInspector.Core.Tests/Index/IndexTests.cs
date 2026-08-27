// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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

    // === Container-level set operations (exit-point coverage) ===

    private static ArrayContainer _Array(params ushort[] values)
    {
        ushort[] sorted = values.Order().ToArray();
        return new ArrayContainer(sorted, sorted.Length);
    }

    [Test]
    public async Task ArrayContainer_SimdLinearContains_HitsEarlyExitPaths()
    {
        ArrayContainer small = _Array(2, 4, 6, 8, 10, 12, 14, 16, 18, 20);
        await Assert.That(small.Contains((ushort)6)).IsTrue();
        await Assert.That(small.Contains((ushort)99)).IsFalse();
    }

    [Test]
    public async Task ArrayContainer_SetOps_ArrayAndBitmapFallback()
    {
        ArrayContainer a = _Array(1, 3, 5, 7);
        BitmapContainer b = new();
        b.Add(3);
        b.Add(9);

        IContainer andResult = a.And(b);
        IContainer orResult = a.Or(b);
        IContainer andNotResult = a.AndNot(b);
        IContainer xorResult = a.Xor(b);

        await Assert.That(andResult.Cardinality).IsGreaterThanOrEqualTo(1);
        await Assert.That(orResult.Cardinality).IsGreaterThan(a.Cardinality);
    }

    [Test]
    public async Task ArrayContainer_Or_PromotesToBitmapWhenTooLarge()
    {
        ushort[] values = new ushort[4090];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (ushort)(i * 2);
        }
        ArrayContainer left = new(values, values.Length);
        // Values must not already be present in left (even indices 0..8178) so merge grows cardinality.
        ArrayContainer right = _Array(8180, 8182, 8184, 8186, 8188, 8190, 8192, 8194);
        IContainer merged = left.Or(right);
        await Assert.That(merged.Cardinality).IsGreaterThan(left.Cardinality);
        await Assert.That(merged.Cardinality).IsGreaterThan(ArrayContainer.MaxCapacity);
    }

    [Test]
    public async Task ArrayContainer_Xor_PromotesToBitmapWhenTooLarge()
    {
        ushort[] values = new ushort[4090];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (ushort)i;
        }
        ArrayContainer left = new(values, values.Length);
        ArrayContainer right = _Array(5000, 5001, 5002, 5003, 5004, 5005, 5006, 5007, 5008);
        IContainer xor = left.Xor(right);
        await Assert.That(xor).IsTypeOf<BitmapContainer>();
    }

    [Test]
    public async Task RoaringBitmap_TryGetMinMax_ReturnPaths()
    {
        RoaringBitmap empty = new();
        await Assert.That(empty.TryGetMin(out uint min)).IsFalse();
        await Assert.That(empty.TryGetMax(out uint max)).IsFalse();
        await Assert.That(min).IsEqualTo(0u);
        await Assert.That(max).IsEqualTo(0u);

        RoaringBitmap bm = new();
        bm.Add(42);
        await Assert.That(bm.TryGetMin(out min)).IsTrue();
        await Assert.That(bm.TryGetMax(out max)).IsTrue();
        await Assert.That(min).IsEqualTo(42u);
        await Assert.That(max).IsEqualTo(42u);
    }

    [Test]
    public async Task RoaringBitmap_Select_ReturnsRankedValue()
    {
        RoaringBitmap bm = new();
        bm.Add(10);
        bm.Add(20);
        bm.Add(30);
        await Assert.That(bm.Select(0)).IsEqualTo(10u);
        await Assert.That(bm.Select(2)).IsEqualTo(30u);
        await Assert.That(bm.Select(-1)).IsNull();
        await Assert.That(bm.Select(99)).IsNull();
    }

    [Test]
    public async Task RoaringBitmap_Select_CrossChunkContainerRank()
    {
        RoaringBitmap bm = new();
        for (uint i = 0; i < 5000; i++)
        {
            bm.Add(i);
        }
        bm.Add(70000);
        await Assert.That(bm.Select(0)).IsEqualTo(0u);
        await Assert.That(bm.Select(4999)).IsEqualTo(4999u);
        await Assert.That(bm.Select(5000)).IsEqualTo(70000u);
    }

    [Test]
    public async Task RoaringBitmap_Rank_PartialWordInBitmapContainer()
    {
        RoaringBitmap bm = new();
        for (uint i = 0; i < 5000; i++)
        {
            bm.Add(i);
        }

        await Assert.That(bm.Rank(90)).IsEqualTo(91L);
        await Assert.That(bm.Rank(63)).IsEqualTo(64L);
    }

    [Test]
    public async Task RoaringBitmap_Rank_FallbackForRunContainer()
    {
        RunContainer run = new();
        for (ushort v = 500; v <= 510; v++)
        {
            run.Add(v);
        }

        RoaringBitmap bm = _RoaringWithRunChunk(0, run);
        await Assert.That(bm.Rank(505)).IsEqualTo(6L);
    }

    [Test]
    public async Task RoaringBitmap_Select_FallbackForRunContainer()
    {
        RunContainer run = new();
        for (ushort v = 500; v <= 510; v++)
        {
            run.Add(v);
        }

        RoaringBitmap bm = _RoaringWithRunChunk(0, run);
        await Assert.That(bm.Select(3)).IsEqualTo(503u);
        await Assert.That(bm.Select(10)).IsEqualTo(510u);
    }

    [Test]
    public async Task RoaringBitmap_ContainerSelect_ReturnsZeroWhenPositionMissing()
    {
        RunContainer run = new();
        run.Add(100);
        run.Add(101);

        MethodInfo? select = typeof(RoaringBitmap).GetMethod(
            "_ContainerSelect",
            BindingFlags.NonPublic | BindingFlags.Static);
        ushort selected = (ushort)select!.Invoke(null, [run, 999])!;
        await Assert.That(selected).IsEqualTo((ushort)0);
    }

    private static RoaringBitmap _RoaringWithRunChunk(ushort highKey, RunContainer run)
    {
        RoaringBitmap bm = new();
        MethodInfo? insert = typeof(RoaringBitmap).GetMethod(
            "_InsertChunk",
            BindingFlags.NonPublic | BindingFlags.Instance);
        insert!.Invoke(bm, [0, highKey, run]);
        return bm;
    }

    [Test]
    public async Task RoaringBitmap_OrWith_MergesOtherBitmap()
    {
        RoaringBitmap a = new();
        a.Add(1);
        RoaringBitmap b = new();
        b.Add(2);
        a.OrWith(b);
        await Assert.That(a.Contains(2)).IsTrue();
    }

    [Test]
    public async Task RoaringBitmap_OrWith_SelfNoOpWhenEmptyOther()
    {
        RoaringBitmap a = new();
        a.Add(1);
        a.Add(2);
        RoaringBitmap empty = new();
        a.OrWith(empty);
        await Assert.That(a.Cardinality).IsEqualTo(2L);
    }

    [Test]
    public async Task RunContainer_MinMaxAndRunMetadata()
    {
        RunContainer runs = new();
        runs.Add(10);
        runs.Add(11);
        runs.Add(12);
        runs.Add(50);

        await Assert.That(runs.Min).IsEqualTo((ushort)10);
        await Assert.That(runs.Max).IsEqualTo((ushort)50);
        await Assert.That(runs.RunCount).IsEqualTo(2);
        (ushort start, ushort end) = runs.RunAt(0);
        await Assert.That(start).IsEqualTo((ushort)10);
        await Assert.That(end).IsEqualTo((ushort)12);
    }

    [Test]
    public async Task RunContainer_SetOps_WithArrayOperand()
    {
        RunContainer runs = _RunsForTest((5, 4), (20, 2));
        ArrayContainer array = _Array(6, 7, 99);

        IContainer andResult = runs.And(array);
        IContainer orResult = runs.Or(array);
        IContainer andNotResult = runs.AndNot(array);
        IContainer xorResult = runs.Xor(array);

        await Assert.That(andResult.Contains(6)).IsTrue();
        await Assert.That(orResult.Contains(99)).IsTrue();
        await Assert.That(andNotResult.Contains(5)).IsTrue();
        await Assert.That(xorResult.Contains(99)).IsTrue();
    }

    private static RunContainer _RunsForTest(params (ushort Start, ushort Length)[] runs)
    {
        RunContainer r = new();
        foreach ((ushort start, ushort length) in runs)
        {
            for (int v = start; v <= start + length; v++)
            {
                r.Add((ushort)v);
            }
        }
        return r;
    }
}

/// <summary>
/// Tests for PacketIndex: IProtocol-driven presence recording during parsing,
/// dedup, PresenceQuery, and integration with real protocols.
/// </summary>
internal sealed class PacketIndexTests
{
    private static (Stack Stack, Frame Frame) _BuildStackAndFrame()
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
        (Stack stack, Frame frame) = _BuildStackAndFrame();
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
        (Stack stack, Frame frame) = _BuildStackAndFrame();
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
        (Stack stack, Frame frame) = _BuildStackAndFrame();
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
        (Stack stack, Frame frame) = _BuildStackAndFrame();
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
        (Stack stack, Frame frame) = _BuildStackAndFrame();
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
        (Stack stack, Frame frame) = _BuildStackAndFrame();
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
        (Stack stack, Frame frame) = _BuildStackAndFrame();
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
        (Stack stack, Frame frame) = _BuildStackAndFrame();

        Packet packet = Packet.ParseFrame(
            new PacketId(0), stack, frame);

        // Packet should parse correctly, no index side effects
        await Assert.That(packet.FieldCount(materialize: false)).IsGreaterThan(0); // materialize: false — current materialized count only
    }

    [Test]
    public async Task PresenceQuery_OrProtocol()
    {
        (Stack stack, _) = _BuildStackAndFrame();
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

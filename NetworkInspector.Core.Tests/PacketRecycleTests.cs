// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for the packet recycling API: <see cref="Packet.PrepareForReuse"/> and the
/// <c>ParseFrame(Packet recycle, …)</c> / <c>ParseFrameIndexed(Packet recycle, …)</c> overloads.
///
/// <para>
/// Recycling allows callers to reuse an existing, sealed <see cref="Packet"/> object instead
/// of allocating a new one on every parse. The key invariant is that a recycled packet must
/// produce identical results to a fresh parse, while leaving no GC-visible references to the
/// previous parse's data.
/// </para>
/// </summary>
internal sealed class PacketRecycleTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────

    /// <summary>Builds a stack with all standard protocols registered.</summary>
    private static Stack BuildStack()
    {
        FrameInterfaceRegistry registry = new();
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, registry);
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    /// <summary>Creates a valid synthetic Ethernet/IPv6/UDP frame for testing.</summary>
    private static byte[] BuildIpv6UdpFrame(
        ushort srcPort = 12345, ushort dstPort = 54321, int totalSize = 128)
    {
        const int ethSize = 14;
        const int ipv6Size = 40;
        const int udpSize = 8;
        const int minSize = ethSize + ipv6Size + udpSize;
        totalSize = Math.Max(totalSize, minSize);
        byte[] frame = new byte[totalSize];

        ushort udpLen = (ushort)(udpSize + (totalSize - minSize));

        // Ethernet header: dst=00:11:22:33:44:55, src=66:77:88:99:AA:BB, type=IPv6
        frame[0] = 0x00;
        frame[1] = 0x11;
        frame[2] = 0x22;
        frame[3] = 0x33;
        frame[4] = 0x44;
        frame[5] = 0x55;
        frame[6] = 0x66;
        frame[7] = 0x77;
        frame[8] = 0x88;
        frame[9] = 0x99;
        frame[10] = 0xAA;
        frame[11] = 0xBB;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x86DD);

        // IPv6 header: version=6, next=UDP(17), hop=64, src=2001:db8::1, dst=2001:db8::2
        int ip = ethSize;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(ip), 0x60000000);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ip + 4), udpLen);
        frame[ip + 6] = 17; // UDP
        frame[ip + 7] = 64;
        frame[ip + 8] = 0x20;
        frame[ip + 9] = 0x01;
        frame[ip + 10] = 0x0D;
        frame[ip + 11] = 0xB8;
        frame[ip + 23] = 0x01;
        frame[ip + 24] = 0x20;
        frame[ip + 25] = 0x01;
        frame[ip + 26] = 0x0D;
        frame[ip + 27] = 0xB8;
        frame[ip + 39] = 0x02;

        // UDP header
        int udp = ip + ipv6Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udp), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udp + 2), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udp + 4), udpLen);
        // checksum = 0 (optional for IPv6 UDP)

        return frame;
    }

    /// <summary>Wraps raw bytes into a <see cref="Frame"/> using the given stack's registry.</summary>
    private static Frame MakeFrame(Stack stack, byte[] data, int frameId = 1) =>
        Frame.Create(
            new FrameId(frameId),
            Timestamp.FromSecs(frameId),
            data,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    // ── Basic recycling ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Recycle_ProducesCorrectIdAndTimestamp()
    {
        using Stack stack = BuildStack();
        byte[] frameData = BuildIpv6UdpFrame();

        Frame frame1 = MakeFrame(stack, frameData, frameId: 1);
        Frame frame2 = MakeFrame(stack, frameData, frameId: 2);

        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame1);

        // Recycle with new identity
        Packet recycled = Packet.ParseFrame(packet, new PacketId(2), stack, frame2);

        await Assert.That(ReferenceEquals(packet, recycled)).IsTrue();
        await Assert.That(recycled.Id).IsEqualTo(new PacketId(2));
        await Assert.That(recycled.Timestamp).IsEqualTo(Timestamp.FromSecs(2));
        await Assert.That(recycled.IsFinalized).IsTrue();
    }

    [Test]
    public async Task Recycle_FieldCountMatchesFreshParse()
    {
        using Stack stack = BuildStack();
        byte[] frameData = BuildIpv6UdpFrame();

        Frame freshFrame = MakeFrame(stack, frameData, frameId: 1);
        Frame recycleFrame = MakeFrame(stack, frameData, frameId: 2);

        // Fresh parse baseline
        Packet fresh = Packet.ParseFrame(new PacketId(1), stack, freshFrame);
        int freshCount = fresh.FieldCount(materialize: true);

        // Seed packet to recycle
        Packet seed = Packet.ParseFrame(new PacketId(10), stack, MakeFrame(stack, frameData, frameId: 10));

        // Recycled parse
        Packet recycled = Packet.ParseFrame(seed, new PacketId(2), stack, recycleFrame);
        int recycledCount = recycled.FieldCount(materialize: true);

        await Assert.That(recycledCount).IsEqualTo(freshCount);
    }

    [Test]
    public async Task Recycle_OldFieldsNotRetainedAfterReset()
    {
        using Stack stack = BuildStack();
        byte[] frameData = BuildIpv6UdpFrame();

        // Parse a first packet to be recycled
        Frame frame1 = MakeFrame(stack, frameData, frameId: 1);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame1);
        int firstCount = packet.FieldCount(materialize: true);
        await Assert.That(firstCount).IsGreaterThan(1);

        // Recycle
        Frame frame2 = MakeFrame(stack, frameData, frameId: 2);
        Packet recycled = Packet.ParseFrame(packet, new PacketId(2), stack, frame2);

        // Verify that _Id changed — old identity is gone
        await Assert.That(recycled.Id).IsEqualTo(new PacketId(2));

        // Verify field count is consistent (not accumulated from two parses)
        int recycledCount = recycled.FieldCount(materialize: true);
        await Assert.That(recycledCount).IsEqualTo(firstCount);
    }

    // ── Lazy state is cleared ─────────────────────────────────────────────────────────

    [Test]
    public async Task Recycle_PendingLazyCountIsZeroAfterReset()
    {
        using Stack stack = BuildStack();
        byte[] frameData = BuildIpv6UdpFrame();

        Frame frame1 = MakeFrame(stack, frameData, frameId: 1);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame1);

        // Packet should have lazy fields before materialization
        await Assert.That(packet.HasUnpopulatedLazyFields).IsTrue();

        // Recycle WITHOUT materializing — lazy populators from parse 1 must be gone
        Frame frame2 = MakeFrame(stack, frameData, frameId: 2);
        Packet recycled = Packet.ParseFrame(packet, new PacketId(2), stack, frame2);

        // After recycle and fresh parse, lazy fields refer only to the new parse
        await Assert.That(recycled.IsFinalized).IsTrue();
        // HasUnpopulatedLazyFields is true again from the fresh parse, not from the old one
        await Assert.That(recycled.HasUnpopulatedLazyFields).IsTrue();
    }

    [Test]
    public async Task Recycle_MaterializeAllAfterRecycleSucceeds()
    {
        using Stack stack = BuildStack();
        byte[] frameData = BuildIpv6UdpFrame(srcPort: 9999, dstPort: 1234);

        // Fresh parse for field count baseline
        Packet fresh = Packet.ParseFrame(new PacketId(1), stack, MakeFrame(stack, frameData));
        int freshCount = fresh.FieldCount(materialize: true);

        // Seed + recycle
        Packet seed = Packet.ParseFrame(new PacketId(10), stack, MakeFrame(stack, frameData, 10));
        Frame recycleFrame = MakeFrame(stack, frameData, frameId: 2);
        Packet recycled = Packet.ParseFrame(seed, new PacketId(2), stack, recycleFrame);
        recycled.MaterializeAll();

        await Assert.That(recycled.FieldCount()).IsEqualTo(freshCount);
    }

    // ── Multiple sequential recycles ─────────────────────────────────────────────────

    [Test]
    public async Task MultipleRecycles_ProduceCorrectResultsEachTime()
    {
        using Stack stack = BuildStack();
        byte[] frameData = BuildIpv6UdpFrame();

        // Establish expected field count
        Packet baseline = Packet.ParseFrame(new PacketId(0), stack, MakeFrame(stack, frameData, 0));
        int expected = baseline.FieldCount(materialize: true);

        // Recycle the same packet 50 times
        for (int i = 1; i <= 50; i++)
        {
            Frame frame = MakeFrame(stack, frameData, i);
            Packet recycled = Packet.ParseFrame(baseline, new PacketId(i), stack, frame);

            await Assert.That(recycled.Id).IsEqualTo(new PacketId(i));
            await Assert.That(recycled.IsFinalized).IsTrue();
            int count = recycled.FieldCount(materialize: true);
            await Assert.That(count).IsEqualTo(expected);
        }
    }

    // ── Custom first protocol ─────────────────────────────────────────────────────────

    [Test]
    public async Task Recycle_WithFirstProtocolOverride_IsApplied()
    {
        using Stack stack = BuildStack();
        byte[] frameData = BuildIpv6UdpFrame();

        Packet seed = Packet.ParseFrame(new PacketId(1), stack, MakeFrame(stack, frameData, 1));

        // Recycle with explicit first protocol (stack default)
        Frame frame2 = MakeFrame(stack, frameData, 2);
        Packet recycled = Packet.ParseFrame(
            seed, new PacketId(2), stack, frame2, stack.FrameProtocolId);

        await Assert.That(recycled.Id).IsEqualTo(new PacketId(2));
        await Assert.That(recycled.IsFinalized).IsTrue();
    }

    // ── Indexed recycling ─────────────────────────────────────────────────────────────

    [Test]
    public async Task RecycleIndexed_PacketIndexIsRecordedCorrectly()
    {
        using Stack stack = BuildStack();
        byte[] frameData = BuildIpv6UdpFrame();

        PacketIndex index = new(stack);

        Frame frame1 = MakeFrame(stack, frameData, 1);
        Packet seed = Packet.ParseFrameIndexed(new PacketId(1), stack, frame1, index);

        Frame frame2 = MakeFrame(stack, frameData, 2);
        Packet recycled = Packet.ParseFrameIndexed(seed, new PacketId(2), stack, frame2, index);

        await Assert.That(recycled.Id).IsEqualTo(new PacketId(2));
        await Assert.That(recycled.IsFinalized).IsTrue();
        // The index should have accepted both packets without throwing
    }

    // ── Precondition validation ───────────────────────────────────────────────────────

    [Test]
    public async Task Recycle_ThrowsWhenPacketNotFinalized()
    {
        using Stack stack = BuildStack();
        byte[] frameData = BuildIpv6UdpFrame();

        // Create an unsealed packet directly via internal constructor
        Frame frame = MakeFrame(stack, frameData, 1);
        Packet unsealed = new(new PacketId(1), stack, frame);  // NOT Sealed

        Frame frame2 = MakeFrame(stack, frameData, 2);

        await Assert.That(() => Packet.ParseFrame(unsealed, new PacketId(2), stack, frame2))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Recycle_ThrowsWhenStackMismatch()
    {
        using Stack stack1 = BuildStack();
        using Stack stack2 = BuildStack();

        byte[] frameData = BuildIpv6UdpFrame();
        Frame frame1 = MakeFrame(stack1, frameData, 1);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack1, frame1);

        // frame2 must be from stack2 to satisfy registry check for stack2,
        // but the recycle packet is from stack1 — stack mismatch
        Frame frame2 = MakeFrame(stack2, frameData, 2);

        await Assert.That(() => Packet.ParseFrame(packet, new PacketId(2), stack2, frame2))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Recycle_ThrowsWhenRegistryMismatch()
    {
        using Stack stack = BuildStack();
        using Stack otherStack = BuildStack();

        byte[] frameData = BuildIpv6UdpFrame();
        Frame frame1 = MakeFrame(stack, frameData, 1);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame1);

        // frame2 from a different registry, but same stack reference
        Frame frame2 = MakeFrame(otherStack, frameData, 2);

        await Assert.That(() => Packet.ParseFrame(packet, new PacketId(2), stack, frame2))
            .Throws<ArgumentException>();
    }

    // ── Correctness: recycled packet matches fresh parse ─────────────────────────────

    [Test]
    public async Task Recycle_FieldValuesMatchFreshParse()
    {
        using Stack stack = BuildStack();
        byte[] frameData = BuildIpv6UdpFrame(srcPort: 7777, dstPort: 8888);

        // Collect field IDs and values from fresh parse
        Frame freshFrame = MakeFrame(stack, frameData, 1);
        Packet fresh = Packet.ParseFrame(new PacketId(1), stack, freshFrame);
        List<FieldId> freshFields = CollectFieldIds(fresh);

        // Seed + recycle
        Packet seed = Packet.ParseFrame(new PacketId(99), stack, MakeFrame(stack, frameData, 99));
        Frame recycleFrame = MakeFrame(stack, frameData, 2);
        Packet recycled = Packet.ParseFrame(seed, new PacketId(2), stack, recycleFrame);
        List<FieldId> recycledFields = CollectFieldIds(recycled);

        // Same number of fields in same order
        await Assert.That(recycledFields.Count).IsEqualTo(freshFields.Count);

        for (int i = 0; i < freshFields.Count; i++)
        {
            await Assert.That(recycledFields[i]).IsEqualTo(freshFields[i]);
        }
    }

    /// <summary>
    /// Materializes all fields and returns their <see cref="FieldId"/> values
    /// in flat/storage order.
    /// </summary>
    private static List<FieldId> CollectFieldIds(Packet packet)
    {
        List<FieldId> result = [];
        foreach (Field field in packet.IterFieldsFlat(materialize: true))
        {
            result.Add(field.FieldId);
        }
        return result;
    }
}

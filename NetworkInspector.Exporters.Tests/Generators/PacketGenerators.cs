// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Generators;

/// <summary>
/// Utility methods for creating parsed <see cref="Packet"/> instances from raw frame data.
/// Uses the shared <see cref="TestHarness"/> stack with all standard protocols.
/// </summary>
internal static class PacketGenerators
{
    /// <summary>
    /// Creates a <see cref="Frame"/> and parses it into a <see cref="Packet"/>.
    /// </summary>
    /// <param name="frameId">Frame/packet sequence number.</param>
    /// <param name="frameData">Raw frame bytes.</param>
    /// <param name="timestampNanos">Timestamp in nanoseconds.</param>
    /// <param name="linkType">Link-layer type.</param>
    internal static Packet CreateParsedPacket(
        int frameId,
        byte[] frameData,
        long timestampNanos = 0,
        LinkType linkType = LinkType.Ethernet)
    {
        // Use frameId-based timestamp if none given
        if (timestampNanos == 0)
        {
            timestampNanos = (long)frameId * 1_000_000; // 1 ms per frame
        }

        Frame frame = TestHarness.CreateFrame(
            frameId, timestampNanos, frameData, linkType);

        return TestHarness.ParseFrame(frame);
    }

    /// <summary>
    /// Creates an array of parsed Ethernet + IPv4 + UDP packets with sequential IDs.
    /// </summary>
    /// <param name="count">Number of packets to generate.</param>
    /// <param name="payloadSize">UDP payload size per packet.</param>
    internal static Packet[] CreateEthernetUdpPackets(int count, int payloadSize = 32)
    {
        Packet[] packets = new Packet[count];
        for (int i = 0; i < count; i++)
        {
            byte[] frameData = FrameGenerators.BuildEthernetIpv4UdpFrame(payloadSize);
            packets[i] = CreateParsedPacket(i, frameData, (long)i * 1_000_000);
        }

        return packets;
    }

    /// <summary>
    /// Creates an array of parsed Ethernet + IPv4 + UDP + DNS "A" query packets, one per entry in
    /// <paramref name="domainNames"/>, with sequential IDs. Useful for exercising string-typed
    /// fields (<c>dns.qry.name</c>) and their dictionary encoding in columnar exporters.
    /// </summary>
    /// <param name="domainNames">Domain name to query for each generated packet.</param>
    internal static Packet[] CreateDnsQueryPackets(params string[] domainNames)
    {
        Packet[] packets = new Packet[domainNames.Length];
        for (int i = 0; i < domainNames.Length; i++)
        {
            byte[] frameData = FrameGenerators.BuildDnsQueryFrame(domainNames[i]);
            packets[i] = CreateParsedPacket(i, frameData, (long)i * 1_000_000);
        }

        return packets;
    }

    /// <summary>
    /// Creates an array of <see cref="Frame"/> instances for frame-level exporters (PCAPNG, BLF).
    /// </summary>
    /// <param name="count">Number of frames to generate.</param>
    /// <param name="payloadSize">Ethernet payload size per frame.</param>
    internal static Frame[] CreateEthernetFrames(int count, int payloadSize = 32)
    {
        Frame[] frames = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            byte[] frameData = FrameGenerators.BuildEthernetIpv4UdpFrame(payloadSize);
            frames[i] = TestHarness.CreateFrame(
                i, (long)i * 1_000_000, frameData);
        }

        return frames;
    }

    /// <summary>
    /// Creates an IPv4-in-IPv4 UDP frame: two <c>ip.src</c> values, outer 10.0.0.1 first and inner 192.168.0.1 second.
    /// </summary>
    /// <param name="id">Frame id; the timestamp is one millisecond per id.</param>
    internal static Frame CreateTunnelFrame(int id = 0) =>
        TestHarness.CreateFrame(
            id, (long)id * 1_000_000, FrameGenerators.BuildIpInIpUdpFrame(0x0A000001, 0xC0A80001));

    /// <summary>Creates a classic SocketCAN frame (id 0x123, 8 data bytes): a packet without any <c>ip.src</c>.</summary>
    /// <param name="id">Frame id; the timestamp is one millisecond per id.</param>
    internal static Frame CreateCanFrame(int id = 0)
    {
        byte[] data = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x123u);
        data[4] = 8;
        return TestHarness.CreateFrame(id, (long)id * 1_000_000, data, LinkType.CanSocketcan);
    }

    /// <summary>
    /// Creates frames in the fixed order UDP, tunnel, CAN (repeated) for tree-free parses: plain packets, a packet with two values
    /// of the same field, and a packet of another protocol family.
    /// </summary>
    /// <param name="count">Number of frames.</param>
    internal static Frame[] CreateMixedFrames(int count)
    {
        Frame[] frames = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            frames[i] = (i % 3) switch
            {
                0 => TestHarness.CreateFrame(
                    i, (long)i * 1_000_000, FrameGenerators.BuildEthernetIpv4UdpFrame(32)),
                1 => CreateTunnelFrame(i),
                _ => CreateCanFrame(i),
            };
        }

        return frames;
    }
}

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
            new FrameId(frameId), timestampNanos, frameData, linkType);

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
                new FrameId(i), (long)i * 1_000_000, frameData);
        }

        return frames;
    }
}

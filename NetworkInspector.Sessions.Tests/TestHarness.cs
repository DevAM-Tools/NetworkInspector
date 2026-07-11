// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Shared test infrastructure for Session tests. Provides stack creation
/// and synthetic frame generation.
/// </summary>
internal static class TestHarness
{
    /// <summary>
    /// Creates a new <see cref="Stack"/> with all standard protocols registered.
    /// Each call returns a fresh instance with its own <see cref="FrameInterfaceRegistry"/>.
    /// </summary>
    internal static Stack CreateStack()
    {
        FrameInterfaceRegistry registry = new();
        return CreateStack(registry);
    }

    /// <summary>
    /// Creates a new <see cref="Stack"/> sharing an existing <see cref="FrameInterfaceRegistry"/>.
    /// Used for <see cref="Session.Restart"/> scenarios where source and interface IDs must remain stable.
    /// </summary>
    internal static Stack CreateStack(FrameInterfaceRegistry registry)
    {
        SettingsManager? settingsManager = new();
        try
        {
            StackBuilder builder = new(settingsManager, registry);
            builder.RegisterStandardProtocols();
            Stack stack = builder.Build();
            settingsManager = null; // ownership transferred to stack
            return stack;
        }
        finally
        {
            settingsManager?.Dispose();
        }
    }

    /// <summary>
    /// Generates a minimal UDP-over-IPv4-over-Ethernet frame (42 bytes + optional payload).
    /// </summary>
    internal static byte[] GenerateUdpFrame(int totalSize = 64)
    {
        const int ethSize = 14;
        const int ipv4Size = 20;
        const int udpSize = 8;
        const int minSize = ethSize + ipv4Size + udpSize;

        totalSize = Math.Max(totalSize, minSize);
        byte[] frame = new byte[totalSize];

        int payloadSize = totalSize - minSize;
        ushort ipTotalLen = (ushort)(ipv4Size + udpSize + payloadSize);
        ushort udpLen = (ushort)(udpSize + payloadSize);

        // Ethernet header
        frame[0] = 0x00;
        frame[1] = 0x11;
        frame[2] = 0x22;
        frame[3] = 0x33;
        frame[4] = 0x44;
        frame[5] = 0x55; // dst
        frame[6] = 0x66;
        frame[7] = 0x77;
        frame[8] = 0x88;
        frame[9] = 0x99;
        frame[10] = 0xAA;
        frame[11] = 0xBB; // src
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800); // IPv4

        // IPv4 header
        int ip = ethSize;
        frame[ip] = 0x45; // v4, IHL=5
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ip + 2), ipTotalLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ip + 4), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ip + 6), 0x4000); // DF
        frame[ip + 8] = 64; // TTL
        frame[ip + 9] = 17; // UDP
        frame[ip + 12] = 192;
        frame[ip + 13] = 168;
        frame[ip + 14] = 1;
        frame[ip + 15] = 1; // src
        frame[ip + 16] = 192;
        frame[ip + 17] = 168;
        frame[ip + 18] = 1;
        frame[ip + 19] = 2; // dst

        // IPv4 checksum
        ushort checksum = _CalculateIpv4Checksum(frame.AsSpan(ip, ipv4Size));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ip + 10), checksum);

        // UDP header
        int udp = ip + ipv4Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udp), 12345);     // src port
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udp + 2), 53);    // dst port (DNS)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udp + 4), udpLen);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udp + 6), 0);     // checksum

        return frame;
    }

    /// <summary>
    /// Computes the one's-complement IPv4 header checksum.
    /// </summary>
    private static ushort _CalculateIpv4Checksum(ReadOnlySpan<byte> header)
    {
        uint sum = 0;
        for (int i = 0; i < header.Length; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(header[i..]);
        }
        while (sum > 0xFFFF)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return (ushort)~sum;
    }
}

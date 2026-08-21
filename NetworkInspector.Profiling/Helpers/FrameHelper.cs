// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>
/// Shared helpers for generating synthetic Ethernet/IPv6/UDP frames and parsing
/// them into <see cref="Packet"/> instances for profiling scenarios.
/// </summary>
internal static class FrameHelper
{
    // ── Frame generation constants ────────────────────────────────────────────

    private const int _EthSize = 14;   // Ethernet II header
    private const int _Ipv6Size = 40;  // Fixed IPv6 header
    private const int _UdpSize = 8;    // UDP header
    private const int _MinSize = _EthSize + _Ipv6Size + _UdpSize; // 62 bytes

    /// <summary>
    /// Generates a single well-formed Ethernet/IPv6/UDP frame.
    /// The header fields are valid; the payload is a repeating byte pattern.
    /// </summary>
    /// <param name="totalSize">Total frame size in bytes (minimum 62).</param>
    /// <param name="udpSrcPort">UDP source port written into the header.</param>
    internal static byte[] GenerateStaticUdpIpv6Frame(int totalSize = 512, ushort udpSrcPort = 12345)
    {
        totalSize = Math.Max(totalSize, _MinSize);
        byte[] frame = new byte[totalSize];

        int payloadSize = totalSize - _MinSize;
        ushort udpLen = (ushort)(_UdpSize + payloadSize);

        // ── Ethernet header ──────────────────────────────────────────────────
        // Dst MAC: 00:11:22:33:44:55
        frame[0] = 0x00;
        frame[1] = 0x11;
        frame[2] = 0x22;
        frame[3] = 0x33;
        frame[4] = 0x44;
        frame[5] = 0x55;
        // Src MAC: 66:77:88:99:AA:BB
        frame[6] = 0x66;
        frame[7] = 0x77;
        frame[8] = 0x88;
        frame[9] = 0x99;
        frame[10] = 0xAA;
        frame[11] = 0xBB;
        // EtherType: IPv6 (0x86DD)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x86DD);

        // ── IPv6 header ──────────────────────────────────────────────────────
        int ip = _EthSize;
        // Version=6, Traffic Class=0, Flow Label=0x12345
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(ip), 0x60012345);
        // Payload length = UDP header + payload
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ip + 4), udpLen);
        frame[ip + 6] = 17;  // Next Header: UDP
        frame[ip + 7] = 64;  // Hop Limit

        // Src: 2001:db8::1
        frame[ip + 8] = 0x20;
        frame[ip + 9] = 0x01;
        frame[ip + 10] = 0x0d;
        frame[ip + 11] = 0xb8;
        frame[ip + 23] = 0x01;

        // Dst: 2001:db8::2
        frame[ip + 24] = 0x20;
        frame[ip + 25] = 0x01;
        frame[ip + 26] = 0x0d;
        frame[ip + 27] = 0xb8;
        frame[ip + 39] = 0x02;

        // ── UDP header ───────────────────────────────────────────────────────
        int udp = ip + _Ipv6Size;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udp), udpSrcPort);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udp + 2), 54321);  // Dst port (no registered protocol)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udp + 4), udpLen); // Length
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udp + 6), 0);      // Checksum (optional for UDP)

        // ── Payload: repeating 0x00-0xFF pattern ─────────────────────────────
        for (int i = 0; i < payloadSize; i++)
        {
            frame[_MinSize + i] = (byte)(i & 0xFF);
        }

        return frame;
    }

    /// <summary>
    /// Builds frames whose UDP source ports follow a deterministic low→high spike pattern for
    /// flank profiling: most packets stay in <c>[10, 89]</c>, every
    /// <paramref name="spikePeriod"/>-th packet jumps to <c>&gt;= 200</c>.
    /// </summary>
    /// <param name="count">Number of frames to create.</param>
    /// <param name="stack">Stack whose frame-interface registry is used.</param>
    /// <param name="spikePeriod">Distance between high-port spikes (must be &gt;= 2).</param>
    /// <param name="enableSpikes">
    /// When <see langword="false"/>, every port stays below 100 so a
    /// <c>from: &lt; 100, to: &gt;= 200</c> flank never fires.
    /// </param>
    internal static Frame[] CreateFlankUdpFrames(
        int count,
        Stack stack,
        int spikePeriod = 50,
        bool enableSpikes = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(spikePeriod, 2);
        Frame[] frames = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            ushort srcPort = enableSpikes && (i % spikePeriod == spikePeriod - 1)
                ? (ushort)(200 + (i % 17))
                : (ushort)(10 + (i % 80));

            byte[] frameData = GenerateStaticUdpIpv6Frame(udpSrcPort: srcPort);
            ParseResult<Frame> result = Frame.Create(
                new FrameId(i),
                Timestamp.FromSecs(i),
                frameData,
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry);

            if (!result.TryGetValue(out Frame frame))
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Failed to create flank frame {i}: {result.Error.Message}"));
            }

            frames[i] = frame;
        }

        return frames;
    }

    /// <summary>
    /// Creates an array of <see cref="Frame"/> instances from a shared backing byte array.
    /// All frames share the same memory to avoid skewing allocation measurements.
    /// </summary>
    /// <param name="count">Number of frames to create.</param>
    /// <param name="stack">Stack whose <see cref="Stack.FrameInterfaceRegistry"/> is used.</param>
    /// <param name="totalSize">Total size per frame in bytes.</param>
    internal static Frame[] CreateSharedFrames(int count, Stack stack, int totalSize = 512)
    {
        byte[] frameData = GenerateStaticUdpIpv6Frame(totalSize);
        ReadOnlyMemory<byte> sharedMemory = frameData;

        Frame[] frames = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            ParseResult<Frame> result = Frame.Create(
                new FrameId(i),
                Timestamp.FromSecs(i),
                sharedMemory,
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry);

            if (!result.TryGetValue(out Frame frame))
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Failed to create synthetic frame {i}: {result.Error.Message}"));
            }

            frames[i] = frame;
        }

        return frames;
    }

    /// <summary>
    /// Parses each frame in the array into a <see cref="Packet"/> and calls
    /// <see cref="Packet.MaterializeAll"/> to pre-materialise all fields.
    /// </summary>
    /// <param name="frames">Source frames to parse.</param>
    /// <param name="stack">The protocol stack used for parsing.</param>
    internal static Packet[] ParseAndMaterialize(Frame[] frames, Stack stack)
    {
        Packet[] packets = new Packet[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            packets[i] = Packet.ParseFrame(new PacketId(i), stack, frames[i]);
            packets[i].MaterializeAll();
        }

        return packets;
    }
}

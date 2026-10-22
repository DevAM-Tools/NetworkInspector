// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>
/// Shared helpers for generating synthetic Ethernet/IPv6/UDP, SocketCAN (classic and FD), and mixed frame streams and
/// parsing them into <see cref="Packet"/> instances for profiling scenarios.
/// </summary>
internal static class SampleFrames
{
    #region Constants

    // ── Frame generation constants ────────────────────────────────────────────

    private const int _EthSize = 14;   // Ethernet II header
    private const int _Ipv6Size = 40;  // Fixed IPv6 header
    private const int _UdpSize = 8;    // UDP header
    private const int _MinSize = _EthSize + _Ipv6Size + _UdpSize; // 62 bytes
    private const int _SocketCanSize = 16;  // id(4) + dlc(1) + flags(1) + reserved(2) + data(8)
    private const int _SocketCanFdSize = 72;   // id(4) + len(1) + flags(1) + reserved(2) + data(64)
    private const byte _CanFdFlags = 0x05;     // FDF (0x04) | BRS (0x01)

    #endregion

    #region Public API

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
        const int ip = _EthSize;
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
    /// Timestamp of frame <paramref name="index"/> in nanoseconds since the Unix epoch: 731 µs spacing plus a
    /// deterministic 0–999 ns jitter, so timestamps are strictly ascending but not perfectly regular.
    /// </summary>
    internal static long TimestampNanos(int index) =>
        1_700_000_000_000_000_000L + (index * 731_000L) + ((index * 7_919L) % 1_000L);

    /// <summary>
    /// Ethernet/IPv6/UDP frames of one size (minimum 62 bytes) for the footprint and access workloads. All frames share
    /// one payload array; timestamps follow <see cref="TimestampNanos"/>.
    /// </summary>
    /// <param name="count">Number of frames.</param>
    /// <param name="stack">Stack whose frame-interface registry is used.</param>
    /// <param name="totalSize">Total size per frame in bytes.</param>
    internal static Frame[] CreateUniformEthernet(int count, Stack stack, int totalSize)
    {
        ReadOnlyMemory<byte> shared = GenerateStaticUdpIpv6Frame(totalSize);
        Frame[] frames = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            frames[i] = _CreateFrame(i, shared, LinkType.Ethernet, stack);
        }

        return frames;
    }

    /// <summary>
    /// IMIX 7:4:1 of 64, 594, and 1518 bytes in the fixed repeating order 64,64,594,64,64,594,64,1518,64,594,64,594.
    /// One shared payload array per size; timestamps follow <see cref="TimestampNanos"/>.
    /// </summary>
    /// <param name="count">Number of frames.</param>
    /// <param name="stack">Stack whose frame-interface registry is used.</param>
    internal static Frame[] CreateImixEthernet(int count, Stack stack)
    {
        // The fixed pattern keeps every run byte-identical while mixing sizes inside each group of 64 frames.
        ReadOnlyMemory<byte> small = GenerateStaticUdpIpv6Frame(64);
        ReadOnlyMemory<byte> medium = GenerateStaticUdpIpv6Frame(594);
        ReadOnlyMemory<byte> large = GenerateStaticUdpIpv6Frame(1518);
        ReadOnlyMemory<byte>[] pattern = [small, small, medium, small, small, medium, small, large, small, medium, small, medium];

        Frame[] frames = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            frames[i] = _CreateFrame(i, pattern[i % pattern.Length], LinkType.Ethernet, stack);
        }

        return frames;
    }

    /// <summary>
    /// SocketCAN classic frames (16 bytes): CAN id <c>0x100 + (i % 0x400)</c> big-endian, DLC 8, data = little-endian
    /// <c>i</c>. Link type <see cref="LinkType.CanSocketcan"/>; timestamps follow <see cref="TimestampNanos"/>.
    /// </summary>
    /// <param name="count">Number of frames.</param>
    /// <param name="stack">Stack whose frame-interface registry is used.</param>
    internal static Frame[] CreateCanFrames(int count, Stack stack)
    {
        // One backing array for all frames: 16 bytes per frame, sliced, so Setup allocates one large array only.
        byte[] backing = new byte[count * _SocketCanSize];
        Frame[] frames = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            Span<byte> frame = backing.AsSpan(i * _SocketCanSize, _SocketCanSize);
            BinaryPrimitives.WriteUInt32BigEndian(frame, CanId(i));
            frame[4] = 8;
            BinaryPrimitives.WriteInt64LittleEndian(frame[8..], i);
            frames[i] = _CreateFrame(i, backing.AsMemory(i * _SocketCanSize, _SocketCanSize), LinkType.CanSocketcan, stack);
        }

        return frames;
    }

    /// <summary>CAN identifier of frame <paramref name="index"/> in the CAN workloads.</summary>
    internal static uint CanId(int index) => 0x100u + (uint)(index % 0x400);

    /// <summary>
    /// SocketCAN FD frames (72 bytes, 64 data bytes, FDF and BRS set): CAN id <see cref="CanId"/>, data = little-endian
    /// <c>i</c>. Link type <see cref="LinkType.CanSocketcan"/>; timestamps follow <see cref="TimestampNanos"/>.
    /// </summary>
    /// <param name="count">Number of frames.</param>
    /// <param name="stack">Stack whose frame-interface registry is used.</param>
    internal static Frame[] CreateCanFdFrames(int count, Stack stack)
    {
        // One backing array for all frames, sliced per frame, as in CreateCanFrames.
        byte[] backing = new byte[count * _SocketCanFdSize];
        Frame[] frames = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            Span<byte> frame = backing.AsSpan(i * _SocketCanFdSize, _SocketCanFdSize);
            BinaryPrimitives.WriteUInt32BigEndian(frame, CanId(i));
            frame[4] = 64;
            frame[5] = _CanFdFlags;
            BinaryPrimitives.WriteInt64LittleEndian(frame[8..], i);
            frames[i] = _CreateFrame(i, backing.AsMemory(i * _SocketCanFdSize, _SocketCanFdSize), LinkType.CanSocketcan, stack);
        }

        return frames;
    }

    /// <summary>
    /// Mixed stream for the field-storage scenarios: per group of four frames CAN, CAN, CAN FD, Ethernet/IPv6/UDP
    /// (512 bytes). With <paramref name="fanoutPeriod"/> &gt; 0, frame <c>i</c> with
    /// <c>i % fanoutPeriod == fanoutPeriod / 2</c> is a <see cref="FanoutProfilingProtocol"/> frame with
    /// <paramref name="fanoutItems"/> items instead. Payloads are shared per kind.
    /// </summary>
    /// <param name="count">Number of frames.</param>
    /// <param name="stack">Stack built by <see cref="StackHelper.CreateStorageStack"/>.</param>
    /// <param name="fanoutPeriod">Distance between fanout frames; 0 = none.</param>
    /// <param name="fanoutItems">Items per fanout frame, 0 … <see cref="FanoutProfilingProtocol.MaxItems"/>.</param>
    internal static Frame[] CreateMixedStorageFrames(int count, Stack stack, int fanoutPeriod, int fanoutItems)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fanoutPeriod);
        ArgumentOutOfRangeException.ThrowIfNegative(fanoutItems);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fanoutItems, FanoutProfilingProtocol.MaxItems);

        // One payload per kind; CAN id 0x123, 8 data bytes; CAN FD id 0x124, 64 data bytes.
        byte[] can = new byte[_SocketCanSize];
        BinaryPrimitives.WriteUInt32BigEndian(can, 0x123u);
        can[4] = 8;
        byte[] canFd = new byte[_SocketCanFdSize];
        BinaryPrimitives.WriteUInt32BigEndian(canFd, 0x124u);
        canFd[4] = 64;
        canFd[5] = _CanFdFlags;
        ReadOnlyMemory<byte> udp = GenerateStaticUdpIpv6Frame(512);
        byte[] fanout = new byte[FanoutProfilingProtocol.FrameSize];
        BinaryPrimitives.WriteUInt16BigEndian(fanout, (ushort)fanoutItems);

        Frame[] frames = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            bool isFanout = fanoutPeriod > 0 && i % fanoutPeriod == fanoutPeriod / 2;
            frames[i] = isFanout
                ? _CreateFrame(i, fanout, LinkType.User0, stack)
                : (i % 4) switch
                {
                    0 or 1 => _CreateFrame(i, can, LinkType.CanSocketcan, stack),
                    2 => _CreateFrame(i, canFd, LinkType.CanSocketcan, stack),
                    _ => _CreateFrame(i, udp, LinkType.Ethernet, stack),
                };
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
        ParseOptions options = new();
        for (int i = 0; i < frames.Length; i++)
        {
            PacketId id = new(i);
            if (!Packet.TryParse(id, stack, frames[i], in options, out Packet? packet, out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }

            packets[i] = packet!;
            packets[i].MaterializeAll();
        }

        return packets;
    }

    #endregion

    #region Private helpers

    /// <summary>Creates frame <paramref name="index"/> with the workload timestamp; throws when the frame is rejected.</summary>
    private static Frame _CreateFrame(int index, ReadOnlyMemory<byte> data, LinkType linkType, Stack stack)
    {
        ParseResult<Frame> result = Frame.Create(
            new FrameId(index),
            new Timestamp(TimestampNanos(index)),
            data,
            linkType,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry);

        if (!result.TryGetValue(out Frame frame))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"Failed to create workload frame {index}: {result.Error.Message}"));
        }

        return frame;
    }

    #endregion
}

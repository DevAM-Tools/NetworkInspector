// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// A PCAPNG trace of the sample protocol's wire format (magic <c>NIS1</c>, counter, gear, speed) over UDP or TCP. Frame
/// <c>i</c> carries counter <c>i</c>, gear <c>i % 5</c>, and the speed of <see cref="SpeedFor"/>: 254 when <c>i % 50 == 0</c>,
/// 255 when <c>i % 100</c> is 70 to 76, otherwise <c>i % 200</c>. The kit writes the bytes from its own copy of the layout, so
/// it does not reference the sample plugin; a test in a project that does reference it compares the two. Over TCP every
/// frame is a PSH|ACK segment of one connection with sequence <c>1 + 14 * i</c>. Immutable; thread-safe.
/// </summary>
public sealed class SampleProtocolRecipe : TraceRecipe
{
    #region Constants

    /// <summary>Speed value that means "error" in the generated pattern.</summary>
    public const byte SpeedError = 254;

    /// <summary>Speed value that means "signal not available" in the generated pattern.</summary>
    public const byte SpeedNotAvailable = 255;

    // Wire layout of the sample payload, a private copy of the layout of the sample plugin (the kit must not reference the
    // plugin): magic "NIS1" at 0 to 3, counter as 8 bytes big endian at 4 to 11, gear at 12, speed at 13, 14 bytes in all.
    // If the sample layout changes, this copy and the recipe version change together.

    /// <summary>Length of the sample payload in bytes.</summary>
    private const int _PayloadLength = 14;

    /// <summary>Offset of the 8-byte big-endian counter in the payload.</summary>
    private const int _CounterOffset = 4;

    /// <summary>Offset of the gear byte in the payload.</summary>
    private const int _GearOffset = 12;

    /// <summary>Offset of the speed byte in the payload.</summary>
    private const int _SpeedOffset = 13;

    /// <summary>Length of the UDP header in bytes.</summary>
    private const int _UdpHeaderLength = 8;

    /// <summary>Length of the TCP header without options in bytes.</summary>
    private const int _TcpHeaderLength = 20;

    /// <summary>Source port of every frame.</summary>
    private const ushort _SourcePort = 50000;

    /// <summary>Distance of two frame timestamps: 1 ms in nanoseconds.</summary>
    private const long _FrameIntervalNanos = 1_000_000;

    /// <summary>Frames between two cancellation checks.</summary>
    private const int _CancellationCheckInterval = 1024;

    #endregion

    #region Lifecycle

    /// <summary>Creates the recipe.</summary>
    /// <param name="frameCount">Number of frames, 1 to <see cref="TraceRecipe.MaxFrames"/>.</param>
    /// <param name="port">Destination port (the sample protocol listens on 40000 over UDP; over TCP any port works).</param>
    /// <param name="tcp"><see langword="true"/> for TCP segments instead of UDP datagrams.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frameCount"/> is out of range or <paramref name="port"/> is 0.</exception>
    public SampleProtocolRecipe(int frameCount, ushort port, bool tcp = false)
    {
        CheckCount(frameCount, nameof(frameCount));
        ArgumentOutOfRangeException.ThrowIfZero(port);
        Frames = frameCount;
        Port = port;
        Tcp = tcp;
    }

    #endregion

    #region Properties

    /// <summary>Number of frames.</summary>
    public int Frames { get; }

    /// <summary>Destination port.</summary>
    public ushort Port { get; }

    /// <summary>Whether the frames are TCP segments.</summary>
    public bool Tcp { get; }

    /// <inheritdoc/>
    public override string Name => "sample-protocol";

    /// <inheritdoc/>
    public override int Version => 1;

    /// <inheritdoc/>
    public override string Extension => ".pcapng";

    /// <inheritdoc/>
    public override int FrameCount => Frames;

    /// <inheritdoc/>
    public override string CanonicalParameters =>
        string.Create(CultureInfo.InvariantCulture, $"count={Frames};port={Port};tcp={Tcp}");

    #endregion

    #region Public API

    /// <summary>The speed byte of frame <paramref name="index"/> (see the type description).</summary>
    /// <param name="index">Zero-based frame index.</param>
    /// <returns>The speed byte.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public static byte SpeedFor(long index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        // The error value is checked first; the "not available" window 70 to 76 of each hundred contains no multiple of 50,
        // so the two special values never compete. Ordinary speeds are below 200 and never equal 254 or 255.
        if (index % 50 == 0)
        {
            return SpeedError;
        }

        long hundred = index % 100;
        return hundred is >= 70 and <= 76
            ? SpeedNotAvailable
            : (byte)(index % 200);
    }

    #endregion

    #region Protected API

    /// <inheritdoc/>
    protected internal override IReadOnlyDictionary<string, string> Write(string tracePath, string sideFileDirectory,
        CancellationToken cancellationToken)
    {
        using PcapngTraceWriter writer = new(tracePath, LinkType.Ethernet);
        for (int i = 0; i < Frames; i++)
        {
            if (i % _CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Gear cycles through the five gears (0 to 4). Over TCP the sequence advances by the 14 payload bytes of the
            // previous frame, so the frames form one coherent stream; the 64-bit product keeps 14 * i from overflowing.
            byte gear = (byte)(i % 5);
            byte[] frame = Tcp
                ? _TcpFrame((ulong)i, gear, SpeedFor(i), Port, (uint)(1 + (14L * i)))
                : _UdpFrame((ulong)i, gear, SpeedFor(i), Port);
            writer.Add(i * _FrameIntervalNanos, frame);
        }

        writer.Finish();
        return FrozenDictionary<string, string>.Empty;
    }

    #endregion

    #region Private Helpers

    /// <summary>A 56-byte Ethernet/IPv4/UDP frame with the sample payload.</summary>
    private static byte[] _UdpFrame(ulong counter, byte gear, byte speed, ushort port)
    {
        byte[] frame = new byte[Ipv4Wire.EthernetHeaderLength + Ipv4Wire.Ipv4HeaderLength + _UdpHeaderLength + _PayloadLength];
        Ipv4Wire.WriteEthernetAndIpv4(frame, Ipv4Wire.Ipv4HeaderLength + _UdpHeaderLength + _PayloadLength, Ipv4Wire.ProtocolUdp);

        // UDP header: source port, destination port, length, checksum 0 (UDP over IPv4 may omit the checksum).
        Span<byte> udp = frame.AsSpan(Ipv4Wire.EthernetHeaderLength + Ipv4Wire.Ipv4HeaderLength);
        BinaryPrimitives.WriteUInt16BigEndian(udp, _SourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[2..], port);
        BinaryPrimitives.WriteUInt16BigEndian(udp[4..], _UdpHeaderLength + _PayloadLength);
        _WritePayload(udp[_UdpHeaderLength..], counter, gear, speed);
        return frame;
    }

    /// <summary>A 68-byte Ethernet/IPv4/TCP frame (one PSH|ACK segment, no handshake) with the sample payload.</summary>
    private static byte[] _TcpFrame(ulong counter, byte gear, byte speed, ushort port, uint sequence)
    {
        byte[] frame = new byte[Ipv4Wire.EthernetHeaderLength + Ipv4Wire.Ipv4HeaderLength + _TcpHeaderLength + _PayloadLength];
        Ipv4Wire.WriteEthernetAndIpv4(frame, Ipv4Wire.Ipv4HeaderLength + _TcpHeaderLength + _PayloadLength, Ipv4Wire.ProtocolTcp);

        // TCP header: ports, sequence, acknowledgment 1, data offset 5 words, flags PSH|ACK, window 65535; the checksum last.
        Span<byte> tcp = frame.AsSpan(Ipv4Wire.EthernetHeaderLength + Ipv4Wire.Ipv4HeaderLength);
        BinaryPrimitives.WriteUInt16BigEndian(tcp, _SourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], port);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], 1);
        tcp[12] = (_TcpHeaderLength / 4) << 4;
        tcp[13] = 0x18;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], 65535);
        _WritePayload(tcp[_TcpHeaderLength..], counter, gear, speed);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[16..], Ipv4Wire.TcpChecksum(tcp[..(_TcpHeaderLength + _PayloadLength)]));
        return frame;
    }

    /// <summary>Magic, counter (big endian), gear, speed.</summary>
    private static void _WritePayload(Span<byte> payload, ulong counter, byte gear, byte speed)
    {
        "NIS1"u8.CopyTo(payload);
        BinaryPrimitives.WriteUInt64BigEndian(payload[_CounterOffset..], counter);
        payload[_GearOffset] = gear;
        payload[_SpeedOffset] = speed;
    }

    #endregion
}

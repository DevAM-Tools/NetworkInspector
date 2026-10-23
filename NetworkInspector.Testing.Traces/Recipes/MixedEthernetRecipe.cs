// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// A PCAPNG trace of Ethernet frames of four kinds, drawn per packet from a seeded generator: a DNS query (UDP 53), a TCP ACK
/// segment to port 80 with 20–200 payload bytes, an ARP request, and a UDP datagram to port 5000 with a 32-byte payload. The
/// frames come from the FrameBuilder layers; the timestamps are 1 ms apart from 2026-01-01T00:00:00Z. Immutable;
/// thread-safe.
/// </summary>
public sealed class MixedEthernetRecipe : TraceRecipe
{
    #region Constants

    /// <summary>UDP destination port of the fixed-size datagrams.</summary>
    public const ushort DatagramPort = 5000;

    /// <summary>Payload length of the UDP datagrams in bytes.</summary>
    public const int DatagramPayloadBytes = 32;

    private const int _CancellationCheckInterval = 1024;

    private const long _FrameIntervalNanos = 1_000_000;

    #endregion

    #region Lifecycle

    /// <summary>Creates the recipe.</summary>
    /// <param name="packetCount">Number of packets, 1 to <see cref="TraceRecipe.MaxFrames"/>.</param>
    /// <param name="seed">Seed of the generator that draws the frame kinds and the bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="packetCount"/> is out of range.</exception>
    public MixedEthernetRecipe(int packetCount, int seed)
    {
        CheckCount(packetCount, nameof(packetCount));
        PacketCount = packetCount;
        Seed = seed;
    }

    #endregion

    #region Properties

    /// <summary>Number of packets.</summary>
    public int PacketCount { get; }

    /// <summary>Seed of the generator.</summary>
    public int Seed { get; }

    /// <inheritdoc/>
    public override string Name => "mixed-ethernet";

    /// <inheritdoc/>
    public override int Version => 1;

    /// <inheritdoc/>
    public override string Extension => ".pcapng";

    /// <inheritdoc/>
    public override int FrameCount => PacketCount;

    /// <inheritdoc/>
    public override string CanonicalParameters => string.Create(CultureInfo.InvariantCulture, $"count={PacketCount};seed={Seed}");

    #endregion

    #region Protected API

    /// <inheritdoc/>
    protected internal override IReadOnlyDictionary<string, string> Write(string tracePath, string sideFileDirectory,
        CancellationToken cancellationToken)
    {
        // System.Random with a seed gives the same sequence on every run and platform of one runtime version; the recipe version
        // changes when a runtime changes it.
        System.Random random = new(Seed);
        using PcapngTraceWriter writer = new(tracePath, LinkType.Ethernet);
        byte[] datagram = new byte[DatagramPayloadBytes];
        for (int i = 0; i < PacketCount; i++)
        {
            if (i % _CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            byte[] frame;
            switch (random.Next(4))
            {
                case 0:
                    frame = EthernetFrames.DnsQuery(5353, (ushort)random.Next(65536),
                        string.Create(CultureInfo.InvariantCulture, $"host{random.Next(1000)}.example.com"));
                    break;
                case 1:
                    byte[] segment = new byte[20 + random.Next(181)];
                    random.NextBytes(segment);
                    frame = EthernetFrames.TcpAck(49152, 80, (uint)(1 + (i * 200)), segment);
                    break;
                case 2:
                    frame = EthernetFrames.ArpRequest();
                    break;
                default:
                    random.NextBytes(datagram);
                    frame = EthernetFrames.Udp(50000, DatagramPort, datagram);
                    break;
            }

            writer.Add(i * _FrameIntervalNanos, frame);
        }

        writer.Finish();
        return FrozenDictionary<string, string>.Empty;
    }

    #endregion
}

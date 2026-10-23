// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// A PCAPNG trace in which every packet fails to parse with the same error: an Ethernet/IPv4 frame whose IPv4 total length is
/// 24 with the protocol UDP, followed by only four bytes where a UDP header of eight bytes belongs. All frames have identical
/// bytes and timestamps 1 ms apart. Used to test that identical messages are shown once. Immutable; thread-safe.
/// </summary>
public sealed class RepeatedFailureRecipe : TraceRecipe
{
    #region Constants

    private const int _TruncatedUdpBytes = 4;

    private const int _CancellationCheckInterval = 1024;

    private const long _FrameIntervalNanos = 1_000_000;

    #endregion

    #region Lifecycle

    /// <summary>Creates the recipe.</summary>
    /// <param name="frameCount">Number of frames, 1 to <see cref="TraceRecipe.MaxFrames"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frameCount"/> is out of range.</exception>
    public RepeatedFailureRecipe(int frameCount)
    {
        CheckCount(frameCount, nameof(frameCount));
        Frames = frameCount;
    }

    #endregion

    #region Properties

    /// <summary>Number of frames.</summary>
    public int Frames { get; }

    /// <inheritdoc/>
    public override string Name => "repeated-failure";

    /// <inheritdoc/>
    public override int Version => 1;

    /// <inheritdoc/>
    public override string Extension => ".pcapng";

    /// <inheritdoc/>
    public override int FrameCount => Frames;

    /// <inheritdoc/>
    public override string CanonicalParameters => string.Create(CultureInfo.InvariantCulture, $"count={Frames}");

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

            // The writer keeps no reference, but a fresh array per frame keeps the frames independent of each other.
            byte[] frame = new byte[Ipv4Wire.EthernetHeaderLength + Ipv4Wire.Ipv4HeaderLength + _TruncatedUdpBytes];
            Ipv4Wire.WriteEthernetAndIpv4(frame, Ipv4Wire.Ipv4HeaderLength + _TruncatedUdpBytes, Ipv4Wire.ProtocolUdp);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(Ipv4Wire.EthernetHeaderLength + Ipv4Wire.Ipv4HeaderLength), 50000);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(Ipv4Wire.EthernetHeaderLength + Ipv4Wire.Ipv4HeaderLength + 2), 6002);
            writer.Add(i * _FrameIntervalNanos, frame);
        }

        writer.Finish();
        return FrozenDictionary<string, string>.Empty;
    }

    #endregion
}

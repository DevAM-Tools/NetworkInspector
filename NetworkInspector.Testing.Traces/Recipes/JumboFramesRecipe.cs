// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// A PCAPNG trace of Ethernet/IPv4/UDP frames (destination port 6000) with large payloads: payload byte <c>j</c> of frame
/// <c>i</c> is <c>(i + j) &amp; 0xFF</c>, IPv4 does not fragment, the timestamps are 1 ms apart. A payload of 9,000 bytes
/// gives frames of 9,042 bytes. Immutable; thread-safe.
/// </summary>
public sealed class JumboFramesRecipe : TraceRecipe
{
    #region Constants

    /// <summary>UDP destination port.</summary>
    public const ushort Port = 6000;

    /// <summary>Smallest payload: a full normal Ethernet payload.</summary>
    public const int MinPayloadBytes = 1_500;

    /// <summary>Largest payload that still fits into one IPv4 datagram.</summary>
    public const int MaxPayloadBytes = 65_000;

    private const long _FrameIntervalNanos = 1_000_000;

    #endregion

    #region Lifecycle

    /// <summary>Creates the recipe.</summary>
    /// <param name="frameCount">Number of frames, 1 to <see cref="TraceRecipe.MaxFrames"/>.</param>
    /// <param name="payloadBytes">UDP payload length, <see cref="MinPayloadBytes"/> to <see cref="MaxPayloadBytes"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public JumboFramesRecipe(int frameCount, int payloadBytes)
    {
        CheckCount(frameCount, nameof(frameCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(payloadBytes, MinPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadBytes, MaxPayloadBytes);
        Frames = frameCount;
        PayloadBytes = payloadBytes;
    }

    #endregion

    #region Properties

    /// <summary>Number of frames.</summary>
    public int Frames { get; }

    /// <summary>UDP payload length in bytes.</summary>
    public int PayloadBytes { get; }

    /// <inheritdoc/>
    public override string Name => "jumbo-frames";

    /// <inheritdoc/>
    public override int Version => 1;

    /// <inheritdoc/>
    public override string Extension => ".pcapng";

    /// <inheritdoc/>
    public override int FrameCount => Frames;

    /// <inheritdoc/>
    public override string CanonicalParameters => string.Create(CultureInfo.InvariantCulture, $"count={Frames};payload={PayloadBytes}");

    #endregion

    #region Protected API

    /// <inheritdoc/>
    protected internal override IReadOnlyDictionary<string, string> Write(string tracePath, string sideFileDirectory,
        CancellationToken cancellationToken)
    {
        using PcapngTraceWriter writer = new(tracePath, LinkType.Ethernet);
        byte[] payload = new byte[PayloadBytes];
        for (int i = 0; i < Frames; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int j = 0; j < payload.Length; j++)
            {
                payload[j] = (byte)((i + j) & 0xFF);
            }

            writer.Add(i * _FrameIntervalNanos, EthernetFrames.Udp(50000, Port, payload, EthernetFrames.MaxJumboFrameSize));
        }

        writer.Finish();
        return FrozenDictionary<string, string>.Empty;
    }

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Random;

/// <summary>
/// Configuration for TCP stream generation modes (<see cref="RandomFrameMode.TcpStreamIPv4"/>
/// and <see cref="RandomFrameMode.TcpStreamIPv6"/>).
/// Controls stream count, segments per stream, payload sizes, and the handshake/teardown phases.
/// </summary>
/// <remarks>
/// Immutable (<c>init</c> setters); <see cref="Validate"/> runs when a <see cref="RandomFrameSource"/> in a stream mode
/// is constructed. All streams share one structure (same segment count, same phases), which is what lets the frame
/// layout be computed by arithmetic from a frame index alone.
/// </remarks>
public sealed class TcpStreamOptions
{
    #region Constants

    /// <summary>Largest payload per segment over IPv4: 65 535 (16-bit total length) minus the IP header (20) and the TCP header (20).</summary>
    internal const int MaxPayloadSizeIPv4 = ushort.MaxValue - 20 - 20;

    /// <summary>Largest payload per segment over IPv6: 65 535 (16-bit payload length) minus the TCP header (20).</summary>
    internal const int MaxPayloadSizeIPv6 = ushort.MaxValue - 20;

    #endregion

    #region Properties

    /// <summary>Number of concurrent TCP streams to generate. At least 1.</summary>
    public int StreamCount { get; init; } = 4;

    /// <summary>
    /// Number of data segments per stream, both directions combined: segments alternate, starting with
    /// client to server (even segment numbers) and then server to client (odd). Not negative. Generating one frame of
    /// a stream costs time proportional to its segment number (the sequence numbers are summed over the earlier
    /// segments), so very large values make the whole stream quadratic; the only upper bound is that the frame counts
    /// checked by <see cref="Validate"/> must fit in an <see cref="int"/>.
    /// </summary>
    public int SegmentsPerStream { get; init; } = 10;

    /// <summary>Minimum payload size per data segment. Not negative, not above <see cref="MaxPayloadSize"/>.</summary>
    public int MinPayloadSize { get; init; } = 20; // bytes

    /// <summary>
    /// Maximum payload size per data segment. Not negative. The upper bound is a fact of the IP header: the 16-bit length
    /// field holds the TCP segment (20 header bytes plus payload) plus, for IPv4, the 20-byte IP header, so the payload may
    /// be at most 65,495 bytes for <see cref="RandomFrameMode.TcpStreamIPv4"/> and 65,515 bytes for
    /// <see cref="RandomFrameMode.TcpStreamIPv6"/>. A larger value is rejected when the source is created.
    /// </summary>
    public int MaxPayloadSize { get; init; } = 1400; // bytes

    /// <summary>Whether to include the 3-way handshake (SYN, SYN-ACK, ACK). Without it, data starts at ISN + 1 as in a mid-stream capture.</summary>
    public bool IncludeHandshake { get; init; } = true;

    /// <summary>Whether to include the teardown sequence (FIN-ACK, ACK, FIN-ACK, ACK: four frames).</summary>
    public bool IncludeTeardown { get; init; } = true;

    /// <summary>
    /// Whether to interleave the streams: frame <c>r</c> of every stream is emitted before frame <c>r + 1</c> of any
    /// stream (round-robin). When false, each stream is emitted completely before the next one starts.
    /// </summary>
    public bool InterleaveStreams { get; init; } = true;

    #endregion

    #region Validation

    /// <summary>
    /// Ensures TCP stream options are consistent for layout and payload generation.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A numeric bound is invalid.</exception>
    /// <exception cref="ArgumentException">Min/Max payload sizes are inconsistent.</exception>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(StreamCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(SegmentsPerStream);
        ArgumentOutOfRangeException.ThrowIfNegative(MinPayloadSize);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxPayloadSize);

        if (MinPayloadSize > MaxPayloadSize)
        {
            throw new ArgumentException(
                Invariant($"{nameof(MinPayloadSize)} ({MinPayloadSize}) must not exceed {nameof(MaxPayloadSize)} ({MaxPayloadSize})."));
        }

        // The layout counts frames in int (TcpStreamLayout multiplies without overflow checks, trusting this method):
        // frames per connection and their total over all streams must fit. The sums are formed in long so the check
        // itself cannot wrap.
        long framesPerConnection = (IncludeHandshake ? 3L : 0L) + SegmentsPerStream + (IncludeTeardown ? 4L : 0L);
        if (framesPerConnection > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(SegmentsPerStream), SegmentsPerStream,
                "Frames per connection (handshake + segments + teardown) must not exceed int.MaxValue.");
        }

        if (framesPerConnection * StreamCount > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(StreamCount), StreamCount,
                "The total frame count (frames per connection × streams) must not exceed int.MaxValue.");
        }
    }

    #endregion
}

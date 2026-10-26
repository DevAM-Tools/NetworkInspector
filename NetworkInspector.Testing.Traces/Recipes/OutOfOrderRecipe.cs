// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// A PCAPNG trace of Ethernet/IPv4/UDP frames (destination port 6001) whose timestamps run backwards at fixed positions: each
/// frame is 1 ms after its predecessor, except frame <c>i</c> with <c>i % 7 == 6</c>, which is 5 ms before it. The payload is
/// the frame index as eight big-endian bytes. Immutable; thread-safe.
/// </summary>
public sealed class OutOfOrderRecipe : TraceRecipe
{
    #region Constants

    /// <summary>UDP destination port.</summary>
    public const ushort Port = 6001;

    /// <summary>Forward step between two frames: 1 ms in nanoseconds.</summary>
    private const long _StepNanos = 1_000_000;

    /// <summary>Backward step of an out-of-order frame: 5 ms in nanoseconds, subtracted from the time of its predecessor.</summary>
    private const long _BackwardNanos = 5_000_000;

    /// <summary>Remainder of the frame index (modulo <c>_BackwardPeriod</c>) that marks a backward frame.</summary>
    private const int _BackwardRemainder = 6;

    /// <summary>Period of the backward frames: every seventh frame.</summary>
    private const int _BackwardPeriod = 7;

    #endregion

    #region Lifecycle

    /// <summary>Creates the recipe.</summary>
    /// <param name="frameCount">Number of frames, 1 to <see cref="TraceRecipe.MaxFrames"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frameCount"/> is out of range.</exception>
    public OutOfOrderRecipe(int frameCount)
    {
        CheckCount(frameCount, nameof(frameCount));
        Frames = frameCount;
    }

    #endregion

    #region Properties

    /// <summary>Number of frames.</summary>
    public int Frames { get; }

    /// <inheritdoc/>
    public override string Name => "out-of-order";

    /// <inheritdoc/>
    public override int Version => 1;

    /// <inheritdoc/>
    public override string Extension => ".pcapng";

    /// <inheritdoc/>
    public override int FrameCount => Frames;

    /// <inheritdoc/>
    public override string CanonicalParameters => string.Create(CultureInfo.InvariantCulture, $"count={Frames}");

    #endregion

    #region Public API

    /// <summary>The indices of the frames whose timestamp lies before the one of the frame in front of them.</summary>
    /// <param name="frameCount">Number of frames of the trace.</param>
    /// <returns>The indices in ascending order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frameCount"/> is negative.</exception>
    public static IReadOnlyList<int> BackwardIndices(int frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        List<int> indices = [];
        for (int i = _BackwardRemainder; i < frameCount; i += _BackwardPeriod)
        {
            indices.Add(i);
        }

        return indices;
    }

    #endregion

    #region Protected API

    /// <inheritdoc/>
    protected internal override IReadOnlyDictionary<string, string> Write(string tracePath, string sideFileDirectory,
        CancellationToken cancellationToken)
    {
        using PcapngTraceWriter writer = new(tracePath, LinkType.Ethernet);
        byte[] payload = new byte[8];
        long nanos = 0;
        for (int i = 0; i < Frames; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The timestamp is relative to the previous frame, not to the index: a backward frame is 5 ms before its
            // predecessor, and the following frame continues from the backward time. The offset never drops below 0 because
            // at least five forward steps (5 ms) precede every backward step (5 ms).
            if (i > 0)
            {
                nanos += i % _BackwardPeriod == _BackwardRemainder
                    ? -_BackwardNanos
                    : _StepNanos;
            }

            BinaryPrimitives.WriteInt64BigEndian(payload, i);
            writer.Add(nanos, EthernetFrames.Udp(50000, Port, payload));
        }

        writer.Finish();
        return FrozenDictionary<string, string>.Empty;
    }

    #endregion
}

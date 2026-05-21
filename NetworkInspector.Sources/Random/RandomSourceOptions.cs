// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Random;

/// <summary>
/// Immutable configuration for <see cref="RandomFrameSource"/>.
/// Use object initializer syntax to override individual defaults:
/// <code>
/// var opts = new RandomSourceOptions
/// {
///     FrameCount = 10_000,
///     Mode       = RandomFrameMode.UdpIPv4,
///     Seed       = 42,
/// };
/// </code>
/// </summary>
public sealed class RandomSourceOptions
{
    #region Properties

    /// <summary>
    /// Total number of frames to generate.
    /// Zero means unlimited (source never returns <c>null</c> from <see cref="IFrameSource.NextFrame"/>).
    /// Maximum is <see cref="int.MaxValue"/> frames.
    /// </summary>
    public int FrameCount { get; init; } = 1000;   // frames

    /// <summary>Minimum generated frame size.</summary>
    public int MinFrameSize { get; init; } = 64;    // bytes

    /// <summary>Maximum generated frame size (standard Ethernet MTU default).</summary>
    public int MaxFrameSize { get; init; } = 1518;  // bytes

    /// <summary>Frame generation mode.</summary>
    public RandomFrameMode Mode { get; init; } = RandomFrameMode.FullRandom;

    /// <summary>
    /// Master PRNG seed.
    /// When <c>null</c>, a seed is derived from system entropy at start time.
    /// Providing an explicit seed guarantees a fully reproducible sequence.
    /// </summary>
    public ulong? Seed
    {
        get; init;
    }

    /// <summary>
    /// Timestamp assigned to the first frame.
    /// When <c>null</c>, the source uses the current UTC wall-clock time at <see cref="IFrameSource.Start"/> time.
    /// </summary>
    public Timestamp? BaseTimestamp
    {
        get; init;
    }

    /// <summary>
    /// Interval added to the base timestamp between consecutive frames.
    /// The default of 1,000,000 ns equals 1 ms per frame.
    /// </summary>
    public long TimestampInterval { get; init; } = 1_000_000;   // nanoseconds

    /// <summary>
    /// TCP stream options for <see cref="RandomFrameMode.TcpStreamIPv4"/> and
    /// <see cref="RandomFrameMode.TcpStreamIPv6"/> modes.
    /// Ignored for all other modes.
    /// </summary>
    public TcpStreamOptions TcpStreamOptions { get; init; } = new();

    #endregion

    #region Internal API

    /// <summary>
    /// Validates that option values are consistent.
    /// </summary>
    /// <exception cref="ArgumentException">MinFrameSize exceeds MaxFrameSize.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of the valid range.</exception>
    internal void Validate()
    {
        if (FrameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FrameCount), FrameCount,
                "FrameCount must be non-negative.");
        }

        // int type already prevents exceeding int.MaxValue at the language level

        if (MinFrameSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinFrameSize), MinFrameSize,
                "MinFrameSize must be at least 1.");
        }

        if (MaxFrameSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFrameSize), MaxFrameSize,
                "MaxFrameSize must be at least 1.");
        }

        if (MinFrameSize > MaxFrameSize)
        {
            throw new ArgumentException(
                $"MinFrameSize ({MinFrameSize}) must not exceed MaxFrameSize ({MaxFrameSize}).");
        }

        if (TimestampInterval < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TimestampInterval), TimestampInterval,
                "TimestampInterval must be non-negative.");
        }

        if (Mode is RandomFrameMode.TcpStreamIPv4 or RandomFrameMode.TcpStreamIPv6)
        {
            TcpStreamOptions.Validate();
        }
    }

    #endregion

}

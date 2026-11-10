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
    /// Zero means unlimited: the source does not stop at a count (<see cref="IFrameSource.NextFrame"/> keeps returning
    /// frames until the frame id range is used up, where it throws <see cref="InvalidOperationException"/>). The TCP
    /// stream modes are the exception: they end after the last frame of their layout, with or without a count.
    /// Maximum is <c>Array.MaxLength</c> frames (valid frame indices 0 … <c>Array.MaxLength - 1</c>), the dense frame id
    /// range every source shares.
    /// </summary>
    public int FrameCount { get; init; } = 1000;   // frames

    /// <summary>
    /// Minimum generated frame size. Modes with headers raise the effective minimum to the size of their headers
    /// (for example 42 bytes for <see cref="RandomFrameMode.UdpIPv4"/>: Ethernet 14 + IPv4 20 + UDP 8); the CAN modes and the
    /// TCP stream modes ignore this value because their sizes follow the frame format and <see cref="TcpStreamOptions"/>.
    /// </summary>
    public int MinFrameSize { get; init; } = 64;    // bytes

    /// <summary>
    /// Maximum generated frame size (default 1518: the largest standard Ethernet frame including the 4-byte FCS).
    /// Never below the effective minimum of the mode. The upper bound follows the format of the mode, because the IP and UDP
    /// length fields are 16 bits wide and must not wrap: 65 549 bytes for <see cref="RandomFrameMode.IPv4"/>,
    /// <see cref="RandomFrameMode.UdpIPv4"/> and <see cref="RandomFrameMode.TcpIPv4"/> (Ethernet 14 + IPv4 total length
    /// 65 535), 65 589 bytes for <see cref="RandomFrameMode.IPv6"/>, <see cref="RandomFrameMode.UdpIPv6"/> and
    /// <see cref="RandomFrameMode.TcpIPv6"/> (Ethernet 14 + IPv6 header 40 + payload length 65 535), and <c>Array.MaxLength</c>
    /// for <see cref="RandomFrameMode.FullRandom"/> and <see cref="RandomFrameMode.Ethernet"/> (no length field; one array of
    /// this size is allocated per frame). A larger value is rejected at construction. Ignored (and not range-checked) by the
    /// CAN modes and the TCP stream modes.
    /// </summary>
    public int MaxFrameSize { get; init; } = 1518;  // bytes

    /// <summary>Frame generation mode.</summary>
    public RandomFrameMode Mode { get; init; } = RandomFrameMode.FullRandom;

    /// <summary>
    /// Master PRNG seed.
    /// When <c>null</c>, a seed is derived at start time from the wall clock (millisecond resolution) and the process
    /// id. That varies between runs but is neither cryptographically random nor unique: two sources started in the same
    /// millisecond by one process get the same seed. The generated frames are test data, not secrets.
    /// Providing an explicit seed guarantees a fully reproducible sequence (the same bytes on every CPU, because the
    /// vectorised and scalar byte fills produce identical output).
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
    /// The default of 1,000,000 ns equals 1 ms per frame. Frame <c>i</c> gets <c>BaseTimestamp + i × interval</c>.
    /// <para>
    /// Range: 0 up to <c>long.MaxValue / L</c>, where <c>L</c> is the largest frame index the source can produce
    /// (<see cref="FrameCount"/> − 1, or the largest frame id when <see cref="FrameCount"/> is 0). That bound keeps the
    /// product <c>i × interval</c> inside a <see cref="long"/>; a larger value is rejected at construction with a message
    /// that names the maximum. The sum with <see cref="BaseTimestamp"/> saturates at <see cref="long.MaxValue"/>
    /// (a base close to the end of the range yields a flat run of the maximum timestamp, never a wrapped, negative time).
    /// </para>
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
    /// Validates that option values are consistent. <see cref="RandomFrameSource"/> calls it from its constructor, so
    /// an invalid configuration fails at construction instead of at the first frame. <see cref="TcpStreamOptions"/>
    /// are validated only for the TCP stream modes, the only modes that read them. The class is immutable
    /// (<c>init</c> setters), so a validated instance stays valid.
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

        int maxFrameCount = ArrayIndexIdRange.MaxCount;
        if (FrameCount > maxFrameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FrameCount),
                FrameCount,
                Invariant($"FrameCount must not exceed {maxFrameCount.ToString(CultureInfo.InvariantCulture)} ") +
                Invariant($"(valid frame indices 0..{ArrayIndexIdRange.MaxValue.ToString(CultureInfo.InvariantCulture)})."));
        }

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
                Invariant($"MinFrameSize ({MinFrameSize}) must not exceed MaxFrameSize ({MaxFrameSize})."));
        }

        // Upper bound of the frame size per mode (a fact of the format, hence a constant, not a setting): the IPv4 total
        // length, the IPv6 payload length and the UDP length are 16-bit fields, and a larger frame used to wrap them into a
        // frame that contradicts its own header. Threat: none from outside (the options come from the caller), the check
        // only protects the generated data from being silently wrong. The CAN modes and the TCP stream modes ignore the
        // frame size options, so they are not checked.
        int maxFrameSizeForMode = MaxFrameSizeForMode(Mode);
        if (MaxFrameSize > maxFrameSizeForMode)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFrameSize), MaxFrameSize,
                Invariant($"MaxFrameSize must be at most {maxFrameSizeForMode.ToString(CultureInfo.InvariantCulture)} for mode {Mode} ") +
                "(the 16-bit length fields of the headers must not wrap).");
        }

        if (TimestampInterval < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TimestampInterval), TimestampInterval,
                "TimestampInterval must be non-negative.");
        }

        // The product index × interval must stay inside a long for every frame the source can produce.
        long largestIndex = FrameCount > 0
            ? FrameCount - 1L
            : ArrayIndexIdRange.MaxValue;
        long maxInterval = long.MaxValue / Math.Max(1L, largestIndex);
        if (TimestampInterval > maxInterval)
        {
            string scope = FrameCount > 0
                ? Invariant($"FrameCount {FrameCount}")
                : "an unlimited source";
            throw new ArgumentOutOfRangeException(nameof(TimestampInterval), TimestampInterval,
                Invariant($"TimestampInterval must be at most {maxInterval} ns for {scope} (frame index × interval must fit in 64 bits)."));
        }

        if (Mode is RandomFrameMode.TcpStreamIPv4 or RandomFrameMode.TcpStreamIPv6)
        {
            TcpStreamOptions.Validate();

            // Upper bound of the payload (a fact of the IP header, so a constant, not a setting): the 16-bit IPv4 total
            // length covers the IP header (20), the TCP header (20) and the payload; the 16-bit IPv6 payload length covers
            // the TCP header and the payload. A larger payload used to wrap the length field into a frame that contradicts
            // its own header. The sizes come from the caller's options (no external input), so the check protects the data.
            int maxPayloadForIp = Mode == RandomFrameMode.TcpStreamIPv6
                ? TcpStreamOptions.MaxPayloadSizeIPv6
                : TcpStreamOptions.MaxPayloadSizeIPv4;
            if (TcpStreamOptions.MaxPayloadSize > maxPayloadForIp)
            {
                throw new ArgumentOutOfRangeException(nameof(TcpStreamOptions.MaxPayloadSize), TcpStreamOptions.MaxPayloadSize,
                    Invariant($"TcpStreamOptions.MaxPayloadSize must be at most {maxPayloadForIp} for mode {Mode} (16-bit IP length)."));
            }
        }
    }

    /// <summary>
    /// Largest <see cref="MaxFrameSize"/> a mode accepts: the size at which the 16-bit length field of the mode's headers
    /// still holds the length. <see cref="int.MaxValue"/> for the modes that ignore the frame size options.
    /// </summary>
    internal static int MaxFrameSizeForMode(RandomFrameMode mode) => mode switch
    {
        // Ethernet 14 + IPv4 total length (16 bits): the UDP length (frame - 34) is smaller still.
        RandomFrameMode.IPv4 or RandomFrameMode.UdpIPv4 or RandomFrameMode.TcpIPv4 => 14 + ushort.MaxValue,

        // Ethernet 14 + IPv6 header 40 + payload length (16 bits).
        RandomFrameMode.IPv6 or RandomFrameMode.UdpIPv6 or RandomFrameMode.TcpIPv6 => 14 + 40 + ushort.MaxValue,

        // No length field: the only limit is the largest array one frame needs.
        RandomFrameMode.FullRandom or RandomFrameMode.Ethernet => Array.MaxLength,

        _ => int.MaxValue,
    };

    #endregion

}

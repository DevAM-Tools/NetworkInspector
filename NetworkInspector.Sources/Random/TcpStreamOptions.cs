// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Random;

/// <summary>
/// Configuration for TCP stream generation modes (<see cref="RandomFrameMode.TcpStreamIPv4"/>
/// and <see cref="RandomFrameMode.TcpStreamIPv6"/>).
/// Controls stream count, segments per stream, handshake/teardown phases, and anomalies.
/// </summary>
public sealed class TcpStreamOptions
{
    #region Properties

    /// <summary>Number of concurrent TCP streams to generate.</summary>
    public int StreamCount { get; init; } = 4;

    /// <summary>Number of data segments per stream (in each direction combined).</summary>
    public int SegmentsPerStream { get; init; } = 10;

    /// <summary>Minimum payload size per data segment.</summary>
    public int MinPayloadSize { get; init; } = 20; // bytes

    /// <summary>Maximum payload size per data segment.</summary>
    public int MaxPayloadSize { get; init; } = 1400; // bytes

    /// <summary>Whether to include the 3-way handshake (SYN, SYN-ACK, ACK).</summary>
    public bool IncludeHandshake { get; init; } = true;

    /// <summary>Whether to include the teardown sequence (FIN, ACK, FIN, ACK).</summary>
    public bool IncludeTeardown { get; init; } = true;

    /// <summary>Whether to interleave segments from different streams.</summary>
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
                $"{nameof(MinPayloadSize)} ({MinPayloadSize}) must not exceed {nameof(MaxPayloadSize)} ({MaxPayloadSize}).");
        }
    }

    #endregion
}

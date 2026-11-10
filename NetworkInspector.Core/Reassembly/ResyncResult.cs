// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// The answer of an <see cref="IResyncHeuristic"/>: how many buffered bytes to discard to reach the next sync point, or a
/// failure when none was found. Build it with <see cref="Skip"/> or <see cref="Failure"/>; <c>default</c> is a success
/// that skips 0 bytes, which makes no progress, so a heuristic never returns it.
/// </summary>
public readonly record struct ResyncResult
{
    /// <summary>Number of bytes to skip on success, or -1 when resynchronization failed.</summary>
    public int SkipBytes { get; }

    /// <summary>Whether resynchronization was successful.</summary>
    public bool IsSuccess => SkipBytes >= 0;

    /// <summary>Creates a result with the given skip count; the factories are the only callers and pass -1 or a checked value.</summary>
    /// <param name="skipBytes">Bytes to skip (at least 0), or -1 for a failure.</param>
    private ResyncResult(int skipBytes) => SkipBytes = skipBytes;

    /// <summary>Resynchronization failed — no valid sync point found. <see cref="SkipBytes"/> is -1.</summary>
    public static ResyncResult Failure => new(-1);

    /// <summary>Skip the given number of bytes to resynchronize.</summary>
    /// <param name="bytes">Number of buffered bytes to discard, counted from the start of the data the heuristic saw; not negative.</param>
    /// <returns>A successful result that carries <paramref name="bytes"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="bytes"/> is negative.</exception>
    public static ResyncResult Skip(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        return new(bytes);
    }
}

/// <summary>
/// Heuristic for resynchronizing when PDU boundary detection fails.
/// The stream buffer runs it after the detector reported <see cref="PduBoundaryResult.Invalid"/>, discards the reported
/// number of bytes, and resumes detection there; a failed resync (or a skip beyond the buffered bytes) puts the stream
/// into its error state.
/// </summary>
public interface IResyncHeuristic
{
    /// <summary>Attempts to find a valid sync point in the data.</summary>
    /// <param name="data">The contiguous buffered bytes, starting at the position where detection failed.</param>
    /// <returns>
    /// The number of bytes to discard (counted from the start of <paramref name="data"/>), or a failure when no
    /// sync point is found. A success should make progress: skipping 0 bytes would leave the same bytes in front and the same detection failure would recur.
    /// </returns>
    ResyncResult Resync(ReadOnlySpan<byte> data);
}

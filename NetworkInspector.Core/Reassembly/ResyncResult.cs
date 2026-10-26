// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>Result of resynchronization heuristic.</summary>
public readonly record struct ResyncResult
{
    #region Properties

    /// <summary>Number of bytes to skip on success, or -1 when resynchronization failed.</summary>
    public int SkipBytes { get; }

    /// <summary>Whether resynchronization was successful.</summary>
    public bool IsSuccess => SkipBytes >= 0;

    #endregion

    #region Constructors

    private ResyncResult(int skipBytes) => SkipBytes = skipBytes;

    #endregion

    #region Factory Methods

    /// <summary>Resynchronization failed — no valid sync point found.</summary>
    public static ResyncResult Failure => new(-1);

    /// <summary>Skip the given number of bytes to resynchronize.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="bytes"/> is negative.</exception>
    public static ResyncResult Skip(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        return new(bytes);
    }

    #endregion
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

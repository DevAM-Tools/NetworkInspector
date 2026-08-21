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
/// </summary>
public interface IResyncHeuristic
{
    /// <summary>Attempts to find a valid sync point in the data.</summary>
    ResyncResult Resync(ReadOnlySpan<byte> data);
}

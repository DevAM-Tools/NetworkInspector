// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>Result of resynchronization heuristic.</summary>
public readonly struct ResyncResult
{
    #region Properties

    /// <summary>Number of bytes to skip, or -1 if cannot resync.</summary>
    public int SkipBytes
    {
        get; init;
    }

    /// <summary>Whether resynchronization was successful.</summary>
    public bool IsSuccess => SkipBytes >= 0;

    #endregion

    #region Factory Methods

    /// <summary>Resynchronization failed — no valid sync point found.</summary>
    public static ResyncResult Failure => new() { SkipBytes = -1 };

    /// <summary>Skip the given number of bytes to resynchronize.</summary>
    public static ResyncResult Skip(int bytes) => new() { SkipBytes = bytes };
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

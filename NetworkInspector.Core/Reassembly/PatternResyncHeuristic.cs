// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Pattern-based resync heuristic. Scans for a known byte pattern.
/// <para>
/// <b>Thread-safety:</b> Immutable after construction (pattern is copied defensively);
/// instances are safe to share across threads.
/// </para>
/// </summary>
public sealed class PatternResyncHeuristic : IResyncHeuristic
{
    #region Fields

    private readonly byte[] _Pattern;

    #endregion

    #region Constructors

    /// <summary>Creates a new <see cref="PatternResyncHeuristic"/> that searches for <paramref name="pattern"/>.</summary>
    /// <param name="pattern">The byte sequence to search for. Must be non-null and non-empty. The array is copied defensively.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pattern"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pattern"/> is empty.</exception>
    public PatternResyncHeuristic(byte[] pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length == 0)
        {
            throw new ArgumentException("Pattern must not be empty.", nameof(pattern));
        }
        // Defensive copy: caller-side mutations must not alter the heuristic after construction.
        _Pattern = (byte[])pattern.Clone();
    }

    #endregion

    #region Public API

    /// <inheritdoc/>
    public ResyncResult Resync(ReadOnlySpan<byte> data)
    {
        // Start search from offset 1 (skip current position)
        if (data.Length <= 1)
        {
            return ResyncResult.Failure;
        }

        int idx = data[1..].IndexOf(_Pattern.AsSpan());
        if (idx >= 0)
        {
            return ResyncResult.Skip(idx + 1);
        }
        return ResyncResult.Failure;
    }

    #endregion
}

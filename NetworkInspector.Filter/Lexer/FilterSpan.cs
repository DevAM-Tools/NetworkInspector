// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Lexer;

/// <summary>Half-open character range <c>[Start, Start + Length)</c> inside a filter expression.</summary>
/// <param name="Start">Inclusive start offset.</param>
/// <param name="Length">Length in characters.</param>
internal readonly record struct FilterSpan(int Start, int Length)
{
    #region Properties

    /// <summary>Exclusive end offset.</summary>
    public int End => Start + Length;

    #endregion

    #region Formatting

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Start}..{End})");

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// PCAPNG 4-byte alignment utilities.
/// All block bodies and option values are padded to 32-bit boundaries.
/// </summary>
internal static class PcapPadding
{
    #region Public API

    /// <summary>
    /// Returns the number of padding bytes needed to reach a 4-byte boundary.
    /// For example: PaddingFor(5) → 3, PaddingFor(4) → 0, PaddingFor(0) → 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PaddingFor(int length) => (4 - (length & 3)) & 3;

    /// <summary>
    /// Rounds a length up to the next 4-byte boundary.
    /// For example: PaddedLength(5) → 8, PaddedLength(4) → 4, PaddedLength(0) → 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PaddedLength(int length) => (length + 3) & ~3;

    /// <summary>
    /// Returns the total size of an option TLV entry:
    /// 4 bytes header + padded value length. Returns 0 for empty values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int OptionSize(int valueLength) =>
        valueLength == 0 ? 0 : 4 + PaddedLength(valueLength);

    /// <summary>
    /// Size of the end-of-options marker (code=0, length=0). Always 4 bytes.
    /// </summary>
    internal const int EndOfOptionsSize = 4;

    #endregion
}

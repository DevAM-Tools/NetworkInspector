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
    /// Rounds a PCAPNG <c>block_total_length</c> up to a multiple of 4.
    /// Values within 3 of <see cref="uint.MaxValue"/> cannot be rounded without wrap, so they fail.
    /// </summary>
    /// <param name="blockLength">Leading block length from the file.</param>
    /// <param name="rounded">Rounded length when this method returns true; 0 otherwise.</param>
    /// <returns>True when <paramref name="rounded"/> is a multiple of 4 and not less than <paramref name="blockLength"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryRoundBlockLength(uint blockLength, out uint rounded)
    {
        rounded = 0;
        if (blockLength > uint.MaxValue - 3u)
        {
            return false;
        }

        rounded = (blockLength + 3u) & ~3u;
        return true;
    }

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
    /// Returns the total size of an option TLV entry: 4-byte header plus padded value.
    /// Empty values still occupy the 4-byte header (PCAPNG length 0 is a valid option).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="valueLength"/> is negative.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int OptionSize(int valueLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(valueLength);
        return 4 + PaddedLength(valueLength);
    }

    /// <summary>
    /// Size of the end-of-options marker (code=0, length=0). Always 4 bytes.
    /// </summary>
    internal const int EndOfOptionsSize = 4;

    #endregion
}

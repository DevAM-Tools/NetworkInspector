// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Compact bitmask for tracking which <see cref="FieldId"/> values have been seen.
/// Used by the compact JSON format for field-info deduplication: the first time a field
/// appears, its name, UI name, and type are emitted. Subsequent occurrences omit them.
/// <para>
/// Storage: one bit per field ID in a <c>ulong[]</c> array (64 fields per word).
/// Automatically grows if a field ID exceeds the initial capacity.
/// </para>
/// </summary>
internal sealed class FieldBitmask
{
    private ulong[] _Words;

    /// <summary>Creates a bitmask sized for the given field count.</summary>
    /// <param name="fieldCount">Expected maximum number of fields.</param>
    internal FieldBitmask(int fieldCount)
    {
        _Words = new ulong[(fieldCount + 63) / 64];
    }

    /// <summary>
    /// Inserts a field ID into the bitmask.
    /// Returns <c>true</c> if the field was newly inserted (first occurrence);
    /// <c>false</c> if it was already present.
    /// </summary>
    /// <param name="fieldIdValue">The numeric value of the <see cref="FieldId"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Insert(int fieldIdValue)
    {
        int wordIndex = fieldIdValue >> 6;   // / 64
        int bitIndex = fieldIdValue & 0x3F;  // % 64

        // Grow array if the field ID exceeds current capacity
        if (wordIndex >= _Words.Length)
        {
            Array.Resize(ref _Words, wordIndex + 1);
        }

        ulong mask = 1UL << bitIndex;
        bool wasSet = (_Words[wordIndex] & mask) != 0;
        _Words[wordIndex] |= mask;
        return !wasSet;
    }
}

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
/// <para>
/// Why a bitmask: the compact writer asks "seen before?" for every field of every packet, so the check must not hash
/// or allocate. A bit test and set on a dense word array costs a few instructions and the whole set stays in cache
/// (2048 field ids need 256 bytes). Field ids are array-index ids (dense, non-negative), so the array stays small.
/// Negative ids, including the <c>FieldId.Invalid</c> sentinel (-1), have no bit and are tracked in a small lazily created set.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; owned by one <see cref="JsonExporterState"/> and used from its single
/// caller thread.
/// </para>
/// </summary>
internal sealed class FieldBitmask
{
    /// <summary>The bits, 64 field ids per word. Replaced (not mutated in size) when a larger field id arrives.</summary>
    private ulong[] _Words;

    /// <summary>Negative ids (the <c>FieldId.Invalid</c> sentinel and similar), which have no bit; created on first use.</summary>
    private HashSet<int>? _NegativeIds;

    /// <summary>Creates a bitmask sized for the given field count.</summary>
    /// <param name="fieldCount">
    /// Expected number of field ids (ids <c>0</c> to <c>fieldCount - 1</c>); a sizing hint, because the mask grows on demand.
    /// Must not be negative.
    /// </param>
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
        // A negative id (the FieldId.Invalid sentinel -1) has no bit: the shift would turn it into a negative word index
        // and throw. Such ids are rare and not expected from registered protocols, so they go to a small lazily created set
        // instead of making the common path pay for a wider structure. Rejecting them would make the exporter fail on a
        // field that is otherwise exportable; ignoring them would write the field info on every occurrence.
        if (fieldIdValue < 0)
        {
            _NegativeIds ??= [];
            return _NegativeIds.Add(fieldIdValue);
        }

        int wordIndex = fieldIdValue >> 6;   // / 64
        int bitIndex = fieldIdValue & 0x3F;  // % 64

        // Grow array if the field ID exceeds current capacity. Rare (the initial size covers the registered fields), so
        // growing to exactly the needed word is enough; doubling is not needed for a few words.
        if (wordIndex >= _Words.Length)
        {
            Array.Resize(ref _Words, wordIndex + 1);
        }

        // Test-and-set in one step: the return value is the "first occurrence" signal the compact writer needs.
        ulong mask = 1UL << bitIndex;
        bool wasSet = (_Words[wordIndex] & mask) != 0;
        _Words[wordIndex] |= mask;
        return !wasSet;
    }

    /// <summary>Forgets every inserted id, so each id counts as unseen again. The word array keeps its size.</summary>
    internal void Clear()
    {
        Array.Clear(_Words);
        _NegativeIds?.Clear();
    }
}

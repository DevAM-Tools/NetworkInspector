// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// Builds a <see cref="SignalEnumTable"/> from raw value→name pairs, choosing dense-low,
/// dense-high, or sparse storage under a hard size cap.
/// </summary>
internal static class SignalEnumTableBuilder
{
    #region Public API

    /// <summary>
    /// Classifies and builds an enum table. Returns <see langword="false"/> when the number of
    /// unique keys exceeds <paramref name="maxEnumValues"/> or a key is outside the raw range.
    /// </summary>
    /// <param name="valueNames">Raw→name map (may be null or empty).</param>
    /// <param name="bitLength">Signal bit length (1–64).</param>
    /// <param name="maxEnumValues">Hard cap on unique enum entries.</param>
    /// <param name="table">Built table on success.</param>
    /// <param name="error">Human-readable error on failure.</param>
    internal static bool TryBuild(
        IReadOnlyDictionary<ulong, string>? valueNames,
        int bitLength,
        int maxEnumValues,
        out SignalEnumTable table,
        [NotNullWhen(false)] out string? error)
    {
        table = SignalEnumTable.None;
        error = null;

        if (valueNames is null || valueNames.Count == 0)
        {
            return true;
        }

        if (maxEnumValues <= 0)
        {
            error = "max_enum_values must be greater than zero.";
            return false;
        }

        if (bitLength < 1 || bitLength > 64)
        {
            error = "bit_length must be in the range 1..64.";
            return false;
        }

        if (valueNames.Count > maxEnumValues)
        {
            error = $"Enum value count {valueNames.Count} exceeds max_enum_values ({maxEnumValues}).";
            return false;
        }

        ulong maxRaw = SignalMessageBits.MaxRawForBitLength(bitLength);
        ulong[] keys = new ulong[valueNames.Count];
        int i = 0;
        foreach (KeyValuePair<ulong, string> kvp in valueNames)
        {
            if (kvp.Key > maxRaw)
            {
                error = $"Enum key {kvp.Key} exceeds max raw value {maxRaw} for bit_length {bitLength}.";
                return false;
            }

            if (string.IsNullOrEmpty(kvp.Value))
            {
                error = $"Enum name for key {kvp.Key} must be non-empty.";
                return false;
            }

            keys[i++] = kvp.Key;
        }

        Array.Sort(keys);

        // Deduplicate check (sorted).
        for (int k = 1; k < keys.Length; k++)
        {
            if (keys[k] == keys[k - 1])
            {
                error = $"Duplicate enum key {keys[k]}.";
                return false;
            }
        }

        if (IsDenseLow(keys))
        {
            string[] dense = new string[keys.Length];
            for (int k = 0; k < keys.Length; k++)
            {
                dense[k] = valueNames[keys[k]];
            }

            table = SignalEnumTable.CreateDenseLow(dense);
            return true;
        }

        if (IsDenseHigh(keys, maxRaw))
        {
            string[] dense = new string[keys.Length];
            for (int k = 0; k < keys.Length; k++)
            {
                // keys sorted ascending: first is maxRaw-n+1, last is maxRaw.
                // Dense-high index = maxRaw - raw → reverse into array.
                ulong raw = keys[k];
                int index = (int)(maxRaw - raw);
                dense[index] = valueNames[raw];
            }

            table = SignalEnumTable.CreateDenseHigh(dense, maxRaw);
            return true;
        }

        Dictionary<ulong, string> sparse = new(valueNames.Count);
        foreach (KeyValuePair<ulong, string> kvp in valueNames)
        {
            sparse[kvp.Key] = kvp.Value;
        }

        table = SignalEnumTable.CreateSparse(sparse.ToFrozenDictionary());
        return true;
    }

    #endregion

    #region Classification

    /// <summary>True when keys are exactly 0,1,...,n-1.</summary>
    internal static bool IsDenseLow(ulong[] sortedKeys)
    {
        for (int i = 0; i < sortedKeys.Length; i++)
        {
            if (sortedKeys[i] != (ulong)i)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when keys are exactly (maxRaw-n+1)..maxRaw contiguous.
    /// Example: 4-bit, n=2 → keys {14,15}.
    /// </summary>
    internal static bool IsDenseHigh(ulong[] sortedKeys, ulong maxRaw)
    {
        int n = sortedKeys.Length;
        if (n == 0)
        {
            return false;
        }

        // Smallest expected key = maxRaw - n + 1 (checked arithmetic for n relative to maxRaw).
        if ((ulong)n - 1UL > maxRaw)
        {
            // Range cannot fit; only possible if keys cover entire domain which is dense-low.
            return false;
        }

        ulong expectedFirst = maxRaw - (ulong)n + 1UL;
        if (sortedKeys[0] != expectedFirst)
        {
            return false;
        }

        for (int i = 1; i < n; i++)
        {
            if (sortedKeys[i] != expectedFirst + (ulong)i)
            {
                return false;
            }
        }

        return sortedKeys[n - 1] == maxRaw;
    }

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// Builds a <see cref="SignalEnumTable"/> from raw value→name pairs, choosing dense-low,
/// dense-high, or sparse storage under a hard size cap.
/// <para>
/// Runs at registration time only (once per signal), never while parsing, so it favours clear validation over speed.
/// The input comes from an operator-supplied JSON config (a trust boundary), which is why every key, name and count is
/// validated and why a failure is reported through <c>error</c> instead of an exception.
/// </para>
/// <para>Thread safety: static and stateless; safe to call concurrently.</para>
/// </summary>
internal static class SignalEnumTableBuilder
{
    #region Public API

    /// <summary>
    /// Classifies and builds an enum table. Returns <see langword="false"/> when the number of
    /// unique keys exceeds <paramref name="maxEnumValues"/> or a key is outside the raw range.
    /// <para>
    /// Limit record for <paramref name="maxEnumValues"/>: the threat is a hostile or broken config whose
    /// <c>value_names</c> map would make registration allocate without bound. The value is the
    /// <c>signal_message.max_enum_values</c> setting (configurable by the operator, default 4096, see
    /// <c>SignalMessageRegistration</c>); exceeding it fails the signal with a message that names the limit, nothing is
    /// truncated.
    /// </para>
    /// </summary>
    /// <param name="valueNames">Raw→name map (may be null or empty; empty yields the empty table and is checked first).</param>
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
        // No names is the common case and always valid, so it is answered before the arguments are validated.
        table = SignalEnumTable.None;
        error = null;

        if (valueNames is null || valueNames.Count == 0)
        {
            return true;
        }

        // Validate the arguments and the size of the map before touching any entry, so the cost of a rejected
        // config is bounded by the cheap checks and not by the number of entries.
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

        // Check every key against the raw range of the signal (a key above it could never match and indicates a
        // config mistake) and every name for emptiness, collecting the keys for classification.
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

        // Sorting makes both the duplicate check and the dense classification a single linear pass. A standard
        // dictionary cannot hold duplicate keys, so the duplicate check is defensive (the parameter is only an
        // IReadOnlyDictionary).
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

        // Classify, cheapest layout first. Dense-low is tested before dense-high, so a map that covers the whole raw
        // range (which satisfies both) becomes dense-low. Anything else, including a run with a gap, is sparse.
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

        // Sparse fallback: a frozen dictionary is built once here and then read concurrently without locking.
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
    /// <param name="sortedKeys">Distinct keys in ascending order.</param>
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
    /// <param name="sortedKeys">Distinct keys in ascending order, each at most <paramref name="maxRaw"/>.</param>
    /// <param name="maxRaw">Largest raw value of the signal (<c>(1 &lt;&lt; bitLength) - 1</c>).</param>
    internal static bool IsDenseHigh(ulong[] sortedKeys, ulong maxRaw)
    {
        int n = sortedKeys.Length;
        if (n == 0)
        {
            return false;
        }

        // Smallest expected key = maxRaw - n + 1. The guard keeps the unsigned subtraction below from wrapping. For
        // distinct keys that are all at most maxRaw it cannot trigger (n is then at most maxRaw + 1), so it only
        // protects direct callers that pass inconsistent arguments.
        if ((ulong)n - 1UL > maxRaw)
        {
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

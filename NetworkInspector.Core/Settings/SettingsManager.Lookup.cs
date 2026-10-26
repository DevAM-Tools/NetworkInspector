// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>Lookup by exact name and "did you mean" suggestions (part of <see cref="SettingsManager"/>).</summary>
public sealed partial class SettingsManager
{
    #region Lookup

    /// <summary>Looks up a registered setting by its exact (ordinal) name.</summary>
    /// <param name="name">The setting name; names are case-sensitive.</param>
    /// <param name="setting">The setting; <see langword="null"/> when no setting has this name.</param>
    /// <returns><see langword="true"/> when a setting with this name is registered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public bool TryGetSetting(string name, [NotNullWhen(true)] out Setting? setting)
    {
        ArgumentNullException.ThrowIfNull(name);

        _Lock.EnterReadLock();
        try
        {
            return _SettingsByName.TryGetValue(name, out setting);
        }
        finally
        {
            _Lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Up to <paramref name="max"/> registered names that are similar to <paramref name="name"/>, for "did you mean"
    /// suggestions. Order: names that start with the query (ignoring case) first; then names within the edit distance
    /// max(2, length / 4) of the query (ignoring case), nearest first; then names whose last segment (after the last dot)
    /// contains the last segment of the query (ignoring case). Ties go by ordinal name. A name appears once, at its best rank.
    /// </summary>
    /// <param name="name">The mistyped name.</param>
    /// <param name="max">Largest number of names to return; at least 1.</param>
    /// <returns>The suggestions, possibly none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="max"/> is less than 1.</exception>
    public IReadOnlyList<string> FindSimilar(string name, int max)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);

        // Copy the names under the read lock and do the ranking after releasing it: the edit-distance work is proportional to
        // the number of settings and must not hold the lock. This runs on error paths ("did you mean"), not per packet, so the
        // copies and the per-candidate row arrays in _EditDistance are accepted.
        string[] names;
        _Lock.EnterReadLock();
        try
        {
            names = [.. _SettingsByName.Keys];
        }
        finally
        {
            _Lock.ExitReadLock();
        }

        // Rank every name once: tier 0 prefix, tier 1 near by edit distance, tier 2 last segment. The sort key is
        // (tier, distance, ordinal name), so ties are deterministic.
        int allowedDistance = Math.Max(2, name.Length / 4);
        string queryTail = _LastSegment(name);
        List<(int Tier, int Distance, string Name)> ranked = [];
        foreach (string candidate in names)
        {
            if (candidate.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                ranked.Add((0, 0, candidate));
                continue;
            }

            int distance = _EditDistance(name, candidate, allowedDistance);
            if (distance <= allowedDistance)
            {
                ranked.Add((1, distance, candidate));
                continue;
            }

            if (queryTail.Length > 0 && _LastSegment(candidate).Contains(queryTail, StringComparison.OrdinalIgnoreCase))
            {
                ranked.Add((2, 0, candidate));
            }
        }

        // Sort by the full key, ending in the ordinal name: List.Sort is not stable, and the tie-break makes the result the same
        // on every run and platform (callers print the list and tests compare it).
        ranked.Sort(static (left, right) =>
        {
            int byTier = left.Tier.CompareTo(right.Tier);
            if (byTier != 0)
            {
                return byTier;
            }

            int byDistance = left.Distance.CompareTo(right.Distance);
            return byDistance != 0
                ? byDistance
                : string.CompareOrdinal(left.Name, right.Name);
        });

        List<string> result = new(Math.Min(max, ranked.Count));
        for (int i = 0; i < ranked.Count && result.Count < max; i++)
        {
            result.Add(ranked[i].Name);
        }

        return result;
    }

    #endregion

    #region Private Helpers

    /// <summary>The part of a name after the last dot (the whole name when it has no dot).</summary>
    private static string _LastSegment(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0
            ? name
            : name[(dot + 1)..];
    }

    /// <summary>
    /// Levenshtein distance ignoring case, with an early exit: the result is only exact up to <paramref name="limit"/>; a larger
    /// distance may be reported as <c>limit + 1</c>. Names are short, so two rows of integers are enough.
    /// </summary>
    private static int _EditDistance(string left, string right, int limit)
    {
        // The distance is at least the difference of the lengths, so such a pair is rejected without touching a character.
        if (Math.Abs(left.Length - right.Length) > limit)
        {
            return limit + 1;
        }

        // Classic two-row dynamic programming (memory O(right.Length) instead of a full matrix); the rows swap each iteration.
        // Case is ignored per character with the invariant lower-case mapping; no strings are allocated for the comparison.
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];
        for (int j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            int rowMinimum = current[0];
            char leftChar = char.ToLowerInvariant(left[i - 1]);
            for (int j = 1; j <= right.Length; j++)
            {
                int substitution = previous[j - 1] + (leftChar == char.ToLowerInvariant(right[j - 1]) ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
                rowMinimum = Math.Min(rowMinimum, current[j]);
            }

            // Every later row is at least this row's minimum, so a row above the limit ends the search.
            if (rowMinimum > limit)
            {
                return limit + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Completion;

/// <summary>
/// Every name a filter expression can bind on one stack — protocols, canonical fields and alias groups — sorted
/// for prefix lookup, and the closest-name search behind "Did you mean …?".
/// <para>
/// <b>One entry per name.</b> Nearly every protocol also registers a container field with its own name (protocol
/// <c>udp</c>, field <c>udp</c>). The catalog keeps the kind the <see cref="SymbolResolver"/> would bind:
/// protocol, then field, then alias.
/// </para>
/// <para>
/// <b>Order.</b> Ordinal ignoring case, then ordinal, so <c>StartsWith(prefix, OrdinalIgnoreCase)</c> matches form
/// one contiguous run that a binary search finds.
/// </para>
/// <para>
/// <b>Caching and thread-safety.</b> A stack's metadata does not change after it is built, so one catalog per stack
/// instance is built on first use and kept for the stack's lifetime (<see cref="ConditionalWeakTable{TKey, TValue}"/>,
/// no strong reference to the stack). The catalog is immutable after construction; concurrent readers are safe.
/// </para>
/// </summary>
internal sealed class FilterNameCatalog
{
    #region Fields

    private static readonly ConditionalWeakTable<IStack, FilterNameCatalog> _ByStack = [];

    /// <summary>Sort order of the catalog and of completion lists.</summary>
    internal static readonly Comparison<string> NameOrder = static (left, right) =>
    {
        int ignoringCase = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return ignoringCase != 0
            ? ignoringCase
            : string.CompareOrdinal(left, right);
    };

    private readonly FilterCompletionItem[] _Entries;

    #endregion

    #region Construction

    private FilterNameCatalog(FilterCompletionItem[] entries)
    {
        _Entries = entries;
    }

    /// <summary>Returns the catalog of <paramref name="stack"/>, building it on first use.</summary>
    public static FilterNameCatalog For(IStack stack)
    {
        ArgumentNullException.ThrowIfNull(stack);

        return _ByStack.GetValue(stack, static target => new FilterNameCatalog(_Build(target)));
    }

    private static FilterCompletionItem[] _Build(IStack stack)
    {
        // Resolver priority: protocol beats field beats alias. Insert in that order and keep the first entry.
        Dictionary<string, FilterCompletionItem> byName = new(StringComparer.Ordinal);

        foreach (ProtocolInfo protocol in stack.Protocols.Span)
        {
            _ = byName.TryAdd(protocol.Name, new FilterCompletionItem(protocol.Name, FilterCompletionKind.Protocol, FieldType.None));
        }

        foreach (FieldInfo field in stack.Fields.Span)
        {
            _ = byName.TryAdd(field.Name, new FilterCompletionItem(field.Name, FilterCompletionKind.Field, field.FieldType));
        }

        foreach (FieldAliasGroupInfo alias in stack.FieldAliasGroups.Span)
        {
            _ = byName.TryAdd(alias.Name, new FilterCompletionItem(alias.Name, FilterCompletionKind.FieldAlias, _AliasType(stack, alias)));
        }

        FilterCompletionItem[] entries = [.. byName.Values];
        Array.Sort(entries, static (left, right) => NameOrder(left.Text, right.Text));
        return entries;
    }

    /// <summary>The members' common type, or <see cref="FieldType.None"/> when they differ or are unknown.</summary>
    private static FieldType _AliasType(IStack stack, FieldAliasGroupInfo alias)
    {
        FieldType common = FieldType.None;
        foreach (FieldId member in alias.Members.Span)
        {
            FieldType memberType = stack.GetField(member)?.FieldType ?? FieldType.None;
            if (common == FieldType.None)
            {
                common = memberType;
            }
            else if (common != memberType)
            {
                return FieldType.None;
            }
        }

        return common;
    }

    #endregion

    #region Prefix lookup

    /// <summary>
    /// Appends every entry whose name starts with <paramref name="prefix"/> (ignoring case) and whose kind passes
    /// <paramref name="valueSourcesOnly"/> — protocols have no value, so value positions skip them.
    /// </summary>
    public void AddMatches(string prefix, bool valueSourcesOnly, List<FilterCompletionItem> destination)
    {
        // First entry not ordered before the prefix; the matches form one contiguous run from there.
        int low = 0;
        int high = _Entries.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (string.Compare(_Entries[middle].Text, prefix, StringComparison.OrdinalIgnoreCase) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (int i = low; i < _Entries.Length; i++)
        {
            FilterCompletionItem entry = _Entries[i];
            if (!entry.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (valueSourcesOnly && entry.Kind == FilterCompletionKind.Protocol)
            {
                continue;
            }

            destination.Add(entry);
        }
    }

    #endregion

    #region Closest name

    /// <summary>
    /// Returns the known name closest to <paramref name="name"/> by edit distance (insert, delete, substitute,
    /// swap of two neighbours; ignoring case), or <see langword="null"/> when nothing is near enough. The allowed
    /// distance grows with the length — 1 up to 4 characters, 2 up to 10, else 3 — so short names are not
    /// "corrected" into unrelated ones. Ties go to the alphabetically first name. Runs only on the error path.
    /// </summary>
    public string? Suggest(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        int allowed = name.Length switch
        {
            <= 4 => 1,
            <= 10 => 2,
            _ => 3,
        };

        string? best = null;
        int bestDistance = allowed + 1;
        foreach (FilterCompletionItem entry in _Entries)
        {
            // The length difference is a lower bound of the distance; skip what cannot win.
            if (Math.Abs(entry.Text.Length - name.Length) >= bestDistance)
            {
                continue;
            }

            int distance = _Distance(name, entry.Text, bestDistance);
            if (distance < bestDistance && !string.Equals(entry.Text, name, StringComparison.Ordinal))
            {
                best = entry.Text;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// Optimal-string-alignment distance between two names, ignoring case. Returns <paramref name="cutoff"/> as
    /// soon as every cell of a row reaches it, because the caller only wants distances below the cutoff.
    /// </summary>
    private static int _Distance(string left, string right, int cutoff)
    {
        // Three rolling rows: two-back (for the swap), previous, current. Names are short; rent for long ones.
        int width = right.Length + 1;
        int[]? rented = width * 3 > 384 ? System.Buffers.ArrayPool<int>.Shared.Rent(width * 3) : null;
        Span<int> rows = rented is null ? stackalloc int[width * 3] : rented.AsSpan(0, width * 3);
        try
        {
            Span<int> twoBack = rows[..width];
            Span<int> previous = rows.Slice(width, width);
            Span<int> current = rows.Slice(width * 2, width);
            for (int j = 0; j < width; j++)
            {
                previous[j] = j;
            }

            // A swap reads two rows back, so stop only when the last two rows both reached the cutoff.
            int previousMinimum = 0;
            for (int i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                int rowMinimum = i;
                char leftChar = char.ToUpperInvariant(left[i - 1]);
                for (int j = 1; j < width; j++)
                {
                    char rightChar = char.ToUpperInvariant(right[j - 1]);
                    int cost = leftChar == rightChar ? 0 : 1;
                    int value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                    if (i > 1 && j > 1
                        && leftChar == char.ToUpperInvariant(right[j - 2])
                        && char.ToUpperInvariant(left[i - 2]) == rightChar)
                    {
                        value = Math.Min(value, twoBack[j - 2] + 1);
                    }

                    current[j] = value;
                    rowMinimum = Math.Min(rowMinimum, value);
                }

                if (rowMinimum >= cutoff && previousMinimum >= cutoff)
                {
                    return cutoff;
                }

                previousMinimum = rowMinimum;

                // Rotate: two-back ← previous ← current; the old two-back row is overwritten next.
                Span<int> recycled = twoBack;
                twoBack = previous;
                previous = current;
                current = recycled;
            }

            return previous[width - 1];
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Metadata for an enum setting including allowed values and bidirectional lookups.
/// Case-insensitive name lookup is supported (ordinal, not culture-sensitive, so the result does not depend on the
/// user's locale).
/// <para>
/// Thread-safety: immutable after construction. Both lookup dictionaries are filled in the constructor and never
/// modified, so concurrent readers need no synchronization. The two dictionaries index into one array of values: a
/// lookup is O(1) by number or by name without scanning <see cref="AllowedValues"/>.
/// </para>
/// </summary>
public sealed class EnumSettingMetadata
{
    #region Fields

    private readonly Dictionary<ulong, int> _NumericToIndex;
    private readonly Dictionary<string, int> _NameToIndex;

    // The private array backs the O(1) lookups; AllowedValues exposes it only through a read-only wrapper that is created
    // once, so callers can neither cast the public list back to the array and mutate it nor cause an allocation per call.
    private readonly EnumSettingValue[] _Values;

    #endregion

    #region Constructors

    /// <summary>Creates new enum metadata from allowed values.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="allowedValues"/> is null.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when duplicate numeric or name values are present.</exception>
    public EnumSettingMetadata(IReadOnlyList<EnumSettingValue> allowedValues)
    {
        ArgumentNullException.ThrowIfNull(allowedValues);

        // Copy into a private array: the caller's list may change later, and the metadata must stay immutable. Names are
        // unique ignoring case and numbers are unique, so every lookup has exactly one answer; a duplicate is a
        // configuration bug and is reported at construction instead of silently shadowing an entry.
        EnumSettingValue[] values = new EnumSettingValue[allowedValues.Count];
        _NumericToIndex = new Dictionary<ulong, int>(allowedValues.Count);
        _NameToIndex = new Dictionary<string, int>(allowedValues.Count, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < allowedValues.Count; i++)
        {
            EnumSettingValue entry = allowedValues[i];
            if (_NumericToIndex.ContainsKey(entry.NumericValue))
            {
                throw ValidationSettingsException.For(
                    $"Duplicate numeric enum value {entry.NumericValue} for name '{entry.Name}'");
            }
            if (_NameToIndex.ContainsKey(entry.Name))
            {
                throw ValidationSettingsException.For(
                    $"Duplicate enum name '{entry.Name}'");
            }

            values[i] = entry;
            _NumericToIndex[entry.NumericValue] = i;
            _NameToIndex[entry.Name] = i;
        }

        _Values = values;
        AllowedValues = Array.AsReadOnly(values);
    }

    #endregion

    #region Public API

    /// <summary>Creates enum metadata from name-value pairs.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pairs"/> is null.</exception>
    public static EnumSettingMetadata FromPairs(IEnumerable<(string Name, ulong Value)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        List<EnumSettingValue> values = [];
        foreach ((string name, ulong value) in pairs)
        {
            values.Add(new EnumSettingValue(name, value));
        }
        return new EnumSettingMetadata(values);
    }

    /// <summary>Returns the list of allowed enum values.</summary>
    public IReadOnlyList<EnumSettingValue> AllowedValues { get; }

    /// <summary>Tries to get an enum value by its numeric representation.</summary>
    public EnumSettingValue? GetByNumeric(ulong value)
    {
        if (_NumericToIndex.TryGetValue(value, out int index))
        {
            return _Values[index];
        }
        return null;
    }

    /// <summary>Tries to get an enum value by its name (case-insensitive).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null (the validation is left to the dictionary lookup).</exception>
    public EnumSettingValue? GetByName(string name)
    {
        if (_NameToIndex.TryGetValue(name, out int index))
        {
            return _Values[index];
        }
        return null;
    }

    /// <summary>Checks if a numeric value is allowed.</summary>
    public bool IsAllowedNumeric(ulong value) => _NumericToIndex.ContainsKey(value);

    /// <summary>Checks if a name is allowed (case-insensitive).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null (the validation is left to the dictionary lookup).</exception>
    public bool IsAllowedName(string name) => _NameToIndex.ContainsKey(name);

    #endregion
}

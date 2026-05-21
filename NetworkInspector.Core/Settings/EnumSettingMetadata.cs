// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Metadata for an enum setting including allowed values and bidirectional lookups.
/// Case-insensitive name lookup is supported.
/// </summary>
public sealed class EnumSettingMetadata
{
    #region Fields

    private readonly EnumSettingValue[] _AllowedValues;
    private readonly Dictionary<ulong, int> _NumericToIndex;
    private readonly Dictionary<string, int> _NameToIndex;

    #endregion

    #region Constructors

    /// <summary>Creates new enum metadata from allowed values.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="allowedValues"/> is null.</exception>
    public EnumSettingMetadata(IReadOnlyList<EnumSettingValue> allowedValues)
    {
        ArgumentNullException.ThrowIfNull(allowedValues);
        _AllowedValues = new EnumSettingValue[allowedValues.Count];
        _NumericToIndex = new Dictionary<ulong, int>(allowedValues.Count);
        _NameToIndex = new Dictionary<string, int>(allowedValues.Count, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < allowedValues.Count; i++)
        {
            _AllowedValues[i] = allowedValues[i];
            _NumericToIndex[allowedValues[i].NumericValue] = i;
            _NameToIndex[allowedValues[i].Name] = i;
        }
    }

    #endregion

    #region Public API

    /// <summary>Creates enum metadata from name-value pairs.</summary>
    public static EnumSettingMetadata FromPairs(IEnumerable<(string Name, ulong Value)> pairs)
    {
        List<EnumSettingValue> values = [];
        foreach ((string name, ulong value) in pairs)
        {
            values.Add(new EnumSettingValue(name, value));
        }
        return new EnumSettingMetadata(values);
    }

    /// <summary>Returns the list of allowed enum values.</summary>
    public IReadOnlyList<EnumSettingValue> AllowedValues => _AllowedValues;

    /// <summary>Tries to get an enum value by its numeric representation.</summary>
    public EnumSettingValue? GetByNumeric(ulong value) =>
        _NumericToIndex.TryGetValue(value, out int index) ? _AllowedValues[index] : null;

    /// <summary>Tries to get an enum value by its name (case-insensitive).</summary>
    public EnumSettingValue? GetByName(string name) =>
        _NameToIndex.TryGetValue(name, out int index) ? _AllowedValues[index] : null;

    /// <summary>Checks if a numeric value is allowed.</summary>
    public bool IsAllowedNumeric(ulong value) => _NumericToIndex.ContainsKey(value);

    /// <summary>Checks if a name is allowed (case-insensitive).</summary>
    public bool IsAllowedName(string name) => _NameToIndex.ContainsKey(name);

    #endregion
}

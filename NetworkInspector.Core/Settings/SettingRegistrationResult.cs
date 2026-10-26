// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Result of registering a setting, containing both the load result and the current value.
/// </summary>
/// <param name="LoadResult">What happened to the persisted or preloaded value of the setting, if there was one.</param>
/// <param name="Value">The current value of the setting right after registration (the default when no value was applied).</param>
public readonly record struct SettingRegistrationResult(SettingLoadResult LoadResult, SettingValue Value)
{
    #region Properties

    /// <summary>Returns true if a persisted value was successfully loaded.</summary>
    public bool WasLoaded => LoadResult == SettingLoadResult.Success;

    /// <summary>
    /// Returns true if no persisted value existed for the setting. This is not the same as "the value is the default": a
    /// persisted value that was refused (type mismatch, decoding error, out of range) leaves the default in place but reports
    /// <see langword="false"/> here, so the caller can tell a damaged file from a fresh install via <see cref="LoadResult"/>.
    /// </summary>
    public bool IsDefault => LoadResult == SettingLoadResult.NoPersistedValue;

    #endregion

    #region TryGetAs* — type check + value extraction in a single operation

    /// <summary>Returns <see langword="true"/> if the value is a <see cref="SettingType.Bool"/> and writes it into <paramref name="value"/>.</summary>
    public bool TryGetAsBool(out bool value) => Value.TryGetAsBool(out value);

    /// <summary>Returns <see langword="true"/> if the value is a <see cref="SettingType.String"/> and writes it into <paramref name="value"/>.</summary>
    public bool TryGetAsString(out string value) => Value.TryGetAsString(out value);

    /// <summary>Returns <see langword="true"/> if the value is an <see cref="SettingType.F64"/> and writes it into <paramref name="value"/>.</summary>
    public bool TryGetAsF64(out double value) => Value.TryGetAsF64(out value);

    /// <summary>Returns <see langword="true"/> if the value is a <see cref="SettingType.U64"/> and writes it into <paramref name="value"/>.</summary>
    public bool TryGetAsU64(out ulong value) => Value.TryGetAsU64(out value);

    /// <summary>Returns <see langword="true"/> if the value is an <see cref="SettingType.I64"/> and writes it into <paramref name="value"/>.</summary>
    public bool TryGetAsI64(out long value) => Value.TryGetAsI64(out value);

    /// <summary>Returns <see langword="true"/> if the value is a <see cref="SettingType.Bytes"/>
    /// and writes a defensive copy into <paramref name="value"/>.</summary>
    public bool TryGetAsBytes(out byte[] value) => Value.TryGetAsBytes(out value);

    /// <summary>Returns <see langword="true"/> if the value is an <see cref="SettingType.Enum"/>
    /// and writes the name and numeric value into <paramref name="value"/>.</summary>
    public bool TryGetAsEnum(out (string Name, ulong Value) value) => Value.TryGetAsEnum(out value);

    /// <summary>Returns <see langword="true"/> if the value is a <see cref="SettingType.BoolArray"/>
    /// and writes a defensive copy into <paramref name="value"/>.</summary>
    public bool TryGetAsBoolArray(out bool[] value) => Value.TryGetAsBoolArray(out value);

    /// <summary>Returns <see langword="true"/> if the value is a <see cref="SettingType.StringArray"/>
    /// and writes a defensive copy into <paramref name="value"/>.</summary>
    public bool TryGetAsStringArray(out string[] value) => Value.TryGetAsStringArray(out value);

    /// <summary>Returns <see langword="true"/> if the value is an <see cref="SettingType.F64Array"/>
    /// and writes a defensive copy into <paramref name="value"/>.</summary>
    public bool TryGetAsF64Array(out double[] value) => Value.TryGetAsF64Array(out value);

    /// <summary>Returns <see langword="true"/> if the value is a <see cref="SettingType.U64Array"/>
    /// and writes a defensive copy into <paramref name="value"/>.</summary>
    public bool TryGetAsU64Array(out ulong[] value) => Value.TryGetAsU64Array(out value);

    /// <summary>Returns <see langword="true"/> if the value is an <see cref="SettingType.I64Array"/>
    /// and writes a defensive copy into <paramref name="value"/>.</summary>
    public bool TryGetAsI64Array(out long[] value) => Value.TryGetAsI64Array(out value);

    #endregion
}

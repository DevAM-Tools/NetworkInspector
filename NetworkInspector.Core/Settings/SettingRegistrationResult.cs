// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Result of registering a setting, containing both the load result and the current value.
/// </summary>
/// <remarks>Creates a new registration result.</remarks>
public readonly struct SettingRegistrationResult(SettingLoadResult loadResult, SettingValue value)
{
    #region Properties

    /// <summary>The result of attempting to load a persisted value.</summary>
    public SettingLoadResult LoadResult { get; } = loadResult;

    /// <summary>
    /// The current value of the setting after registration.
    /// This is either the persisted value (if loaded) or the default value.
    /// </summary>
    public SettingValue Value { get; } = value;

    /// <summary>Returns true if a persisted value was successfully loaded.</summary>
    public bool WasLoaded => LoadResult == SettingLoadResult.Success;

    /// <summary>Returns true if the setting is using its default value.</summary>
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

    #endregion
}
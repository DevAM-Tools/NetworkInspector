// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Read-only view of a single registered setting.
/// <para>
/// Exposes all metadata and value accessors without allowing mutation
/// (no <see cref="Setting.SetPendingValue"/>, <see cref="Setting.Apply"/>,
/// or <see cref="Setting.Reset"/>).
/// Returned as <see cref="ReadOnlySettingView"/> by <see cref="IReadOnlySettingsManager"/>
/// so consumers cannot modify settings through the read-only surface.
/// </para>
/// <para>
/// Thread-safety: value reads are lock-free and safe from any thread (they read one immutable snapshot of the owning
/// setting); the metadata properties never change after registration. Array and byte accessors return defensive copies,
/// so a caller can neither mutate the stored value nor observe a later change through a returned array.
/// </para>
/// </summary>
public interface IReadOnlySetting
{
    #region Metadata

    /// <summary>Machine-readable name (e.g., "app.check_updates").</summary>
    string Name
    {
        get;
    }

    /// <summary>Human-readable display name.</summary>
    string UiName
    {
        get;
    }

    /// <summary>Optional description.</summary>
    string? Description
    {
        get;
    }

    /// <summary>Group name for UI organization.</summary>
    string GroupName
    {
        get;
    }

    /// <summary>The setting value type.</summary>
    SettingType Type
    {
        get;
    }

    /// <summary>The default value.</summary>
    SettingValue DefaultValue
    {
        get;
    }

    /// <summary>Optional minimum value (for numeric types).</summary>
    SettingValue? MinValue
    {
        get;
    }

    /// <summary>Optional maximum value (for numeric types).</summary>
    SettingValue? MaxValue
    {
        get;
    }

    /// <summary>Enum metadata, if this is an enum setting.</summary>
    EnumSettingMetadata? EnumMetadata
    {
        get;
    }

    /// <summary>How a change of the setting takes effect; informational for the application.</summary>
    SettingActivation Activation
    {
        get;
    }

    /// <summary>
    /// The definition of the limit this setting stands for (key, unit, range, fixed default, flags, end-user texts), or
    /// <see langword="null"/> when the setting is not a <see cref="SettingType.Limit"/>.
    /// </summary>
    LimitDefinition? LimitDefinition
    {
        get;
    }

    /// <summary>
    /// The read side of the live holder of the limit, for hot code that keeps it and reads <see cref="Limit.Ceiling"/>; the same
    /// object for the life of the setting, with no way to change the value. <see langword="null"/> when the setting is not a
    /// <see cref="SettingType.Limit"/>.
    /// </summary>
    Limit? LiveLimit
    {
        get;
    }

    #endregion

    #region Current Value

    /// <summary>Gets the current (applied) value. Lock-free.</summary>
    SettingValue Value
    {
        get;
    }

    /// <summary>
    /// Gets the pending value (may differ from current before Apply). Lock-free. Each of <see cref="Value"/> and
    /// this property reads the latest published snapshot on its own, so two separate reads can straddle an update;
    /// use <see cref="IsDirty"/> when a consistent current-versus-pending comparison is needed.
    /// </summary>
    SettingValue PendingValue
    {
        get;
    }

    /// <summary>Returns true if the pending value differs from the current value. Lock-free.</summary>
    bool IsDirty
    {
        get;
    }

    #endregion

    #region TryGetAs* — type check + value extraction in a single operation

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.Bool"/>
    /// and writes it into <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    bool TryGetAsBool(out bool value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.String"/>
    /// and writes it into <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    bool TryGetAsString(out string value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.F64"/>
    /// and writes it into <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    bool TryGetAsF64(out double value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.U64"/>
    /// and writes it into <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    bool TryGetAsU64(out ulong value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.I64"/>
    /// and writes it into <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    bool TryGetAsI64(out long value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.Limit"/>
    /// and writes it into <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    bool TryGetAsLimit(out LimitValue value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.Bytes"/>
    /// and writes a <em>defensive copy</em> of the byte array into <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    bool TryGetAsBytes(out byte[] value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.Enum"/>
    /// and writes the enum name and numeric value into <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    bool TryGetAsEnum(out (string Name, ulong Value) value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.BoolArray"/>
    /// and writes a <em>defensive copy</em> of the boolean array into <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    bool TryGetAsBoolArray(out bool[] value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.StringArray"/>
    /// and writes a <em>defensive copy</em> of the string array into <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    bool TryGetAsStringArray(out string[] value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.F64Array"/>
    /// and writes a <em>defensive copy</em> of the double array into <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    bool TryGetAsF64Array(out double[] value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.U64Array"/>
    /// and writes a <em>defensive copy</em> of the ulong array into <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    bool TryGetAsU64Array(out ulong[] value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.I64Array"/>
    /// and writes a <em>defensive copy</em> of the long array into <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    bool TryGetAsI64Array(out long[] value);

    #endregion
}

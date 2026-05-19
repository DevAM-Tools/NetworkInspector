// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Read-only view of a single registered setting.
/// <para>
/// Exposes all metadata and value accessors without allowing mutation
/// (no <see cref="Setting.SetPendingValue"/>, <see cref="Setting.Apply"/>,
/// or <see cref="Setting.Reset"/>).
/// Returned by <see cref="IReadOnlySettingsManager"/> so consumers cannot
/// modify settings through the read-only surface.
/// </para>
/// </summary>
public interface IReadOnlySetting
{
    #region Metadata

    /// <summary>Machine-readable name (e.g., "tcp.check_checksum").</summary>
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

    #endregion

    #region Current Value

    /// <summary>Gets the current (applied) value. Lock-free.</summary>
    SettingValue Value
    {
        get;
    }

    /// <summary>Gets the pending value (may differ from current before Apply). Lock-free.</summary>
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
    bool TryGetAsBool(out bool value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.String"/>
    /// and writes it into <paramref name="value"/>.
    /// </summary>
    bool TryGetAsString(out string value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.F64"/>
    /// and writes it into <paramref name="value"/>.
    /// </summary>
    bool TryGetAsF64(out double value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.U64"/>
    /// and writes it into <paramref name="value"/>.
    /// </summary>
    bool TryGetAsU64(out ulong value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.I64"/>
    /// and writes it into <paramref name="value"/>.
    /// </summary>
    bool TryGetAsI64(out long value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.Bytes"/>
    /// and writes a <em>defensive copy</em> of the byte array into <paramref name="value"/>.
    /// </summary>
    bool TryGetAsBytes(out byte[] value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.Enum"/>
    /// and writes the enum name and numeric value into <paramref name="value"/>.
    /// </summary>
    bool TryGetAsEnum(out (string Name, ulong Value) value);

    #endregion
}
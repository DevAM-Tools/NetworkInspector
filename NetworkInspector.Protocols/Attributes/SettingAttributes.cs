// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Attributes;

/// <summary>
/// Marks a boolean field as a setting that is registered and loaded at startup.
/// The source generator will register this setting in <c>RegisterFields()</c>
/// and load it in <c>OnStart()</c>.
/// </summary>
/// <remarks>Initializes a new bool setting attribute.</remarks>
/// <param name="name">Machine-readable setting name.</param>
/// <param name="uiName">Human-readable UI name.</param>
/// <param name="groupName">Setting group name.</param>
[AttributeUsage(AttributeTargets.Field)]
public sealed class BoolSettingAttribute(string name, string uiName, string groupName) : Attribute
{
    /// <summary>Machine-readable setting name (e.g., "tcp.verify_checksum").</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable UI name.</summary>
    public string UiName { get; } = uiName;

    /// <summary>Setting group name for UI grouping.</summary>
    public string GroupName { get; } = groupName;

    /// <summary>Default value when no override is configured.</summary>
    public bool Default
    {
        get; set;
    }

    /// <summary>Optional description text.</summary>
    public string? Description
    {
        get; set;
    }
}

/// <summary>
/// Marks a string field as a setting that is registered and loaded at startup.
/// </summary>
/// <remarks>Initializes a new string setting attribute.</remarks>
/// <param name="name">Machine-readable setting name.</param>
/// <param name="uiName">Human-readable UI name.</param>
/// <param name="groupName">Setting group name.</param>
[AttributeUsage(AttributeTargets.Field)]
public sealed class StringSettingAttribute(string name, string uiName, string groupName) : Attribute
{
    /// <summary>Machine-readable setting name.</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable UI name.</summary>
    public string UiName { get; } = uiName;

    /// <summary>Setting group name for UI grouping.</summary>
    public string GroupName { get; } = groupName;

    /// <summary>Default value when no override is configured.</summary>
    public string Default { get; set; } = "";

    /// <summary>Optional description text.</summary>
    public string? Description
    {
        get; set;
    }
}

/// <summary>
/// Marks a <see cref="double"/> field as an F64 setting that is registered and loaded at startup.
/// Supports optional min/max range validation.
/// </summary>
/// <remarks>Initializes a new F64 setting attribute.</remarks>
/// <param name="name">Machine-readable setting name.</param>
/// <param name="uiName">Human-readable UI name.</param>
/// <param name="groupName">Setting group name.</param>
[AttributeUsage(AttributeTargets.Field)]
public sealed class F64SettingAttribute(string name, string uiName, string groupName) : Attribute
{
    /// <summary>Machine-readable setting name.</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable UI name.</summary>
    public string UiName { get; } = uiName;

    /// <summary>Setting group name for UI grouping.</summary>
    public string GroupName { get; } = groupName;

    /// <summary>Default value when no override is configured.</summary>
    public double Default
    {
        get; set;
    }

    /// <summary>Optional minimum allowed value.</summary>
    public double Min { get; set; } = double.NaN;

    /// <summary>Optional maximum allowed value.</summary>
    public double Max { get; set; } = double.NaN;

    /// <summary>Optional description text.</summary>
    public string? Description
    {
        get; set;
    }
}

/// <summary>
/// Marks a <see cref="ulong"/> field as a U64 setting that is registered and loaded at startup.
/// Supports optional min/max range validation.
/// </summary>
/// <remarks>Initializes a new U64 setting attribute.</remarks>
/// <param name="name">Machine-readable setting name.</param>
/// <param name="uiName">Human-readable UI name.</param>
/// <param name="groupName">Setting group name.</param>
[AttributeUsage(AttributeTargets.Field)]
public sealed class U64SettingAttribute(string name, string uiName, string groupName) : Attribute
{
    /// <summary>Machine-readable setting name.</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable UI name.</summary>
    public string UiName { get; } = uiName;

    /// <summary>Setting group name for UI grouping.</summary>
    public string GroupName { get; } = groupName;

    /// <summary>Default value when no override is configured.</summary>
    public ulong Default
    {
        get; set;
    }

    /// <summary>Whether <see cref="Min"/> is set. Set this to <see langword="true"/> together with <see cref="Min"/>
    /// to enable lower-bound validation.</summary>
    public bool HasMin
    {
        get; set;
    }

    /// <summary>Optional minimum allowed value. Only applied when <see cref="HasMin"/> is <see langword="true"/>.</summary>
    public ulong Min
    {
        get; set;
    }

    /// <summary>Whether <see cref="Max"/> is set. Set this to <see langword="true"/> together with <see cref="Max"/>
    /// to enable upper-bound validation.</summary>
    public bool HasMax
    {
        get; set;
    }

    /// <summary>Optional maximum allowed value. Only applied when <see cref="HasMax"/> is <see langword="true"/>.</summary>
    public ulong Max
    {
        get; set;
    }

    /// <summary>Optional description text.</summary>
    public string? Description
    {
        get; set;
    }
}

/// <summary>
/// Marks a <see cref="long"/> field as an I64 setting that is registered and loaded at startup.
/// Supports optional min/max range validation.
/// </summary>
/// <remarks>Initializes a new I64 setting attribute.</remarks>
/// <param name="name">Machine-readable setting name.</param>
/// <param name="uiName">Human-readable UI name.</param>
/// <param name="groupName">Setting group name.</param>
[AttributeUsage(AttributeTargets.Field)]
public sealed class I64SettingAttribute(string name, string uiName, string groupName) : Attribute
{
    /// <summary>Machine-readable setting name.</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable UI name.</summary>
    public string UiName { get; } = uiName;

    /// <summary>Setting group name for UI grouping.</summary>
    public string GroupName { get; } = groupName;

    /// <summary>Default value when no override is configured.</summary>
    public long Default
    {
        get; set;
    }

    /// <summary>Whether <see cref="Min"/> is set. Set this to <see langword="true"/> together with <see cref="Min"/>
    /// to enable lower-bound validation.</summary>
    public bool HasMin
    {
        get; set;
    }

    /// <summary>Optional minimum allowed value. Only applied when <see cref="HasMin"/> is <see langword="true"/>.</summary>
    public long Min
    {
        get; set;
    }

    /// <summary>Whether <see cref="Max"/> is set. Set this to <see langword="true"/> together with <see cref="Max"/>
    /// to enable upper-bound validation.</summary>
    public bool HasMax
    {
        get; set;
    }

    /// <summary>Optional maximum allowed value. Only applied when <see cref="HasMax"/> is <see langword="true"/>.</summary>
    public long Max
    {
        get; set;
    }

    /// <summary>Optional description text.</summary>
    public string? Description
    {
        get; set;
    }
}

/// <summary>
/// Marks a <c>byte[]</c> field as a bytes setting that is registered and loaded at startup.
/// </summary>
/// <remarks>Initializes a new bytes setting attribute.</remarks>
/// <param name="name">Machine-readable setting name.</param>
/// <param name="uiName">Human-readable UI name.</param>
/// <param name="groupName">Setting group name.</param>
[AttributeUsage(AttributeTargets.Field)]
public sealed class BytesSettingAttribute(string name, string uiName, string groupName) : Attribute
{
    /// <summary>Machine-readable setting name.</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable UI name.</summary>
    public string UiName { get; } = uiName;

    /// <summary>Setting group name for UI grouping.</summary>
    public string GroupName { get; } = groupName;

    /// <summary>
    /// Optional default value expressed as an even-length uppercase hexadecimal string (e.g. <c>"0102AABB"</c>).
    /// Each pair of characters represents one byte. Leave empty for an empty-array default.
    /// </summary>
    public string DefaultHex { get; set; } = "";

    /// <summary>Optional description text.</summary>
    public string? Description
    {
        get; set;
    }
}

/// <summary>
/// Marks a <see cref="ulong"/> field as an enum setting that is registered and loaded at startup.
/// The allowed values must be specified as a semicolon-delimited list of <c>Name=Value</c> pairs
/// in the <see cref="AllowedValues"/> property (e.g., <c>"Off=0;Low=1;High=2"</c>).
/// </summary>
/// <remarks>Initializes a new enum setting attribute.</remarks>
/// <param name="name">Machine-readable setting name.</param>
/// <param name="uiName">Human-readable UI name.</param>
/// <param name="groupName">Setting group name.</param>
[AttributeUsage(AttributeTargets.Field)]
public sealed class EnumSettingAttribute(string name, string uiName, string groupName) : Attribute
{
    /// <summary>Machine-readable setting name.</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable UI name.</summary>
    public string UiName { get; } = uiName;

    /// <summary>Setting group name for UI grouping.</summary>
    public string GroupName { get; } = groupName;

    /// <summary>Default numeric value when no override is configured.</summary>
    public ulong Default
    {
        get; set;
    }

    /// <summary>
    /// Semicolon-delimited list of <c>Name=Value</c> pairs defining allowed enum values.
    /// Example: <c>"Off=0;Low=1;Medium=2;High=3"</c>.
    /// </summary>
    public string AllowedValues { get; set; } = "";

    /// <summary>Optional description text.</summary>
    public string? Description
    {
        get; set;
    }
}

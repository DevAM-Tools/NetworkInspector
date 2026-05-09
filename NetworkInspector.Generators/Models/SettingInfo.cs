// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

using System;

namespace NetworkInspector.Generators.Models;

/// <summary>
/// Metadata for a single setting extracted from a <c>[*Setting]</c>-annotated member.
/// </summary>
internal sealed class SettingInfo(
    string fieldName, string name, string uiName, string groupName,
    string settingType, string defaultValue, string? description,
    string? min = null, string? max = null, string? enumValues = null)
    : IEquatable<SettingInfo>
{
    /// <summary>C# field name for the generated setting backing store.</summary>
    public string FieldName { get; } = fieldName;

    /// <summary>Machine-readable setting name.</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable UI label.</summary>
    public string UiName { get; } = uiName;

    /// <summary>UI group/category name.</summary>
    public string GroupName { get; } = groupName;

    /// <summary>Setting type discriminator (e.g., <c>"U64"</c>, <c>"Bool"</c>, <c>"Enum"</c>).</summary>
    public string SettingType { get; } = settingType;

    /// <summary>Default value as a C# literal string.</summary>
    public string DefaultValue { get; } = defaultValue;

    /// <summary>Optional description.</summary>
    public string? Description { get; } = description;

    /// <summary>Optional minimum value for numeric settings as a C# literal string.</summary>
    public string? Min { get; } = min;

    /// <summary>Optional maximum value for numeric settings as a C# literal string.</summary>
    public string? Max { get; } = max;

    /// <summary>Pre-formatted C# tuple content for enum settings (e.g., <c>("Name", 1UL), ("Other", 2UL)</c>).</summary>
    public string? EnumValues { get; } = enumValues;

    /// <inheritdoc />
    public bool Equals(SettingInfo? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        return FieldName == other.FieldName && Name == other.Name && UiName == other.UiName
            && GroupName == other.GroupName && SettingType == other.SettingType
            && DefaultValue == other.DefaultValue && Description == other.Description
            && Min == other.Min && Max == other.Max && EnumValues == other.EnumValues;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SettingInfo);

    /// <inheritdoc />
    public override int GetHashCode() => (FieldName, Name, SettingType, DefaultValue).GetHashCode();
}

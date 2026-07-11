// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Generators.Models;

/// <summary>
/// Metadata for a single field extracted from a <c>[*Field]</c>-annotated member.
/// </summary>
internal sealed class FieldInfo(string fieldName, string name, string uiName, string fieldType, string? indexGroup, string? description)
    : IEquatable<FieldInfo>
{
    /// <summary>C# field name for the generated field ID backing store (e.g., <c>_EtherTypeFieldId</c>).</summary>
    public string FieldName { get; } = fieldName;

    /// <summary>Machine-readable field name (e.g., <c>"eth.type"</c>).</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable UI label.</summary>
    public string UiName { get; } = uiName;

    /// <summary>Fully-qualified <c>FieldType</c> enum value (e.g., <c>global::NetworkInspector.Core.Fields.FieldType.U64</c>).</summary>
    public string FieldType { get; } = fieldType;

    /// <summary>Optional index group name; null if the field is not indexed.</summary>
    public string? IndexGroup { get; } = indexGroup;

    /// <summary>Optional description shown in UI/tooling.</summary>
    public string? Description { get; } = description;

    /// <inheritdoc />
    public bool Equals(FieldInfo? other)
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
            && FieldType == other.FieldType && IndexGroup == other.IndexGroup && Description == other.Description;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as FieldInfo);

    /// <inheritdoc />
    public override int GetHashCode() => (FieldName, Name, FieldType).GetHashCode();
}

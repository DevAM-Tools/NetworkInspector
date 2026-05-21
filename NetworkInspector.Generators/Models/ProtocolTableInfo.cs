// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using System;

namespace NetworkInspector.Generators.Models;

/// <summary>
/// Metadata for a protocol dispatch table extracted from a <c>[ProtocolTable*]</c>-annotated member.
/// </summary>
internal sealed class ProtocolTableInfo(string fieldName, string name, string uiName, string keyType, string? description)
    : IEquatable<ProtocolTableInfo>
{
    /// <summary>C# field name for the generated table ID backing store.</summary>
    public string FieldName { get; } = fieldName;

    /// <summary>Machine-readable dispatch table name.</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable UI label for the dispatch table.</summary>
    public string UiName { get; } = uiName;

    /// <summary>Key type discriminator string (e.g., <c>"U64"</c>).</summary>
    public string KeyType { get; } = keyType;

    /// <summary>Optional description.</summary>
    public string? Description { get; } = description;

    /// <inheritdoc />
    public bool Equals(ProtocolTableInfo? other)
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
            && KeyType == other.KeyType && Description == other.Description;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ProtocolTableInfo);

    /// <inheritdoc />
    public override int GetHashCode() => (FieldName, Name, KeyType).GetHashCode();
}

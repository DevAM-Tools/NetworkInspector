// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using System;

namespace NetworkInspector.Generators.Models;

/// <summary>
/// Represents an external table reference extracted from <c>[UsesTable("table.name")]</c>.
/// Generates a <c>WhenProtocolTableRegistered</c> call to cache the external table's ID.
/// </summary>
internal sealed class UsesTableInfo(string fieldName, string tableName)
    : IEquatable<UsesTableInfo>
{
    /// <summary>Name of the field to store the resolved table ID (e.g., "_EtherTypeTableId").</summary>
    public string FieldName { get; } = fieldName;

    /// <summary>Machine-readable table name to resolve (e.g., "eth.type").</summary>
    public string TableName { get; } = tableName;

    /// <inheritdoc />
    public bool Equals(UsesTableInfo? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        return FieldName == other.FieldName && TableName == other.TableName;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as UsesTableInfo);

    /// <inheritdoc />
    public override int GetHashCode() => (FieldName, TableName).GetHashCode();
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Generators.Models;

/// <summary>
/// Represents a table registration extracted from <c>[RegisterAtTable]</c>, <c>[RegisterAtStringTable]</c>,
/// <c>[RegisterAtBoolTable]</c>, <c>[RegisterAtBytesTable]</c>, or <c>[RegisterAtAnyTable]</c> class-level attributes.
/// </summary>
internal sealed class TableRegistration : IEquatable<TableRegistration>
{
    /// <summary>Name of the dispatch table (e.g., "eth.type", "ip.proto").</summary>
    public string Table
    {
        get;
    }

    /// <summary>
    /// The key type discriminator: "U64", "String", "Bool", "Bytes", or "Any".
    /// Determines which RegisterParserIn*TableByName method to call.
    /// </summary>
    public string KeyType
    {
        get;
    }

    /// <summary>U64 key value (only valid when <see cref="KeyType"/> == "U64").</summary>
    public ulong U64Key
    {
        get;
    }

    /// <summary>String key value (only valid when <see cref="KeyType"/> == "String").</summary>
    public string? StringKey
    {
        get;
    }

    /// <summary>Bool key value (only valid when <see cref="KeyType"/> == "Bool").</summary>
    public bool BoolKey
    {
        get;
    }

    /// <summary>Bytes key value as an uppercase hex string, e.g. "0102AABB" (only valid when <see cref="KeyType"/> == "Bytes").</summary>
    public string? BytesKey
    {
        get;
    }

    /// <summary>Creates a U64 table registration.</summary>
    public TableRegistration(string table, ulong key)
    {
        Table = table;
        KeyType = "U64";
        U64Key = key;
    }

    /// <summary>Creates a String table registration.</summary>
    public TableRegistration(string table, string stringKey)
    {
        Table = table;
        KeyType = "String";
        StringKey = stringKey;
    }

    /// <summary>Creates a Bool table registration.</summary>
    public TableRegistration(string table, bool boolKey)
    {
        Table = table;
        KeyType = "Bool";
        BoolKey = boolKey;
    }

    /// <summary>Creates an Any (catch-all) table registration.</summary>
    public TableRegistration(string table)
    {
        Table = table;
        KeyType = "Any";
    }

    /// <summary>Creates a Bytes table registration from an uppercase hex key string (e.g. "0102AABB").</summary>
    public static TableRegistration ForBytes(string table, string hexKey)
        => new(table, hexKey, bytes: true);

    // Private constructor used only by ForBytes to set KeyType = "Bytes".
    private TableRegistration(string table, string bytesHexKey, bool bytes)
    {
        Table = table;
        KeyType = "Bytes";
        BytesKey = bytesHexKey;
        _ = bytes; // Discriminator parameter — value unused after KeyType assignment.
    }

    /// <inheritdoc />
    public bool Equals(TableRegistration? other)
    {
        if (other is null)
        {
            return false;
        }
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        return Table == other.Table && KeyType == other.KeyType
            && U64Key == other.U64Key && StringKey == other.StringKey
            && BoolKey == other.BoolKey && BytesKey == other.BytesKey;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as TableRegistration);

    /// <inheritdoc />
    public override int GetHashCode() => (Table, KeyType, U64Key, StringKey, BoolKey, BytesKey).GetHashCode();
}

// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Protocols.Attributes;

/// <summary>
/// Registers this protocol in a U64 dispatch table at build time.
/// Multiple instances allowed for multiple dispatch keys.
/// </summary>
/// <remarks>Initializes a new table registration attribute.</remarks>
/// <param name="table">Name of the dispatch table.</param>
/// <param name="key">The dispatch key value.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RegisterAtTableAttribute(string table, ulong key) : Attribute
{
    /// <summary>Name of the dispatch table (e.g., "eth.type", "ip.proto").</summary>
    public string Table { get; } = table;

    /// <summary>The dispatch key value.</summary>
    public ulong Key { get; } = key;
}

/// <summary>
/// Registers this protocol in a String dispatch table at build time.
/// Multiple instances allowed for multiple dispatch keys.
/// </summary>
/// <remarks>Initializes a new string table registration attribute.</remarks>
/// <param name="table">Name of the dispatch table.</param>
/// <param name="key">The string dispatch key.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RegisterAtStringTableAttribute(string table, string key) : Attribute
{
    /// <summary>Name of the dispatch table.</summary>
    public string Table { get; } = table;

    /// <summary>The string dispatch key.</summary>
    public string Key { get; } = key;
}

/// <summary>
/// Registers this protocol in a Bool dispatch table at build time.
/// </summary>
/// <remarks>Initializes a new bool table registration attribute.</remarks>
/// <param name="table">Name of the dispatch table.</param>
/// <param name="key">The boolean dispatch key.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RegisterAtBoolTableAttribute(string table, bool key) : Attribute
{
    /// <summary>Name of the dispatch table.</summary>
    public string Table { get; } = table;

    /// <summary>The boolean dispatch key.</summary>
    public bool Key { get; } = key;
}

/// <summary>
/// Registers this protocol in a Bytes dispatch table at build time.
/// Multiple instances allowed for multiple dispatch keys.
/// </summary>
/// <remarks>Initializes a new bytes table registration attribute.</remarks>
/// <param name="table">Name of the dispatch table.</param>
/// <param name="key">The byte-sequence dispatch key.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RegisterAtBytesTableAttribute(string table, params byte[] key) : Attribute
{
    /// <summary>Name of the dispatch table.</summary>
    public string Table { get; } = table;

    /// <summary>The byte-sequence dispatch key.</summary>
    public byte[] Key { get; } = key;
}

/// <summary>
/// Registers this protocol as a catch-all in a dispatch table at build time.
/// The protocol will be invoked for any key not explicitly registered.
/// </summary>
/// <remarks>Initializes a new any-table registration attribute.</remarks>
/// <param name="table">Name of the dispatch table.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RegisterAtAnyTableAttribute(string table) : Attribute
{
    /// <summary>Name of the dispatch table.</summary>
    public string Table { get; } = table;
}

// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Protocols.Attributes;

/// <summary>
/// Marks a <see cref="ProtocolTableId"/> field as referencing an external protocol dispatch table
/// owned by another protocol. The source generator will emit a deferred resolution call
/// (<c>WhenProtocolTableRegistered</c>) in <c>RegisterFields()</c> to cache the table ID.
/// <para>
/// This is the C# equivalent of the Rust <c>#[uses_table(name = "...")]</c> attribute.
/// </para>
/// </summary>
/// <remarks>Initializes a new external table reference attribute.</remarks>
/// <param name="name">Machine-readable name of the external table (e.g., "eth.type", "ip.proto").</param>
[AttributeUsage(AttributeTargets.Field)]
public sealed class UsesTableAttribute(string name) : Attribute
{
    /// <summary>Machine-readable name of the external table to resolve.</summary>
    public string Name { get; } = name;
}

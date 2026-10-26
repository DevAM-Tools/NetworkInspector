// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Attributes;

/// <summary>
/// Marks a <see cref="ProtocolTableId"/> field as referencing an external protocol dispatch table
/// owned by another protocol. The source generator will emit a deferred resolution call
/// (<c>WhenProtocolTableRegistered</c>) in <c>RegisterFields()</c> to cache the table ID. The call is deferred because
/// the owning protocol may register its table after this protocol's <c>RegisterFields</c> has run; the callback then
/// assigns the annotated field as soon as the table exists, so the lookup happens once at build time and never per packet.
/// </summary>
/// <remarks>Initializes a new external table reference attribute.</remarks>
/// <param name="name">Machine-readable name of the external table (e.g., "eth.type", "ip.proto").</param>
[AttributeUsage(AttributeTargets.Field)]
public sealed class UsesTableAttribute(string name) : Attribute
{
    /// <summary>Machine-readable name of the external table to resolve.</summary>
    public string Name { get; } = name;
}

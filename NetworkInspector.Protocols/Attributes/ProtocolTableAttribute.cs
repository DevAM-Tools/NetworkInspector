// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Attributes;

/// <summary>
/// Abstract base for protocol dispatch table attributes.
/// Provides common properties for table name, UI name, and description.
/// </summary>
/// <remarks>Initializes a new protocol table attribute.</remarks>
/// <param name="name">Machine-readable table name.</param>
/// <param name="uiName">Human-readable UI name.</param>
public abstract class ProtocolTableAttribute(string name, string uiName) : Attribute
{
    /// <summary>Machine-readable table name (e.g., "eth.type", "udp.port").</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable UI name.</summary>
    public string UiName { get; } = uiName;

    /// <summary>Optional description text.</summary>
    public string? Description
    {
        get; set;
    }
}

/// <summary>
/// Marks a <see cref="ProtocolTableId"/> field as a U64 protocol dispatch table.
/// Dispatches sub-protocols by unsigned 64-bit integer key (e.g., EtherType, IP protocol number).
/// </summary>
/// <remarks>Initializes a new U64 protocol table attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ProtocolTableU64Attribute(string name, string uiName) : ProtocolTableAttribute(name, uiName)
{
}

/// <summary>
/// Marks a <see cref="ProtocolTableId"/> field as a String protocol dispatch table.
/// Dispatches sub-protocols by text-based identifier key.
/// </summary>
/// <remarks>Initializes a new String protocol table attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ProtocolTableStringAttribute(string name, string uiName) : ProtocolTableAttribute(name, uiName)
{
}

/// <summary>
/// Marks a <see cref="ProtocolTableId"/> field as a Bytes protocol dispatch table.
/// Dispatches sub-protocols by binary data key (e.g., magic signatures, prefixes).
/// </summary>
/// <remarks>Initializes a new Bytes protocol table attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ProtocolTableBytesAttribute(string name, string uiName) : ProtocolTableAttribute(name, uiName)
{
}

/// <summary>
/// Marks a <see cref="ProtocolTableId"/> field as a Bool protocol dispatch table.
/// Dispatches sub-protocols by boolean key (binary branching).
/// </summary>
/// <remarks>Initializes a new Bool protocol table attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ProtocolTableBoolAttribute(string name, string uiName) : ProtocolTableAttribute(name, uiName)
{
}

/// <summary>
/// Marks a <see cref="ProtocolTableId"/> field as an Any (catch-all) protocol dispatch table.
/// A single parser is registered that handles all remaining data.
/// </summary>
/// <remarks>Initializes a new Any protocol table attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ProtocolTableAnyAttribute(string name, string uiName) : ProtocolTableAttribute(name, uiName)
{
}

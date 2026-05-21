// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Attributes;

/// <summary>
/// Abstract base for all field registration attributes.
/// Provides common properties for field name, display name, and index group.
/// </summary>
/// <remarks>Initializes a new field attribute.</remarks>
/// <param name="name">Machine-readable field name.</param>
/// <param name="uiName">Human-readable UI name.</param>
public abstract class FieldRegistrationAttribute(string name, string uiName) : Attribute
{
    /// <summary>Machine-readable field name (e.g., "eth.dst", "ip.src").</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable UI name (e.g., "Destination", "Source Address").</summary>
    public string UiName { get; } = uiName;

    /// <summary>
    /// Name of the index group. Fields sharing a group name are tracked by a single
    /// bitmap, reducing memory overhead in the packet index.
    /// </summary>
    public string? IndexGroup
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
/// Marks a <see cref="FieldId"/> field as a None field (grouping node).
/// None fields hold child fields but no own value. Registered as FieldType.None.
/// </summary>
/// <remarks>Initializes a new None field attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class NoneFieldAttribute(string name, string uiName) : FieldRegistrationAttribute(name, uiName)
{
}

/// <summary>Marks a <see cref="FieldId"/> field as an I64 (signed 64-bit integer) field.</summary>
/// <remarks>Initializes a new I64 field attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class I64FieldAttribute(string name, string uiName) : FieldRegistrationAttribute(name, uiName)
{
}

/// <summary>Marks a <see cref="FieldId"/> field as a U64 (unsigned 64-bit integer) field.</summary>
/// <remarks>Initializes a new U64 field attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class U64FieldAttribute(string name, string uiName) : FieldRegistrationAttribute(name, uiName)
{
}

/// <summary>Marks a <see cref="FieldId"/> field as an F64 (64-bit floating point) field.</summary>
/// <remarks>Initializes a new F64 field attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class F64FieldAttribute(string name, string uiName) : FieldRegistrationAttribute(name, uiName)
{
}

/// <summary>Marks a <see cref="FieldId"/> field as a String field.</summary>
/// <remarks>Initializes a new String field attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class StringFieldAttribute(string name, string uiName) : FieldRegistrationAttribute(name, uiName)
{
}

/// <summary>Marks a <see cref="FieldId"/> field as a Bytes (raw byte data) field.</summary>
/// <remarks>Initializes a new Bytes field attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class BytesFieldAttribute(string name, string uiName) : FieldRegistrationAttribute(name, uiName)
{
}

/// <summary>Marks a <see cref="FieldId"/> field as a MacAddress field.</summary>
/// <remarks>Initializes a new MacAddress field attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class MacFieldAttribute(string name, string uiName) : FieldRegistrationAttribute(name, uiName)
{
}

/// <summary>Marks a <see cref="FieldId"/> field as an IPv4Address field.</summary>
/// <remarks>Initializes a new IPv4 field attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class IPv4FieldAttribute(string name, string uiName) : FieldRegistrationAttribute(name, uiName)
{
}

/// <summary>Marks a <see cref="FieldId"/> field as an IPv6Address field.</summary>
/// <remarks>Initializes a new IPv6 field attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class IPv6FieldAttribute(string name, string uiName) : FieldRegistrationAttribute(name, uiName)
{
}

/// <summary>Marks a <see cref="FieldId"/> field as an EUI-64 (Extended Unique Identifier) field.</summary>
/// <remarks>Initializes a new EUI-64 field attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class Eui64FieldAttribute(string name, string uiName) : FieldRegistrationAttribute(name, uiName)
{
}

/// <summary>Marks a <see cref="FieldId"/> field as a UUID (128-bit Universally Unique Identifier) field.</summary>
/// <remarks>Initializes a new UUID field attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class UuidFieldAttribute(string name, string uiName) : FieldRegistrationAttribute(name, uiName)
{
}

/// <summary>Marks a <see cref="FieldId"/> field as a Timestamp (nanosecond-precision) field.</summary>
/// <remarks>Initializes a new Timestamp field attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class TimestampFieldAttribute(string name, string uiName) : FieldRegistrationAttribute(name, uiName)
{
}

/// <summary>Marks a <see cref="FieldId"/> field as a Bool field.</summary>
/// <remarks>Initializes a new Bool field attribute.</remarks>
[AttributeUsage(AttributeTargets.Field)]
public sealed class BoolFieldAttribute(string name, string uiName) : FieldRegistrationAttribute(name, uiName)
{
}

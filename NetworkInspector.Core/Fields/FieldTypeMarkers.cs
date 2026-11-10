// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Internal marker singletons used by the 24-byte <see cref="FieldValueData"/> layout
/// (<c>_Data</c> + <c>_Data1</c> + <c>_Ref</c> on x64).
/// Each sealed class acts as a type discriminant: the reference field points to one of these
/// singletons for inline scalar types (Bool, I64, U64, F64, Mac, IPv4, Eui64, Timestamp).
/// IPv6 and Uuid use their marker plus inline payload in <c>_Data</c>/<c>_Data1</c>.
/// String and Bytes use their actual payload object (<c>string</c>, <c>byte[]</c>, or boxed
/// <see cref="LazyString"/>). <see cref="FieldType.None"/> uses <c>null</c>.
/// <para>
/// The marker approach eliminates a separate stored <see cref="FieldType"/> field; type is
/// inferred from reference identity at read time.
/// </para>
/// </summary>
/// <remarks>
/// Each marker is a unique sealed class so that ReferenceEquals checks are sufficient for type discrimination, with no virtual
/// dispatch. The markers carry no state and each has exactly one static instance, created once per process; building a
/// field value therefore stores a reference to an existing object and allocates nothing. Do not add instance
/// state or a second instance of a marker: type checks compare references, so a second instance would make
/// otherwise identical values look like different types.
/// <para><b>Thread-safety:</b> immutable; the singletons are created by the type initializer and shared by all threads.</para>
/// </remarks>
internal static class FieldTypeMarkers
{
    /// <summary>Discriminant of <see cref="FieldType.Bool"/> values; the payload is 1 or 0 in <c>_Data</c>.</summary>
    internal sealed class BoolMarker
    {
        /// <summary>The one instance every <see cref="FieldType.Bool"/> value references.</summary>
        internal static readonly BoolMarker Instance = new();
    }

    /// <summary>Discriminant of <see cref="FieldType.I64"/> values; the payload is the two's-complement bits in <c>_Data</c>.</summary>
    internal sealed class I64Marker
    {
        /// <summary>The one instance every <see cref="FieldType.I64"/> value references.</summary>
        internal static readonly I64Marker Instance = new();
    }

    /// <summary>Discriminant of <see cref="FieldType.U64"/> values; the payload is the number in <c>_Data</c>.</summary>
    internal sealed class U64Marker
    {
        /// <summary>The one instance every <see cref="FieldType.U64"/> value references.</summary>
        internal static readonly U64Marker Instance = new();
    }

    /// <summary>Discriminant of <see cref="FieldType.F64"/> values; the payload is the IEEE 754 bit pattern in <c>_Data</c>.</summary>
    internal sealed class F64Marker
    {
        /// <summary>The one instance every <see cref="FieldType.F64"/> value references.</summary>
        internal static readonly F64Marker Instance = new();
    }

    /// <summary>Discriminant of <see cref="FieldType.MacAddress"/> values; the payload is the 48-bit address in <c>_Data</c>.</summary>
    internal sealed class MacAddressMarker
    {
        /// <summary>The one instance every <see cref="FieldType.MacAddress"/> value references.</summary>
        internal static readonly MacAddressMarker Instance = new();
    }

    /// <summary>Discriminant of <see cref="FieldType.IPv4Address"/> values; the payload is the 32-bit address in <c>_Data</c>.</summary>
    internal sealed class IPv4AddressMarker
    {
        /// <summary>The one instance every <see cref="FieldType.IPv4Address"/> value references.</summary>
        internal static readonly IPv4AddressMarker Instance = new();
    }

    /// <summary>Discriminant of <see cref="FieldType.Eui64"/> values; the payload is the 64-bit identifier in <c>_Data</c>.</summary>
    internal sealed class Eui64Marker
    {
        /// <summary>The one instance every <see cref="FieldType.Eui64"/> value references.</summary>
        internal static readonly Eui64Marker Instance = new();
    }

    /// <summary>Discriminant of <see cref="FieldType.Timestamp"/> values; the payload is the nanosecond count in <c>_Data</c>.</summary>
    internal sealed class TimestampMarker
    {
        /// <summary>The one instance every <see cref="FieldType.Timestamp"/> value references.</summary>
        internal static readonly TimestampMarker Instance = new();
    }

    /// <summary>Discriminant of <see cref="FieldType.IPv6Address"/> values; the payload is the high and low halves in <c>_Data</c>/<c>_Data1</c>.</summary>
    internal sealed class IPv6AddressMarker
    {
        /// <summary>The one instance every <see cref="FieldType.IPv6Address"/> value references.</summary>
        internal static readonly IPv6AddressMarker Instance = new();
    }

    /// <summary>Discriminant of <see cref="FieldType.Uuid"/> values; the payload is the high and low halves in <c>_Data</c>/<c>_Data1</c>.</summary>
    internal sealed class UuidMarker
    {
        /// <summary>The one instance every <see cref="FieldType.Uuid"/> value references.</summary>
        internal static readonly UuidMarker Instance = new();
    }
}

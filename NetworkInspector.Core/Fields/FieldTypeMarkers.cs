// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Internal marker objects used by the 16-byte <see cref="FieldValueData"/> layout.
/// Each sealed class acts as a type discriminant: the <see cref="FieldValueData._Ref"/> field
/// points to one of these singletons for inline value types (Bool, I64, U64, F64, Mac, IPv4,
/// Eui64, Timestamp). Reference-carrying types (String, Bytes, IPv6, Uuid) use their actual
/// payload object directly, and None uses <c>null</c>.
/// <para>
/// The marker approach eliminates the explicit <see cref="FieldType"/> byte field, saving 8 bytes
/// of padding on x64 and reducing the struct from 24 to 16 bytes.
/// </para>
/// </summary>
internal static class FieldTypeMarkers
{
    #region Marker Types

    // Each marker is a unique sealed class so that ReferenceEquals checks are
    // sufficient for type discrimination — no virtual dispatch needed.

    internal sealed class BoolMarker
    {
        internal static readonly BoolMarker Instance = new();
    }
    internal sealed class I64Marker
    {
        internal static readonly I64Marker Instance = new();
    }
    internal sealed class U64Marker
    {
        internal static readonly U64Marker Instance = new();
    }
    internal sealed class F64Marker
    {
        internal static readonly F64Marker Instance = new();
    }
    internal sealed class MacAddressMarker
    {
        internal static readonly MacAddressMarker Instance = new();
    }
    internal sealed class IPv4AddressMarker
    {
        internal static readonly IPv4AddressMarker Instance = new();
    }
    internal sealed class Eui64Marker
    {
        internal static readonly Eui64Marker Instance = new();
    }
    internal sealed class TimestampMarker
    {
        internal static readonly TimestampMarker Instance = new();
    }
    internal sealed class IPv6AddressMarker
    {
        internal static readonly IPv6AddressMarker Instance = new();
    }
    internal sealed class UuidMarker
    {
        internal static readonly UuidMarker Instance = new();
    }

    /// <summary>
    /// Sentinel for <see cref="FieldValueData.NewString(string)"/> when the underlying
    /// <see cref="LazyString.RawValue"/> is <c>null</c> (default LazyString).
    /// Without this, a default-constructed string value would be indistinguishable
    /// from <see cref="FieldType.None"/> (both would have <c>_Ref == null</c>).
    /// </summary>
    internal sealed class EmptyStringMarker
    {
        internal static readonly EmptyStringMarker Instance = new();
    }

    #endregion
}
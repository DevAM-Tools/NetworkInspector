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

    #endregion
}

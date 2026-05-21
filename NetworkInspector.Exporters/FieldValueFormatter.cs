// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Shared formatter that converts a <see cref="FieldValue"/> into a stable
/// invariant-culture string suitable for same-as-previous detection in the
/// JSON, PBF, and PBF-Columnar exporters.
/// <para>
/// Centralised in this single helper so all exporters apply identical
/// formatting rules. Returns <c>null</c> only for <see cref="FieldType.None"/>;
/// every other concrete field type produces a comparable string.
/// </para>
/// <para>
/// <b>Thread safety:</b> Stateless — safe to call concurrently from any thread.
/// </para>
/// </summary>
internal static class FieldValueFormatter
{
    /// <summary>
    /// Formats <paramref name="value"/> as an invariant-culture string for
    /// same-as-previous comparison. Returns <c>null</c> when no comparable
    /// string representation exists (currently only <see cref="FieldType.None"/>).
    /// </summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The string representation, or <c>null</c> for <see cref="FieldType.None"/>.</returns>
    internal static string? Format(FieldValue value) => value.Type switch
    {
        FieldType.I64 => value.Data.TryGetAsI64(out long i64)
            ? i64.ToString(CultureInfo.InvariantCulture)
            : null,
        FieldType.U64 => value.Data.TryGetAsU64(out ulong u64)
            ? u64.ToString(CultureInfo.InvariantCulture)
            : null,
        FieldType.F64 => value.Data.TryGetAsF64(out double f64)
            ? f64.ToString(CultureInfo.InvariantCulture)
            : null,
        FieldType.String => value.Data.TryGetAsString(out string str) ? str : null,
        FieldType.Bool => value.Data.TryGetAsBool(out bool b) ? (b ? "true" : "false") : null,
        FieldType.Timestamp => value.Data.TryGetAsTimestamp(out Timestamp ts)
            ? ts.AsNanos.ToString(CultureInfo.InvariantCulture)
            : null,
        FieldType.Bytes => value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytes)
            ? Convert.ToHexString(bytes.Span)
            : null,
        FieldType.MacAddress => value.Data.TryGetAsMacAddress(out MacAddress mac)
            ? mac.ToString()
            : null,
        FieldType.IPv4Address => value.Data.TryGetAsIPv4(out IPv4Address ipv4)
            ? ipv4.ToString()
            : null,
        FieldType.IPv6Address => value.Data.TryGetAsIPv6(out IPv6Address ipv6)
            ? ipv6.ToString()
            : null,
        FieldType.Eui64 => value.Data.TryGetAsEui64(out Eui64 eui)
            ? eui.ToString()
            : null,
        FieldType.Uuid => value.Data.TryGetAsUuid(out Uuid uuid)
            ? uuid.ToString()
            : null,
        // FieldType.None — there is no comparable string representation
        _ => null,
    };
}

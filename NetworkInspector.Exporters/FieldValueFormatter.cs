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
    internal static string? Format(FieldValue value)
    {
        switch (value.Type)
        {
            case FieldType.I64:
                if (value.Data.TryGetAsI64(out long i64))
                {
                    return i64.ToString(CultureInfo.InvariantCulture);
                }
                return null;
            case FieldType.U64:
                if (value.Data.TryGetAsU64(out ulong u64))
                {
                    return u64.ToString(CultureInfo.InvariantCulture);
                }
                return null;
            case FieldType.F64:
                if (value.Data.TryGetAsF64(out double f64))
                {
                    return f64.ToString(CultureInfo.InvariantCulture);
                }
                return null;
            case FieldType.String:
                if (value.Data.TryGetAsString(out string str))
                {
                    return str;
                }
                return null;
            case FieldType.Bool:
                if (value.Data.TryGetAsBool(out bool b))
                {
                    if (b)
                    {
                        return "true";
                    }
                    return "false";
                }
                return null;
            case FieldType.Timestamp:
                if (value.Data.TryGetAsTimestamp(out Timestamp ts))
                {
                    return ts.AsNanos.ToString(CultureInfo.InvariantCulture);
                }
                return null;
            case FieldType.Bytes:
                if (value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytes))
                {
                    return Convert.ToHexString(bytes.Span);
                }
                return null;
            case FieldType.MacAddress:
                if (value.Data.TryGetAsMacAddress(out MacAddress mac))
                {
                    // MacAddress.ToString() uses a fixed colon-hex layout (culture-independent).
                    return mac.ToString();
                }
                return null;
            case FieldType.IPv4Address:
                if (value.Data.TryGetAsIPv4(out IPv4Address ipv4))
                {
                    // IPv4Address.ToString() uses dotted-decimal layout (culture-independent).
                    return ipv4.ToString();
                }
                return null;
            case FieldType.IPv6Address:
                if (value.Data.TryGetAsIPv6(out IPv6Address ipv6))
                {
                    // IPv6Address.ToString() uses RFC 5952 layout (culture-independent).
                    return ipv6.ToString();
                }
                return null;
            case FieldType.Eui64:
                if (value.Data.TryGetAsEui64(out Eui64 eui))
                {
                    // Eui64.ToString() uses colon-hex layout (culture-independent).
                    return eui.ToString();
                }
                return null;
            case FieldType.Uuid:
                if (value.Data.TryGetAsUuid(out Uuid uuid))
                {
                    // Uuid.ToString() uses canonical hyphenated hex (culture-independent).
                    return uuid.ToString();
                }
                return null;
            default:
                // FieldType.None — there is no comparable string representation
                return null;
        }
    }
}

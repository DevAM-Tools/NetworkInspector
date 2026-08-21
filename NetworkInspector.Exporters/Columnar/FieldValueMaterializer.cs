// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// Helpers for reading optional export columns from a <see cref="Field"/> / <see cref="FieldValue"/>
/// under <see cref="ColumnarDetailFlags"/>, and for materializing fixed-size address / bytes
/// payloads as <see cref="byte"/> arrays for columnar sinks.
/// </summary>
internal static class FieldValueMaterializer
{
    #region Public API

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="field"/> carries a storable value column
    /// (any type other than <see cref="FieldType.None"/>).
    /// </summary>
    internal static bool HasValueColumn(Field field) => field.Value.Data.Type != FieldType.None;

    /// <summary>
    /// Returns the custom representation string when the flag is set and a representation is present;
    /// otherwise <see langword="null"/>.
    /// </summary>
    internal static string? GetCustomRepresentation(FieldValue value, ColumnarDetailFlags flags)
    {
        if ((flags & ColumnarDetailFlags.IncludeCustomRepresentation) == 0)
        {
            return null;
        }

        LazyString custom = value.CustomRepresentation;
        return custom.IsNull
            ? null
            : custom.AsString;
    }

    /// <summary>
    /// Returns the field custom text when the flag is set and text is present; otherwise
    /// <see langword="null"/>.
    /// </summary>
    internal static string? GetCustomText(Field field, ColumnarDetailFlags flags)
    {
        if ((flags & ColumnarDetailFlags.IncludeCustomText) == 0)
        {
            return null;
        }

        LazyString text = field.CustomText;
        return text.IsNull
            ? null
            : text.AsString;
    }

    /// <summary>
    /// Materializes <see cref="FieldType.Bytes"/> and fixed-size address types as a new
    /// <see cref="byte"/> array suitable for Parquet/DuckDB BLOB columns. Returns
    /// <see langword="null"/> for non-byte payload types.
    /// </summary>
    internal static byte[]? ToBytesArray(in FieldValueData data)
    {
        switch (data.Type)
        {
            case FieldType.Bytes:
                if (!data.TryGetAsBytes(out ReadOnlyMemory<byte> bytes))
                {
                    return null;
                }
                {
                    byte[] copy = GC.AllocateUninitializedArray<byte>(bytes.Length);
                    bytes.Span.CopyTo(copy);
                    return copy;
                }

            case FieldType.MacAddress:
                if (!data.TryGetAsMacAddress(out MacAddress mac))
                {
                    return null;
                }
                {
                    byte[] macBytes = GC.AllocateUninitializedArray<byte>(6);
                    mac.ToBytes(macBytes);
                    return macBytes;
                }

            case FieldType.IPv4Address:
                if (!data.TryGetAsIPv4(out IPv4Address ipv4))
                {
                    return null;
                }
                {
                    byte[] ipv4Bytes = GC.AllocateUninitializedArray<byte>(4);
                    ipv4.ToBytes(ipv4Bytes);
                    return ipv4Bytes;
                }

            case FieldType.IPv6Address:
                if (!data.TryGetAsIPv6(out IPv6Address ipv6))
                {
                    return null;
                }
                {
                    byte[] ipv6Bytes = GC.AllocateUninitializedArray<byte>(16);
                    ipv6.ToBytes(ipv6Bytes);
                    return ipv6Bytes;
                }

            case FieldType.Eui64:
                if (!data.TryGetAsEui64(out Eui64 eui64))
                {
                    return null;
                }
                {
                    byte[] eui64Bytes = GC.AllocateUninitializedArray<byte>(8);
                    eui64.ToBytes(eui64Bytes);
                    return eui64Bytes;
                }

            case FieldType.Uuid:
                if (!data.TryGetAsUuid(out Uuid uuid))
                {
                    return null;
                }
                {
                    byte[] uuidBytes = GC.AllocateUninitializedArray<byte>(16);
                    uuid.ToBytes(uuidBytes);
                    return uuidBytes;
                }

            default:
                return null;
        }
    }

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Zero-allocation JSON formatting helpers.
/// All methods write directly to a <see cref="PooledBuffer"/> using stack-allocated
/// scratch buffers for numeric formatting, avoiding heap allocations on the hot path.
/// </summary>
internal static class JsonHelpers
{
    /// <summary>Writes a signed 64-bit integer as a decimal string.</summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="value">The value to format.</param>
    internal static void WriteI64(ref PooledBuffer buffer, long value)
    {
        Span<byte> scratch = stackalloc byte[20]; // max i64 decimal digits + sign
        if (!value.TryFormat(scratch, out int bytesWritten, default, CultureInfo.InvariantCulture))
        {
            // TryFormat cannot fail for a 20-byte buffer and a 64-bit integer; guard
            // defensively so any unexpected future change produces null rather than
            // a truncated/malformed JSON token.
            buffer.Write("null"u8);
            return;
        }
        buffer.Write(scratch[..bytesWritten]);
    }

    /// <summary>Writes an unsigned 64-bit integer as a decimal string.</summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="value">The value to format.</param>
    internal static void WriteU64(ref PooledBuffer buffer, ulong value)
    {
        Span<byte> scratch = stackalloc byte[20]; // max u64 decimal digits
        if (!value.TryFormat(scratch, out int bytesWritten, default, CultureInfo.InvariantCulture))
        {
            buffer.Write("null"u8);
            return;
        }
        buffer.Write(scratch[..bytesWritten]);
    }

    /// <summary>
    /// Writes a 64-bit float as a decimal string. NaN and Infinity are written as JSON <c>null</c>.
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="value">The value to format.</param>
    internal static void WriteF64(ref PooledBuffer buffer, double value)
    {
        if (!double.IsFinite(value))
        {
            buffer.Write("null"u8);
            return;
        }
        Span<byte> scratch = stackalloc byte[32];
        value.TryFormat(scratch, out int bytesWritten, default, CultureInfo.InvariantCulture);
        buffer.Write(scratch[..bytesWritten]);
    }

    /// <summary>Writes <c>true</c> or <c>false</c>.</summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="value">The boolean value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteBool(ref PooledBuffer buffer, bool value) =>
        buffer.Write(value ? "true"u8 : "false"u8);

    /// <summary>
    /// Writes a JSON string with surrounding double quotes and SIMD-accelerated escaping.
    /// Short strings (≤512 encoded bytes) use stack allocation; longer ones rent from the pool.
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="value">The string to write.</param>
    internal static void WriteJsonString(ref PooledBuffer buffer, ReadOnlySpan<char> value)
    {
        buffer.WriteByte((byte)'"');
        if (value.IsEmpty)
        {
            buffer.WriteByte((byte)'"');
            return;
        }

        int maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        if (maxBytes <= 512)
        {
            Span<byte> utf8 = stackalloc byte[maxBytes];
            int written = Encoding.UTF8.GetBytes(value, utf8);
            SimdEscape.EscapeJsonStringTo(ref buffer, utf8[..written]);
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(maxBytes);
            int written = Encoding.UTF8.GetBytes(value, rented);
            SimdEscape.EscapeJsonStringTo(ref buffer, rented.AsSpan(0, written));
            ArrayPool<byte>.Shared.Return(rented);
        }
        buffer.WriteByte((byte)'"');
    }

    /// <summary>
    /// Writes a JSON string from UTF-8 bytes with surrounding quotes and escaping.
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="utf8">UTF-8 encoded bytes.</param>
    internal static void WriteJsonStringUtf8(ref PooledBuffer buffer, ReadOnlySpan<byte> utf8)
    {
        buffer.WriteByte((byte)'"');
        SimdEscape.EscapeJsonStringTo(ref buffer, utf8);
        buffer.WriteByte((byte)'"');
    }

    /// <summary>
    /// Writes a base-64 encoded string with surrounding JSON quotes.
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="data">Raw bytes to encode.</param>
    internal static void WriteBase64String(ref PooledBuffer buffer, ReadOnlySpan<byte> data)
    {
        buffer.WriteByte((byte)'"');
        if (!data.IsEmpty)
        {
            int maxLen = Base64.GetMaxEncodedToUtf8Length(data.Length);
            if (maxLen <= 512)
            {
                Span<byte> scratch = stackalloc byte[maxLen];
                Base64.EncodeToUtf8(data, scratch, out _, out int bytesWritten);
                buffer.Write(scratch[..bytesWritten]);
            }
            else
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(maxLen);
                Base64.EncodeToUtf8(data, rented, out _, out int bytesWritten);
                buffer.Write(rented.AsSpan(0, bytesWritten));
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        buffer.WriteByte((byte)'"');
    }

    /// <summary>
    /// Writes a MAC address as a quoted string in colon-separated hex format (XX:XX:XX:XX:XX:XX).
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="mac">The MAC address value.</param>
    internal static void WriteMacAddress(ref PooledBuffer buffer, MacAddress mac)
    {
        // MAC address formatted string
        buffer.WriteByte((byte)'"');
        Span<byte> scratch = stackalloc byte[MacAddress.FormattedLength];
        mac.TryFormat(scratch, out int written, default, CultureInfo.InvariantCulture);
        buffer.Write(scratch[..written]);
        buffer.WriteByte((byte)'"');
    }

    /// <summary>
    /// Writes an IPv4 address as a quoted string in dotted decimal format.
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="ip">The IPv4 address value.</param>
    internal static void WriteIPv4(ref PooledBuffer buffer, IPv4Address ip)
    {
        buffer.WriteByte((byte)'"');
        Span<byte> scratch = stackalloc byte[IPv4Address.MaxFormattedLength];
        ip.TryFormat(scratch, out int written, default, CultureInfo.InvariantCulture);
        buffer.Write(scratch[..written]);
        buffer.WriteByte((byte)'"');
    }

    /// <summary>
    /// Writes a timestamp as a quoted ISO 8601 string.
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="ts">The timestamp value.</param>
    internal static void WriteTimestamp(ref PooledBuffer buffer, Timestamp ts)
    {
        buffer.WriteByte((byte)'"');
        Span<byte> scratch = stackalloc byte[Timestamp.MaxFormattedLength];
        ts.TryFormat(scratch, out int written, default, CultureInfo.InvariantCulture);
        buffer.Write(scratch[..written]);
        buffer.WriteByte((byte)'"');
    }

    /// <summary>Writes a comma separator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteComma(ref PooledBuffer buffer) => buffer.WriteByte((byte)',');

    /// <summary>Writes a colon separator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteColon(ref PooledBuffer buffer) => buffer.WriteByte((byte)':');

    /// <summary>
    /// Writes a field value to the buffer in the appropriate JSON representation.
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="value">The field value to serialize.</param>
    internal static void WriteFieldValue(ref PooledBuffer buffer, FieldValue value)
    {
        switch (value.Type)
        {
            case FieldType.None:
                buffer.Write("null"u8);
                break;
            case FieldType.I64:
                if (!value.Data.TryGetAsI64(out long i64))
                {
                    break;
                }
                WriteI64(ref buffer, i64);
                break;
            case FieldType.U64:
                if (!value.Data.TryGetAsU64(out ulong u64))
                {
                    break;
                }
                WriteU64(ref buffer, u64);
                break;
            case FieldType.F64:
                if (!value.Data.TryGetAsF64(out double f64))
                {
                    break;
                }
                WriteF64(ref buffer, f64);
                break;
            case FieldType.String:
                if (!value.Data.TryGetAsString(out string str))
                {
                    break;
                }
                WriteJsonString(ref buffer, str);
                break;
            case FieldType.Bytes:
                if (!value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytesVal))
                {
                    break;
                }
                WriteBase64String(ref buffer, bytesVal.Span);
                break;
            case FieldType.MacAddress:
                if (!value.Data.TryGetAsMacAddress(out MacAddress mac))
                {
                    break;
                }
                WriteMacAddress(ref buffer, mac);
                break;
            case FieldType.IPv4Address:
                if (!value.Data.TryGetAsIPv4(out IPv4Address ipv4))
                {
                    break;
                }
                WriteIPv4(ref buffer, ipv4);
                break;
            case FieldType.IPv6Address:
                if (!value.Data.TryGetAsIPv6(out IPv6Address ipv6))
                {
                    break;
                }
                buffer.WriteByte((byte)'"');
                Span<byte> ipv6Scratch = stackalloc byte[64];
                ipv6.TryFormat(ipv6Scratch, out int ipv6Written, default, CultureInfo.InvariantCulture);
                buffer.Write(ipv6Scratch[..ipv6Written]);
                buffer.WriteByte((byte)'"');
                break;
            case FieldType.Eui64:
                if (!value.Data.TryGetAsEui64(out Eui64 eui64))
                {
                    break;
                }
                buffer.WriteByte((byte)'"');
                Span<byte> euiScratch = stackalloc byte[32];
                eui64.TryFormat(euiScratch, out int euiWritten, default, CultureInfo.InvariantCulture);
                buffer.Write(euiScratch[..euiWritten]);
                buffer.WriteByte((byte)'"');
                break;
            case FieldType.Uuid:
                if (!value.Data.TryGetAsUuid(out NetworkInspector.Values.Uuid uuid))
                {
                    break;
                }
                buffer.WriteByte((byte)'"');
                Span<byte> uuidScratch = stackalloc byte[64];
                uuid.TryFormat(uuidScratch, out int uuidWritten, default, CultureInfo.InvariantCulture);
                buffer.Write(uuidScratch[..uuidWritten]);
                buffer.WriteByte((byte)'"');
                break;
            case FieldType.Timestamp:
                if (!value.Data.TryGetAsTimestamp(out Timestamp ts))
                {
                    break;
                }
                WriteTimestamp(ref buffer, ts);
                break;
            case FieldType.Bool:
                if (!value.Data.TryGetAsBool(out bool boolVal))
                {
                    break;
                }
                WriteBool(ref buffer, boolVal);
                break;
            default:
                buffer.Write("null"u8);
                break;
        }
    }
}

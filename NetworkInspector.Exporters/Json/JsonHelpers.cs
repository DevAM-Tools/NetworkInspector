// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Zero-allocation JSON formatting helpers.
/// All methods write directly to a <see cref="PooledBuffer"/> using stack-allocated
/// scratch buffers for numeric formatting, avoiding heap allocations on the hot path
/// (these helpers run once per exported field value). Only strings whose worst-case encoded size exceeds 512 bytes fall back to
/// <see cref="ArrayPool{T}.Shared"/>; base-64 payloads are encoded straight into the target buffer.
/// <para>
/// All formatting uses <see cref="CultureInfo.InvariantCulture"/> so the output never depends on the host culture.
/// Thread safety: stateless; safe from any thread as long as each thread uses its own <see cref="PooledBuffer"/>.
/// </para>
/// </summary>
internal static class JsonHelpers
{
    #region Numbers

    /// <summary>Writes a signed 64-bit integer as a decimal string.</summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="value">The value to format.</param>
    /// <exception cref="InvalidOperationException">The number did not fit its stack buffer (a broken invariant).</exception>
    internal static void WriteI64(ref PooledBuffer buffer, long value)
    {
        // Format into a stack buffer instead of value.ToString() to avoid a string allocation per number.
        // "-9223372036854775808" is the longest i64 text: 19 digits plus the sign = 20 bytes, so the format cannot fail; a failure
        // would be a broken invariant and is reported instead of writing a malformed or silently replaced token.
        Span<byte> scratch = stackalloc byte[20];
        if (!value.TryFormat(scratch, out int bytesWritten, default, CultureInfo.InvariantCulture))
        {
            ThrowValueUnreadable(FieldType.I64);
        }

        buffer.Write(scratch[..bytesWritten]);
    }

    /// <summary>Writes an unsigned 64-bit integer as a decimal string.</summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="value">The value to format.</param>
    /// <exception cref="InvalidOperationException">The number did not fit its stack buffer (a broken invariant).</exception>
    internal static void WriteU64(ref PooledBuffer buffer, ulong value)
    {
        // Stack buffer instead of ToString() to avoid a per-number string allocation.
        // "18446744073709551615" is the longest u64 text: 20 digits, no sign.
        Span<byte> scratch = stackalloc byte[20];
        if (!value.TryFormat(scratch, out int bytesWritten, default, CultureInfo.InvariantCulture))
        {
            ThrowValueUnreadable(FieldType.U64);
        }

        buffer.Write(scratch[..bytesWritten]);
    }

    /// <summary>
    /// Writes a 64-bit float as a decimal string in the shortest round-trippable form.
    /// NaN and Infinity are written as JSON <c>null</c>.
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="value">The value to format.</param>
    /// <exception cref="InvalidOperationException">The number did not fit its stack buffer (a broken invariant).</exception>
    internal static void WriteF64(ref PooledBuffer buffer, double value)
    {
        // JSON (RFC 8259) has no token for NaN or Infinity; emitting "NaN" would make the whole document unparsable,
        // so these degrade to null.
        if (!double.IsFinite(value))
        {
            buffer.Write("null"u8);
            return;
        }

        // The default shortest round-trip text is at most 24 characters (for example "-1.7976931348623157E+308");
        // 32 bytes leave headroom, so TryFormat cannot run out of space here.
        Span<byte> scratch = stackalloc byte[32];
        if (!value.TryFormat(scratch, out int bytesWritten, default, CultureInfo.InvariantCulture))
        {
            ThrowValueUnreadable(FieldType.F64);
        }

        buffer.Write(scratch[..bytesWritten]);
    }

    /// <summary>Writes <c>true</c> or <c>false</c>.</summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="value">The boolean value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteBool(ref PooledBuffer buffer, bool value) =>
        buffer.Write(value
            ? "true"u8
            : "false"u8);

    #endregion

    #region Strings

    /// <summary>
    /// Writes a JSON string with surrounding double quotes and SIMD-accelerated escaping.
    /// Strings whose worst-case UTF-8 size is at most 512 bytes (at most 170 UTF-16 code units) use stack allocation;
    /// longer ones rent from the pool. Invalid UTF-16 (lone surrogates) is replaced with U+FFFD by the UTF-8 encoder.
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="value">The string to write.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="value"/> is longer than <see cref="_MaxJsonStringChars"/> characters.
    /// </exception>
    internal static void WriteJsonString(ref PooledBuffer buffer, ReadOnlySpan<char> value)
    {
        buffer.WriteByte((byte)'"');
        if (value.IsEmpty)
        {
            buffer.WriteByte((byte)'"');
            return;
        }

        // The escaper works on UTF-8 bytes, so the string is transcoded first. The scratch buffer is sized for the
        // worst case (GetMaxByteCount is (length + 1) * 3), which avoids a second counting pass over the string.
        // The 512-byte cap keeps the stackalloc small and bounded no matter how long the string is (stack overflow
        // is not recoverable); longer strings use a pooled array instead of a fresh heap allocation.
        if (value.Length > _MaxJsonStringChars)
        {
            _ThrowStringTooLarge(value.Length);
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
            // The rental is returned in finally: the escaper writes into a pooled buffer that can fail to grow.
            byte[] rented = ArrayPool<byte>.Shared.Rent(maxBytes);
            try
            {
                int written = Encoding.UTF8.GetBytes(value, rented);
                SimdEscape.EscapeJsonStringTo(ref buffer, rented.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        buffer.WriteByte((byte)'"');
    }

    /// <summary>
    /// The largest number of UTF-16 code units that <see cref="WriteJsonString"/> can encode: the worst-case UTF-8 size of the text
    /// (three bytes per code unit, <see cref="Encoding.GetMaxByteCount"/>) must fit an <see cref="int"/>. A fact of the encoder, a
    /// constant. Threat: a text near <see cref="int.MaxValue"/> characters would make the size computation throw a framework
    /// exception that names no export limit; the packet that holds it is reported as skipped with a message that names the limit.
    /// Posture: not configurable, the encoder cannot do more.
    /// </summary>
    private const int _MaxJsonStringChars = 715_827_881;

    /// <summary>The largest byte count that <see cref="Base64.GetMaxEncodedToUtf8Length"/> accepts: a fact of the encoder, a constant.</summary>
    private const int _MaxBase64Input = 1_610_612_733;

    /// <summary>
    /// Writes a base-64 encoded string with surrounding JSON quotes (the JSON form of a bytes value and of the frame bytes).
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="data">Raw bytes to encode; empty gives the empty string <c>""</c>.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="data"/> is longer than 1 610 612 733 bytes (the limit of the Base64 encoder), or the encoder did not finish
    /// (a broken invariant, see <see cref="WriteBase64"/>).
    /// </exception>
    internal static void WriteBase64String(ref PooledBuffer buffer, ReadOnlySpan<byte> data)
    {
        buffer.WriteByte((byte)'"');
        WriteBase64(ref buffer, data);
        buffer.WriteByte((byte)'"');
    }

    /// <summary>
    /// Writes the standard Base64 text (RFC 4648 §4 alphabet, with <c>=</c> padding) of <paramref name="data"/> without quotes. The
    /// text contains no character that needs JSON escaping, so it bypasses the escaper.
    /// </summary>
    /// <param name="buffer">Target buffer; grows as for every write.</param>
    /// <param name="data">Raw bytes to encode; empty writes nothing.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="data"/> is longer than 1 610 612 733 bytes (the limit of the Base64 encoder), or the encoder did not finish
    /// (a broken invariant: the destination is sized exactly).
    /// </exception>
    /// <remarks>
    /// Perf: the text is encoded straight into the pooled buffer (<see cref="PooledBuffer.Reserve"/>), so a frame of any size costs
    /// no string, no scratch array, and no second copy; <c>Convert.ToBase64String</c> would allocate a string per value. The
    /// reservation is the exact encoded size, so the status of the encoder is always <see cref="OperationStatus.Done"/>; any other
    /// status is a broken invariant and goes to a cold helper, never into a truncated value.
    /// </remarks>
    internal static void WriteBase64(ref PooledBuffer buffer, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        // Limit: the encoder's own bound, checked first so the failure names the limit instead of an argument of the framework.
        if (data.Length > _MaxBase64Input)
        {
            _ThrowBase64TooLarge(data.Length);
        }

        int reserved = Base64.GetMaxEncodedToUtf8Length(data.Length);
        Span<byte> target = buffer.Reserve(reserved);
        // Invariant: for padded Base64 the maximum is the exact encoded size, so a finished encode fills the reservation completely.
        // Reserve already counted the reservation as written; a shorter result would leave unspecified bytes in the buffer, so it is
        // treated like any other unfinished status.
        OperationStatus status = Base64.EncodeToUtf8(data, target, out _, out int written);
        if (status != OperationStatus.Done || written != reserved)
        {
            _ThrowBase64Failed(status);
        }
    }

    /// <summary>
    /// The Base64 encoder did not fill an exactly sized destination (any status but <see cref="OperationStatus.Done"/>, or a short
    /// result): a broken invariant. A cold helper keeps the throw site out of <see cref="WriteBase64"/>.
    /// </summary>
    /// <param name="status">The status the encoder returned.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowBase64Failed(OperationStatus status) =>
        throw new InvalidOperationException(
            Invariant($"Base64 encoding did not finish (status {status}) although the destination was sized for the whole output."));

    /// <summary>
    /// A byte sequence is longer than the Base64 encoder accepts. A cold helper that names the limit, so the packet that holds the
    /// bytes is skipped with a message that says why.
    /// </summary>
    /// <param name="length">The length of the byte sequence.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowBase64TooLarge(int length) =>
        throw new InvalidOperationException(
            Invariant($"A byte sequence of {length} bytes cannot be written as one Base64 string (limit {_MaxBase64Input})."));

    /// <summary>
    /// A string is longer than <see cref="_MaxJsonStringChars"/> characters. A cold helper that names the limit, so the packet that
    /// holds the string is skipped with a message that says why.
    /// </summary>
    /// <param name="length">The length of the string in UTF-16 code units.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowStringTooLarge(int length) =>
        throw new InvalidOperationException(
            Invariant($"A string of {length} characters cannot be written as one JSON string (limit {_MaxJsonStringChars})."));

    /// <summary>
    /// A value could not be read through the accessor of its own type or formatted into its scratch buffer: a broken invariant (the
    /// type was just read from the same value, and every scratch is larger than the longest text of its type). Reported from a cold
    /// helper so the writers have no throw site and never write a key without its value. The packet is skipped.
    /// </summary>
    /// <param name="type">The type of the value that could not be written.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowValueUnreadable(FieldType type) =>
        throw new InvalidOperationException(Invariant($"A value of type {type} could not be read or formatted (a broken invariant)."));

    /// <summary>
    /// A packet without a field tree reached a tree writer: the shape check of the exporter should have refused it. A cold helper
    /// shared by the three tree writers. The message is the one every exporter with a hierarchy shape reports for a skip packet.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowSkipPacket() =>
        throw new InvalidOperationException("Skip packets cannot be exported (HasFieldTree is false).");

    #endregion

    #region Packet buffers

    /// <summary>
    /// Writes the additional buffers of <paramref name="packet"/> as a compact JSON array
    /// <c>[{"index":1,"bytes":"&lt;Base64&gt;"},…]</c> in ascending index order (the form of the flat, array, and compact writers).
    /// The caller writes the <c>buffers</c> key only when the packet has an additional buffer
    /// (<see cref="Packet.BufferCount"/> above 1), so a packet without buffers has no key.
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="packet">The packet whose buffers index 1 up to <see cref="Packet.BufferCount"/> - 1 are written.</param>
    /// <exception cref="InvalidOperationException">
    /// A buffer inside the packet's buffer count is missing, or the Base64 encoder did not finish (broken invariants).
    /// </exception>
    /// <remarks>
    /// The bytes are encoded into <paramref name="buffer"/> here, inside the exporter's <c>OnPacket</c>, because the packet's buffers are
    /// recycled afterwards. A bound empty buffer is <c>"bytes":""</c>, so the record stays and an empty buffer is not "absent".
    /// Perf: Base64 goes straight into the pooled buffer like the frame bytes (<see cref="WriteBase64String"/>), no string per buffer.
    /// Security: the Base64 text has no character that needs JSON escaping, so the capture bytes cannot break out of the string.
    /// </remarks>
    internal static void WriteBuffers(ref PooledBuffer buffer, Packet packet)
    {
        buffer.WriteByte((byte)'[');
        int bufferCount = packet.BufferCount;
        for (int index = 1; index < bufferCount; index++)
        {
            if (index > 1)
            {
                buffer.WriteByte((byte)',');
            }

            buffer.Write("{\"index\":"u8);
            WriteU64(ref buffer, (ulong)index);
            buffer.Write(",\"bytes\":"u8);
            WriteBase64String(ref buffer, PacketBuffers.Get(packet, index));
            buffer.WriteByte((byte)'}');
        }

        buffer.WriteByte((byte)']');
    }

    #endregion

    #region Addresses and timestamps

    /// <summary>
    /// Writes a MAC address as a quoted string in colon-separated hex format (XX:XX:XX:XX:XX:XX).
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="mac">The MAC address value.</param>
    /// <exception cref="InvalidOperationException">The text did not fit its stack buffer (a broken invariant).</exception>
    internal static void WriteMacAddress(ref PooledBuffer buffer, MacAddress mac)
    {
        // Fixed-size text (FormattedLength bytes), so the stack buffer always fits and needs no escaping.
        buffer.WriteByte((byte)'"');
        Span<byte> scratch = stackalloc byte[MacAddress.FormattedLength];
        if (!mac.TryFormat(scratch, out int written, default, CultureInfo.InvariantCulture))
        {
            ThrowValueUnreadable(FieldType.MacAddress);
        }

        buffer.Write(scratch[..written]);
        buffer.WriteByte((byte)'"');
    }

    /// <summary>
    /// Writes an IPv4 address as a quoted string in dotted decimal format.
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="ip">The IPv4 address value.</param>
    /// <exception cref="InvalidOperationException">The text did not fit its stack buffer (a broken invariant).</exception>
    internal static void WriteIPv4(ref PooledBuffer buffer, IPv4Address ip)
    {
        buffer.WriteByte((byte)'"');
        Span<byte> scratch = stackalloc byte[IPv4Address.MaxFormattedLength];
        if (!ip.TryFormat(scratch, out int written, default, CultureInfo.InvariantCulture))
        {
            ThrowValueUnreadable(FieldType.IPv4Address);
        }

        buffer.Write(scratch[..written]);
        buffer.WriteByte((byte)'"');
    }

    /// <summary>
    /// Writes a timestamp as a quoted string using the <see cref="Timestamp"/> text form
    /// (at most <see cref="Timestamp.MaxFormattedLength"/> bytes, so it never needs escaping).
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="ts">The timestamp value.</param>
    /// <exception cref="InvalidOperationException">The text did not fit its stack buffer (a broken invariant).</exception>
    internal static void WriteTimestamp(ref PooledBuffer buffer, Timestamp ts)
    {
        buffer.WriteByte((byte)'"');
        Span<byte> scratch = stackalloc byte[Timestamp.MaxFormattedLength];
        if (!ts.TryFormat(scratch, out int written, default, CultureInfo.InvariantCulture))
        {
            ThrowValueUnreadable(FieldType.Timestamp);
        }

        buffer.Write(scratch[..written]);
        buffer.WriteByte((byte)'"');
    }

    #endregion

    #region Field values

    /// <summary>
    /// Writes a field type as a quoted JSON string using the canonical enum name.
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="type">The field type to serialize.</param>
    internal static void WriteFieldTypeName(ref PooledBuffer buffer, FieldType type) =>
        WriteJsonString(ref buffer, FieldTypeFormatter.GetName(type));

    /// <summary>
    /// Writes a field value to the buffer in the appropriate JSON representation.
    /// Mapping: integers and floats become JSON numbers, <see cref="FieldType.Bool"/> becomes <c>true</c>/<c>false</c>,
    /// <see cref="FieldType.Bytes"/> becomes a base-64 string, and every address, UUID and timestamp type becomes a
    /// quoted text form. <see cref="FieldType.None"/> and unknown types become <c>null</c>.
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="value">The field value to serialize.</param>
    /// <exception cref="InvalidOperationException">The value could not be read or formatted (a broken invariant).</exception>
    internal static void WriteFieldValue(ref PooledBuffer buffer, FieldValue value)
    {
        FieldValueData data = value.Data;
        WriteFieldValueData(ref buffer, in data);
    }

    /// <summary>
    /// Writes a field value payload in the JSON representation of <see cref="WriteFieldValue"/>. The body of the mapping: staged
    /// values of the flat shapes (no <see cref="FieldValue"/> wrapper) use the same mapping as the tree writers.
    /// </summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="data">The payload to serialize.</param>
    /// <exception cref="InvalidOperationException">The value could not be read or formatted (a broken invariant).</exception>
    internal static void WriteFieldValueData(ref PooledBuffer buffer, in FieldValueData data)
    {
        switch (data.Type)
        {
            case FieldType.None:
                buffer.Write("null"u8);
                break;
            case FieldType.I64:
                if (!data.TryGetAsI64(out long i64))
                {
                    ThrowValueUnreadable(data.Type);
                }
                WriteI64(ref buffer, i64);
                break;
            case FieldType.U64:
                if (!data.TryGetAsU64(out ulong u64))
                {
                    ThrowValueUnreadable(data.Type);
                }
                WriteU64(ref buffer, u64);
                break;
            case FieldType.F64:
                if (!data.TryGetAsF64(out double f64))
                {
                    ThrowValueUnreadable(data.Type);
                }
                WriteF64(ref buffer, f64);
                break;
            case FieldType.String:
                if (!data.TryGetAsString(out string str))
                {
                    ThrowValueUnreadable(data.Type);
                }
                WriteJsonString(ref buffer, str);
                break;
            case FieldType.Bytes:
                if (!data.TryGetAsBytes(out ReadOnlyMemory<byte> bytesVal))
                {
                    ThrowValueUnreadable(data.Type);
                }
                WriteBase64String(ref buffer, bytesVal.Span);
                break;
            case FieldType.MacAddress:
                if (!data.TryGetAsMacAddress(out MacAddress mac))
                {
                    ThrowValueUnreadable(data.Type);
                }
                WriteMacAddress(ref buffer, mac);
                break;
            case FieldType.IPv4Address:
                if (!data.TryGetAsIPv4(out IPv4Address ipv4))
                {
                    ThrowValueUnreadable(data.Type);
                }
                WriteIPv4(ref buffer, ipv4);
                break;
            case FieldType.IPv6Address:
                if (!data.TryGetAsIPv6(out IPv6Address ipv6))
                {
                    ThrowValueUnreadable(data.Type);
                }
                // Scratch sizes below exceed the maximum text length of each type (IPv6 39, EUI-64 23, UUID 36);
                // none of these texts contains a character that needs JSON escaping.
                buffer.WriteByte((byte)'"');
                Span<byte> ipv6Scratch = stackalloc byte[64];
                if (!ipv6.TryFormat(ipv6Scratch, out int ipv6Written, default, CultureInfo.InvariantCulture))
                {
                    ThrowValueUnreadable(FieldType.IPv6Address);
                }

                buffer.Write(ipv6Scratch[..ipv6Written]);
                buffer.WriteByte((byte)'"');
                break;
            case FieldType.Eui64:
                if (!data.TryGetAsEui64(out Eui64 eui64))
                {
                    ThrowValueUnreadable(data.Type);
                }
                buffer.WriteByte((byte)'"');
                Span<byte> euiScratch = stackalloc byte[32];
                if (!eui64.TryFormat(euiScratch, out int euiWritten, default, CultureInfo.InvariantCulture))
                {
                    ThrowValueUnreadable(FieldType.Eui64);
                }

                buffer.Write(euiScratch[..euiWritten]);
                buffer.WriteByte((byte)'"');
                break;
            case FieldType.Uuid:
                if (!data.TryGetAsUuid(out NetworkInspector.Values.Uuid uuid))
                {
                    ThrowValueUnreadable(data.Type);
                }
                buffer.WriteByte((byte)'"');
                Span<byte> uuidScratch = stackalloc byte[64];
                if (!uuid.TryFormat(uuidScratch, out int uuidWritten, default, CultureInfo.InvariantCulture))
                {
                    ThrowValueUnreadable(FieldType.Uuid);
                }

                buffer.Write(uuidScratch[..uuidWritten]);
                buffer.WriteByte((byte)'"');
                break;
            case FieldType.Timestamp:
                if (!data.TryGetAsTimestamp(out Timestamp ts))
                {
                    ThrowValueUnreadable(data.Type);
                }
                WriteTimestamp(ref buffer, ts);
                break;
            case FieldType.Bool:
                if (!data.TryGetAsBool(out bool boolVal))
                {
                    ThrowValueUnreadable(data.Type);
                }
                WriteBool(ref buffer, boolVal);
                break;
            default:
                buffer.Write("null"u8);
                break;
        }
    }

    #endregion
}

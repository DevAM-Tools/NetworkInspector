// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp.Payload;

/// <summary>
/// Turns the bytes of a SOME/IP string into display text. The bytes come from the wire and are untrusted, so the result is always
/// a well-formed string: a byte or code unit that is not valid text is written as an escape instead of being dropped or replaced
/// by a placeholder, which keeps the raw value recoverable from the text.
/// <para>
/// Escape forms: <c>\xNN</c> for an invalid byte and for a control character below U+00A0 (C0, DEL and C1), <c>\uNNNN</c> for a
/// lone UTF-16 surrogate. A backslash in the data is not escaped; the ambiguity (a text that contains the characters
/// <c>\x41</c> looks like an escaped byte 0x41) is accepted because escaping it would make every ordinary text with a backslash
/// harder to read, and the raw bytes stay one click away.
/// </para>
/// <para>
/// Format facts applied before decoding: one terminating NUL (a single zero byte, or one zero code unit for UTF-16) is removed
/// because the format requires it on dynamic-length strings and it is not part of the text; a byte order mark at the start is
/// removed because the format requires one on UTF-8 and UTF-16 strings and it is not part of the text. The byte count that the
/// display shows is the count on the wire, including both.
/// </para>
/// <para>
/// <b>Security:</b> control characters are escaped because a payload is attacker-controlled text that ends up in a terminal, a log
/// line or a table cell; an ESC sequence or a line break in a string must not be able to rewrite the display (spoofing and
/// tampering of what the analyst reads). Rejected alternative: stripping them, which would make two different payloads display as
/// the same text. Accepted residual risk: a string of invalid bytes expands to four characters per byte; the expansion is bounded by
/// the payload size, which the message and reassembly limits already bound.
/// </para>
/// <para>
/// <b>Allocation:</b> two passes over the bytes, the first counts the characters and the second writes them into the string that
/// <see cref="string.Create{TState}(int, TState, SpanAction{char, TState})"/> allocates, so the result is the only allocation that
/// grows with the input and no builder exists whatever the length.
/// </para>
/// <para><b>Thread-safety:</b> stateless.</para>
/// </summary>
internal static class SomeIpTextDecoder
{
    #region Constants

    /// <summary>The first code point that is not a control character: U+00A0 follows the C1 block.</summary>
    private const int _FirstPrintable = 0xA0;

    /// <summary>The delete character, a control character outside the C0 block.</summary>
    private const int _Delete = 0x7F;

    /// <summary>The first printable ASCII character; everything below is a C0 control character.</summary>
    private const int _Space = 0x20;

    #endregion

    #region Public API

    /// <summary>Decodes the bytes of a string into display text.</summary>
    /// <param name="text">The string bytes without the length field. Memory and not a span, so the second pass can read them without a copy.</param>
    /// <param name="encoding">The encoding of the string.</param>
    /// <returns>The text; empty for an empty string. Never <see langword="null"/>, never throws for any input.</returns>
    internal static string Decode(ReadOnlyMemory<byte> text, SomeIpStringEncoding encoding)
    {
        bool utf16 = encoding is SomeIpStringEncoding.Utf16BigEndian or SomeIpStringEncoding.Utf16LittleEndian;
        ReadOnlySpan<byte> bytes = _StripFraming(text.Span, encoding, utf16);
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        // Pass one: count. Pass two: write. The same routine runs twice with a different destination so the two cannot disagree
        // about the length, which would otherwise corrupt the string that string.Create hands out. A span cannot be captured by
        // the callback, so the callback re-slices the memory by the same framing rule.
        int length = _Emit(bytes, encoding, default);
        if (length == 0)
        {
            return string.Empty;
        }

        return string.Create(length, (Text: text, Encoding: encoding, Utf16: utf16), static (destination, state) =>
        {
            _ = _Emit(_StripFraming(state.Text.Span, state.Encoding, state.Utf16), state.Encoding, destination);
        });
    }

    #endregion

    #region Framing

    /// <summary>Removes one terminating NUL and a leading byte order mark.</summary>
    private static ReadOnlySpan<byte> _StripFraming(ReadOnlySpan<byte> bytes, SomeIpStringEncoding encoding, bool utf16)
    {
        // The terminator first, so a string that consists of nothing but a byte order mark and a NUL ends up empty.
        // An odd byte count under UTF-16 has no whole terminating code unit; the trailing byte is shown as an escape instead.
        if (utf16)
        {
            if (bytes.Length >= 2 && (bytes.Length & 1) == 0 && bytes[^1] == 0 && bytes[^2] == 0)
            {
                bytes = bytes[..^2];
            }
        }
        else if (!bytes.IsEmpty && bytes[^1] == 0)
        {
            bytes = bytes[..^1];
        }

        switch (encoding)
        {
            case SomeIpStringEncoding.Utf8:
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    bytes = bytes[3..];
                }

                break;
            case SomeIpStringEncoding.Utf16BigEndian:
                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                {
                    bytes = bytes[2..];
                }

                break;
            case SomeIpStringEncoding.Utf16LittleEndian:
                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                {
                    bytes = bytes[2..];
                }

                break;
        }

        return bytes;
    }

    #endregion

    #region Emission

    /// <summary>Writes (or, with an empty destination, only counts) the text of the bytes and returns its length.</summary>
    private static int _Emit(ReadOnlySpan<byte> bytes, SomeIpStringEncoding encoding, Span<char> destination)
    {
        Sink sink = new(destination);
        switch (encoding)
        {
            case SomeIpStringEncoding.Ascii:
                foreach (byte b in bytes)
                {
                    // Seven-bit characters only; a byte with the high bit is not ASCII and is shown as the byte it is.
                    if (b is < _Space or >= _Delete)
                    {
                        sink.WriteByteEscape(b);
                    }
                    else
                    {
                        sink.Write((char)b);
                    }
                }

                break;
            case SomeIpStringEncoding.Utf16BigEndian:
                _EmitUtf16(bytes, bigEndian: true, ref sink);
                break;
            case SomeIpStringEncoding.Utf16LittleEndian:
                _EmitUtf16(bytes, bigEndian: false, ref sink);
                break;
            default:
                _EmitUtf8(bytes, ref sink);
                break;
        }

        return sink.Length;
    }

    private static void _EmitUtf8(ReadOnlySpan<byte> bytes, ref Sink sink)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            // A strict decoder: an invalid or cut-off sequence escapes its first byte only and decoding resumes at the next byte,
            // so one bad byte never swallows the valid text behind it.
            OperationStatus status = Rune.DecodeFromUtf8(bytes[offset..], out Rune rune, out int consumed);
            if (status != OperationStatus.Done)
            {
                sink.WriteByteEscape(bytes[offset]);
                offset++;
                continue;
            }

            sink.WriteRune(rune);
            offset += consumed;
        }
    }

    private static void _EmitUtf16(ReadOnlySpan<byte> bytes, bool bigEndian, ref Sink sink)
    {
        int offset = 0;
        while (offset + 1 < bytes.Length)
        {
            char unit = _ReadUnit(bytes[offset..], bigEndian);
            offset += 2;

            if (char.IsHighSurrogate(unit))
            {
                // A high surrogate is valid only when a low surrogate follows directly; otherwise it is shown as the unit it is
                // and the next unit is read on its own.
                if (offset + 1 < bytes.Length)
                {
                    char next = _ReadUnit(bytes[offset..], bigEndian);
                    if (char.IsLowSurrogate(next))
                    {
                        sink.Write(unit);
                        sink.Write(next);
                        offset += 2;
                        continue;
                    }
                }

                sink.WriteUnitEscape(unit);
                continue;
            }

            if (char.IsLowSurrogate(unit))
            {
                sink.WriteUnitEscape(unit);
                continue;
            }

            sink.WriteRune(new Rune(unit));
        }

        // A lone trailing byte cannot be a code unit; it is shown as the byte it is.
        if (offset < bytes.Length)
        {
            sink.WriteByteEscape(bytes[offset]);
        }
    }

    private static char _ReadUnit(ReadOnlySpan<byte> bytes, bool bigEndian) =>
        (char)(bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes)
            : BinaryPrimitives.ReadUInt16LittleEndian(bytes));

    /// <summary>
    /// The destination of one pass. An empty destination makes every write a count, so the counting pass and the writing pass
    /// run the same code.
    /// </summary>
    private ref struct Sink(Span<char> destination)
    {
        private const string _HexDigits = "0123456789ABCDEF";

        private readonly Span<char> _Destination = destination;

        /// <summary>The number of characters written or counted so far.</summary>
        internal int Length { get; private set; }

        internal void Write(char c)
        {
            if (!_Destination.IsEmpty)
            {
                _Destination[Length] = c;
            }

            Length++;
        }

        internal void WriteRune(Rune rune)
        {
            // Control characters of the C0 block, DEL and the C1 block are escaped as the byte value they have.
            if (rune.Value < _FirstPrintable && (rune.Value < _Space || rune.Value >= _Delete))
            {
                WriteByteEscape((byte)rune.Value);
                return;
            }

            if (rune.IsBmp)
            {
                Write((char)rune.Value);
                return;
            }

            Span<char> pair = stackalloc char[2];
            _ = rune.EncodeToUtf16(pair);
            Write(pair[0]);
            Write(pair[1]);
        }

        internal void WriteByteEscape(byte value)
        {
            Write('\\');
            Write('x');
            Write(_HexDigits[value >> 4]);
            Write(_HexDigits[value & 0xF]);
        }

        internal void WriteUnitEscape(char unit)
        {
            Write('\\');
            Write('u');
            Write(_HexDigits[(unit >> 12) & 0xF]);
            Write(_HexDigits[(unit >> 8) & 0xF]);
            Write(_HexDigits[(unit >> 4) & 0xF]);
            Write(_HexDigits[unit & 0xF]);
        }
    }

    #endregion
}

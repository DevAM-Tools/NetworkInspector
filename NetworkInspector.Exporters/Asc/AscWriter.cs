// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Asc;

/// <summary>
/// Low-level ASCII text writer for the ASC (Vector CANalyzer) log format.
/// Formats individual frame records as text lines and writes them directly to
/// the target stream. Uses a reusable <see cref="PooledBuffer"/> scratch buffer so
/// that no heap allocation occurs on the per-frame hot path.
/// <para>
/// The file header (<c>date</c>, <c>base hex</c>, <c>Begin Triggerblock</c>) is
/// written by the constructor; the footer (<c>End TriggerBlock</c>) is written by
/// <see cref="Finish"/>. Frame records are written via <c>Write*</c> methods.
/// </para>
/// <para>
/// All numeric identifiers and data bytes are written in uppercase hexadecimal;
/// timestamps, channel numbers, DLC, and length fields are written in decimal.
/// Line endings are <c>\r\n</c> for compatibility with Vector tooling.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. All calls must be made from the same thread.
/// </para>
/// </summary>
internal sealed class AscWriter
{
    #region Constants

    /// <summary>Uppercase hex digit lookup table (ASCII).</summary>
    private static ReadOnlySpan<byte> _HexDigits => "0123456789ABCDEF"u8;

    /// <summary>Windows-style line ending.</summary>
    private static ReadOnlySpan<byte> _CrLf => "\r\n"u8;

    #endregion

    #region Fields

    /// <summary>Anchor timestamp for computing relative offsets (nanoseconds since Unix epoch).</summary>
    private readonly long _AnchorNs;

    /// <summary>Target stream. Owned by the caller; not disposed here.</summary>
    private readonly Stream _Stream;

    /// <summary>
    /// Per-line build buffer. Content is flushed to <see cref="_Stream"/> and reset
    /// after every line so that peak memory use is proportional to the longest single line.
    /// </summary>
    private readonly PooledBuffer _LineBuffer = new(256);

    #endregion

    #region Construction

    /// <summary>
    /// Creates a new <see cref="AscWriter"/> and writes the ASC file header
    /// (<c>date</c>, <c>base hex</c>, <c>no internal events logged</c>,
    /// <c>Begin Triggerblock</c>) to the stream.
    /// </summary>
    /// <param name="stream">Target output stream (must be open for writing).</param>
    /// <param name="anchorNs">
    /// Capture start time in nanoseconds since Unix epoch. Used both as the
    /// date/time in the header and as the baseline for computing relative timestamps.
    /// Pass 0 to produce an epoch-zero header (suitable for empty exports).
    /// </param>
    internal AscWriter(Stream stream, long anchorNs)
    {
        _Stream = stream;
        _AnchorNs = Math.Max(0L, anchorNs);
        _WriteHeader(_AnchorNs);
    }

    #endregion

    #region Public write methods

    /// <summary>
    /// Writes a CAN classic frame line.
    /// </summary>
    /// <remarks>
    /// Output format: <c>{ts} {ch} {id}[x] Rx d|r {dlc} [data bytes...]</c>
    /// <list type="bullet">
    /// <item>Timestamp: decimal seconds, 6 decimal places.</item>
    /// <item>Channel: decimal integer.</item>
    /// <item>CAN ID: 3-char uppercase hex for standard frames (11-bit);
    ///   8-char uppercase hex with trailing <c>x</c> for extended frames (29-bit).</item>
    /// <item>DLC: decimal integer.</item>
    /// <item>Data bytes: 2-char uppercase hex each, space-separated.</item>
    /// </list>
    /// </remarks>
    /// <param name="timestampNs">Frame timestamp in nanoseconds since Unix epoch.</param>
    /// <param name="channel">CAN channel number (decimal).</param>
    /// <param name="rawCanId">29-bit CAN ID without flag bits (bits 28–0).</param>
    /// <param name="isExtended">Whether the frame uses the Extended Frame Format (29-bit ID).</param>
    /// <param name="isRemote">Whether the frame is a Remote Transmission Request (RTR).</param>
    /// <param name="dlc">Data Length Code (0–8).</param>
    /// <param name="data">Payload bytes (0–8 bytes).</param>
    internal void WriteCanMessage(
        long timestampNs, int channel,
        uint rawCanId, bool isExtended, bool isRemote, byte dlc,
        ReadOnlySpan<byte> data)
    {
        _AppendTimestamp(timestampNs);
        _LineBuffer.WriteByte((byte)' ');
        _AppendDecimalInt(channel);
        _LineBuffer.WriteByte((byte)' ');

        // CAN ID: 3-char hex for standard (11-bit), 8-char hex + 'x' for extended (29-bit).
        if (isExtended)
        {
            _AppendHexUInt32(rawCanId, 8);
            _LineBuffer.WriteByte((byte)'x');
        }
        else
        {
            _AppendHexUInt32(rawCanId, 3);
        }

        _LineBuffer.Write(" Rx "u8);
        _LineBuffer.WriteByte(isRemote ? (byte)'r' : (byte)'d');
        _LineBuffer.WriteByte((byte)' ');
        _AppendDecimalInt(dlc);

        // Remote frames carry no data bytes.
        if (!isRemote)
        {
            foreach (byte b in data)
            {
                _LineBuffer.WriteByte((byte)' ');
                _AppendHexByte(b);
            }
        }

        _LineBuffer.Write(_CrLf);
        _FlushLine();
    }

    /// <summary>
    /// Writes a CAN FD frame line.
    /// </summary>
    /// <remarks>
    /// Output format: <c>{ts} CANFD {ch} Rx {id}[x] {brs} {esi} {dlc} {dlen} [data bytes...]</c>
    /// <list type="bullet">
    /// <item>BRS and ESI: decimal <c>0</c> or <c>1</c>.</item>
    /// <item>DLC: decimal integer (FD DLC code, 0–15).</item>
    /// <item>Data length: decimal integer (actual byte count, 0–64).</item>
    /// <item>Data bytes: 2-char uppercase hex each, space-separated.</item>
    /// </list>
    /// </remarks>
    /// <param name="timestampNs">Frame timestamp in nanoseconds since Unix epoch.</param>
    /// <param name="channel">CAN channel number (decimal).</param>
    /// <param name="rawCanId">29-bit CAN ID without flag bits.</param>
    /// <param name="isExtended">Whether the frame uses the Extended Frame Format.</param>
    /// <param name="brs">Bit Rate Switch flag.</param>
    /// <param name="esi">Error State Indicator flag.</param>
    /// <param name="dlc">Data Length Code (0–15 for CAN FD).</param>
    /// <param name="data">Payload bytes (0–64 bytes).</param>
    internal void WriteCanFdMessage(
        long timestampNs, int channel,
        uint rawCanId, bool isExtended, bool brs, bool esi, byte dlc,
        ReadOnlySpan<byte> data)
    {
        _AppendTimestamp(timestampNs);
        _LineBuffer.Write(" CANFD "u8);
        _AppendDecimalInt(channel);
        _LineBuffer.Write(" Rx "u8);

        if (isExtended)
        {
            _AppendHexUInt32(rawCanId, 8);
            _LineBuffer.WriteByte((byte)'x');
        }
        else
        {
            _AppendHexUInt32(rawCanId, 3);
        }

        _LineBuffer.WriteByte((byte)' ');
        _LineBuffer.WriteByte(brs ? (byte)'1' : (byte)'0');
        _LineBuffer.WriteByte((byte)' ');
        _LineBuffer.WriteByte(esi ? (byte)'1' : (byte)'0');
        _LineBuffer.WriteByte((byte)' ');
        _AppendDecimalInt(dlc);
        _LineBuffer.WriteByte((byte)' ');
        _AppendDecimalInt(data.Length);

        foreach (byte b in data)
        {
            _LineBuffer.WriteByte((byte)' ');
            _AppendHexByte(b);
        }

        _LineBuffer.Write(_CrLf);
        _FlushLine();
    }

    /// <summary>
    /// Writes a LIN frame line.
    /// </summary>
    /// <remarks>
    /// Output format:
    /// <c>{ts} L{ch} {frameId:X2} Rx {dlc} [data bytes...] checksum = {cs:X2} CSM = enhanced</c>
    /// <list type="bullet">
    /// <item>Channel: decimal integer prefixed with <c>L</c> (e.g., <c>L1</c>).</item>
    /// <item>Frame ID: 2-char uppercase hex (6-bit value, 0x00–0x3F).</item>
    /// <item>DLC: decimal integer (data byte count).</item>
    /// <item>Data bytes: 2-char uppercase hex each, space-separated.</item>
    /// <item>Checksum: 2-char uppercase hex.</item>
    /// <item>Checksum method: always <c>enhanced</c> (most common in LIN 2.x).</item>
    /// </list>
    /// </remarks>
    /// <param name="timestampNs">Frame timestamp in nanoseconds since Unix epoch.</param>
    /// <param name="channel">LIN channel number (decimal, written as <c>L{channel}</c>).</param>
    /// <param name="frameId">6-bit LIN frame identifier (0–63).</param>
    /// <param name="data">Payload bytes (0–8 bytes).</param>
    /// <param name="checksum">LIN checksum byte.</param>
    internal void WriteLinMessage(
        long timestampNs, int channel,
        byte frameId, ReadOnlySpan<byte> data, byte checksum)
    {
        _AppendTimestamp(timestampNs);
        _LineBuffer.WriteByte((byte)' ');
        _LineBuffer.WriteByte((byte)'L');
        _AppendDecimalInt(channel);
        _LineBuffer.WriteByte((byte)' ');
        _AppendHexByte((byte)(frameId & 0x3F));
        _LineBuffer.Write(" Rx "u8);
        _AppendDecimalInt(data.Length);

        foreach (byte b in data)
        {
            _LineBuffer.WriteByte((byte)' ');
            _AppendHexByte(b);
        }

        _LineBuffer.Write(" checksum = "u8);
        _AppendHexByte(checksum);
        _LineBuffer.Write(" CSM = enhanced"u8);
        _LineBuffer.Write(_CrLf);
        _FlushLine();
    }

    /// <summary>
    /// Writes a FlexRay frame line.
    /// </summary>
    /// <remarks>
    /// Output format:
    /// <c>{ts} Fr {ch} V9 {frameId:X4} {payloadWords} {cycle} 0 {headerCrc:X4} x {dlen} [data bytes...]</c>
    /// <list type="bullet">
    /// <item>Channel: decimal integer (raw value from DLT_FLEXRAY header byte 0).</item>
    /// <item>Frame ID: 4-char uppercase hex (11-bit slot ID, 0x0000–0x07FF).</item>
    /// <item>Payload words: decimal ceiling count of 16-bit words needed for the payload.</item>
    /// <item>Cycle: decimal integer (0–63).</item>
    /// <item>NM flag: always <c>0</c> (not available from the frame data).</item>
    /// <item>Header CRC: 4-char uppercase hex.</item>
    /// <item>Identifier: literal <c>x</c> placeholder token.</item>
    /// <item>Data length: decimal integer (actual byte count).</item>
    /// <item>Data bytes: 2-char uppercase hex each, space-separated.</item>
    /// </list>
    /// </remarks>
    /// <param name="timestampNs">Frame timestamp in nanoseconds since Unix epoch.</param>
    /// <param name="channel">FlexRay physical channel (from DLT_FLEXRAY header byte 0).</param>
    /// <param name="frameId">11-bit FlexRay slot/frame ID.</param>
    /// <param name="cycle">Cycle counter (0–63).</param>
    /// <param name="headerCrc">FlexRay header CRC value.</param>
    /// <param name="data">Payload bytes (0–254 bytes).</param>
    internal void WriteFlexRayMessage(
        long timestampNs, int channel,
        ushort frameId, byte cycle, ushort headerCrc,
        ReadOnlySpan<byte> data)
    {
        // Payload length in 16-bit words (ceiling division).
        int payloadWords = (data.Length + 1) / 2;

        _AppendTimestamp(timestampNs);
        _LineBuffer.Write(" Fr "u8);
        _AppendDecimalInt(channel);
        _LineBuffer.Write(" V9 "u8);
        _AppendHexUInt16(frameId, 4);
        _LineBuffer.WriteByte((byte)' ');
        _AppendDecimalInt(payloadWords);
        _LineBuffer.WriteByte((byte)' ');
        _AppendDecimalInt(cycle);
        _LineBuffer.Write(" 0 "u8); // NM flag = 0
        _AppendHexUInt16(headerCrc, 4);
        _LineBuffer.Write(" x "u8); // identifier placeholder
        _AppendDecimalInt(data.Length);

        foreach (byte b in data)
        {
            _LineBuffer.WriteByte((byte)' ');
            _AppendHexByte(b);
        }

        _LineBuffer.Write(_CrLf);
        _FlushLine();
    }

    /// <summary>
    /// Writes the <c>End TriggerBlock</c> footer line and flushes the stream.
    /// Must be called exactly once, after all frame lines have been written.
    /// </summary>
    internal void Finish()
    {
        _LineBuffer.Write("End TriggerBlock"u8);
        _LineBuffer.Write(_CrLf);
        _FlushLine();
        _Stream.Flush();
    }

    /// <summary>
    /// Returns the internal <see cref="PooledBuffer"/> to the array pool.
    /// Must be called when the writer is no longer needed (from the owner's cleanup).
    /// Idempotent.
    /// </summary>
    internal void Return() => _LineBuffer.Return();

    #endregion

    #region Private helpers

    /// <summary>
    /// Writes the ASC file header to the stream. This is a one-time operation called
    /// from the constructor. String allocations here are acceptable since the header
    /// is written only once per export.
    /// </summary>
    /// <param name="anchorNs">Capture start time in nanoseconds since Unix epoch (non-negative).</param>
    private void _WriteHeader(long anchorNs)
    {
        // Convert to UTC DateTimeOffset for the date string.
        DateTimeOffset dto = DateTimeOffset.FromUnixTimeMilliseconds(anchorNs / 1_000_000);
        string dateStr = _FormatAscDate(dto);
        byte[] dateBytes = Encoding.ASCII.GetBytes(dateStr);

        // date <dateStr>
        _LineBuffer.Write("date "u8);
        _LineBuffer.Write(dateBytes);
        _LineBuffer.Write(_CrLf);
        _FlushLine();

        // base hex  timestamps absolute
        _LineBuffer.Write("base hex  timestamps absolute"u8);
        _LineBuffer.Write(_CrLf);
        _FlushLine();

        // no internal events logged
        _LineBuffer.Write("no internal events logged"u8);
        _LineBuffer.Write(_CrLf);
        _FlushLine();

        // Begin Triggerblock <dateStr>
        _LineBuffer.Write("Begin Triggerblock "u8);
        _LineBuffer.Write(dateBytes);
        _LineBuffer.Write(_CrLf);
        _FlushLine();
    }

    /// <summary>
    /// Formats a <see cref="DateTimeOffset"/> as an ASC-compatible date string.
    /// Uses Unix <c>ctime</c>-style format: <c>ddd MMM  d HH:mm:ss.fff yyyy</c>
    /// where single-digit days are space-padded (e.g., <c>Mon Jan  1 10:00:00.000 2024</c>).
    /// </summary>
    private static string _FormatAscDate(DateTimeOffset dto)
    {
        string dow = dto.ToString("ddd", CultureInfo.InvariantCulture);
        string mon = dto.ToString("MMM", CultureInfo.InvariantCulture);
        // Space-pad single-digit day to match ctime output (e.g., " 1" not "01").
        string day = dto.Day < 10
            ? " " + dto.Day.ToString(CultureInfo.InvariantCulture)
            : dto.Day.ToString(CultureInfo.InvariantCulture);
        string time = dto.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string year = dto.Year.ToString(CultureInfo.InvariantCulture);
        return $"{dow} {mon} {day} {time} {year}";
    }

    /// <summary>
    /// Appends the relative timestamp (6 decimal places) to <see cref="_LineBuffer"/>.
    /// The relative time is <c>max(0, (timestampNs − anchorNs) / 1e9)</c>.
    /// Uses <c>Utf8Formatter.TryFormat</c> with format specifier <c>F6</c> for
    /// zero-allocation decimal formatting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _AppendTimestamp(long timestampNs)
    {
        double relSec = Math.Max(0.0, (timestampNs - _AnchorNs) / 1_000_000_000.0);
        Span<byte> scratch = stackalloc byte[32];
        Utf8Formatter.TryFormat(relSec, scratch, out int written, new StandardFormat('F', 6));
        _LineBuffer.Write(scratch[..written]);
    }

    /// <summary>
    /// Appends exactly 2 uppercase hex ASCII digits for the given byte
    /// to <see cref="_LineBuffer"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _AppendHexByte(byte b)
    {
        ReadOnlySpan<byte> hex = _HexDigits;
        _LineBuffer.WriteByte(hex[b >> 4]);
        _LineBuffer.WriteByte(hex[b & 0xF]);
    }

    /// <summary>
    /// Appends a 16-bit unsigned integer as uppercase hex to <see cref="_LineBuffer"/>,
    /// zero-padded to at least <paramref name="minWidth"/> characters (1–4).
    /// Leading zeros beyond the minimum width are suppressed.
    /// </summary>
    /// <param name="value">Value to format.</param>
    /// <param name="minWidth">Minimum output width (1–4). Used for FlexRay slot IDs and CRCs.</param>
    private void _AppendHexUInt16(ushort value, int minWidth)
    {
        ReadOnlySpan<byte> hex = _HexDigits;
        // Produce all 4 hex digits, then trim leading zeros down to minWidth.
        Span<byte> buf = stackalloc byte[4];
        buf[0] = hex[(value >> 12) & 0xF];
        buf[1] = hex[(value >> 8) & 0xF];
        buf[2] = hex[(value >> 4) & 0xF];
        buf[3] = hex[value & 0xF];
        int start = 0;
        while (start < 4 - minWidth && buf[start] == (byte)'0')
        {
            start++;
        }
        _LineBuffer.Write(buf[start..]);
    }

    /// <summary>
    /// Appends a 32-bit unsigned integer as uppercase hex to <see cref="_LineBuffer"/>,
    /// zero-padded to at least <paramref name="minWidth"/> characters (1–8).
    /// Leading zeros beyond the minimum width are suppressed.
    /// </summary>
    /// <param name="value">Value to format.</param>
    /// <param name="minWidth">Minimum output width (1–8). Use 3 for standard CAN IDs, 8 for extended.</param>
    private void _AppendHexUInt32(uint value, int minWidth)
    {
        ReadOnlySpan<byte> hex = _HexDigits;
        // Produce all 8 hex digits, then trim leading zeros down to minWidth.
        Span<byte> buf = stackalloc byte[8];
        buf[0] = hex[(int)((value >> 28) & 0xF)];
        buf[1] = hex[(int)((value >> 24) & 0xF)];
        buf[2] = hex[(int)((value >> 20) & 0xF)];
        buf[3] = hex[(int)((value >> 16) & 0xF)];
        buf[4] = hex[(int)((value >> 12) & 0xF)];
        buf[5] = hex[(int)((value >> 8) & 0xF)];
        buf[6] = hex[(int)((value >> 4) & 0xF)];
        buf[7] = hex[(int)(value & 0xF)];
        int start = 0;
        while (start < 8 - minWidth && buf[start] == (byte)'0')
        {
            start++;
        }
        _LineBuffer.Write(buf[start..]);
    }

    /// <summary>
    /// Appends a non-negative decimal integer to <see cref="_LineBuffer"/>.
    /// Uses <c>Utf8Formatter.TryFormat</c> for zero-allocation decimal formatting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _AppendDecimalInt(int value)
    {
        Span<byte> scratch = stackalloc byte[12];
        Utf8Formatter.TryFormat(value, scratch, out int written);
        _LineBuffer.Write(scratch[..written]);
    }

    /// <summary>
    /// Flushes the content of <see cref="_LineBuffer"/> to the stream and resets it.
    /// The reset is guaranteed by a <c>finally</c> block so stale data cannot accumulate
    /// when the stream write throws.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _FlushLine()
    {
        try
        {
            _Stream.Write(_LineBuffer.WrittenSpan);
        }
        finally
        {
            // Always reset so partial/failed lines do not corrupt subsequent writes.
            _LineBuffer.Reset();
        }
    }

    #endregion
}

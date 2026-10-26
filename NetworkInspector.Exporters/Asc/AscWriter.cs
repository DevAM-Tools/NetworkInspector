// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Asc;

/// <summary>
/// Low-level ASCII text writer for the ASC (Vector CANalyzer) log format.
/// Formats individual frame records as text lines and writes them directly to
/// the target stream. Every line is built as raw ASCII bytes in reusable <see cref="PooledBuffer"/> instances
/// (stack scratch for numbers, no <c>string</c> formatting) so that no heap allocation occurs on the per-frame hot path;
/// only the one-time header allocates. Lines are collected in a 64 KiB output buffer and written to the stream in large
/// blocks.
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

    /// <summary>
    /// Uppercase hex digit lookup table (ASCII). A <c>u8</c> literal property returns a span over static data in the
    /// assembly, so there is no array allocation and no static initialisation cost.
    /// </summary>
    private static ReadOnlySpan<byte> _HexDigits => "0123456789ABCDEF"u8;

    /// <summary>Windows-style line ending (CRLF), for compatibility with Vector tooling.</summary>
    private static ReadOnlySpan<byte> _CrLf => "\r\n"u8;

    #endregion

    #region Fields

    /// <summary>Anchor timestamp for computing relative offsets (nanoseconds since Unix epoch).</summary>
    private readonly long _AnchorNs;

    /// <summary>Fraction digits of the line timestamps (6 to 9).</summary>
    private readonly int _TimestampDecimals;

    /// <summary>Target stream. Owned by the caller; not disposed here.</summary>
    private readonly Stream _Stream;

    /// <summary>
    /// Per-line build buffer. Content is appended into <see cref="_OutputBuffer"/> and reset
    /// after every line so that peak per-line memory use is proportional to the longest single line.
    /// A line is assembled here first (instead of directly in the output buffer) so that a line is either appended
    /// whole or not at all; a failure while formatting cannot leave a partial line in the output.
    /// The initial 256 bytes cover typical CAN, LIN and FlexRay lines; longer Ethernet and CAN XL lines grow it once.
    /// </summary>
    private readonly PooledBuffer _LineBuffer;

    /// <summary>
    /// Accumulates multiple ASC lines before writing to <see cref="_Stream"/>, reducing
    /// stream write crossings (BufferedStream still helps, but this batches at the writer layer).
    /// Flushed when full, on <see cref="Finish"/>, or when a write would exceed capacity.
    /// The 64 KiB size is a tuning value, not a format fact; it is rented from the array pool and stays below the
    /// large-object-heap threshold (about 85 KB). It must match the flush threshold in <see cref="_FlushLine"/>.
    /// </summary>
    private readonly PooledBuffer _OutputBuffer;

    /// <summary>Bytes written to <see cref="_Stream"/> (committed output).</summary>
    private long _BytesWritten;

    #endregion

    #region Properties

    /// <summary>
    /// Approximate output size if all line and output buffers were flushed now.
    /// </summary>
    internal long EstimatedOutputBytes =>
        _BytesWritten + _OutputBuffer.Length + _LineBuffer.Length;

    #endregion

    #region Construction

    /// <summary>
    /// Creates a new <see cref="AscWriter"/> and emits the ASC file header
    /// (<c>date</c>, <c>base hex</c>, <c>no internal events logged</c>,
    /// <c>Begin Triggerblock</c>) into the output buffer (it reaches the stream with the first flush).
    /// </summary>
    /// <param name="stream">Target output stream (must be open for writing).</param>
    /// <param name="anchorNs">
    /// Capture start time in nanoseconds since Unix epoch. Used both as the
    /// date/time in the header and as the baseline for computing relative timestamps.
    /// Pass 0 to produce an epoch-zero header (suitable for empty exports).
    /// </param>
    /// <param name="timestampDecimals">
    /// Fraction digits of the line timestamps, 6 (microseconds, the CANoe default) to 9 (nanoseconds). Values are
    /// rounded half up to that many digits; the ASC reader keeps up to nine.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timestampDecimals"/> is outside 6..9.</exception>
    /// <remarks>
    /// <paramref name="stream"/> is not null-checked: this type is internal and its only caller passes a non-null stream.
    /// The header is only appended to the internal output buffer, so a failing stream surfaces on a later write or in
    /// <see cref="Finish"/>, not here.
    /// </remarks>
    internal AscWriter(Stream stream, long anchorNs, int timestampDecimals = 6)
    {
        // 6..9 digits: microseconds (CANoe's own resolution) up to nanoseconds (the frame timestamp resolution).
        // More digits would only append zeros and the lookup table in _AppendTimestamp stops at 10^9.
        ArgumentOutOfRangeException.ThrowIfLessThan(timestampDecimals, 6);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timestampDecimals, 9);
        _Stream = stream;

        // A negative anchor would make the epoch-based header date and the relative-time subtraction meaningless;
        // clamping keeps the "anchor >= 0" invariant that _AppendTimestamp relies on to rule out overflow.
        _AnchorNs = Math.Max(0L, anchorNs);
        _TimestampDecimals = timestampDecimals;

        // The pooled buffers are rented only now, after the arguments passed validation: a field initializer would rent
        // them before the checks above and leak both arrays to the pool when the constructor throws.
        _LineBuffer = new PooledBuffer(256);
        _OutputBuffer = new PooledBuffer(65_536);
        try
        {
            _WriteHeader(_AnchorNs);
        }
        catch
        {
            // The owner never gets a writer to call Return on, so the buffers go back here.
            Return();
            throw;
        }
    }

    #endregion

    #region Public write methods

    /// <summary>
    /// Writes a CAN classic frame line.
    /// </summary>
    /// <remarks>
    /// Output format: <c>{ts} {ch} {id}[x] Rx d|r {dlc} [data bytes...]</c>
    /// <list type="bullet">
    /// <item>Timestamp: decimal seconds with the configured number of fraction digits (6 to 9).</item>
    /// <item>Channel: decimal integer.</item>
    /// <item>CAN ID: 3-char uppercase hex for standard frames (11-bit);
    ///   8-char uppercase hex with trailing <c>x</c> for extended frames (29-bit).</item>
    /// <item>DLC: one uppercase hex digit (<c>0</c>–<c>F</c>), because the header declares <c>base hex</c>; 0–9 read the
    ///   same in both bases, a bus DLC of 10–15 must be <c>A</c>–<c>F</c>.</item>
    /// <item>Data bytes: 2-char uppercase hex each, space-separated.</item>
    /// </list>
    /// Direction is always Rx. SocketCAN, DLT_LIN, and LINKTYPE_FLEXRAY payloads
    /// do not carry Tx versus Rx, and the ASC parser discards the direction token.
    /// </remarks>
    /// <param name="timestampNs">Frame timestamp in nanoseconds since Unix epoch.</param>
    /// <param name="channel">CAN channel number (decimal).</param>
    /// <param name="rawCanId">29-bit CAN ID without flag bits (bits 28–0).</param>
    /// <param name="isExtended">Whether the frame uses the Extended Frame Format (29-bit ID).</param>
    /// <param name="isRemote">Whether the frame is a Remote Transmission Request (RTR).</param>
    /// <param name="dlc">Bus Data Length Code (0–15; 9–15 still carry 8 data bytes).</param>
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

        // No Tx/Rx bit on the frame. Parser ignores this token. Keep Rx.
        _LineBuffer.Write(" Rx "u8);
        _LineBuffer.WriteByte(isRemote ? (byte)'r' : (byte)'d');
        _LineBuffer.WriteByte((byte)' ');
        _LineBuffer.WriteByte(_HexDigits[dlc & 0x0F]);

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
    /// <item>DLC: one uppercase hex digit (0–9, A–F). The file header is <c>base hex</c>.</item>
    /// <item>Data length: decimal integer (actual byte count, 0–64).</item>
    /// <item>Data bytes: 2-char uppercase hex each, space-separated.</item>
    /// </list>
    /// Direction is always Rx. SocketCAN, DLT_LIN, and LINKTYPE_FLEXRAY payloads
    /// do not carry Tx versus Rx, and the ASC parser discards the direction token.
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
        // No Tx/Rx bit on the frame. Parser ignores this token. Keep Rx.
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
        // DLC is hex under base hex. One digit keeps 10..15 distinct from the decimal data length. The caller passes a DLC
        // code (0..15) from the FD length table; the mask is the same as on the classic path and keeps a stray value from
        // indexing past the 16 digits (an exception here would abort the frame's line half-built).
        _LineBuffer.WriteByte(_HexDigits[dlc & 0x0F]);
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
    /// <c>{ts} Li|{L2..} {frameId:X2} Rx {dlc} [data bytes...] checksum = {cs:X2} CSM = classic|enhanced</c>
    /// <list type="bullet">
    /// <item>Channel 1 is <c>Li</c>. Channels 2 and above are <c>L</c> plus the decimal number.</item>
    /// <item>Frame ID: 2-char uppercase hex (6-bit value, 0x00–0x3F).</item>
    /// <item>DLC: decimal integer (data byte count).</item>
    /// <item>Data bytes: 2-char uppercase hex each, space-separated.</item>
    /// <item>Checksum: 2-char uppercase hex.</item>
    /// <item>Checksum model: <c>classic</c> or <c>enhanced</c> from DLT_LIN byte 4 bits 1-0 (1 = classic, 2 = enhanced);
    ///   <c>enhanced</c> (the LIN 2.x default) when the frame does not state it.</item>
    /// </list>
    /// Direction is always Rx. SocketCAN, DLT_LIN, and LINKTYPE_FLEXRAY payloads
    /// do not carry Tx versus Rx, and the ASC parser discards the direction token.
    /// </remarks>
    /// <param name="timestampNs">Frame timestamp in nanoseconds since Unix epoch.</param>
    /// <param name="channel">LIN channel number. Channel 1 is written as <c>Li</c>.</param>
    /// <param name="frameId">6-bit LIN frame identifier (0–63).</param>
    /// <param name="data">Payload bytes (0–8 bytes).</param>
    /// <param name="checksum">LIN checksum byte.</param>
    /// <param name="checksumTypeBits">DLT_LIN checksum-type bits: 1 = classic, 2 = enhanced, 0 = not stated.</param>
    internal void WriteLinMessage(
        long timestampNs, int channel,
        byte frameId, ReadOnlySpan<byte> data, byte checksum, byte checksumTypeBits = 0)
    {
        _AppendTimestamp(timestampNs);
        _LineBuffer.WriteByte((byte)' ');
        if (channel == 1)
        {
            _LineBuffer.Write("Li"u8);
        }
        else
        {
            _LineBuffer.WriteByte((byte)'L');
            _AppendDecimalInt(channel);
        }
        _LineBuffer.WriteByte((byte)' ');
        _AppendHexByte((byte)(frameId & 0x3F));
        // No Tx/Rx bit on the frame. Parser ignores this token. Keep Rx.
        _LineBuffer.Write(" Rx "u8);
        _AppendDecimalInt(data.Length);

        foreach (byte b in data)
        {
            _LineBuffer.WriteByte((byte)' ');
            _AppendHexByte(b);
        }

        _LineBuffer.Write(" checksum = "u8);
        _AppendHexByte(checksum);
        _LineBuffer.Write(checksumTypeBits == 1
            ? " CSM = classic"u8
            : " CSM = enhanced"u8);
        _LineBuffer.Write(_CrLf);
        _FlushLine();
    }

    /// <summary>
    /// Writes a FlexRay frame line.
    /// </summary>
    /// <remarks>
    /// Output format:
    /// <c>{ts} Fr {ch} V9 {frameId:X4} {cycle:X2} 0 0 {headerCrc:X4} x {byteCount:X2} [data bytes...]</c>
    /// <list type="bullet">
    /// <item>Channel: decimal integer, 1 = channel A, 2 = channel B (derived by the caller from the channel bit of the
    ///   LINKTYPE_FLEXRAY measurement header).</item>
    /// <item>Frame ID: 4-char uppercase hex (11-bit slot ID, 0x0000–0x07FF).</item>
    /// <item>Cycle: uppercase hex (0–63). The file header is <c>base hex</c>.</item>
    /// <item>NM and Sync: always <c>0</c>. The link-type frame does not carry them.</item>
    /// <item>Header CRC: 4-char uppercase hex.</item>
    /// <item>Identifier: literal <c>x</c> placeholder token.</item>
    /// <item>Byte count: uppercase hex, because the file base is hex. Written through a <c>byte</c> cast, which is safe
    ///   because the payload is at most 254 bytes (127 words).</item>
    /// <item>Data bytes: 2-char uppercase hex each, space-separated.</item>
    /// </list>
    /// FlexRay ASC lines have no direction column, so this method emits no Rx/Tx token (the link-type payload carries
    /// no direction either).
    /// </remarks>
    /// <param name="timestampNs">Frame timestamp in nanoseconds since Unix epoch.</param>
    /// <param name="channel">FlexRay channel number as written to the line: 1 = channel A, 2 = channel B.</param>
    /// <param name="frameId">11-bit FlexRay slot/frame ID.</param>
    /// <param name="cycle">Cycle counter (0–63).</param>
    /// <param name="headerCrc">FlexRay header CRC value (11 bits).</param>
    /// <param name="data">Payload bytes (0–254 bytes).</param>
    internal void WriteFlexRayMessage(
        long timestampNs, int channel,
        ushort frameId, byte cycle, ushort headerCrc,
        ReadOnlySpan<byte> data)
    {
        // No Tx/Rx bit on the frame. FlexRay ASC has no direction column, so none is written.
        // Column order is id, cycle, NM, sync, header CRC, name, byte count.
        _AppendTimestamp(timestampNs);
        _LineBuffer.Write(" Fr "u8);
        _AppendDecimalInt(channel);
        _LineBuffer.Write(" V9 "u8);
        _AppendHexUInt16(frameId, 4);
        _LineBuffer.WriteByte((byte)' ');
        _AppendHexByte(cycle);
        _LineBuffer.Write(" 0 0 "u8);
        _AppendHexUInt16(headerCrc, 4);
        _LineBuffer.Write(" x "u8);
        _AppendHexByte((byte)data.Length);

        foreach (byte b in data)
        {
            _LineBuffer.WriteByte((byte)' ');
            _AppendHexByte(b);
        }

        _LineBuffer.Write(_CrLf);
        _FlushLine();
    }

    /// <summary>
    /// Writes one Ethernet ASC 1.3.1 section 4.2 packet on a single physical line.
    /// The exporter header is <c>base hex</c>, so the length column is uppercase hex.
    /// </summary>
    /// <remarks>
    /// Direction is always Rx. SocketCAN, DLT_LIN, and LINKTYPE_FLEXRAY payloads
    /// do not carry Tx versus Rx, and the ASC parser discards the direction token.
    /// The caller rejects lengths outside 0..1518 before calling this method.
    /// </remarks>
    /// <param name="timestampNs">Frame timestamp in nanoseconds since Unix epoch.</param>
    /// <param name="channel">Ethernet channel (1..255).</param>
    /// <param name="frame">Frame bytes starting at the destination MAC, without the Ethernet CRC.</param>
    internal void WriteEthernetFrame(long timestampNs, int channel, ReadOnlySpan<byte> frame)
    {
        _AppendTimestamp(timestampNs);
        _LineBuffer.Write(" ETH "u8);
        _AppendDecimalInt(channel);
        // No Tx/Rx bit on the frame. Parser ignores this token. Keep Rx.
        _LineBuffer.Write(" Rx "u8);
        _AppendHexUInt32((uint)frame.Length, 1);
        _LineBuffer.WriteByte((byte)':');
        foreach (byte value in frame)
        {
            _AppendHexByte(value);
        }

        _LineBuffer.Write(_CrLf);
        _FlushLine();
    }

    /// <summary>
    /// Writes one CAN XL <c>XLFF</c> line. Classic and FD frames are not wrapped in <c>CANXL</c>.
    /// </summary>
    /// <remarks>
    /// Direction is always Rx. SocketCAN, DLT_LIN, and LINKTYPE_FLEXRAY payloads
    /// do not carry Tx versus Rx, and the ASC parser discards the direction token.
    /// The caller checks the XLF bit, the 1..2048 length, and that the buffer holds the payload.
    /// </remarks>
    /// <param name="timestampNs">Frame timestamp in nanoseconds since Unix epoch.</param>
    /// <param name="channel">CAN channel.</param>
    /// <param name="frame">Compact 12-byte CAN XL header plus payload.</param>
    internal void WriteCanXlFrame(long timestampNs, int channel, ReadOnlySpan<byte> frame)
    {
        // Decode the compact CAN XL buffer (the layout the ASC reader builds): word 0 = priority in bits 10-0 and VCID in bits
        // 23-16 (big-endian), byte 4 = flags (bit 0 SEC, bit 1 RRS), byte 5 = SDT, bytes 6-7 = payload length, bytes 8-11 =
        // acceptance field (little-endian). The caller has already validated the lengths, so no bounds checks are repeated.
        uint word = BinaryPrimitives.ReadUInt32BigEndian(frame);
        uint priority = word & 0x7FFu;
        byte vcid = (byte)((word >> 16) & 0xFF);
        byte flags = frame[4];
        byte sdt = frame[5];
        int length = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2));
        uint acceptanceField = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(8, 4));

        // Trailing ASC flag word: the XLF bit (0x00400000) is always set for an XLFF line; SEC and RRS are mapped from the
        // compact flag byte to their ASC positions (0x01000000 and 0x00800000, the same constants the reader decodes).
        uint ascFlags = 0x00400000u;
        if ((flags & 0x01) != 0)
        {
            ascFlags |= 0x01000000u;
        }

        if ((flags & 0x02) != 0)
        {
            ascFlags |= 0x00800000u;
        }

        // Write the columns in the order the CANXL line defines; the constant "0" columns are ones the ASC reader skips.
        // The data length appears twice: as hex "length - 1" (the DLC column; length is 1..2048, so it never underflows) and
        // as the decimal byte count; the reader requires the two to agree.
        _AppendTimestamp(timestampNs);
        _LineBuffer.Write(" CANXL "u8);
        _AppendDecimalInt(channel);
        // No Tx/Rx bit on the frame. Parser ignores this token. Keep Rx.
        _LineBuffer.Write(" Rx XLFF 0 0 "u8);
        _AppendHexUInt32(priority, 1);
        _LineBuffer.WriteByte((byte)' ');
        _AppendHexByte(sdt);
        _LineBuffer.WriteByte((byte)' ');
        _LineBuffer.WriteByte((flags & 0x01) != 0 ? (byte)'1' : (byte)'0');
        _LineBuffer.WriteByte((byte)' ');
        _AppendHexUInt32((uint)(length - 1), 1);
        _LineBuffer.WriteByte((byte)' ');
        _AppendDecimalInt(length);
        _LineBuffer.Write(" 0 0 "u8);
        _AppendHexByte(vcid);
        _LineBuffer.WriteByte((byte)' ');
        _AppendHexUInt32(acceptanceField, 8);

        ReadOnlySpan<byte> payload = frame.Slice(12, length);
        foreach (byte value in payload)
        {
            _LineBuffer.WriteByte((byte)' ');
            _AppendHexByte(value);
        }

        _LineBuffer.Write(" 0 "u8);
        _AppendHexUInt32(ascFlags, 8);
        _LineBuffer.Write(_CrLf);
        _FlushLine();
    }

    /// <summary>
    /// Writes the <c>End TriggerBlock</c> footer line and flushes the stream.
    /// Must be called exactly once, after all frame lines have been written.
    /// </summary>
    internal void Finish()
    {
        // The footer is built and appended like any frame line; the output buffer is then forced out and the stream is
        // flushed so the complete file (header included, even for an empty export) is handed to the stream before the
        // owner closes it.
        _LineBuffer.Write("End TriggerBlock"u8);
        _LineBuffer.Write(_CrLf);
        _FlushLine();
        _FlushOutputBuffer();
        _Stream.Flush();
    }

    /// <summary>
    /// Returns the internal buffers to the array pool.
    /// Must be called when the writer is no longer needed (from the owner's cleanup).
    /// Idempotent.
    /// </summary>
    internal void Return()
    {
        _LineBuffer.Return();
        _OutputBuffer.Return();
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Emits the ASC file header (buffered, see the constructor remarks). This is a one-time operation called
    /// from the constructor. String allocations here are acceptable since the header
    /// is written only once per export.
    /// </summary>
    /// <param name="anchorNs">Capture start time in nanoseconds since Unix epoch (non-negative).</param>
    private void _WriteHeader(long anchorNs)
    {
        // Convert to UTC DateTimeOffset for the date string. The header date has millisecond resolution (the ctime-style
        // ASC date has no finer field), whereas the line timestamps are relative to the full-resolution anchor, so the
        // sub-millisecond part of the anchor is not recoverable from the header. A non-negative nanosecond value divided by
        // 10^6 stays far inside the DateTimeOffset range, so FromUnixTimeMilliseconds cannot throw.
        DateTimeOffset dto = DateTimeOffset.FromUnixTimeMilliseconds(anchorNs / 1_000_000);
        string dateStr = _FormatAscDate(dto);
        byte[] dateBytes = Encoding.ASCII.GetBytes(dateStr);

        // Each header line goes through the normal line path (_FlushLine) so the byte accounting and batching are identical
        // to frame lines. The header is therefore only buffered here; it reaches the stream with the first output-buffer
        // flush, so a stream failure surfaces on a later frame write or in Finish, not in the constructor.
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
    /// Appends the relative timestamp <c>max(0, timestampNs - anchorNs)</c> in seconds with
    /// <see cref="_TimestampDecimals"/> fraction digits to <see cref="_LineBuffer"/>. Integer arithmetic, rounded half up:
    /// exact at nanosecond resolution for any capture length (a double loses nanoseconds after about 100 days).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _AppendTimestamp(long timestampNs)
    {
        // Integer arithmetic only: no floating point and no string formatting on the per-frame path.
        // timestampNs <= anchor gives 0 (a frame older than the first one is clamped, never printed negative);
        // otherwise the difference of two values with anchor >= 0 cannot overflow.
        long relative = timestampNs <= _AnchorNs ? 0L : timestampNs - _AnchorNs;
        long seconds = relative / 1_000_000_000L;
        long fraction = relative % 1_000_000_000L;

        // Round the nanosecond fraction to the configured digits; a carry moves into the seconds.
        long divisor = _PowersOfTen[9 - _TimestampDecimals];
        long unit = _PowersOfTen[_TimestampDecimals];
        long rounded = (fraction + (divisor / 2)) / divisor;
        if (rounded >= unit)
        {
            seconds++;
            rounded -= unit;
        }

        // Format "seconds.fraction" into 32 stack bytes (a long needs at most 19 digits plus '.' plus 9 fraction digits, so
        // the TryFormat calls cannot fail). The 'D' format with a precision zero-pads the fraction to the configured width.
        Span<byte> scratch = stackalloc byte[32];
        Utf8Formatter.TryFormat(seconds, scratch, out int written);
        scratch[written++] = (byte)'.';
        Utf8Formatter.TryFormat(rounded, scratch[written..], out int fractionWritten, new StandardFormat('D', (byte)_TimestampDecimals));
        _LineBuffer.Write(scratch[..(written + fractionWritten)]);
    }

    /// <summary>10^0 … 10^9 for the timestamp rounding.</summary>
    private static ReadOnlySpan<long> _PowersOfTen =>
        [1L, 10L, 100L, 1_000L, 10_000L, 100_000L, 1_000_000L, 10_000_000L, 100_000_000L, 1_000_000_000L];

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
        // Fixed-size nibble expansion on the stack instead of ToString("X"): no string allocation per value. All digits are
        // produced, then leading zeros are skipped down to minWidth (minWidth 4 keeps all four digits).
        ReadOnlySpan<byte> hex = _HexDigits;
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
        // Same technique as _AppendHexUInt16: stack nibble expansion, then skip leading zeros down to minWidth.
        ReadOnlySpan<byte> hex = _HexDigits;
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
    /// Uses <c>Utf8Formatter.TryFormat</c> into a 12-byte stack buffer (an <c>int</c> needs at most 11 characters) for
    /// zero-allocation, culture-independent formatting directly to UTF-8.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _AppendDecimalInt(int value)
    {
        Span<byte> scratch = stackalloc byte[12];
        Utf8Formatter.TryFormat(value, scratch, out int written);
        _LineBuffer.Write(scratch[..written]);
    }

    /// <summary>
    /// Appends the content of <see cref="_LineBuffer"/> into <see cref="_OutputBuffer"/> and
    /// resets the line buffer. Flushes the output buffer to the stream when it is full.
    /// The line-buffer reset is guaranteed by a <c>finally</c> block so stale data cannot
    /// accumulate when a write throws.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _FlushLine()
    {
        try
        {
            // Make room first so appending the line never triggers a pool re-rent and copy of the whole output buffer.
            ReadOnlySpan<byte> line = _LineBuffer.WrittenSpan;
            if (_OutputBuffer.Length + line.Length > _OutputBuffer.Capacity
                && _OutputBuffer.Length > 0)
            {
                _FlushOutputBuffer();
            }

            _OutputBuffer.Write(line);

            // Flush once the buffer holds a full 64 KiB block (the initial capacity of _OutputBuffer), so the stream sees a
            // few large writes instead of one write per line. Lines are far shorter than the buffer (the longest, a CAN XL
            // line, is about 6 KB), so the buffer never needs to grow.
            if (_OutputBuffer.Length >= 65_536)
            {
                _FlushOutputBuffer();
            }
        }
        finally
        {
            // Always reset so partial/failed lines do not corrupt subsequent writes.
            _LineBuffer.Reset();
        }
    }

    /// <summary>Writes accumulated output bytes to the stream and resets the output buffer.</summary>
    private void _FlushOutputBuffer()
    {
        if (_OutputBuffer.Length == 0)
        {
            return;
        }

        try
        {
            ReadOnlySpan<byte> written = _OutputBuffer.WrittenSpan;
            _Stream.Write(written);
            _BytesWritten += written.Length;
        }
        finally
        {
            // Reset even when the write throws: a stream that failed mid-write cannot be resumed, and keeping the bytes would
            // make the next flush write them again after an unknown prefix. The exception still propagates to the caller.
            _OutputBuffer.Reset();
        }
    }

    #endregion
}

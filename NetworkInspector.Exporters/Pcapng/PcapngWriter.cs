// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pcapng;

/// <summary>
/// Options for the Section Header Block (SHB).
/// These are written once at the start of the file. Immutable after construction, so safe to share across threads.
/// </summary>
internal sealed record ShbOptions
{
    #region Section header options

    /// <summary>Hardware description string.</summary>
    internal string? Hardware { get; init; }

    /// <summary>Operating system description string.</summary>
    internal string? Os { get; init; }

    /// <summary>User application name string.</summary>
    internal string? Application { get; init; }

    /// <summary>Optional comment string.</summary>
    internal string? Comment { get; init; }

    /// <summary>Returns true if any option is set.</summary>
    internal bool HasOptions =>
        Hardware is not null || Os is not null || Application is not null || Comment is not null;

    /// <summary>
    /// Computes the total size of all options including end-of-options marker. Each option occupies its 4-byte header
    /// plus the UTF-8 value padded to a 4-byte boundary, in the same order in which
    /// <see cref="PcapngWriter.WriteSectionHeader"/> writes them. The writer checks the serialized size against this
    /// value, so both must be changed together.
    /// </summary>
    /// <returns>The size in bytes; 0 when no option is set (no end-of-options marker is written then).</returns>
    internal int TotalOptionsSize()
    {
        if (!HasOptions)
        {
            return 0;
        }

        int size = 0;
        if (Hardware is not null)
        {
            size += PcapPadding.OptionSize(Encoding.UTF8.GetByteCount(Hardware));
        }
        if (Os is not null)
        {
            size += PcapPadding.OptionSize(Encoding.UTF8.GetByteCount(Os));
        }
        if (Application is not null)
        {
            size += PcapPadding.OptionSize(Encoding.UTF8.GetByteCount(Application));
        }
        if (Comment is not null)
        {
            size += PcapPadding.OptionSize(Encoding.UTF8.GetByteCount(Comment));
        }
        // End-of-options marker
        size += PcapPadding.EndOfOptionsSize;
        return size;
    }

    #endregion
}

/// <summary>
/// Low-level PCAPNG binary writer. Writes SHB, IDB, and EPB blocks to a <see cref="Stream"/>.
/// All blocks are written in little-endian byte order with 4-byte alignment padding
/// as required by the PCAPNG specification.
/// <para>
/// This writer does not track interfaces or state — it simply serializes blocks.
/// Higher-level logic (interface tracking, lazy init) is in <see cref="PcapngExporter"/>.
/// </para>
/// <para>
/// Every block is assembled completely in a buffer and then handed to the stream in one write, so a block is never
/// interleaved with partial stream writes and its length fields are verified before any byte is written.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; owned by <see cref="PcapngExporter"/> and used from its single caller thread
/// (the shared block buffer is reused for every block).
/// </para>
/// </summary>
internal sealed class PcapngWriter
{
    #region Block layout

    /// <summary>Zero padding buffer (4 bytes max needed for 32-bit alignment).</summary>
    private static readonly byte[] _ZeroPadding = [0, 0, 0, 0];

    /// <summary>SHB fixed header size: type(4) + len(4) + magic(4) + ver_major(2) + ver_minor(2) + section_len(8) = 24 bytes.</summary>
    private const int _ShbHeaderSize = 24;

    /// <summary>IDB fixed header size: type(4) + len(4) + linktype(2) + reserved(2) + snaplen(4) = 16 bytes.</summary>
    private const int _IdbHeaderSize = 16;

    /// <summary>EPB fixed header size: type(4) + len(4) + iface(4) + ts_hi(4) + ts_lo(4) + cap_len(4) + orig_len(4) = 28 bytes.</summary>
    private const int _EpbHeaderSize = 28;

    /// <summary>Option header size: code(2) + length(2) = 4 bytes.</summary>
    private const int _OptionHeaderSize = 4;

    /// <summary>Trailing block length size: 4 bytes.</summary>
    private const int _TrailingLengthSize = 4;

    /// <summary>
    /// Unspecified section length value (all bits set). The PCAPNG specification defines a section length of -1 as
    /// "not specified"; a streaming writer uses it because it cannot know the length up front.
    /// </summary>
    private const long _SectionLengthUnspecified = -1;

    /// <summary>Timestamp resolution for nanoseconds: the <c>if_tsresol</c> exponent (10^-9 s per tick).</summary>
    internal const byte TsResolNanoseconds = 9;

    /// <summary>Timestamp resolution for microseconds: the <c>if_tsresol</c> exponent (10^-6 s per tick).</summary>
    internal const byte TsResolMicroseconds = 6;

    /// <summary>The output stream every finished block is written to; owned by the caller, never closed by the writer.</summary>
    private readonly Stream _Stream;

    /// <summary>
    /// Reusable block assembly buffer for IDB/EPB writes (header + options/data + trailer).
    /// Each block is built here completely and then written with a single stream write, instead of several small
    /// writes for header, payload, padding and trailer. Reused (Reset) for every block, so steady state allocates
    /// nothing; the cost is one extra copy of each frame payload. After a write the buffer still holds the block that
    /// was just written until the next block resets it.
    /// </summary>
    private readonly PooledBuffer _BlockBuffer = new(4096);

    /// <summary>Bytes written to <see cref="_Stream"/> (committed output).</summary>
    private long _BytesWritten;

    /// <summary>Creates a new PCAPNG writer wrapping the given stream.</summary>
    /// <param name="stream">The output stream; the writer never closes it.</param>
    internal PcapngWriter(Stream stream)
    {
        _Stream = stream;
    }

    /// <summary>Returns rented block buffers to the pool.</summary>
    internal void ReturnBuffers() => _BlockBuffer.Return();

    /// <summary>
    /// Approximate output size: the committed bytes plus the length of the block buffer. Blocks are written to the
    /// stream as soon as they are assembled and the buffer is only reset when the next block starts, so the buffer
    /// still holds the most recently written block and the value can exceed the true size by that block.
    /// </summary>
    internal long EstimatedOutputBytes => _BytesWritten + _BlockBuffer.Length;

    /// <summary>Flushes the underlying stream.</summary>
    /// <exception cref="IOException">The stream could not be flushed.</exception>
    internal void Flush() => _Stream.Flush();

    /// <summary>Writes bytes to the stream and updates <see cref="_BytesWritten"/>.</summary>
    /// <param name="data">The finished block; written with one stream write.</param>
    /// <exception cref="IOException">The stream could not be written.</exception>
    private void _WriteToStream(ReadOnlySpan<byte> data)
    {
        _Stream.Write(data);
        _BytesWritten += data.Length;
    }

    #endregion

    #region Section header block

    /// <summary>
    /// Writes the Section Header Block (SHB).
    /// Must be the first block in a PCAPNG file/section.
    /// </summary>
    /// <param name="options">Optional SHB metadata (hardware, OS, application, comment).</param>
    /// <exception cref="InvalidOperationException">The serialized options do not match their pre-calculated size.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option value is longer than the 16-bit option length allows.</exception>
    internal void WriteSectionHeader(ShbOptions? options)
    {
        // The block length appears twice (leading and trailing) and must be exact, so it is computed first from the
        // option sizes and verified after serialization below.
        int optionsSize = options?.TotalOptionsSize() ?? 0;
        // total = fixed header + options + trailing length
        uint blockTotalLength = (uint)(_ShbHeaderSize + optionsSize + _TrailingLengthSize);

        // Build the entire SHB in a temporary buffer before writing anything
        // to the stream. If the pre-calculated size and the actual serialised size
        // disagree, the exception fires before any bytes reach the stream, so the
        // output is never left in a partially-written (corrupt) state.
        PooledBuffer shbBuffer = new((int)blockTotalLength);
        try
        {
            // Fixed header (24 bytes): block type, total length, byte-order magic (the reader derives the file's
            // endianness from it; this writer always writes little-endian), format version 1.0 and the section length.
            Span<byte> header = shbBuffer.Reserve(_ShbHeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header, PcapConstants.BlockTypeSHB);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], blockTotalLength);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], PcapConstants.PcapngMagic);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], PcapConstants.PcapngVersionMajor);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], PcapConstants.PcapngVersionMinor);
            BinaryPrimitives.WriteInt64LittleEndian(header[16..], _SectionLengthUnspecified);

            // Options, each followed by padding to 4 bytes, terminated by the end-of-options marker.
            if (options is not null && options.HasOptions)
            {
                int actualOptionsSize = 0;

                if (options.Hardware is not null)
                {
                    _WriteOptionToBuffer(shbBuffer, PcapConstants.OptShbHardware, options.Hardware);
                    actualOptionsSize += PcapPadding.OptionSize(Encoding.UTF8.GetByteCount(options.Hardware));
                }
                if (options.Os is not null)
                {
                    _WriteOptionToBuffer(shbBuffer, PcapConstants.OptShbOs, options.Os);
                    actualOptionsSize += PcapPadding.OptionSize(Encoding.UTF8.GetByteCount(options.Os));
                }
                if (options.Application is not null)
                {
                    _WriteOptionToBuffer(shbBuffer, PcapConstants.OptShbUserAppl, options.Application);
                    actualOptionsSize += PcapPadding.OptionSize(Encoding.UTF8.GetByteCount(options.Application));
                }
                if (options.Comment is not null)
                {
                    _WriteOptionToBuffer(shbBuffer, PcapConstants.OptComment, options.Comment);
                    actualOptionsSize += PcapPadding.OptionSize(Encoding.UTF8.GetByteCount(options.Comment));
                }
                _WriteEndOfOptionsToBuffer(shbBuffer);
                actualOptionsSize += PcapPadding.EndOfOptionsSize;

                // Validate that the bytes we just serialised match the pre-calculated size.
                // Any discrepancy means TotalOptionsSize() and the _WriteOption* calls are
                // out of sync — catching this before the stream write prevents corruption.
                if (actualOptionsSize != optionsSize)
                {
                    throw new InvalidOperationException(
                        $"PCAPNG SHB options size mismatch: pre-calculated {optionsSize} bytes " +
                        $"but serialised {actualOptionsSize} bytes. " +
                        "TotalOptionsSize() and _WriteOption() calls are out of sync.");
                }
            }

            // Trailing Block Total Length (4 bytes)
            Span<byte> trailing = shbBuffer.Reserve(_TrailingLengthSize);
            BinaryPrimitives.WriteUInt32LittleEndian(trailing, blockTotalLength);

            // All bytes are in the buffer and validated — write atomically.
            _WriteToStream(shbBuffer.WrittenSpan);
        }
        finally
        {
            shbBuffer.Return();
        }
    }

    #endregion

    #region Interface description block

    /// <summary>Name under which a left-out interface name is reported in the omitted list.</summary>
    private const string _IfNameLabel = "if_name";

    /// <summary>Name under which a left-out interface description is reported in the omitted list.</summary>
    private const string _IfDescriptionLabel = "if_description";

    /// <summary>
    /// Writes an Interface Description Block (IDB).
    /// Must appear before any EPBs that reference the interface.
    /// Metadata that a PCAPNG option cannot hold (longer than 65 535 UTF-8 bytes, wrong type, negative) is left out and
    /// named in <paramref name="omitted"/>, so the interface's frames are still written and the caller can report the loss
    /// once.
    /// </summary>
    /// <param name="linkType">The link-layer type (DLT value).</param>
    /// <param name="snapLength">Maximum captured packet length.</param>
    /// <param name="tsResolution">Timestamp resolution (power-of-10 exponent, e.g. 9 = nanosecond).</param>
    /// <param name="name">Optional interface name (<c>if_name</c>); left out when longer than 65 535 UTF-8 bytes.</param>
    /// <param name="fcsLength">
    /// Optional interface FCS length in bytes (<c>if_fcslen</c>). Omitted when null.
    /// Per-packet EPB flags are not written: <see cref="Frame"/> does not store them.
    /// </param>
    /// <param name="description">
    /// Optional interface description (<c>if_description</c>). Omitted when null or empty; left out and reported when
    /// longer than 65 535 UTF-8 bytes (the 16-bit option length limit), never truncated.
    /// </param>
    /// <param name="properties">
    /// Interface properties of the source. Every key of the shared PCAPNG option table
    /// (<c>PcapInterfacePropertyKeys.InterfaceOptions</c>) is written back as its IDB option; other keys are ignored.
    /// </param>
    /// <param name="omitted">
    /// Receives the name of every option that was left out (<c>if_name</c>, <c>if_description</c>, or the property key of a
    /// table option); the writer only appends, so the caller clears the list before the call. Nothing is appended when
    /// everything fit.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="omitted"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="linkType"/> does not fit the 16-bit link type field of the block.
    /// </exception>
    /// <exception cref="InvalidOperationException">The serialized block does not match its pre-calculated length.</exception>
    /// <exception cref="IOException">The stream could not be written.</exception>
    internal void WriteInterfaceDescription(
        LinkType linkType,
        uint snapLength,
        byte tsResolution,
        string? name,
        byte? fcsLength,
        string? description,
        IReadOnlyDictionary<string, object>? properties,
        List<string> omitted)
    {
        ArgumentNullException.ThrowIfNull(omitted);

        // The IDB stores the link type in 16 bits. A larger value (a pcap file can carry a 32-bit value) would be cut
        // to its low 16 bits and name a different link type, so the frame is rejected before anything is written.
        if ((uint)linkType > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(linkType),
                (int)linkType,
                "The link type does not fit the 16-bit link type field of a PCAPNG interface description block.");
        }

        // Security: interface metadata comes from the source capture file or the registry, so its size is not under our
        // control, and an option longer than its 16-bit length field cannot be represented. A throw here would fail the
        // IDB again for every frame of the interface. The option is therefore left out and named in the omitted list (the
        // exporter reports it once). Rejected: truncating the value, because a cut name or filter still looks valid but
        // says something else. Residual risk: the interface appears in the file without that metadata; the report names it.
        int nameByteCount = 0;
        if (name is not null)
        {
            nameByteCount = Encoding.UTF8.GetByteCount(name);
            if (nameByteCount > ushort.MaxValue)
            {
                omitted.Add(_IfNameLabel);
                name = null;
            }
        }

        byte[]? descriptionBytes = null;
        if (description is { Length: > 0 })
        {
            if (Encoding.UTF8.GetByteCount(description) > ushort.MaxValue)
            {
                omitted.Add(_IfDescriptionLabel);
            }
            else
            {
                descriptionBytes = Encoding.UTF8.GetBytes(description);
            }
        }

        // Interface metadata the reader stored as properties goes back into the same IDB options (one shared table),
        // so a PCAPNG -> PCAPNG copy keeps speed, filter, OS, hardware, and TX / RX speed. Encoded up front: the block
        // length must be known before the header is written. IDBs are written once per interface, not per frame.
        List<(ushort Code, byte[] Value)> tableOptions = _EncodeInterfaceOptions(properties, omitted);

        // if_tsresol is always present, so readers know the unit of the EPB timestamps (which must match the
        // resolution later passed to WriteEnhancedPacket). if_name, if_description, if_fcslen, and the table options
        // are optional. The size computed here must stay in step with the option writes further down; the serialized
        // length is verified against it before the block reaches the stream (as for the SHB).
        int optionsSize = PcapPadding.OptionSize(1);
        if (name is not null)
        {
            optionsSize += PcapPadding.OptionSize(nameByteCount);
        }

        if (descriptionBytes is not null)
        {
            optionsSize += PcapPadding.OptionSize(descriptionBytes.Length);
        }

        if (fcsLength.HasValue)
        {
            optionsSize += PcapPadding.OptionSize(1);
        }

        foreach ((ushort _, byte[] value) in tableOptions)
        {
            optionsSize += PcapPadding.OptionSize(value.Length);
        }

        optionsSize += PcapPadding.EndOfOptionsSize;

        uint blockTotalLength = (uint)(_IdbHeaderSize + optionsSize + _TrailingLengthSize);

        // Assemble the full IDB before touching the stream so a mid-write failure
        // cannot leave a truncated block on disk.
        _BlockBuffer.Reset();
        Span<byte> header = _BlockBuffer.Reserve(_IdbHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, PcapConstants.BlockTypeIDB);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], blockTotalLength);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], (ushort)linkType);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], snapLength);

        if (name is not null)
        {
            _WriteOptionToBuffer(_BlockBuffer, PcapConstants.OptIfName, name);
        }

        if (descriptionBytes is not null)
        {
            _WriteOptionRawToBuffer(_BlockBuffer, PcapConstants.OptIfDescription, descriptionBytes);
        }

        _WriteOptionRawToBuffer(_BlockBuffer, PcapConstants.OptIfTsResol, [tsResolution]);
        if (fcsLength.HasValue)
        {
            _WriteOptionRawToBuffer(_BlockBuffer, PcapConstants.OptIfFcsLen, [fcsLength.Value]);
        }

        foreach ((ushort code, byte[] value) in tableOptions)
        {
            _WriteOptionRawToBuffer(_BlockBuffer, code, value);
        }

        _WriteEndOfOptionsToBuffer(_BlockBuffer);

        Span<byte> trailing = _BlockBuffer.Reserve(_TrailingLengthSize);
        BinaryPrimitives.WriteUInt32LittleEndian(trailing, blockTotalLength);

        // Validate that the bytes just assembled match the length written into the block: a mismatch means the size
        // calculation above and the option writes are out of sync, and the block would corrupt the file. Checked before
        // the stream write, so nothing is written.
        if ((uint)_BlockBuffer.Length != blockTotalLength)
        {
            throw new InvalidOperationException(
                $"PCAPNG IDB size mismatch: pre-calculated {blockTotalLength} bytes but serialised {_BlockBuffer.Length} bytes. " +
                "The IDB size calculation and the option writes are out of sync.");
        }

        _WriteToStream(_BlockBuffer.WrittenSpan);
    }

    #endregion

    #region Enhanced packet block

    /// <summary>
    /// Writes an Enhanced Packet Block (EPB).
    /// </summary>
    /// <param name="interfaceId">Zero-based PCAPNG interface ID.</param>
    /// <param name="timestamp">Frame capture timestamp.</param>
    /// <param name="data">Captured frame bytes (possibly truncated to snap length).</param>
    /// <param name="originalLength">
    /// EPB original_len. Callers pass <see cref="Frame.Length"/> (captured bytes).
    /// When this write snap-truncates, pass the pre-truncation captured length.
    /// </param>
    /// <param name="tsResolution">Timestamp resolution (power-of-10 exponent).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="data"/> is so long that padding or the 32-bit block length would overflow.
    /// </exception>
    /// <exception cref="IOException">The stream could not be written.</exception>
    internal void WriteEnhancedPacket(
        uint interfaceId,
        Timestamp timestamp,
        ReadOnlySpan<byte> data,
        uint originalLength,
        byte tsResolution)
    {
        // Validate at the trust boundary before any state changes: the block length field is 32-bit and the payload is
        // padded to 4 bytes, either of which could wrap for a huge span and produce a corrupt block.
        if (data.Length > int.MaxValue - 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                "PCAPNG payload cannot be padded to a 4-byte boundary.");
        }

        if (!TryBlockLength(data.Length, out uint blockTotalLength))
        {
            throw new ArgumentOutOfRangeException(nameof(data), "PCAPNG block length exceeds 32 bits.");
        }

        uint capturedLength = (uint)data.Length;
        // PCAPNG EPB requires both captured and original length. The original length
        // must never be smaller than captured bytes.
        uint originalPacketLength = Math.Max(capturedLength, originalLength);

        // Convert timestamp to the appropriate resolution; the EPB stores the 64-bit tick count as two 32-bit halves,
        // high word first.
        ulong tsValue = ConvertTimestamp(timestamp, tsResolution);
        uint timestampHigh = (uint)(tsValue >> 32);
        uint timestampLow = (uint)tsValue;

        // Assemble the full EPB, then one stream write — fewer syscalls and no
        // partial EPB if the write fails mid-block. Hot path (once per frame): the buffer is reused, so the only cost
        // beyond the stream write is copying the payload into it; the alternative of writing header, payload,
        // padding and trailer as separate stream writes avoids that copy but multiplies the writes.
        _BlockBuffer.Reset();
        Span<byte> header = _BlockBuffer.Reserve(_EpbHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, PcapConstants.BlockTypeEPB);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], blockTotalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], interfaceId);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], timestampHigh);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], timestampLow);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], capturedLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], originalPacketLength);

        _BlockBuffer.Write(data);
        int padding = PcapPadding.PaddingFor(data.Length);
        if (padding > 0)
        {
            _BlockBuffer.Write(_ZeroPadding.AsSpan(0, padding));
        }

        Span<byte> trailing = _BlockBuffer.Reserve(_TrailingLengthSize);
        BinaryPrimitives.WriteUInt32LittleEndian(trailing, blockTotalLength);

        _WriteToStream(_BlockBuffer.WrittenSpan);
    }

    /// <summary>
    /// Computes the enhanced-packet block length for <paramref name="payloadLength"/> captured bytes.
    /// Does not write to the stream.
    /// </summary>
    /// <param name="payloadLength">Captured payload length in bytes, before padding.</param>
    /// <param name="blockLength">The block length including header, padding, and trailer; 0 when the method returns false.</param>
    /// <returns>False when <paramref name="payloadLength"/> is negative or the padding or the 32-bit block length would wrap.</returns>
    internal static bool TryBlockLength(int payloadLength, out uint blockLength)
    {
        blockLength = 0;
        if (payloadLength < 0 || payloadLength > int.MaxValue - 3)
        {
            return false;
        }

        long total = (long)_EpbHeaderSize + PcapPadding.PaddedLength(payloadLength) + _TrailingLengthSize;
        if (total > uint.MaxValue)
        {
            return false;
        }

        blockLength = (uint)total;
        return true;
    }

    #endregion

    #region Option encoding

    /// <summary>
    /// Encodes the interface properties listed in <see cref="PcapInterfacePropertyKeys.InterfaceOptions"/> as IDB option
    /// values, in table order: 64-bit values little-endian, text as UTF-8, a filter as kind byte 0 plus the text. A value
    /// of the wrong type, a negative number, or text longer than an option can hold (65 535 bytes) is left out and its
    /// property key is added to <paramref name="omitted"/>; an empty text has nothing to store and is skipped unreported.
    /// </summary>
    /// <param name="properties">The interface properties of the source; null or empty yields no options.</param>
    /// <param name="omitted">Receives the property key of every value that was left out.</param>
    /// <returns>The option code and encoded value of every property that could be stored, in table order.</returns>
    private static List<(ushort Code, byte[] Value)> _EncodeInterfaceOptions(
        IReadOnlyDictionary<string, object>? properties,
        List<string> omitted)
    {
        List<(ushort Code, byte[] Value)> options = [];
        if (properties is null || properties.Count == 0)
        {
            return options;
        }

        foreach (PcapInterfaceOption entry in PcapInterfacePropertyKeys.InterfaceOptions)
        {
            if (!properties.TryGetValue(entry.Key, out object? value))
            {
                continue;
            }

            // An empty text option carries no information, so it is skipped without a report (it is not a lost value).
            if (entry.Kind != PcapOptionValueKind.UInt64 && value is string { Length: 0 })
            {
                continue;
            }

            byte[]? encoded = entry.Kind switch
            {
                PcapOptionValueKind.UInt64 => _EncodeUInt64(value),
                PcapOptionValueKind.Utf8 => _EncodeText(value, kindPrefix: false),
                PcapOptionValueKind.Filter => _EncodeText(value, kindPrefix: true),
                _ => null,
            };

            if (encoded is null)
            {
                omitted.Add(entry.Key);
                continue;
            }

            options.Add((entry.Code, encoded));
        }

        return options;
    }

    /// <summary>Little-endian 64-bit option value from an unsigned or non-negative integer property.</summary>
    /// <param name="value">The property value.</param>
    /// <returns>The 8 encoded bytes; null when <paramref name="value"/> is not an unsigned or non-negative integer.</returns>
    private static byte[]? _EncodeUInt64(object value)
    {
        ulong? number = value switch
        {
            ulong u => u,
            long l when l >= 0 => (ulong)l,
            uint ui => ui,
            int i when i >= 0 => (ulong)i,
            _ => null,
        };
        if (number is not { } raw)
        {
            return null;
        }

        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, raw);
        return bytes;
    }

    /// <summary>UTF-8 option value, optionally behind a filter-kind byte 0 (<c>if_filter</c>: libpcap filter string).</summary>
    /// <param name="value">The property value.</param>
    /// <param name="kindPrefix">True to put the filter-kind byte 0 in front of the text.</param>
    /// <returns>The encoded bytes; null when <paramref name="value"/> is not a string or the result exceeds 65 535 bytes.</returns>
    private static byte[]? _EncodeText(object value, bool kindPrefix)
    {
        if (value is not string text)
        {
            return null;
        }

        int prefix = kindPrefix
            ? 1
            : 0;
        int length = prefix + Encoding.UTF8.GetByteCount(text);
        if (length > ushort.MaxValue)
        {
            return null;
        }

        byte[] bytes = new byte[length];
        Encoding.UTF8.GetBytes(text, bytes.AsSpan(prefix));
        return bytes;
    }

    /// <summary>Writes a PCAPNG option with a UTF-8 string value into a <see cref="PooledBuffer"/>.</summary>
    /// <param name="buffer">The block under assembly.</param>
    /// <param name="code">The option code.</param>
    /// <param name="value">The option text.</param>
    /// <exception cref="ArgumentOutOfRangeException">The UTF-8 form of <paramref name="value"/> is longer than 65 535 bytes.</exception>
    private static void _WriteOptionToBuffer(PooledBuffer buffer, ushort code, string value)
    {
        // Short values are encoded on the stack, longer ones into a heap array. Options are written once per file or
        // per interface (never per frame), so the rare heap allocation is acceptable here; the 256-byte cap keeps the
        // stack use small. Lone surrogates are replaced with U+FFFD by the UTF-8 encoder.
        int maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        Span<byte> utf8 = maxBytes <= 256
            ? stackalloc byte[maxBytes]
            : new byte[maxBytes];
        int written = Encoding.UTF8.GetBytes(value, utf8);
        _WriteOptionRawToBuffer(buffer, code, utf8[..written]);
    }

    /// <summary>Writes a PCAPNG option with raw byte value into a <see cref="PooledBuffer"/>.</summary>
    /// <param name="buffer">The block under assembly.</param>
    /// <param name="code">The option code.</param>
    /// <param name="value">The option bytes; padded to a 4-byte boundary after the length field counts them.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is longer than 65 535 bytes.</exception>
    private static void _WriteOptionRawToBuffer(PooledBuffer buffer, ushort code, ReadOnlySpan<byte> value)
    {
        // Option layout: 16-bit code, 16-bit value length (excluding padding), value, zero padding to 4 bytes.
        // The length field is 16-bit, so a longer value cannot be represented and is rejected instead of truncated.
        if (value.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value),
                $"PCAPNG option value length {value.Length} exceeds the 16-bit limit ({ushort.MaxValue}).");
        }

        Span<byte> header = buffer.Reserve(_OptionHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header, code);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)value.Length);
        buffer.Write(value);
        int padding = PcapPadding.PaddingFor(value.Length);
        if (padding > 0)
        {
            buffer.Write(_ZeroPadding.AsSpan(0, padding));
        }
    }

    /// <summary>Writes the end-of-options marker into a <see cref="PooledBuffer"/>.</summary>
    /// <param name="buffer">The block under assembly.</param>
    private static void _WriteEndOfOptionsToBuffer(PooledBuffer buffer)
    {
        Span<byte> eoo = buffer.Reserve(_OptionHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(eoo, PcapConstants.OptEndOfOpt);
        BinaryPrimitives.WriteUInt16LittleEndian(eoo[2..], 0);
    }

    #endregion

    #region Timestamps

    /// <summary>
    /// Converts a nanosecond timestamp to the target resolution.
    /// Negative timestamps (before Unix epoch) are clamped to 0 since PCAPNG timestamps are unsigned.
    /// Coarser resolutions truncate toward zero. Finer-than-nanosecond resolutions multiply and saturate at
    /// <see cref="ulong.MaxValue"/>: a timestamp of about 1.8e18 nanoseconds since the Unix epoch only fits up to
    /// resolution 10, so finer exponents saturate for realistic timestamps.
    /// </summary>
    /// <param name="timestamp">The timestamp in nanoseconds since the Unix epoch.</param>
    /// <param name="tsResolution">
    /// Decimal power-of-ten exponent of the tick length (6 = microseconds, 9 = nanoseconds); 0 to 19, as the exporter
    /// builder guarantees.
    /// </param>
    /// <returns>The tick count in the requested resolution.</returns>
    /// <exception cref="System.Diagnostics.UnreachableException">
    /// <paramref name="tsResolution"/> is above 27, so the power of ten is outside the table (the builder allows 0 to 19).
    /// </exception>
    internal static ulong ConvertTimestamp(Timestamp timestamp, byte tsResolution)
    {
        long nanos = timestamp.AsNanos;
        // PCAPNG timestamps are unsigned — clamp negative values to 0
        ulong nanosUnsigned = (ulong)Math.Max(nanos, 0);

        // The two common resolutions are handled first, without a table lookup: nanoseconds is the identity and
        // microseconds a constant division the JIT can turn into a multiplication.
        if (tsResolution == TsResolNanoseconds)
        {
            return nanosUnsigned;
        }
        if (tsResolution == TsResolMicroseconds)
        {
            return nanosUnsigned / 1_000;
        }
        if (tsResolution < 9)
        {
            // Coarser resolution: divide
            ulong divisor = _Pow10((uint)(9 - tsResolution));
            return nanosUnsigned / divisor;
        }
        // Finer resolution (rare): multiply with saturation
        ulong multiplier = _Pow10((uint)(tsResolution - 9));
        if (multiplier != 0 && nanosUnsigned > ulong.MaxValue / multiplier)
        {
            return ulong.MaxValue;
        }

        return nanosUnsigned * multiplier;
    }

    /// <summary>Computes 10^exponent by table lookup for the exponents 0 to 18.</summary>
    /// <param name="exponent">The power of ten, 0 to 18.</param>
    /// <returns>10 raised to <paramref name="exponent"/>.</returns>
    /// <exception cref="System.Diagnostics.UnreachableException"><paramref name="exponent"/> is above 18.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong _Pow10(uint exponent)
    {
        // A constant collection expression of a ReadOnlySpan is served from static data, so the table costs no
        // allocation per call. The table covers 10^0 to 10^18.
        ReadOnlySpan<ulong> powers =
        [
            1UL,
            10UL,
            100UL,
            1_000UL,
            10_000UL,
            100_000UL,
            1_000_000UL,
            10_000_000UL,
            100_000_000UL,
            1_000_000_000UL,
            10_000_000_000UL,
            100_000_000_000UL,
            1_000_000_000_000UL,
            10_000_000_000_000UL,
            100_000_000_000_000UL,
            1_000_000_000_000_000UL,
            10_000_000_000_000_000UL,
            100_000_000_000_000_000UL,
            1_000_000_000_000_000_000UL,
        ];

        // Invariant: the builder limits the resolution to 0..19, so ConvertTimestamp asks for at most 10^10; the table
        // covers 10^18.
        if (exponent >= (uint)powers.Length)
        {
            throw new System.Diagnostics.UnreachableException("The timestamp resolution exponent is limited to 0..19 by the builder.");
        }

        return powers[(int)exponent];
    }

    #endregion
}

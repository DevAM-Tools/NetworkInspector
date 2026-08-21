// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pcapng;

/// <summary>
/// Options for the Section Header Block (SHB).
/// These are written once at the start of the file.
/// </summary>
internal sealed record ShbOptions
{
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

    /// <summary>Computes the total size of all options including end-of-options marker.</summary>
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
}

/// <summary>
/// Low-level PCAPNG binary writer. Writes SHB, IDB, and EPB blocks to a <see cref="Stream"/>.
/// All blocks are written in little-endian byte order with 4-byte alignment padding
/// as required by the PCAPNG specification.
/// <para>
/// This writer does not track interfaces or state — it simply serializes blocks.
/// Higher-level logic (interface tracking, lazy init) is in <see cref="PcapngExporter"/>.
/// </para>
/// </summary>
internal sealed class PcapngWriter
{
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

    /// <summary>Unspecified section length value (all bits set).</summary>
    private const long _SectionLengthUnspecified = -1;

    /// <summary>Timestamp resolution for nanoseconds.</summary>
    internal const byte TsResolNanoseconds = 9;

    /// <summary>Timestamp resolution for microseconds.</summary>
    internal const byte TsResolMicroseconds = 6;

    private readonly Stream _Stream;

    /// <summary>
    /// Reusable block assembly buffer for atomic IDB/EPB writes (header + options/data + trailer).
    /// Avoids partial blocks on mid-write I/O failure and coalesces into a single stream write.
    /// </summary>
    private readonly PooledBuffer _BlockBuffer = new(4096);

    /// <summary>Bytes written to <see cref="_Stream"/> (committed output).</summary>
    private long _BytesWritten;

    /// <summary>Creates a new PCAPNG writer wrapping the given stream.</summary>
    /// <param name="stream">The output stream.</param>
    internal PcapngWriter(Stream stream)
    {
        _Stream = stream;
    }

    /// <summary>Returns rented block buffers to the pool.</summary>
    internal void ReturnBuffers() => _BlockBuffer.Return();

    /// <summary>
    /// Approximate output size if the block buffer were flushed now.
    /// </summary>
    internal long EstimatedOutputBytes => _BytesWritten + _BlockBuffer.Length;

    /// <summary>
    /// Writes the Section Header Block (SHB).
    /// Must be the first block in a PCAPNG file/section.
    /// </summary>
    /// <param name="options">Optional SHB metadata (hardware, OS, application, comment).</param>
    internal void WriteSectionHeader(ShbOptions? options)
    {
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

        // Fixed header (24 bytes)
        Span<byte> header = shbBuffer.Reserve(_ShbHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, PcapConstants.BlockTypeSHB);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], blockTotalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], PcapConstants.PcapngMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], PcapConstants.PcapngVersionMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], PcapConstants.PcapngVersionMinor);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], _SectionLengthUnspecified);

        // Options
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

    /// <summary>
    /// Writes an Interface Description Block (IDB).
    /// Must appear before any EPBs that reference the interface.
    /// </summary>
    /// <param name="linkType">The link-layer type (DLT value).</param>
    /// <param name="snapLength">Maximum captured packet length.</param>
    /// <param name="tsResolution">Timestamp resolution (power-of-10 exponent, e.g. 9 = nanosecond).</param>
    /// <param name="name">Optional interface name.</param>
    internal void WriteInterfaceDescription(LinkType linkType, uint snapLength, byte tsResolution, string? name)
    {
        // Calculate options size: always write if_tsresol (1 byte value)
        int optionsSize = PcapPadding.OptionSize(1); // if_tsresol
        if (name is not null)
        {
            optionsSize += PcapPadding.OptionSize(Encoding.UTF8.GetByteCount(name));
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
        _WriteOptionRawToBuffer(_BlockBuffer, PcapConstants.OptIfTsResol, [tsResolution]);
        _WriteEndOfOptionsToBuffer(_BlockBuffer);

        Span<byte> trailing = _BlockBuffer.Reserve(_TrailingLengthSize);
        BinaryPrimitives.WriteUInt32LittleEndian(trailing, blockTotalLength);

        _WriteToStream(_BlockBuffer.WrittenSpan);
    }

    /// <summary>
    /// Writes an Enhanced Packet Block (EPB).
    /// </summary>
    /// <param name="interfaceId">Zero-based PCAPNG interface ID.</param>
    /// <param name="timestamp">Frame capture timestamp.</param>
    /// <param name="data">Captured frame bytes (possibly truncated to snap length).</param>
    /// <param name="originalLength">Original on-wire frame length before truncation.</param>
    /// <param name="tsResolution">Timestamp resolution (power-of-10 exponent).</param>
    internal void WriteEnhancedPacket(
        uint interfaceId,
        Timestamp timestamp,
        ReadOnlySpan<byte> data,
        uint originalLength,
        byte tsResolution)
    {
        uint capturedLength = (uint)data.Length;
        // PCAPNG EPB requires both captured and original length. The original length
        // must never be smaller than captured bytes.
        uint originalPacketLength = Math.Max(capturedLength, originalLength);
        int paddedDataLength = PcapPadding.PaddedLength(data.Length);

        // total = fixed header (28) + padded data + trailing length (4)
        // No options for EPBs in this implementation
        uint blockTotalLength = (uint)(_EpbHeaderSize + paddedDataLength + _TrailingLengthSize);

        // Convert timestamp to the appropriate resolution
        ulong tsValue = ConvertTimestamp(timestamp, tsResolution);
        uint timestampHigh = (uint)(tsValue >> 32);
        uint timestampLow = (uint)tsValue;

        // Assemble the full EPB, then one stream write — fewer syscalls and no
        // partial EPB if the write fails mid-block.
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

    /// <summary>Flushes the underlying stream.</summary>
    internal void Flush() => _Stream.Flush();

    // ========================================================================
    // Private helpers
    // ========================================================================

    /// <summary>Writes bytes to the stream and updates <see cref="_BytesWritten"/>.</summary>
    private void _WriteToStream(ReadOnlySpan<byte> data)
    {
        _Stream.Write(data);
        _BytesWritten += data.Length;
    }

    /// <summary>
    /// Converts a nanosecond timestamp to the target resolution.
    /// Negative timestamps (before Unix epoch) are clamped to 0 since PCAPNG timestamps are unsigned.
    /// </summary>
    internal static ulong ConvertTimestamp(Timestamp timestamp, byte tsResolution)
    {
        long nanos = timestamp.AsNanos;
        // PCAPNG timestamps are unsigned — clamp negative values to 0
        ulong nanosUnsigned = (ulong)Math.Max(nanos, 0);

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
        // Use checked multiplication to detect overflow, saturate to ulong.MaxValue
        try
        {
            return checked(nanosUnsigned * multiplier);
        }
        catch (OverflowException)
        {
            return ulong.MaxValue;
        }
    }

    /// <summary>Computes 10^exponent for small exponents via lookup (exponents 0–18 cover PCAPNG resol range).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong _Pow10(uint exponent)
    {
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
        if (exponent < (uint)powers.Length)
        {
            return powers[(int)exponent];
        }

        // Rare out-of-range resol: match prior unchecked iterative overflow behavior.
        ulong result = powers[^1];
        unchecked
        {
            for (uint i = (uint)(powers.Length - 1); i < exponent; i++)
            {
                result *= 10UL;
            }
        }
        return result;
    }

    /// <summary>Writes a PCAPNG option with a UTF-8 string value into a <see cref="PooledBuffer"/>.</summary>
    private static void _WriteOptionToBuffer(PooledBuffer buffer, ushort code, string value)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        Span<byte> utf8 = maxBytes <= 256 ? stackalloc byte[maxBytes] : new byte[maxBytes];
        int written = Encoding.UTF8.GetBytes(value, utf8);
        _WriteOptionRawToBuffer(buffer, code, utf8[..written]);
    }

    /// <summary>Writes a PCAPNG option with raw byte value into a <see cref="PooledBuffer"/>.</summary>
    private static void _WriteOptionRawToBuffer(PooledBuffer buffer, ushort code, ReadOnlySpan<byte> value)
    {
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
    private static void _WriteEndOfOptionsToBuffer(PooledBuffer buffer)
    {
        Span<byte> eoo = buffer.Reserve(_OptionHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(eoo, PcapConstants.OptEndOfOpt);
        BinaryPrimitives.WriteUInt16LittleEndian(eoo[2..], 0);
    }
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Generators;

/// <summary>
/// Builds synthetic PcapNG files in memory for testing.
/// Writes the binary PcapNG format: SHB → IDB(s) → EPB(s).
/// </summary>
internal sealed class PcapNgTestWriter : IDisposable
{
    // ========================================================================
    // Constants
    // ========================================================================

    private const uint _ShbType = 0x0A0D_0D0A;
    private const uint _IdbType = 0x0000_0001;
    private const uint _EpbType = 0x0000_0006;
    private const uint _ByteOrderMagic = 0x1A2B_3C4D;

    // ========================================================================
    // State
    // ========================================================================

    private readonly MemoryStream _Stream = new();
    private readonly List<ulong> _TsResolutions = [];

    /// <summary>
    /// Creates a new PcapNG writer — immediately writes the Section Header Block.
    /// </summary>
    internal PcapNgTestWriter()
    {
        _WriteSectionHeaderBlock();
    }

    /// <summary>
    /// Adds an interface and returns its interface ID (0-based).
    /// </summary>
    /// <param name="linkType">Link-layer type for this interface.</param>
    /// <param name="nanosecondResolution">If true, timestamps use nanosecond resolution (default: microseconds).</param>
    /// <param name="snapLen">Max captured length per frame.</param>
    /// <param name="name">Optional IDB if_name.</param>
    /// <param name="timestampOffsetSeconds">Optional IDB if_tsoffset in seconds.</param>
    /// <param name="rawLinkType">When set, written as the IDB link type instead of <paramref name="linkType"/>.</param>
    /// <param name="rawTimestampResolution">When set, written as the raw <c>if_tsresol</c> byte.</param>
    internal uint AddInterface(
        LinkType linkType = LinkType.Ethernet,
        bool nanosecondResolution = false,
        uint snapLen = 65535,
        string? name = null,
        long timestampOffsetSeconds = 0,
        ushort? rawLinkType = null,
        byte? rawTimestampResolution = null)
    {
        uint interfaceId = (uint)_TsResolutions.Count;
        byte tsResolution = rawTimestampResolution ?? (nanosecondResolution ? (byte)9 : (byte)6);
        // Exponents above 19 do not fit in ulong. The reader rejects them; the writer only needs a divisor for the test clock.
        ulong divisor = tsResolution <= 19 ? _Pow10(tsResolution) : 1UL;
        _TsResolutions.Add(divisor);

        ushort linkTypeCode = rawLinkType ?? (ushort)linkType;
        _WriteInterfaceDescriptionBlock(linkTypeCode, snapLen, tsResolution, name, timestampOffsetSeconds);
        return interfaceId;
    }

    /// <summary>
    /// Writes an Enhanced Packet Block with the given frame data.
    /// </summary>
    /// <param name="interfaceId">Interface this frame was captured on.</param>
    /// <param name="timestampNanos">Timestamp in nanoseconds since Unix epoch.</param>
    /// <param name="data">Raw frame data.</param>
    /// <param name="originalLength">
    /// EPB orig_len. When omitted, equals captured length.
    /// Readers ignore this field; it exists so tests can prove captured-length-only behavior.
    /// </param>
    internal void WriteFrame(uint interfaceId, long timestampNanos, ReadOnlySpan<byte> data, uint? originalLength = null)
    {
        ulong divisor = interfaceId < (uint)_TsResolutions.Count
            ? _TsResolutions[(int)interfaceId]
            : 1_000_000; // default to microseconds

        // Convert nanoseconds to interface units
        ulong tsUnits = divisor == 1_000_000_000
            ? (ulong)timestampNanos
            : (ulong)timestampNanos / (1_000_000_000 / divisor);

        uint tsHigh = (uint)(tsUnits >> 32);
        uint tsLow = (uint)tsUnits;

        uint capturedLen = (uint)data.Length;
        uint origLen = originalLength ?? capturedLen;
        int paddedLen = (data.Length + 3) & ~3;

        // Block total length: 32 (header+fields) + padded data
        uint blockLen = (uint)(32 + paddedLen);

        Span<byte> header = stackalloc byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(header, _EpbType);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], blockLen);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], interfaceId);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], tsHigh);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], tsLow);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], capturedLen);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], origLen);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], blockLen);    // trailing block len

        _Stream.Write(header[..28]);
        _Stream.Write(data);

        // Padding
        int padding = paddedLen - data.Length;
        if (padding > 0)
        {
            Span<byte> pad = stackalloc byte[4];
            pad.Clear();
            _Stream.Write(pad[..padding]);
        }

        // Trailing block length
        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, blockLen);
        _Stream.Write(trailer);
    }

    /// <summary>Appends an already-serialized PCAPNG block.</summary>
    internal void WriteRawBlock(ReadOnlySpan<byte> block) => _Stream.Write(block);

    /// <summary>Returns the complete PcapNG file as a byte array.</summary>
    internal byte[] Build() => _Stream.ToArray();

    /// <summary>
    /// Disposes the underlying stream.
    /// </summary>
    public void Dispose() => _Stream.Dispose();

    // ========================================================================
    // Block writers
    // ========================================================================

    private void _WriteSectionHeaderBlock()
    {
        // Minimum SHB: 28 bytes (no options)
        uint blockLen = 28;
        Span<byte> block = stackalloc byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(block, _ShbType);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], blockLen);
        BinaryPrimitives.WriteUInt32LittleEndian(block[8..], _ByteOrderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(block[12..], 1); // major version
        BinaryPrimitives.WriteUInt16LittleEndian(block[14..], 0); // minor version
        BinaryPrimitives.WriteInt64LittleEndian(block[16..], -1); // section length = unknown
        BinaryPrimitives.WriteUInt32LittleEndian(block[24..], blockLen); // trailing block len
        _Stream.Write(block);
    }

    private void _WriteInterfaceDescriptionBlock(
        ushort linkType,
        uint snapLen,
        byte tsResolution,
        string? name,
        long timestampOffsetSeconds)
    {
        using MemoryStream options = new();
        if (name is not null)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            _WriteOption(options, 2, nameBytes);
        }

        _WriteOption(options, 9, [tsResolution]);

        if (timestampOffsetSeconds != 0)
        {
            Span<byte> offsetBytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(offsetBytes, timestampOffsetSeconds);
            _WriteOption(options, 14, offsetBytes);
        }

        // opt_endofopt
        options.Write(stackalloc byte[4]);

        byte[] optionBytes = options.ToArray();
        uint blockLen = (uint)(20 + optionBytes.Length);
        byte[] block = new byte[blockLen];
        BinaryPrimitives.WriteUInt32LittleEndian(block, _IdbType);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), blockLen);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), linkType);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), snapLen);
        optionBytes.CopyTo(block.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan((int)blockLen - 4), blockLen);
        _Stream.Write(block);
    }

    private static void _WriteOption(Stream dest, ushort code, ReadOnlySpan<byte> value)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(header, code);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)value.Length);
        dest.Write(header);
        dest.Write(value);
        int pad = (4 - (value.Length & 3)) & 3;
        if (pad > 0)
        {
            Span<byte> padBytes = stackalloc byte[4];
            padBytes.Clear();
            dest.Write(padBytes[..pad]);
        }
    }

    /// <summary>Returns 10 raised to <paramref name="exponent"/>; used to convert the pcapng timestamp resolution option to a divisor.</summary>
    private static ulong _Pow10(byte exponent)
    {
        ulong result = 1;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }
        return result;
    }
}

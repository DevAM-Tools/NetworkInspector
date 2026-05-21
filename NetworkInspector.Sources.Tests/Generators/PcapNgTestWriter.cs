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

    private const uint ShbType = 0x0A0D_0D0A;
    private const uint IdbType = 0x0000_0001;
    private const uint EpbType = 0x0000_0006;
    private const uint ByteOrderMagic = 0x1A2B_3C4D;

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
        WriteSectionHeaderBlock();
    }

    /// <summary>
    /// Adds an interface and returns its interface ID (0-based).
    /// </summary>
    /// <param name="linkType">Link-layer type for this interface.</param>
    /// <param name="nanosecondResolution">If true, timestamps use nanosecond resolution (default: microseconds).</param>
    /// <param name="snapLen">Max captured length per frame.</param>
    internal uint AddInterface(
        LinkType linkType = LinkType.Ethernet,
        bool nanosecondResolution = false,
        uint snapLen = 65535)
    {
        uint interfaceId = (uint)_TsResolutions.Count;
        byte tsResolution = nanosecondResolution ? (byte)9 : (byte)6;
        ulong divisor = Pow10(tsResolution);
        _TsResolutions.Add(divisor);

        WriteInterfaceDescriptionBlock(linkType, snapLen, tsResolution);
        return interfaceId;
    }

    /// <summary>
    /// Writes an Enhanced Packet Block with the given frame data.
    /// </summary>
    /// <param name="interfaceId">Interface this frame was captured on.</param>
    /// <param name="timestampNanos">Timestamp in nanoseconds since Unix epoch.</param>
    /// <param name="data">Raw frame data.</param>
    internal void WriteFrame(uint interfaceId, long timestampNanos, ReadOnlySpan<byte> data)
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
        int paddedLen = (data.Length + 3) & ~3;

        // Block total length: 32 (header+fields) + padded data
        uint blockLen = (uint)(32 + paddedLen);

        Span<byte> header = stackalloc byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(header, EpbType);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], blockLen);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], interfaceId);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], tsHigh);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], tsLow);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], capturedLen);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], capturedLen); // original length
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

    /// <summary>
    /// Returns the complete PcapNG file as a byte array.
    /// </summary>
    internal byte[] Build() => _Stream.ToArray();

    /// <summary>
    /// Disposes the underlying stream.
    /// </summary>
    public void Dispose() => _Stream.Dispose();

    // ========================================================================
    // Block writers
    // ========================================================================

    private void WriteSectionHeaderBlock()
    {
        // Minimum SHB: 28 bytes (no options)
        uint blockLen = 28;
        Span<byte> block = stackalloc byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(block, ShbType);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], blockLen);
        BinaryPrimitives.WriteUInt32LittleEndian(block[8..], ByteOrderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(block[12..], 1); // major version
        BinaryPrimitives.WriteUInt16LittleEndian(block[14..], 0); // minor version
        BinaryPrimitives.WriteInt64LittleEndian(block[16..], -1); // section length = unknown
        BinaryPrimitives.WriteUInt32LittleEndian(block[24..], blockLen); // trailing block len
        _Stream.Write(block);
    }

    private void WriteInterfaceDescriptionBlock(LinkType linkType, uint snapLen, byte tsResolution)
    {
        // Options: if_tsresol (code 9, length 1, padded to 4) + opt_endofopt (4)
        // Options size: 4 (code+len) + 4 (value+padding) + 4 (endofopt) = 12
        // But standard: code(2)+len(2)+value(1)+padding(3) + endofopt(4) = 12
        int optionsLen = 12;
        uint blockLen = (uint)(20 + optionsLen);

        byte[] block = new byte[blockLen];
        BinaryPrimitives.WriteUInt32LittleEndian(block, IdbType);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), blockLen);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), (ushort)linkType);
        // reserved 2 bytes at offset 10 (already zero)
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), snapLen);

        // if_tsresol option
        int optOff = 16;
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(optOff), 9);     // option code
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(optOff + 2), 1); // option length
        block[optOff + 4] = tsResolution;
        // padding (3 bytes already zero)

        // opt_endofopt
        // code=0, length=0 → 4 zero bytes (already zero)

        // Trailing block length
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan((int)blockLen - 4), blockLen);
        _Stream.Write(block);
    }

    private static ulong Pow10(byte exponent)
    {
        ulong result = 1;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }
        return result;
    }
}

// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Exporters.Tests.Verification;

/// <summary>
/// Verifies PCAPNG files by parsing the binary block structure.
/// Validates SHB/IDB/EPB blocks, alignment, and structural integrity.
/// </summary>
internal sealed class PcapngVerifier
{
    // Block type constants
    private const uint ShbType = 0x0A0D_0D0A;
    private const uint IdbType = 0x0000_0001;
    private const uint EpbType = 0x0000_0006;
    private const uint ByteOrderMagic = 0x1A2B_3C4D;

    /// <summary>Number of Section Header Blocks found.</summary>
    internal int SectionCount
    {
        get; private set;
    }

    /// <summary>Number of Interface Description Blocks found.</summary>
    internal int InterfaceCount
    {
        get; private set;
    }

    /// <summary>Number of Enhanced Packet Blocks found.</summary>
    internal int FrameCount
    {
        get; private set;
    }

    /// <summary>Per-frame info from all EPBs.</summary>
    internal List<EpbInfo> Frames { get; } = [];

    /// <summary>Per-interface info from all IDBs.</summary>
    internal List<IdbInfo> Interfaces { get; } = [];

    /// <summary>
    /// Opens and parses a PCAPNG file, returning a verifier with structural information.
    /// </summary>
    /// <param name="path">Path to the PCAPNG file.</param>
    internal static PcapngVerifier Open(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        PcapngVerifier verifier = new();
        verifier.Parse(data);
        return verifier;
    }

    /// <summary>
    /// Opens and parses a PCAPNG file from a byte array.
    /// </summary>
    internal static PcapngVerifier FromData(byte[] data)
    {
        PcapngVerifier verifier = new();
        verifier.Parse(data);
        return verifier;
    }

    /// <summary>
    /// Parses all blocks in the PCAPNG data. Throws on structural errors.
    /// </summary>
    private void Parse(byte[] data)
    {
        int offset = 0;

        while (offset + 8 <= data.Length)
        {
            uint blockType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            uint blockLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));

            if (blockLen < 12 || offset + (int)blockLen > data.Length)
            {
                throw new InvalidDataException(
                    $"Invalid block length {blockLen} at offset {offset}");
            }

            // Verify trailing block length matches header
            uint trailingLen = BinaryPrimitives.ReadUInt32LittleEndian(
                data.AsSpan(offset + (int)blockLen - 4));

            if (trailingLen != blockLen)
            {
                throw new InvalidDataException(
                    $"Trailing block length {trailingLen} != header length {blockLen} at offset {offset}");
            }

            switch (blockType)
            {
                case ShbType:
                    ParseShb(data, offset, blockLen);
                    break;
                case IdbType:
                    ParseIdb(data, offset, blockLen);
                    break;
                case EpbType:
                    ParseEpb(data, offset);
                    break;
            }

            // Advance to next block (lengths are already 32-bit aligned per spec)
            offset += (int)blockLen;
        }
    }

    /// <summary>Parses an Interface Description Block and records interface info.</summary>
    private void ParseIdb(byte[] data, int offset, uint blockLen)
    {
        if (blockLen < 20)
        {
            throw new InvalidDataException($"IDB too small: {blockLen} bytes at offset {offset}");
        }

        ushort linkType = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 8));
        uint snapLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12));

        Interfaces.Add(new IdbInfo(linkType, snapLength));
        InterfaceCount++;
    }

    /// <summary>Validates the Section Header Block structure.</summary>
    private void ParseShb(byte[] data, int offset, uint blockLen)
    {
        if (blockLen < 28)
        {
            throw new InvalidDataException($"SHB too small: {blockLen} bytes at offset {offset}");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8));
        if (magic != ByteOrderMagic)
        {
            throw new InvalidDataException(
                $"Invalid byte order magic 0x{magic:X8} at offset {offset}");
        }

        SectionCount++;
    }

    /// <summary>Parses an Enhanced Packet Block and records frame info.</summary>
    private void ParseEpb(byte[] data, int offset)
    {
        uint interfaceId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8));
        uint tsHigh = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12));
        uint tsLow = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 16));
        uint capturedLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 20));
        uint originalLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 24));

        ulong timestamp = ((ulong)tsHigh << 32) | tsLow;

        // Extract frame data
        int dataOffset = offset + 28;
        byte[] frameData = new byte[capturedLen];
        data.AsSpan(dataOffset, (int)capturedLen).CopyTo(frameData);

        Frames.Add(new EpbInfo(interfaceId, timestamp, capturedLen, originalLen, frameData));
        FrameCount++;
    }

    /// <summary>Information extracted from an Enhanced Packet Block.</summary>
    /// <param name="InterfaceId">Interface ID this frame belongs to.</param>
    /// <param name="Timestamp">Raw timestamp value (interpretation depends on IDB resolution).</param>
    /// <param name="CapturedLength">Number of captured bytes.</param>
    /// <param name="OriginalLength">Original frame length on the wire.</param>
    /// <param name="Data">Raw frame data.</param>
    internal readonly record struct EpbInfo(
        uint InterfaceId,
        ulong Timestamp,
        uint CapturedLength,
        uint OriginalLength,
        byte[] Data);

    /// <summary>Information extracted from an Interface Description Block.</summary>
    /// <param name="LinkType">Link-layer type (DLT value).</param>
    /// <param name="SnapLength">Maximum captured packet length.</param>
    internal readonly record struct IdbInfo(
        ushort LinkType,
        uint SnapLength);

}

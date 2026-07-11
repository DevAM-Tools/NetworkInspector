// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Verification;

/// <summary>
/// Structural verification helper for BLF (Binary Logging Format) files produced by exporters.
/// Parses the LOGG header and iterates uncompressed LOBJ block headers — it does not
/// decompress LOG_CONTAINER payloads (see EXPORTER_GUIDE).
/// Validates file magic, block headers, and object types encountered at top level.
/// </summary>
internal sealed class BlfStructuralVerifier
{
    // Magic constants (little-endian u32)
    private const uint _FileMagic = 0x47474F4C;  // "LOGG"
    private const uint _ObjectMagic = 0x4A424F4C; // "LOBJ"
    private const int _FileHeaderMinSize = 144;
    private const int _BlockHeaderSize = 16;

    /// <summary>Total number of objects found (excluding container payloads).</summary>
    internal int ObjectCount
    {
        get; private set;
    }

    /// <summary>Distinct object types encountered.</summary>
    internal HashSet<uint> ObjectTypes { get; } = [];

    /// <summary>Whether the file header signature is valid.</summary>
    internal bool HasValidHeader
    {
        get; private set;
    }

    /// <summary>
    /// Opens and parses a BLF file, returning a verifier with structural information.
    /// </summary>
    internal static BlfStructuralVerifier Open(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        BlfStructuralVerifier verifier = new();
        verifier._Parse(data);
        return verifier;
    }

    /// <summary>
    /// Parses the BLF file structure from raw bytes.
    /// </summary>
    private void _Parse(byte[] data)
    {
        if (data.Length < _FileHeaderMinSize)
        {
            throw new InvalidDataException($"BLF file too small: {data.Length} bytes");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        HasValidHeader = magic == _FileMagic;

        if (!HasValidHeader)
        {
            throw new InvalidDataException($"Invalid BLF magic: 0x{magic:X8}");
        }

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        int offset = (int)headerSize;

        while (offset + _BlockHeaderSize <= data.Length)
        {
            uint objMagic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            if (objMagic != _ObjectMagic)
            {
                break;
            }

            ushort totalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
            uint objectLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8));
            uint objectType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12));

            ObjectTypes.Add(objectType);
            ObjectCount++;

            int totalSize = ((int)objectLength + 3) & ~3;
            offset += totalSize;
        }
    }
}

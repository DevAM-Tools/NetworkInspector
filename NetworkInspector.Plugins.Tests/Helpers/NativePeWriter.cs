// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Writes a minimal native PE32 file without a CLI header: the shape of a native DLL that a plugin folder may contain next to
/// plugin assemblies. Stateless; thread-safe.
/// </summary>
internal static class NativePeWriter
{
    #region Public API

    /// <summary>Writes a 512-byte PE32 file with an MZ header, 0 sections, and 16 empty data directories.</summary>
    /// <param name="path">Output file.</param>
    internal static void Write(string path)
    {
        byte[] file = new byte[512];
        Span<byte> bytes = file;

        // DOS header: "MZ" and the offset of the PE header at 0x3C.
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes[0x3C..], 0x80);

        // PE signature and COFF header: machine x86, no sections, optional header of 0xE0 bytes, DLL characteristics.
        "PE\0\0"u8.CopyTo(bytes[0x80..]);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[0x84..], 0x14C);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[0x94..], 0xE0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[0x96..], 0x2102);

        // Optional header: PE32 magic, image base, alignments, sizes, subsystem, and 16 empty data directories.
        Span<byte> optional = bytes[0x98..];
        BinaryPrimitives.WriteUInt16LittleEndian(optional, 0x10B);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[28..], 0x400000);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[32..], 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[36..], 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[56..], 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[60..], 0x200);
        BinaryPrimitives.WriteUInt16LittleEndian(optional[68..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[92..], 16);

        File.WriteAllBytes(path, file);
    }

    #endregion
}

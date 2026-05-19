// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using ZeroAlloc;

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF block/object header — 16 bytes.
/// Every BLF object (including log containers) starts with this header.
/// The signature field must equal <see cref="BlfConstants.ObjectMagic"/> ("LOBJ").
///
/// Layout:
///   [0..4)  Signature  — "LOBJ" as LE u32
///   [4..6)  HeaderSize — total header size (block + log object header)
///   [6..8)  HeaderType — selects V1/V2/V3 log object header format
///   [8..12) ObjectLength — payload length after the full header
///   [12..16) ObjectType — identifies the object kind (CAN, Ethernet, etc.)
/// </summary>
[BinaryParsable]
internal readonly partial struct BlfBlockHeader
{
    /// <summary>Object signature. Must be "LOBJ" (<see cref="BlfConstants.ObjectMagic"/>).</summary>
    public U32LE Signature
    {
        get; init;
    }

    /// <summary>Total header size in bytes (block header + log object header).</summary>
    public U16LE HeaderSize
    {
        get; init;
    }

    /// <summary>
    /// Header type selector:
    /// 1 = <see cref="BlfLogObjectHeaderV1"/> (16B),
    /// 2 = <see cref="BlfLogObjectHeaderV2"/> (24B),
    /// 3 = <see cref="BlfLogObjectHeaderV3"/> (16B).
    /// </summary>
    public U16LE HeaderType
    {
        get; init;
    }

    /// <summary>Object payload length in bytes (after the full header).</summary>
    public U32LE ObjectLength
    {
        get; init;
    }

    /// <summary>Object type identifier (see <see cref="BlfConstants"/>).</summary>
    public U32LE ObjectType
    {
        get; init;
    }
}

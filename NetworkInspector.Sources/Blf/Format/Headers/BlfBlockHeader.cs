// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
///   [8..12) ObjectLength — total LOBJ size from the first magic byte, including this header
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

    /// <summary>
    /// Total size in bytes of this LOBJ from the first magic byte, including this 16-byte header.
    /// Inner writers store the unpadded total; 0–3 alignment zeros are written after the object
    /// so the next LOBJ is 4-aligned. Skip distance is <c>max(max(16, ObjectLength), HeaderSize)</c>;
    /// the following 1-byte LOBJ scan consumes those pad bytes. Padding is not part of the
    /// compressed container payload.
    /// </summary>
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

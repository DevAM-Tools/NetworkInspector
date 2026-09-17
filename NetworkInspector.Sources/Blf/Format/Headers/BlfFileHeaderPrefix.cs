// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF file header prefix — 12 bytes.
/// Magic, header length, and API version. Measurement start time is a <see cref="BlfDate"/>
/// at file offset 40 (after application/compression bytes and the compressed/uncompressed
/// length fields). Those later bytes are not part of this prefix.
/// </summary>
[BinaryParsable]
internal readonly partial struct BlfFileHeaderPrefix
{
    /// <summary>File signature. Must equal <see cref="BlfConstants.FileMagic"/> ("LOGG").</summary>
    public U32LE Signature
    {
        get; init;
    }

    /// <summary>Total file header size in bytes. Objects start at this offset.</summary>
    public U32LE HeaderSize
    {
        get; init;
    }

    /// <summary>BLF API version.</summary>
    public U32LE ApiVersion
    {
        get; init;
    }
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF file header prefix — 20 bytes.
/// Contains the first 5 fields of the BLF file header before the embedded BlfDate structs.
/// The full file header is at least 144 bytes; this prefix contains the fields needed
/// for validation and header size determination.
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

    /// <summary>Platform identifier (Windows = 1).</summary>
    public U32LE Platform
    {
        get; init;
    }

    /// <summary>Creation flags.</summary>
    public U32LE CreationFlags
    {
        get; init;
    }
}

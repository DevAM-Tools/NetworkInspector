// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using ZeroAlloc;

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF Container Header — 16 bytes.
/// Follows the log object header when the object type is LOG_CONTAINER (10).
/// Describes the compression method and uncompressed payload size.
///
/// Layout:
///   [0..2)   CompressionMethod  — 0 = none, 2 = zlib
///   [2..4)   Reserved1A         — reserved
///   [4..8)   Reserved1B         — reserved
///   [8..12)  UncompressedSize   — decompressed payload size in bytes
///   [12..16) Reserved2          — reserved
/// </summary>
[BinaryParsable]
internal readonly partial struct BlfContainerHeader
{
    /// <summary>
    /// Compression method: 0 = uncompressed, 2 = zlib.
    /// See <see cref="BlfConstants.CompressionNone"/> and <see cref="BlfConstants.CompressionZlib"/>.
    /// </summary>
    public U16LE CompressionMethod
    {
        get; init;
    }

    /// <summary>Reserved field (part of 6-byte reserved block).</summary>
    public U16LE Reserved1A
    {
        get; init;
    }

    /// <summary>Reserved field (part of 6-byte reserved block).</summary>
    public U32LE Reserved1B
    {
        get; init;
    }

    /// <summary>Size in bytes of the decompressed payload.</summary>
    public U32LE UncompressedSize
    {
        get; init;
    }

    /// <summary>Reserved field.</summary>
    public U32LE Reserved2
    {
        get; init;
    }
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format;

/// <summary>
/// Parsed BLF object metadata extracted from block + log object headers.
/// Combines the critical fields needed for frame extraction: object type,
/// raw timestamp, flags, channel index, and payload data boundaries.
/// </summary>
internal readonly ref struct BlfObjectInfo
{
    /// <summary>Object type identifier.</summary>
    internal uint ObjectType
    {
        get; init;
    }

    /// <summary>Timestamp in nanoseconds (already resolved from raw + flags).</summary>
    internal long TimestampNanos
    {
        get; init;
    }

    /// <summary>Raw flags from the log object header.</summary>
    internal uint Flags
    {
        get; init;
    }

    /// <summary>Client/channel index (0 for V3 headers which lack this field).</summary>
    internal ushort ClientIndex
    {
        get; init;
    }

    /// <summary>Object struct version.</summary>
    internal uint ObjectVersion
    {
        get; init;
    }

    /// <summary>Payload data following the complete header.</summary>
    internal ReadOnlySpan<byte> Payload
    {
        get; init;
    }
}

/// <summary>
/// Dispatches BLF block + log object header parsing based on header_type.
/// Reads the block header, then the appropriate V1/V2/V3 log object header,
/// and returns a <see cref="BlfObjectInfo"/> with resolved timestamp and payload slice.
/// </summary>
internal static class BlfObjectHeaderParser
{
    #region Public API

    /// <summary>
    /// Tries to parse a complete BLF object (block header + log object header + payload)
    /// from the given data span.
    /// </summary>
    /// <param name="data">Data starting at the "LOBJ" magic.</param>
    /// <param name="startOffset">Offset of this object in the file (for error messages).</param>
    /// <param name="info">Parsed object info on success.</param>
    /// <param name="skipDistance">
    /// Total bytes to advance past this object to reach the next.
    /// Computed as: max(max(16, objectLength), headerSize).
    /// </param>
    /// <returns>True if a valid object was parsed.</returns>
    internal static bool TryParse(
        ReadOnlySpan<byte> data,
        long startOffset,
        out BlfObjectInfo info,
        out int skipDistance)
    {
        info = default;
        skipDistance = 0;

        // Need at least the block header (16B)
        if (data.Length < BlfConstants.BlockHeaderSize)
        {
            return false;
        }

        if (!BlfBlockHeader.TryParse(data, out BlfBlockHeader blockHeader, out _))
        {
            return false;
        }

        // Validate "LOBJ" magic
        if (blockHeader.Signature.Value != BlfConstants.ObjectMagic)
        {
            return false;
        }

        ushort headerSize = blockHeader.HeaderSize.Value;
        uint objectLength = blockHeader.ObjectLength.Value;
        uint objectType = blockHeader.ObjectType.Value;

        // Compute skip distance using long arithmetic to prevent uint overflow:
        // objectLength is untrusted and can be > int.MaxValue, which would wrap to a
        // negative int and produce a negative skip distance or slice length.
        // Reject objects that claim more bytes than a .NET span can address.
        long totalObjectSizeLong = Math.Max(
            Math.Max((long)BlfConstants.BlockHeaderSize, objectLength),
            headerSize);
        if (totalObjectSizeLong > int.MaxValue)
        {
            // Claimed size exceeds addressable span range — treat as corrupt.
            return false;
        }
        int totalObjectSize = (int)totalObjectSizeLong;
        skipDistance = totalObjectSize;

        // Total data needed: block header + log object header
        int fullHeaderSize = BlfConstants.BlockHeaderSize + GetLogObjectHeaderSize(blockHeader.HeaderType.Value);
        if (data.Length < fullHeaderSize || data.Length < totalObjectSize)
        {
            return false;
        }

        // Parse log object header based on type
        ReadOnlySpan<byte> logHeaderData = data[BlfConstants.BlockHeaderSize..];

        switch (blockHeader.HeaderType.Value)
        {
            case 1:
                return TryParseWithV1(logHeaderData, objectType, headerSize, data, out info);
            case 2:
                return TryParseWithV2(logHeaderData, objectType, headerSize, data, out info);
            case 3:
                return TryParseWithV3(logHeaderData, objectType, headerSize, data, out info);
            default:
                // Unknown header type — skip this object
                return false;
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>Returns the size of a log object header variant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetLogObjectHeaderSize(ushort headerType) => headerType switch
    {
        1 => BlfConstants.LogObjectHeaderType1Size,
        2 => BlfConstants.LogObjectHeaderType2Size,
        3 => BlfConstants.LogObjectHeaderType3Size,
        _ => 0,
    };

    /// <summary>Parse with V1 log object header.</summary>
    private static bool TryParseWithV1(
        ReadOnlySpan<byte> logHeaderData,
        uint objectType,
        ushort headerSize,
        ReadOnlySpan<byte> fullData,
        out BlfObjectInfo info)
    {
        info = default;
        if (!BlfLogObjectHeaderV1.TryParse(logHeaderData, out BlfLogObjectHeaderV1 v1, out _))
        {
            return false;
        }

        // Payload starts after the full header (block + log object)
        ReadOnlySpan<byte> payload = fullData.Length > headerSize
            ? fullData[headerSize..]
            : ReadOnlySpan<byte>.Empty;

        info = new BlfObjectInfo
        {
            ObjectType = objectType,
            TimestampNanos = BlfTimestamp.ToNanoseconds(v1.Timestamp.Value, v1.Flags.Value),
            Flags = v1.Flags.Value,
            ClientIndex = v1.ClientIndex.Value,
            ObjectVersion = v1.ObjectVersion.Value,
            Payload = payload,
        };
        return true;
    }

    /// <summary>Parse with V2 log object header.</summary>
    private static bool TryParseWithV2(
        ReadOnlySpan<byte> logHeaderData,
        uint objectType,
        ushort headerSize,
        ReadOnlySpan<byte> fullData,
        out BlfObjectInfo info)
    {
        info = default;
        if (!BlfLogObjectHeaderV2.TryParse(logHeaderData, out BlfLogObjectHeaderV2 v2, out _))
        {
            return false;
        }

        ReadOnlySpan<byte> payload = fullData.Length > headerSize
            ? fullData[headerSize..]
            : ReadOnlySpan<byte>.Empty;

        info = new BlfObjectInfo
        {
            ObjectType = objectType,
            TimestampNanos = BlfTimestamp.ToNanoseconds(v2.Timestamp.Value, v2.Flags.Value),
            Flags = v2.Flags.Value,
            ClientIndex = 0, // V2 (per Vector blf.h) has no client_index field; uses timestamp_status instead
            ObjectVersion = v2.ObjectVersion.Value,
            Payload = payload,
        };
        return true;
    }

    /// <summary>Parse with V3 log object header.</summary>
    private static bool TryParseWithV3(
        ReadOnlySpan<byte> logHeaderData,
        uint objectType,
        ushort headerSize,
        ReadOnlySpan<byte> fullData,
        out BlfObjectInfo info)
    {
        info = default;
        if (!BlfLogObjectHeaderV3.TryParse(logHeaderData, out BlfLogObjectHeaderV3 v3, out _))
        {
            return false;
        }

        ReadOnlySpan<byte> payload = fullData.Length > headerSize
            ? fullData[headerSize..]
            : ReadOnlySpan<byte>.Empty;

        info = new BlfObjectInfo
        {
            ObjectType = objectType,
            TimestampNanos = BlfTimestamp.ToNanoseconds(v3.Timestamp.Value, v3.Flags.Value),
            Flags = v3.Flags.Value,
            ClientIndex = 0, // V3 has no client index
            ObjectVersion = v3.ObjectVersion.Value,
            Payload = payload,
        };
        return true;
    }

    #endregion
}

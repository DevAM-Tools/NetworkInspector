// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// Parsed EPB, SPB, or obsolete PB fields. Packet bytes are a slice of the caller's span.
/// </summary>
internal readonly ref struct EpbFields
{
    /// <summary>Zero-based interface ID (always 0 for SPB).</summary>
    internal uint InterfaceId
    {
        get; init;
    }

    /// <summary>Original packet length from the block (ignored for captured payload slicing).</summary>
    internal uint OriginalLength
    {
        get; init;
    }

    /// <summary>Captured payload length after pad-aware bounds checks.</summary>
    internal int CapturedLength
    {
        get; init;
    }

    /// <summary>Timestamp in nanoseconds, including <c>if_tsoffset</c>. Zero for SPB.</summary>
    internal long TimestampNanos
    {
        get; init;
    }

    /// <summary>Captured packet bytes. Valid only while the source span is valid.</summary>
    internal ReadOnlySpan<byte> PacketData
    {
        get; init;
    }
}

/// <summary>
/// Parsed IDB link type, snap length, and option bytes.
/// </summary>
internal readonly ref struct IdbFields
{
    /// <summary>Raw link-layer type from the IDB.</summary>
    internal ushort LinkType
    {
        get; init;
    }

    /// <summary>Snapshot length from the IDB.</summary>
    internal uint SnapLength
    {
        get; init;
    }

    /// <summary>Option TLV bytes between the fixed header and the trailing length.</summary>
    internal ReadOnlySpan<byte> OptionData
    {
        get; init;
    }
}

/// <summary>
/// Shared PCAPNG field parsing for the file scanner and the stream reader.
/// </summary>
/// <remarks>
/// Stateless helpers; all methods are thread-safe. Returned spans alias the caller's buffer.
/// </remarks>
internal static class PcapngPacketBlocks
{
    #region Constants

    /// <summary>SHB bytes before options: type, length, magic, versions, section length.</summary>
    private const int _ShbBytesBeforeOptions = 24;

    /// <summary>IDB bytes before options: type, length, link type, reserved, snap length.</summary>
    private const int _IdbBytesBeforeOptions = 16;

    #endregion

    #region Public API

    /// <summary>
    /// Reads an Enhanced Packet Block from a full block or from a body that omits the 8-byte header.
    /// <c>orig_len</c> is returned but captured payload uses <c>cap_len</c> plus pad bounds.
    /// </summary>
    /// <param name="blockOrBody">Full block, or body after type and length.</param>
    /// <param name="bodyOmits8ByteHeader">True when <paramref name="blockOrBody"/> starts at interface ID.</param>
    /// <param name="swap">True when the section byte order is swapped.</param>
    /// <param name="blockLength">Declared <c>block_total_length</c>.</param>
    /// <param name="section">Section that owns the referenced interface.</param>
    /// <param name="fields">Parsed fields when the result is <see cref="ScanPacketResult.Frame"/>.</param>
    internal static ScanPacketResult TryReadEpb(
        ReadOnlySpan<byte> blockOrBody,
        bool bodyOmits8ByteHeader,
        bool swap,
        uint blockLength,
        SectionInfo section,
        out EpbFields fields)
    {
        fields = default;
        if (blockLength < PcapConstants.EpbFixedSize)
        {
            return ScanPacketResult.SkipMalformed;
        }

        int dataOffset = _DataOffsetInSpan(bodyOmits8ByteHeader, PcapngBlockLayout.EpbBytesBeforePacketData);
        if (blockOrBody.Length < dataOffset)
        {
            return ScanPacketResult.SkipMalformed;
        }

        EndianReader reader = new(swap);
        uint interfaceId;
        uint tsHigh;
        uint tsLow;
        uint capturedLength;
        uint originalLength;
        if (bodyOmits8ByteHeader)
        {
            interfaceId = reader.ReadU32(blockOrBody);
            tsHigh = reader.ReadU32(blockOrBody[4..]);
            tsLow = reader.ReadU32(blockOrBody[8..]);
            capturedLength = reader.ReadU32(blockOrBody[12..]);
            originalLength = reader.ReadU32(blockOrBody[16..]);
        }
        else
        {
            if (!EnhancedPacketBlock.TryParse(blockOrBody, out EnhancedPacketBlock epb, out _))
            {
                return ScanPacketResult.SkipMalformed;
            }

            interfaceId = reader.Swap(epb.InterfaceId.Value);
            tsHigh = reader.Swap(epb.TimestampHigh.Value);
            tsLow = reader.Swap(epb.TimestampLow.Value);
            capturedLength = reader.Swap(epb.CapturedLength.Value);
            originalLength = reader.Swap(epb.OriginalLength.Value);
        }

        ulong rawTimestamp = ((ulong)tsHigh << 32) | tsLow;
        return _FinishPacket(
            section,
            interfaceId,
            rawTimestamp,
            applyTimestamp: true,
            blockOrBody,
            PcapngBlockLayout.EpbBytesBeforePacketData,
            dataOffset,
            blockLength,
            capturedLength,
            originalLength,
            out fields);
    }

    /// <summary>
    /// Reads a Simple Packet Block. Stored length is min(orig, snap) when that plus pad fits the block.
    /// </summary>
    internal static ScanPacketResult TryReadSpb(
        ReadOnlySpan<byte> blockOrBody,
        bool bodyOmits8ByteHeader,
        bool swap,
        uint blockLength,
        SectionInfo section,
        out EpbFields fields)
    {
        fields = default;
        if (blockLength < PcapConstants.SpbFixedSize)
        {
            return ScanPacketResult.SkipMalformed;
        }

        int dataOffset = _DataOffsetInSpan(bodyOmits8ByteHeader, PcapngBlockLayout.SpbBytesBeforePacketData);
        if (blockOrBody.Length < dataOffset)
        {
            return ScanPacketResult.SkipMalformed;
        }

        InterfaceInfo? iface = section.Interface(0);
        if (iface is null)
        {
            return ScanPacketResult.SkipNoInterface;
        }

        if (iface.LinkType is null)
        {
            return ScanPacketResult.SkipUnknownLinkType;
        }

        if (!iface.HasSupportedTimestampResolution)
        {
            return ScanPacketResult.SkipUnsupportedTimestampResolution;
        }

        EndianReader reader = new(swap);
        uint originalLength;
        if (bodyOmits8ByteHeader)
        {
            originalLength = reader.ReadU32(blockOrBody);
        }
        else
        {
            if (!SimplePacketBlock.TryParse(blockOrBody, out SimplePacketBlock spb, out _))
            {
                return ScanPacketResult.SkipMalformed;
            }

            originalLength = reader.Swap(spb.OriginalPacketLength.Value);
        }

        uint capturedCandidate = originalLength < iface.SnapLength ? originalLength : iface.SnapLength;
        if (!_TrySlicePacket(
                blockOrBody,
                PcapngBlockLayout.SpbBytesBeforePacketData,
                dataOffset,
                blockLength,
                capturedCandidate,
                out int captured,
                out ReadOnlySpan<byte> packetData))
        {
            return ScanPacketResult.SkipMalformed;
        }

        fields = new EpbFields
        {
            InterfaceId = 0,
            OriginalLength = originalLength,
            CapturedLength = captured,
            TimestampNanos = 0,
            PacketData = packetData,
        };
        return ScanPacketResult.Frame;
    }

    /// <summary>
    /// Reads an obsolete Packet Block. <c>orig_len</c> is returned but captured payload uses <c>cap_len</c>.
    /// </summary>
    internal static ScanPacketResult TryReadPb(
        ReadOnlySpan<byte> blockOrBody,
        bool bodyOmits8ByteHeader,
        bool swap,
        uint blockLength,
        SectionInfo section,
        out EpbFields fields)
    {
        fields = default;
        if (blockLength < PcapConstants.PbFixedSize)
        {
            return ScanPacketResult.SkipMalformed;
        }

        int dataOffset = _DataOffsetInSpan(bodyOmits8ByteHeader, PcapngBlockLayout.PbBytesBeforePacketData);
        if (blockOrBody.Length < dataOffset)
        {
            return ScanPacketResult.SkipMalformed;
        }

        EndianReader reader = new(swap);
        ushort interfaceId;
        uint tsHigh;
        uint tsLow;
        uint capturedLength;
        uint originalLength;
        if (bodyOmits8ByteHeader)
        {
            interfaceId = reader.ReadU16(blockOrBody);
            tsHigh = reader.ReadU32(blockOrBody[4..]);
            tsLow = reader.ReadU32(blockOrBody[8..]);
            capturedLength = reader.ReadU32(blockOrBody[12..]);
            originalLength = reader.ReadU32(blockOrBody[16..]);
        }
        else
        {
            if (!ObsoletePacketBlock.TryParse(blockOrBody, out ObsoletePacketBlock pb, out _))
            {
                return ScanPacketResult.SkipMalformed;
            }

            interfaceId = reader.Swap(pb.InterfaceId.Value);
            tsHigh = reader.Swap(pb.TimestampHigh.Value);
            tsLow = reader.Swap(pb.TimestampLow.Value);
            capturedLength = reader.Swap(pb.CapturedLength.Value);
            originalLength = reader.Swap(pb.OriginalLength.Value);
        }

        ulong rawTimestamp = ((ulong)tsHigh << 32) | tsLow;
        return _FinishPacket(
            section,
            interfaceId,
            rawTimestamp,
            applyTimestamp: true,
            blockOrBody,
            PcapngBlockLayout.PbBytesBeforePacketData,
            dataOffset,
            blockLength,
            capturedLength,
            originalLength,
            out fields);
    }

    /// <summary>
    /// Reads IDB link type, snap length, and the option span.
    /// </summary>
    internal static bool TryReadIdb(
        ReadOnlySpan<byte> blockOrBody,
        bool bodyOmits8ByteHeader,
        bool swap,
        uint blockLength,
        out IdbFields fields)
    {
        fields = default;
        int headerSkip = bodyOmits8ByteHeader ? 8 : 0;
        int minSpan = _IdbBytesBeforeOptions - headerSkip;
        if (blockOrBody.Length < minSpan)
        {
            return false;
        }

        EndianReader reader = new(swap);
        ushort linkType;
        uint snapLength;
        if (bodyOmits8ByteHeader)
        {
            linkType = reader.ReadU16(blockOrBody);
            snapLength = reader.ReadU32(blockOrBody[4..]);
        }
        else
        {
            if (!InterfaceDescriptionBlock.TryParse(blockOrBody, out InterfaceDescriptionBlock idb, out _))
            {
                return false;
            }

            linkType = reader.Swap(idb.LinkType.Value);
            snapLength = reader.Swap(idb.SnapLength.Value);
        }

        if (!_TryGetOptionsSpan(
                blockOrBody,
                bodyOmits8ByteHeader,
                blockLength,
                _IdbBytesBeforeOptions,
                out ReadOnlySpan<byte> optionData))
        {
            optionData = ReadOnlySpan<byte>.Empty;
        }

        fields = new IdbFields
        {
            LinkType = linkType,
            SnapLength = snapLength,
            OptionData = optionData,
        };
        return true;
    }

    /// <summary>
    /// Reads SHB section length and option bytes from a complete Section Header Block.
    /// </summary>
    internal static bool TryReadShb(
        ReadOnlySpan<byte> fullBlock,
        bool swap,
        uint blockLength,
        out long sectionLength,
        out ReadOnlySpan<byte> optionData)
    {
        sectionLength = 0;
        optionData = ReadOnlySpan<byte>.Empty;
        if (blockLength < PcapConstants.ShbFixedSize || fullBlock.Length < _ShbBytesBeforeOptions)
        {
            return false;
        }

        if (!SectionHeaderBlock.TryParse(fullBlock, out SectionHeaderBlock shb, out _))
        {
            return false;
        }

        EndianReader reader = new(swap);
        sectionLength = reader.Swap(shb.SectionLength.Value);
        if (!_TryGetOptionsSpan(fullBlock, bodyOmits8ByteHeader: false, blockLength, _ShbBytesBeforeOptions, out optionData))
        {
            optionData = ReadOnlySpan<byte>.Empty;
        }

        return true;
    }

    #endregion

    #region Private helpers

    /// <summary>Packet-data offset in the caller's span when the 8-byte header may be omitted.</summary>
    private static int _DataOffsetInSpan(bool bodyOmits8ByteHeader, int headerBytesBeforeData)
    {
        if (bodyOmits8ByteHeader)
        {
            return headerBytesBeforeData - 8;
        }

        return headerBytesBeforeData;
    }

    /// <summary>
    /// Resolves the interface, applies pad bounds, and converts the timestamp.
    /// </summary>
    private static ScanPacketResult _FinishPacket(
        SectionInfo section,
        uint interfaceId,
        ulong rawTimestamp,
        bool applyTimestamp,
        ReadOnlySpan<byte> blockOrBody,
        int headerBytesBeforeData,
        int dataOffsetInSpan,
        uint blockLength,
        uint capturedLength,
        uint originalLength,
        out EpbFields fields)
    {
        fields = default;
        if (interfaceId > (uint)int.MaxValue)
        {
            return ScanPacketResult.SkipMalformed;
        }

        InterfaceInfo? iface = section.Interface((int)interfaceId);
        if (iface is null)
        {
            return ScanPacketResult.SkipNoInterface;
        }

        if (iface.LinkType is null)
        {
            return ScanPacketResult.SkipUnknownLinkType;
        }

        if (!iface.HasSupportedTimestampResolution)
        {
            return ScanPacketResult.SkipUnsupportedTimestampResolution;
        }

        if (!_TrySlicePacket(
                blockOrBody,
                headerBytesBeforeData,
                dataOffsetInSpan,
                blockLength,
                capturedLength,
                out int captured,
                out ReadOnlySpan<byte> packetData))
        {
            return ScanPacketResult.SkipMalformed;
        }

        long timestampNanos = 0;
        if (applyTimestamp)
        {
            timestampNanos = iface.TimestampToNanos(rawTimestamp);
        }

        fields = new EpbFields
        {
            InterfaceId = interfaceId,
            OriginalLength = originalLength,
            CapturedLength = captured,
            TimestampNanos = timestampNanos,
            PacketData = packetData,
        };
        return ScanPacketResult.Frame;
    }

    /// <summary>Checks pad-aware bounds and slices packet bytes from the caller's span.</summary>
    private static bool _TrySlicePacket(
        ReadOnlySpan<byte> blockOrBody,
        int headerBytesBeforeData,
        int dataOffsetInSpan,
        uint blockLength,
        uint capturedLength,
        out int captured,
        out ReadOnlySpan<byte> packetData)
    {
        packetData = default;
        captured = 0;
        if (!PcapngBlockLayout.TryPacketDataBounds(
                headerBytesBeforeData,
                blockLength,
                capturedLength,
                out captured,
                out _))
        {
            return false;
        }

        if ((uint)dataOffsetInSpan > (uint)blockOrBody.Length
            || captured > blockOrBody.Length - dataOffsetInSpan)
        {
            return false;
        }

        packetData = blockOrBody.Slice(dataOffsetInSpan, captured);
        return true;
    }

    /// <summary>Option bytes between the fixed header and the trailing 4-byte length.</summary>
    private static bool _TryGetOptionsSpan(
        ReadOnlySpan<byte> blockOrBody,
        bool bodyOmits8ByteHeader,
        uint blockLength,
        int bytesBeforeOptions,
        out ReadOnlySpan<byte> optionData)
    {
        optionData = ReadOnlySpan<byte>.Empty;
        if (blockLength > (uint)int.MaxValue)
        {
            return false;
        }

        int headerSkip = bodyOmits8ByteHeader ? 8 : 0;
        int optionsStart = bytesBeforeOptions - headerSkip;
        int optionsEnd = (int)blockLength - 4 - headerSkip;
        if (optionsEnd <= optionsStart || optionsEnd > blockOrBody.Length || optionsStart < 0)
        {
            return false;
        }

        optionData = blockOrBody[optionsStart..optionsEnd];
        return true;
    }

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Represents a single scanned frame with its metadata and data reference.
/// Zero-copy: the data references the backing buffer directly.
/// </summary>
internal readonly ref struct ScannedFrame
{
    /// <summary>Zero-based frame index.</summary>
    internal int FrameIndex
    {
        get; init;
    }

    /// <summary>File offset where the packet data starts.</summary>
    internal long FileOffset
    {
        get; init;
    }

    /// <summary>
    /// Number of captured bytes stored in the packet block.
    /// PCAPNG/PCAP <c>orig_len</c> is not retained; <see cref="Frame"/> exposes captured bytes only.
    /// </summary>
    internal int CapturedLength
    {
        get; init;
    }

    /// <summary>Timestamp in nanoseconds since Unix epoch.</summary>
    internal long TimestampNanos
    {
        get; init;
    }

    /// <summary>Section index within the file.</summary>
    internal ushort SectionIndex
    {
        get; init;
    }

    /// <summary>Interface ID within the section.</summary>
    internal ushort InterfaceId
    {
        get; init;
    }

    /// <summary>Direct reference to the packet data (only valid during scan).</summary>
    internal ReadOnlySpan<byte> Data
    {
        get; init;
    }
}

/// <summary>Outcome of scanning a packet block (EPB, SPB, or obsolete PB).</summary>
internal enum ScanPacketResult
{
    /// <summary>A frame was produced.</summary>
    Frame,

    /// <summary>The block is truncated or length fields do not fit with padding.</summary>
    SkipMalformed,

    /// <summary>The packet names an interface that was not declared in this section.</summary>
    SkipNoInterface,

    /// <summary>The interface DLT is not a defined <see cref="LinkType"/> member.</summary>
    SkipUnknownLinkType,

    /// <summary>The interface timestamp resolution cannot be represented.</summary>
    SkipUnsupportedTimestampResolution,
}

/// <summary>
/// Format-specific state for the scanner.
/// </summary>
internal abstract class ScannerFormat
{
    /// <summary>Returns the format type.</summary>
    internal abstract FileFormat Format
    {
        get;
    }
}

/// <summary>PCAPNG format state — tracks sections and their interfaces.</summary>
internal sealed class PcapNgFormat : ScannerFormat
{
    /// <summary>All sections discovered so far.</summary>
    internal readonly List<SectionInfo> Sections = [];

    /// <inheritdoc />
    internal override FileFormat Format => FileFormat.PcapNg;

    /// <summary>Gets the current (last) section.</summary>
    internal SectionInfo CurrentSection => Sections[^1];

    /// <summary>Gets the current section index.</summary>
    internal ushort CurrentSectionIndex => (ushort)(Sections.Count - 1);
}

/// <summary>Legacy PCAP format state.</summary>
internal sealed class LegacyPcapFormat : ScannerFormat
{
    /// <summary>Legacy PCAP metadata.</summary>
    internal LegacyPcapInfo Info
    {
        get;
    }

    /// <inheritdoc />
    internal override FileFormat Format => FileFormat.LegacyPcap;

    /// <summary>Creates legacy format state from the detected info.</summary>
    internal LegacyPcapFormat(LegacyPcapInfo info)
    {
        Info = info;
    }
}

/// <summary>
/// Incremental scanner for PCAPNG and legacy PCAP files.
/// Implements a state machine that discovers frames one at a time,
/// building the <see cref="FrameIndex"/> incrementally.
/// </summary>
/// <remarks>
/// <para>
/// Two usage patterns:
/// <list type="bullet">
/// <item><b>Full scan:</b> call <see cref="NextFrame"/> in a loop until exhausted.</item>
/// <item><b>Lazy scan:</b> call <see cref="NextFrame"/> on demand; frames
/// discovered so far are available via the index.</item>
/// </list>
/// </para>
/// <para>
/// Windowed I/O model: instead of receiving a single whole-file
/// <see cref="ReadOnlySpan{T}"/> (which is bounded by <c>int.MaxValue</c> ≈ 2 GiB),
/// the scanner holds a <see cref="DataBackend"/> reference and fetches each PCAPNG
/// block or PCAP record individually via <see cref="DataBackend.GetScanSpan"/>.
/// Block lengths are 32-bit fields in both PCAPNG and legacy PCAP, so a single
/// block is always well within the <c>int</c> range. This allows files of
/// arbitrary size to be scanned with the only practical constraint being
/// available virtual address space for mmap.
/// </para>
/// <para><b>Thread-safety:</b> This class is <b>not</b> thread-safe.
/// All scanning must occur from a single thread.</para>
/// </remarks>
internal sealed class IncrementalScanner
{
    #region Fields

    /// <summary>Data backend that provides windowed access to the file.</summary>
    private readonly DataBackend _Backend;

    /// <summary>Current read position in the file.</summary>
    private long _Offset;

    /// <summary>Frame index built incrementally.</summary>
    private readonly FrameIndex _Index;

    /// <summary>Format-specific scanning state.</summary>
    private readonly ScannerFormat _Format;

    /// <summary>Whether the scanner has reached end of file.</summary>
    private bool _Exhausted;

    /// <summary>
    /// Optional skip callback. Set by <see cref="PcapSource"/> so malformed packet blocks
    /// raise <c>FrameSkipped</c>. Null in isolated scanner tests.
    /// </summary>
    internal Action<FrameReadErrorEventArgs>? OnSkip;

    #endregion

    #region Properties

    /// <summary>Gets the frame index built so far.</summary>
    internal FrameIndex Index => _Index;

    /// <summary>Whether scanning is complete (end of file).</summary>
    internal bool IsExhausted => _Exhausted;

    /// <summary>Gets the scanner format info.</summary>
    internal ScannerFormat Format => _Format;

    /// <summary>Gets the number of frames discovered so far.</summary>
    internal int FrameCount => _Index.Count;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a new scanner backed by the given <see cref="DataBackend"/>.
    /// Detects the file format, parses the initial file header, and prepares
    /// for incremental block-by-block scanning.
    /// </summary>
    /// <param name="backend">Backend that provides windowed access to the capture file.</param>
    /// <param name="fileSize">Total file size in bytes.</param>
    /// <exception cref="PcapException">The file format could not be detected or the initial header is corrupt.</exception>
    internal IncrementalScanner(DataBackend backend, long fileSize)
    {
        _Backend = backend;
        _Index = new();

        // Read the minimum number of bytes needed for format detection.
        ReadOnlySpan<byte> peek = backend.GetScanSpan(0, PcapFormatDetection.MinDetectionBytes);

        if (!PcapFormatDetection.TryDetect(peek, out FormatDetectionResult detection))
        {
            throw new PcapException("Unrecognized capture file format.");
        }

        if (detection.Format == FileFormat.PcapNg)
        {
            _Format = _InitializePcapNg(detection.ByteSwapped, fileSize);
        }
        else
        {
            _Format = _InitializeLegacyPcap(detection);
        }
    }

    #endregion

    #region Scanning Implementation

    /// <summary>
    /// Reads the initial SHB via the backend and creates the first section.
    /// </summary>
    /// <param name="byteSwapped">Whether the file uses swapped byte order.</param>
    /// <param name="fileSize">Total file size in bytes.</param>
    private PcapNgFormat _InitializePcapNg(bool byteSwapped, long fileSize)
    {
        // Read just the 8-byte block prefix to obtain blockLength before fetching
        // the full block — this avoids mapping the entire file for initialization.
        ReadOnlySpan<byte> prefix = _Backend.GetScanSpan(0, 8);
        if (prefix.Length < 8)
        {
            throw new PcapException("File too small for SHB block header.");
        }

        EndianReader reader = new(byteSwapped);
        uint blockLength = reader.ReadU32(prefix[4..]);

        if (!PcapPadding.TryRoundBlockLength(blockLength, out uint roundedLength))
        {
            throw new PcapException($"SHB block length {blockLength} cannot be rounded to a multiple of 4.");
        }

        blockLength = roundedLength;
        if (blockLength < PcapConstants.ShbFixedSize)
        {
            throw new PcapException($"SHB block length {blockLength} is less than minimum {PcapConstants.ShbFixedSize}.");
        }

        if (blockLength > PcapConstants.MaxBlockSize)
        {
            throw new PcapException(
                $"SHB block length {blockLength} exceeds the {PcapConstants.MaxBlockSize / (1024 * 1024)} MiB safety cap.");
        }

        // Fetch the complete SHB. Length is already capped and aligned.
        int readSize = (int)blockLength;
        ReadOnlySpan<byte> shbData = _Backend.GetScanSpan(0, readSize);
        if (shbData.Length < readSize)
        {
            throw new PcapException("File too small for the declared SHB block length.");
        }

        if (!SectionHeaderBlock.TryParse(shbData, out SectionHeaderBlock shb, out _))
        {
            throw new PcapException("Failed to parse Section Header Block.");
        }

        if (!PcapngBlockLayout.TryValidatePcapngBlockLength(blockLength, shbData.Slice(readSize - 4, 4), byteSwapped, out _))
        {
            throw new PcapException("SHB trailing block_total_length does not match the leading length.");
        }

        long sectionLength = reader.Swap(shb.SectionLength.Value);

        // First SHB starts at file offset 0.
        SectionInfo section = new(byteSwapped, sectionLength, 0);

        // Parse SHB options if present (after the 24-byte struct, before the trailing length)
        // Options area: offset 24 .. blockLength - 4
        int optionsStart = 24; // SHB struct size
        int optionsEnd = (int)blockLength - 4; // before trailing block_total_length
        if (optionsEnd > optionsStart && optionsEnd <= shbData.Length)
        {
            section.ParseShbOptions(shbData[optionsStart..optionsEnd]);
        }

        PcapNgFormat format = new();
        format.Sections.Add(section);

        // Advance past the SHB (4-byte aligned)
        _Offset = PcapPadding.PaddedLength((int)blockLength);
        if (_Offset >= fileSize)
        {
            _Exhausted = true;
        }

        return format;
    }

    /// <summary>
    /// Reads the 24-byte legacy PCAP global header via the backend and prepares scanning.
    /// </summary>
    /// <param name="detection">Format detection result containing byte order and timestamp resolution.</param>
    private LegacyPcapFormat _InitializeLegacyPcap(FormatDetectionResult detection)
    {
        ReadOnlySpan<byte> header = _Backend.GetScanSpan(0, PcapConstants.PcapGlobalHeaderSize);

        if (header.Length < PcapConstants.PcapGlobalHeaderSize)
        {
            throw new PcapException("File too small for legacy PCAP global header.");
        }

        if (!PcapGlobalHeader.TryParse(header, out PcapGlobalHeader hdr, out _))
        {
            throw new PcapException("Failed to parse legacy PCAP global header.");
        }

        EndianReader reader = new(detection.ByteSwapped);
        uint rawNetwork = reader.Swap(hdr.Network.Value);
        uint rawSnapLen = reader.Swap(hdr.SnapLen.Value);

        LegacyPcapInfo info = new(
            detection.ByteSwapped,
            detection.NanosecondTimestamps,
            (ushort)rawNetwork,
            rawSnapLen);

        _Offset = PcapConstants.PcapGlobalHeaderSize;
        return new LegacyPcapFormat(info);
    }

    /// <summary>
    /// Tries to scan the next frame from the file.
    /// Returns true if a frame was found. Returns false when scanning is exhausted
    /// (<see cref="IsExhausted"/>) or when a packet block was skipped (OnSkip invoked, not exhausted).
    /// Uses windowed reads via <see cref="DataBackend.GetScanSpan"/> so that
    /// files larger than 2 GiB are handled correctly.
    /// </summary>
    /// <param name="frame">The scanned frame (only valid when the method returns true).</param>
    /// <returns>True if a frame was discovered; false if scanning is complete.</returns>
    internal bool NextFrame(out ScannedFrame frame)
    {
        if (_Exhausted)
        {
            frame = default;
            return false;
        }

        if (_Format is PcapNgFormat pcapng)
        {
            return _NextFramePcapNg(pcapng, out frame);
        }

        return _NextFrameLegacy((LegacyPcapFormat)_Format, out frame);
    }

    /// <summary>
    /// PCAPNG block-scanning loop. Reads each block individually from the
    /// backend (windowed I/O), skipping non-packet blocks, processing SHBs
    /// and IDBs, and returning when a packet block is found.
    /// The <c>_Offset</c> field is always a <c>long</c>; no int cast is
    /// performed so files beyond 2 GiB are handled correctly.
    /// </summary>
    private bool _NextFramePcapNg(PcapNgFormat format, out ScannedFrame frame)
    {
        long fileSize = _Backend.FileSize;

        while (true)
        {
            // Need at least 8 bytes: block type (4) + block length (4).
            // For SHB detection we also need the byte-order magic at +8,
            // so always attempt to read 12 bytes; a short read at EOF is safe.
            if (_Offset + 8 > fileSize)
            {
                _Exhausted = true;
                frame = default;
                return false;
            }

            int headerPeekSize = (int)Math.Min(12L, fileSize - _Offset);
            ReadOnlySpan<byte> headerPeek = _Backend.GetScanSpan(_Offset, headerPeekSize);

            if (headerPeek.Length < 8)
            {
                _Exhausted = true;
                frame = default;
                return false;
            }

            // Read raw block type as LE first (SHB is palindromic, so always readable as LE)
            uint rawBlockType = BinaryPrimitives.ReadUInt32LittleEndian(headerPeek);

            uint blockType;
            uint blockLength;
            bool shbSwap = false; // byte-swap flag for SHB processing

            if (rawBlockType == PcapConstants.BlockTypeSHB)
            {
                // SHB: determine byte order from the byte-order magic at offset +8
                if (headerPeek.Length < 12)
                {
                    _Exhausted = true;
                    frame = default;
                    return false;
                }

                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(headerPeek[8..]);
                shbSwap = magic == PcapConstants.PcapngSwappedMagic;

                blockType = PcapConstants.BlockTypeSHB;
                EndianReader shbReader = new(shbSwap);
                blockLength = shbReader.ReadU32(headerPeek[4..]);
            }
            else
            {
                // Non-SHB: use the byte order established by the current section's SHB
                bool swap = format.CurrentSection.ByteSwapped;
                EndianReader reader = new(swap);
                blockType = reader.Swap(rawBlockType);
                blockLength = reader.ReadU32(headerPeek[4..]);
            }

            // Validate block length, 4-byte alignment, and trailing length copy.
            bool trailingSwap = blockType == PcapConstants.BlockTypeSHB
                ? shbSwap
                : format.CurrentSection.ByteSwapped;

            if (!PcapPadding.TryRoundBlockLength(blockLength, out uint roundedLength))
            {
                return _ExhaustCorrupt(
                    $"PCAPNG block length {blockLength} cannot be rounded to a multiple of 4.",
                    out frame);
            }

            blockLength = roundedLength;
            if (blockLength < PcapConstants.MinBlockSize)
            {
                return _ExhaustCorrupt(
                    $"PCAPNG block length {blockLength} is below the minimum block size.",
                    out frame);
            }

            if (_Offset + blockLength > fileSize)
            {
                return _ExhaustCorrupt("PCAPNG block extends past the end of the file.", out frame);
            }

            if (blockLength > PcapConstants.MaxBlockSize)
            {
                return _ExhaustCorrupt(
                    $"Block length {blockLength} exceeds the {PcapConstants.MaxBlockSize / (1024 * 1024)} MiB safety cap.",
                    out frame);
            }

            int readSize = (int)blockLength;
            ReadOnlySpan<byte> blockData = _Backend.GetScanSpan(_Offset, readSize);
            if (blockData.Length < readSize)
            {
                return _ExhaustCorrupt("PCAPNG block read was shorter than the declared length.", out frame);
            }

            if (!PcapngBlockLayout.TryValidatePcapngBlockLength(
                    blockLength, blockData.Slice(readSize - 4, 4), trailingSwap, out _))
            {
                return _ExhaustCorrupt("PCAPNG trailing block_total_length does not match the leading length.", out frame);
            }

            // Advance offset past this block (4-byte aligned)
            long nextOffset = (_Offset + blockLength + 3) & ~3L;

            switch (blockType)
            {
                case PcapConstants.BlockTypeSHB:
                    if (!_ProcessSectionHeader(blockData, format, shbSwap, blockLength, _Offset))
                    {
                        return _ExhaustCorrupt("Failed to parse a subsequent Section Header Block.", out frame);
                    }
                    _Offset = nextOffset;
                    continue;

                case PcapConstants.BlockTypeIDB:
                    _ProcessInterfaceDescription(blockData, format, blockLength);
                    _Offset = nextOffset;
                    continue;

                case PcapConstants.BlockTypeEPB:
                    {
                        ScanPacketResult epbResult = _TryScanEnhancedPacket(blockData, format, blockLength, out frame);
                        long blockOffset = _Offset;
                        _Offset = nextOffset;
                        if (epbResult == ScanPacketResult.Frame)
                        {
                            return true;
                        }

                        _ReportPacketSkip(epbResult, blockOffset);
                        frame = default;
                        return false;
                    }

                case PcapConstants.BlockTypeSPB:
                    {
                        ScanPacketResult spbResult = _TryScanSimplePacket(blockData, format, blockLength, out frame);
                        long blockOffset = _Offset;
                        _Offset = nextOffset;
                        if (spbResult == ScanPacketResult.Frame)
                        {
                            return true;
                        }

                        _ReportPacketSkip(spbResult, blockOffset);
                        frame = default;
                        return false;
                    }

                case PcapConstants.BlockTypePB:
                    {
                        ScanPacketResult pbResult = _TryScanObsoletePacket(blockData, format, blockLength, out frame);
                        long blockOffset = _Offset;
                        _Offset = nextOffset;
                        if (pbResult == ScanPacketResult.Frame)
                        {
                            return true;
                        }

                        _ReportPacketSkip(pbResult, blockOffset);
                        frame = default;
                        return false;
                    }

                default:
                    // Unknown block type — not a frame; do not raise FrameSkipped.
                    _Offset = nextOffset;
                    continue;
            }
        }
    }

    /// <summary>
    /// Legacy PCAP scanning — reads the 16-byte packet record header and
    /// the packet data via the backend (windowed I/O).
    /// <c>_Offset</c> is always a <c>long</c>; no int cast is performed.
    /// </summary>
    private bool _NextFrameLegacy(LegacyPcapFormat format, out ScannedFrame frame)
    {
        long fileSize = _Backend.FileSize;

        // Need at least 16 bytes for the packet record header
        if (_Offset + PcapConstants.PcapPacketHeaderSize > fileSize)
        {
            _Exhausted = true;
            frame = default;
            return false;
        }

        // Fetch the 16-byte packet record header
        ReadOnlySpan<byte> packetHeader = _Backend.GetScanSpan(_Offset, PcapConstants.PcapPacketHeaderSize);
        if (packetHeader.Length < PcapConstants.PcapPacketHeaderSize)
        {
            _Exhausted = true;
            frame = default;
            return false;
        }

        EndianReader reader = new(format.Info.ByteSwapped);

        if (!PcapPacketHeader.TryParse(packetHeader, out PcapPacketHeader rec, out _))
        {
            _Exhausted = true;
            frame = default;
            return false;
        }

        uint tsSec = reader.Swap(rec.TsSec.Value);
        uint tsFrac = reader.Swap(rec.TsFrac.Value);
        uint inclLen = reader.Swap(rec.InclLen.Value);
        // orig_len is parsed but not stored. Frame.Data is captured bytes only.

        // Compute timestamp
        long timestampNanos = format.Info.TimestampToNanos(tsSec, tsFrac);

        // Packet data starts after the 16-byte record header
        long dataOffset = _Offset + PcapConstants.PcapPacketHeaderSize;
        int capturedLength = (int)Math.Min(inclLen, fileSize - dataOffset);

        if (capturedLength < 0)
        {
            _Exhausted = true;
            frame = default;
            return false;
        }

        // Build the frame index entry
        int frameIndex = _Index.Push(new PcapFrameEntry
        {
            FileOffset = dataOffset,
            SectionIndex = 0,
            InterfaceId = 0,
            CapturedLength = capturedLength,
            TimestampNanos = timestampNanos,
        });

        // Fetch the packet data via the backend.
        // For in-memory backends this is a zero-copy slice of the byte array;
        // for mmap backends it is a zero-copy window into the primary view.
        ReadOnlySpan<byte> packetData = _Backend.GetScanSpan(dataOffset, capturedLength);

        frame = new ScannedFrame
        {
            FrameIndex = frameIndex,
            FileOffset = dataOffset,
            CapturedLength = capturedLength,
            TimestampNanos = timestampNanos,
            SectionIndex = 0,
            InterfaceId = 0,
            Data = packetData,
        };

        // Advance past the packet record (inclLen bytes of data, not capturedLength)
        _Offset = dataOffset + inclLen;
        return true;
    }

    /// <summary>Processes a Section Header Block — creates a new section at <paramref name="fileOffset"/>.</summary>
    private static bool _ProcessSectionHeader(
        ReadOnlySpan<byte> blockData,
        PcapNgFormat format,
        bool swap,
        uint blockLength,
        long fileOffset)
    {
        // CurrentSectionIndex is a ushort (0–65535); adding another section when
        // there are already 65536 sections would silently wrap the section index to 0,
        // aliasing new interfaces onto the first section. Hard-fail instead.
        if (format.Sections.Count >= ushort.MaxValue)
        {
            return false;
        }

        if (!PcapngPacketBlocks.TryReadShb(
                blockData,
                swap,
                blockLength,
                out long sectionLength,
                out ReadOnlySpan<byte> optionData))
        {
            return false;
        }

        SectionInfo section = new(swap, sectionLength, fileOffset);
        if (!optionData.IsEmpty)
        {
            section.ParseShbOptions(optionData);
        }

        format.Sections.Add(section);
        return true;
    }

    /// <summary>Processes an Interface Description Block — adds an interface to the current section.</summary>
    private static void _ProcessInterfaceDescription(ReadOnlySpan<byte> blockData, PcapNgFormat format, uint blockLength)
    {
        SectionInfo section = format.CurrentSection;
        if (!PcapngPacketBlocks.TryReadIdb(
                blockData,
                bodyOmits8ByteHeader: false,
                section.ByteSwapped,
                blockLength,
                out IdbFields idb))
        {
            return;
        }

        InterfaceInfo info = section.ParseIdbOptions(idb.LinkType, idb.SnapLength, idb.OptionData);
        section.AddInterface(info);
    }

    /// <summary>Scans an Enhanced Packet Block. EPB orig_len is ignored; captured length is the payload.</summary>
    private ScanPacketResult _TryScanEnhancedPacket(
        ReadOnlySpan<byte> blockData,
        PcapNgFormat format,
        uint blockLength,
        out ScannedFrame frame)
    {
        frame = default;
        ScanPacketResult result = PcapngPacketBlocks.TryReadEpb(
            blockData,
            bodyOmits8ByteHeader: false,
            format.CurrentSection.ByteSwapped,
            blockLength,
            format.CurrentSection,
            out EpbFields fields);
        if (result != ScanPacketResult.Frame)
        {
            return result;
        }

        return _CommitScannedPacket(
            format,
            PcapngBlockLayout.EpbBytesBeforePacketData,
            fields,
            out frame);
    }

    /// <summary>Scans a Simple Packet Block. Stored length is min(orig, snap) when that plus pad fits the block.</summary>
    private ScanPacketResult _TryScanSimplePacket(
        ReadOnlySpan<byte> blockData,
        PcapNgFormat format,
        uint blockLength,
        out ScannedFrame frame)
    {
        frame = default;
        ScanPacketResult result = PcapngPacketBlocks.TryReadSpb(
            blockData,
            bodyOmits8ByteHeader: false,
            format.CurrentSection.ByteSwapped,
            blockLength,
            format.CurrentSection,
            out EpbFields fields);
        if (result != ScanPacketResult.Frame)
        {
            return result;
        }

        return _CommitScannedPacket(
            format,
            PcapngBlockLayout.SpbBytesBeforePacketData,
            fields,
            out frame);
    }

    /// <summary>Scans an Obsolete Packet Block. orig_len is ignored; captured length is the payload.</summary>
    private ScanPacketResult _TryScanObsoletePacket(
        ReadOnlySpan<byte> blockData,
        PcapNgFormat format,
        uint blockLength,
        out ScannedFrame frame)
    {
        frame = default;
        ScanPacketResult result = PcapngPacketBlocks.TryReadPb(
            blockData,
            bodyOmits8ByteHeader: false,
            format.CurrentSection.ByteSwapped,
            blockLength,
            format.CurrentSection,
            out EpbFields fields);
        if (result != ScanPacketResult.Frame)
        {
            return result;
        }

        return _CommitScannedPacket(
            format,
            PcapngBlockLayout.PbBytesBeforePacketData,
            fields,
            out frame);
    }

    /// <summary>Pushes a parsed packet into the frame index and returns a zero-copy scan view.</summary>
    private ScanPacketResult _CommitScannedPacket(
        PcapNgFormat format,
        int bytesBeforePacketData,
        EpbFields fields,
        out ScannedFrame frame)
    {
        long dataFileOffset = _Offset + bytesBeforePacketData;
        ushort interfaceId = (ushort)fields.InterfaceId;

        int frameIndex = _Index.Push(new PcapFrameEntry
        {
            FileOffset = dataFileOffset,
            SectionIndex = format.CurrentSectionIndex,
            InterfaceId = interfaceId,
            CapturedLength = fields.CapturedLength,
            TimestampNanos = fields.TimestampNanos,
        });

        frame = new ScannedFrame
        {
            FrameIndex = frameIndex,
            FileOffset = dataFileOffset,
            CapturedLength = fields.CapturedLength,
            TimestampNanos = fields.TimestampNanos,
            SectionIndex = format.CurrentSectionIndex,
            InterfaceId = interfaceId,
            Data = fields.PacketData,
        };

        return ScanPacketResult.Frame;
    }

    /// <summary>Reports a packet-block skip through <see cref="OnSkip"/> without exhausting the file.</summary>
    private void _ReportPacketSkip(ScanPacketResult result, long fileOffset)
    {
        FrameReadErrorKind kind = result switch
        {
            ScanPacketResult.SkipNoInterface => FrameReadErrorKind.UnresolvedInterface,
            ScanPacketResult.SkipUnknownLinkType => FrameReadErrorKind.Other,
            ScanPacketResult.SkipUnsupportedTimestampResolution => FrameReadErrorKind.Other,
            _ => FrameReadErrorKind.CorruptedBlock,
        };

        string message = result switch
        {
            ScanPacketResult.SkipNoInterface => "Packet block references an interface that was not declared.",
            ScanPacketResult.SkipUnknownLinkType => "Packet block uses an unknown link-layer type; frame skipped.",
            ScanPacketResult.SkipUnsupportedTimestampResolution => "unsupported if_tsresol",
            _ => "Malformed packet block.",
        };

        OnSkip?.Invoke(new FrameReadErrorEventArgs
        {
            FrameIndex = _Index.Count,
            FileOffset = fileOffset,
            Kind = kind,
            Message = message,
        });
    }

    /// <summary>Raises a skip for a corrupt block boundary and stops scanning.</summary>
    private bool _ExhaustCorrupt(string message, out ScannedFrame frame)
    {
        OnSkip?.Invoke(new FrameReadErrorEventArgs
        {
            FrameIndex = _Index.Count,
            FileOffset = _Offset,
            Kind = FrameReadErrorKind.CorruptedBlock,
            Message = message,
        });
        _Exhausted = true;
        frame = default;
        return false;
    }

    #endregion
}

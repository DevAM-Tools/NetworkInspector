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

    /// <summary>
    /// Direct reference to the packet data in the backing buffer (the in-memory array or the primary mmap view). It is a
    /// <see cref="ReadOnlySpan{T}"/> so it cannot be stored; copy the bytes before the scan step that produced it ends,
    /// because a mapped view is only valid while the caller holds its scan lease (<see cref="DataBackend.TryAcquireScanLease"/>).
    /// </summary>
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

    /// <summary>The packet names an interface whose Interface Description Block was malformed (see <see cref="InterfaceInfo.IsMalformed"/>).</summary>
    SkipMalformedInterface,

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
/// <remarks>
/// <b>Thread-safety:</b> the scanner is the single writer (<see cref="AddSection"/>); readers on any thread use
/// <see cref="SectionCount"/>, <see cref="TryGetSection"/>, and <see cref="TryFindSection"/>. A section is fully built
/// (SHB options parsed) before it is added, and afterwards only its interface list grows (see <see cref="SectionInfo"/>).
/// Sections are published before any block they contain is indexed, so <c>FrameById</c> resolves the section of every
/// indexed block also during a lazy scan.
/// </remarks>
internal sealed class PcapNgFormat : ScannerFormat
{
    /// <summary>All sections discovered so far, in file order (ascending start offsets). Reference slots publish lock-free.</summary>
    private readonly ChunkedGrowOnlyStore<SectionInfo> _Sections = new(chunkShift: 4);

    /// <inheritdoc />
    internal override FileFormat Format => FileFormat.PcapNg;

    /// <summary>Number of sections published so far. Any thread.</summary>
    internal int SectionCount => _Sections.Count;

    /// <summary>Gets the current (last) section. Scanner thread.</summary>
    internal SectionInfo CurrentSection => _Sections.Get(_Sections.Count - 1)!;

    /// <summary>Gets the current section index. Scanner thread.</summary>
    internal ushort CurrentSectionIndex => (ushort)(_Sections.Count - 1);

    /// <summary>Publishes a fully built section. Single writer (scanner).</summary>
    internal void AddSection(SectionInfo section) => _Sections.Append(section);

    /// <summary>Section at <paramref name="index"/>. Any thread; false for an index not published yet.</summary>
    internal bool TryGetSection(int index, [NotNullWhen(true)] out SectionInfo? section) =>
        _Sections.TryGet(index, out section) && section is not null;

    /// <summary>
    /// Section whose start offset is the greatest one ≤ <paramref name="blockOffset"/>: the section that was current when
    /// the scanner read the block at that offset. Any thread. False before the first section.
    /// </summary>
    internal bool TryFindSection(long blockOffset, out ushort sectionIndex, [NotNullWhen(true)] out SectionInfo? section)
    {
        // Almost every capture has one section starting at 0: answer without a search.
        int count = _Sections.Count;
        if (count == 1 && blockOffset >= 0)
        {
            sectionIndex = 0;
            section = _Sections.Get(0);
            return section is not null;
        }

        // Sections are appended in file order, so start offsets ascend: binary search over the published prefix.
        int low = 0;
        int high = count - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            if (_Sections.Get(middle)!.StartOffset <= blockOffset)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        sectionIndex = (ushort)Math.Max(found, 0);
        section = found >= 0 ? _Sections.Get(found) : null;
        return section is not null;
    }
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
/// All scanning (<see cref="NextFrame"/>, <see cref="OnSkip"/> and <see cref="OnInterfaceAdded"/> callbacks) occurs on a
/// single thread, the only writer of the index and the sections. Other threads may read what the scanner has published
/// through <see cref="Index"/> and <see cref="Format"/> because both publish their entries lock-free.</para>
/// <para>
/// Error policy: a corrupt file header throws <see cref="PcapException"/> from the constructor (nothing has been indexed
/// yet). A corrupt block later in the file keeps every frame found before it: the scanner reports the problem through
/// <see cref="OnSkip"/> and ends the scan, because a bad length field makes the position of the next block unknowable.
/// A packet block that is well framed but unusable (unknown interface, unsupported timestamp resolution) is skipped
/// and scanning continues.
/// </para>
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
    /// Optional skip callback. Set by <see cref="PcapngSource"/> so malformed packet blocks
    /// raise <c>FrameSkipped</c>. Null in isolated scanner tests.
    /// </summary>
    internal Action<FrameReadErrorEventArgs>? OnSkip;

    /// <summary>
    /// Optional callback for every IDB the scanner adds: section index, interface index, section, interface.
    /// Runs on the scanning thread before any packet of that interface is indexed, so the owner can publish
    /// the interface before <see cref="FrameIndex.Count"/> covers its first frame. Null in isolated scanner tests.
    /// </summary>
    internal Action<ushort, ushort, SectionInfo, InterfaceInfo>? OnInterfaceAdded;

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

        // Read the minimum number of bytes needed for format detection. A shorter span (tiny file) fails detection
        // below instead of reading past the end: the backend clamps to the file size.
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
    /// <param name="byteSwapped">Whether the file uses swapped byte order (from the byte-order magic of the first SHB).</param>
    /// <param name="fileSize">Total file size in bytes.</param>
    /// <exception cref="PcapException">The first SHB is truncated, oversized, unparsable, or its trailing length disagrees.</exception>
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

        // The length is attacker-controlled: round it without wrap, then bound it from below (smallest valid SHB)
        // and above (safety cap) before it sizes any read. The same rules protect every later block in the scan loop;
        // here a violation throws because a file whose first block is unusable has no frames to keep.
        if (!PcapPadding.TryRoundBlockLength(blockLength, out uint roundedLength))
        {
            throw new PcapException(Invariant($"SHB block length {blockLength} cannot be rounded to a multiple of 4."));
        }

        blockLength = roundedLength;
        if (blockLength < PcapConstants.ShbFixedSize)
        {
            throw new PcapException(Invariant($"SHB block length {blockLength} is less than minimum {PcapConstants.ShbFixedSize}."));
        }

        if (blockLength > PcapConstants.MaxBlockSize)
        {
            throw new PcapException(
                Invariant($"SHB block length {blockLength} exceeds the {PcapConstants.MaxBlockSize / (1024 * 1024)} MiB safety cap."));
        }

        // Fetch the complete SHB. Length is already capped and aligned.
        int readSize = (int)blockLength;
        ReadOnlySpan<byte> shbData = _Backend.GetScanSpan(0, readSize);
        if (shbData.Length < readSize)
        {
            throw new PcapException("File too small for the declared SHB block length.");
        }

        if (!SectionHeaderBlock.TryParse(shbData, out SectionHeaderBlock shb))
        {
            throw new PcapException("Failed to parse Section Header Block.");
        }

        if (!PcapngBlockLayout.TryValidatePcapngBlockLength(blockLength, shbData.Slice(readSize - 4, 4), byteSwapped, out _))
        {
            throw new PcapException("SHB trailing block_total_length does not match the leading length.");
        }

        long sectionLength = reader.Swap(shb.SectionLength);

        // First SHB starts at file offset 0.
        SectionInfo section = new(byteSwapped, sectionLength, 0);

        // Parse SHB options if present (after the fixed fields, before the trailing length). The section is fully built here,
        // before AddSection publishes it, so readers on other threads never see a section without its options.
        // Options area: offset 24 .. blockLength - 4
        int optionsStart = 24; // type(4) + length(4) + byte-order magic(4) + version(4) + section length(8)
        int optionsEnd = (int)blockLength - 4; // before trailing block_total_length
        if (optionsEnd > optionsStart && optionsEnd <= shbData.Length)
        {
            section.ParseShbOptions(shbData[optionsStart..optionsEnd]);
        }

        PcapNgFormat format = new();
        format.AddSection(section);

        // Advance past the SHB (blockLength is already a multiple of 4; the cast is safe because of the safety cap above).
        // A file that consists of the SHB alone is exhausted immediately, with an empty index.
        _Offset = PcapPadding.PaddedLength((int)blockLength);
        if (_Offset >= fileSize)
        {
            _MarkExhausted();
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

        if (!PcapGlobalHeader.TryParse(header, out PcapGlobalHeader hdr))
        {
            throw new PcapException("Failed to parse legacy PCAP global header.");
        }

        EndianReader reader = new(detection.ByteSwapped);
        uint rawNetwork = reader.Swap(hdr.Network);
        uint rawSnapLen = reader.Swap(hdr.SnapLen);

        // The link type is the low 16 bits of the 32-bit network field. The snaplen is kept exactly as declared
        // (0 means not declared) because TryValidateLegacyInclLen applies the default cap only for an undeclared value.
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
    /// <returns>
    /// True if a frame was discovered. False if scanning is complete (<see cref="IsExhausted"/> is then true) or if a
    /// well-framed packet block was skipped (<see cref="IsExhausted"/> stays false: the caller must call again).
    /// </returns>
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
    /// A block whose framing is corrupt ends the scan through <see cref="_ExhaustCorrupt"/> (the position of the next
    /// block cannot be trusted); a well-framed but unusable packet block is skipped through <see cref="_ReportPacketSkip"/>
    /// and the scan can continue with the next call.
    /// </summary>
    private bool _NextFramePcapNg(PcapNgFormat format, out ScannedFrame frame)
    {
        long fileSize = _Backend.FileSize;

        // Loops only over blocks that are not packets (SHB, IDB, unknown): a packet block returns to the caller, so
        // one call yields at most one frame and a lazy scan can stop after any frame.
        while (true)
        {
            // Need at least 8 bytes: block type (4) + block length (4).
            // For SHB detection we also need the byte-order magic at +8,
            // so always attempt to read 12 bytes; a short read at EOF is safe.
            if (_Offset + 8 > fileSize)
            {
                _MarkExhausted();
                frame = default;
                return false;
            }

            int headerPeekSize = (int)Math.Min(12L, fileSize - _Offset);
            ReadOnlySpan<byte> headerPeek = _Backend.GetScanSpan(_Offset, headerPeekSize);

            if (headerPeek.Length < 8)
            {
                _MarkExhausted();
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
                    _MarkExhausted();
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

            // Validate block length, 4-byte alignment, and trailing length copy. The length field comes from the file,
            // so every check below runs before the length sizes a read (GetScanSpan) or is cast to int. Corruption here
            // ends the scan: without a trustworthy length the next block cannot be located.
            // The trailing copy of an SHB is in the byte order of the new section, any other block in the current one.
            bool trailingSwap = blockType == PcapConstants.BlockTypeSHB
                ? shbSwap
                : format.CurrentSection.ByteSwapped;

            if (!PcapPadding.TryRoundBlockLength(blockLength, out uint roundedLength))
            {
                return _ExhaustCorrupt(
                    Invariant($"PCAPNG block length {blockLength} cannot be rounded to a multiple of 4."),
                    FrameReadErrorKind.CorruptedBlock,
                    out frame);
            }

            blockLength = roundedLength;
            if (blockLength < PcapConstants.MinBlockSize)
            {
                return _ExhaustCorrupt(
                    Invariant($"PCAPNG block length {blockLength} is below the minimum block size."),
                    FrameReadErrorKind.CorruptedBlock,
                    out frame);
            }

            if (_Offset + blockLength > fileSize)
            {
                return _ExhaustCorrupt("PCAPNG block extends past the end of the file.", FrameReadErrorKind.CorruptedBlock, out frame);
            }

            if (blockLength > PcapConstants.MaxBlockSize)
            {
                return _ExhaustCorrupt(
                    Invariant($"Block length {blockLength} exceeds the {PcapConstants.MaxBlockSize / (1024 * 1024)} MiB safety cap."),
                    FrameReadErrorKind.CorruptedBlock,
                    out frame);
            }

            // The cast is safe: blockLength <= MaxBlockSize < int.MaxValue. The span is a zero-copy view of the backing
            // buffer; the whole block is requested once so the parsers below never read past it.
            int readSize = (int)blockLength;
            ReadOnlySpan<byte> blockData = _Backend.GetScanSpan(_Offset, readSize);
            if (blockData.Length < readSize)
            {
                return _ExhaustCorrupt(
                    "PCAPNG block read was shorter than the declared length.", FrameReadErrorKind.CorruptedBlock, out frame);
            }

            if (!PcapngBlockLayout.TryValidatePcapngBlockLength(
                    blockLength, blockData.Slice(readSize - 4, 4), trailingSwap, out _))
            {
                return _ExhaustCorrupt(
                    "PCAPNG trailing block_total_length does not match the leading length.",
                    FrameReadErrorKind.CorruptedBlock,
                    out frame);
            }

            // Advance offset past this block (4-byte aligned; blockLength is already a multiple of 4, so this is
            // a defensive round-up that keeps the offset aligned even if that validation is ever relaxed).
            // For the packet cases below _Offset is advanced before the result is evaluated, so a skipped
            // packet block never makes the next call read the same block again.
            long nextOffset = (_Offset + blockLength + 3) & ~3L;

            switch (blockType)
            {
                case PcapConstants.BlockTypeSHB:
                    if (!_ProcessSectionHeader(blockData, format, shbSwap, blockLength, _Offset))
                    {
                        return _ExhaustCorrupt(
                            "Failed to parse a subsequent Section Header Block.", FrameReadErrorKind.CorruptedBlock, out frame);
                    }
                    _Offset = nextOffset;
                    continue;

                case PcapConstants.BlockTypeIDB:
                    _ProcessInterfaceDescription(blockData, format, blockLength);
                    _Offset = nextOffset;
                    continue;

                // The three packet cases share one shape: the scan runs first because _CommitScannedPacket records
                // _Offset (still the block start) in the index; only then is _Offset moved past the block. The skip
                // report gets the saved start offset because _Offset has already advanced.
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
                    // Unknown or irrelevant block type (name resolution, statistics, custom, ...) — not a frame; do not
                    // raise FrameSkipped. The validated length lets the scan step over it without interpreting it.
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
            _MarkExhausted();
            frame = default;
            return false;
        }

        // Fetch the 16-byte packet record header
        ReadOnlySpan<byte> packetHeader = _Backend.GetScanSpan(_Offset, PcapConstants.PcapPacketHeaderSize);
        if (packetHeader.Length < PcapConstants.PcapPacketHeaderSize)
        {
            _MarkExhausted();
            frame = default;
            return false;
        }

        EndianReader reader = new(format.Info.ByteSwapped);

        // The record header fields are stored in the file's byte order (swapped below via EndianReader, since the
        // parsed struct reads them little-endian). The span has the full header
        // size (checked above), so a parse failure is not expected; the branch is defensive, and it reports the failure like
        // every other record-boundary error instead of ending the scan unseen (a scan that stops silently looks like a
        // file that simply ended).
        if (!PcapPacketHeader.TryParse(packetHeader, out PcapPacketHeader rec))
        {
            return _ExhaustCorrupt(
                "Legacy PCAP record header could not be parsed.", FrameReadErrorKind.MalformedHeader, out frame);
        }

        uint tsSec = reader.Swap(rec.TsSec);
        uint tsFrac = reader.Swap(rec.TsFrac);
        uint inclLen = reader.Swap(rec.InclLen);
        // orig_len is parsed but not stored. Frame.Data is captured bytes only.

        // Bound incl_len by the declared snaplen and the block cap before anything reads or allocates
        // that many bytes: a hostile 16-byte header must not request gigabytes. Same rule and text as the stream reader.
        if (!PcapngBlockLayout.TryValidateLegacyInclLen(inclLen, format.Info.DeclaredSnapLength, out string? reason))
        {
            return _ExhaustCorrupt(reason!, FrameReadErrorKind.MalformedHeader, out frame);
        }

        // Compute timestamp
        long timestampNanos = format.Info.TimestampToNanos(tsSec, tsFrac);

        // Packet data starts after the 16-byte record header. A record whose data is cut by EOF is reported
        // as truncated, never returned as a shortened frame.
        long dataOffset = _Offset + PcapConstants.PcapPacketHeaderSize;
        if (dataOffset + inclLen > fileSize)
        {
            return _ExhaustCorrupt(
                "Legacy PCAP record is truncated by the end of the file.", FrameReadErrorKind.TruncatedStream, out frame);
        }

        // Validated above: inclLen <= MaxBlockSize < int.MaxValue.
        int capturedLength = (int)inclLen;

        // Only the record header start: the header is re-read with the same rules for random access.
        int frameIndex = _Index.Push(_Offset);

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
        // CurrentSectionIndex is a ushort (0–65535); a section index beyond that would silently wrap
        // to 0, aliasing new interfaces onto the first section. Hard-fail instead (the corrupt-block
        // path ends the scan). The guard allows at most ushort.MaxValue sections (indexes 0 to 65534), one fewer
        // than a ushort could address.
        if (format.SectionCount >= ushort.MaxValue)
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

        format.AddSection(section);
        return true;
    }

    /// <summary>
    /// Processes an Interface Description Block — adds an interface to the current section and reports it through
    /// <see cref="OnInterfaceAdded"/>. An IDB beyond <see cref="SectionInfo.MaxInterfaceCount"/> is ignored; packets
    /// that reference it are skipped as <see cref="ScanPacketResult.SkipNoInterface"/>.
    /// </summary>
    private void _ProcessInterfaceDescription(ReadOnlySpan<byte> blockData, PcapNgFormat format, uint blockLength)
    {
        SectionInfo section = format.CurrentSection;
        if (!PcapngPacketBlocks.TryReadIdb(
                blockData,
                bodyOmits8ByteHeader: false,
                section.ByteSwapped,
                blockLength,
                out IdbFields idb))
        {
            // A malformed IDB still occupies its interface index, and it is reported. Problem avoided: packets name their
            // interface by the position of its IDB in the section, so dropping the block silently shifted every later
            // interface by one and decoded their packets with the wrong link type and clock. The placeholder makes the packets
            // of the broken interface skip (with a message) and keeps the following interfaces where the file puts them.
            // _Offset is still the start of this block here; the caller advances it afterwards.
            InterfaceInfo placeholder = InterfaceInfo.CreateMalformed();
            int placeholderIndex = section.AddInterface(placeholder);
            if (placeholderIndex >= 0)
            {
                // The owner reserves the position in its interface table (it registers nothing for a malformed interface).
                OnInterfaceAdded?.Invoke(format.CurrentSectionIndex, (ushort)placeholderIndex, section, placeholder);
                OnSkip?.Invoke(new FrameReadErrorEventArgs
                {
                    FrameIndex = _Index.Count,
                    FileOffset = _Offset,
                    Kind = FrameReadErrorKind.MalformedHeader,
                    Message = Invariant(
                        $"Interface Description Block {placeholderIndex} is malformed; its packets are skipped."),
                });
            }

            return;
        }

        InterfaceInfo info = section.ParseIdbOptions(idb.LinkType, idb.SnapLength, idb.OptionData);
        int interfaceIndex = section.AddInterface(info);
        if (interfaceIndex < 0)
        {
            return;
        }

        OnInterfaceAdded?.Invoke(format.CurrentSectionIndex, (ushort)interfaceIndex, section, info);
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
        // _Offset is still the start of the block here (the caller advances it afterwards). The interface id was
        // validated against the section's interface list by the block parser, so the ushort cast cannot truncate.
        long dataFileOffset = _Offset + bytesBeforePacketData;
        ushort interfaceId = (ushort)fields.InterfaceId;

        // Only the block start: section, interface, length, and timestamp are re-read from the block by the same parser.
        int frameIndex = _Index.Push(_Offset);

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

    /// <summary>Ends the scan: no further block is read, and the index seals its last partial group.</summary>
    private void _MarkExhausted()
    {
        _Exhausted = true;
        _Index.Complete();
    }

    /// <summary>Reports a packet-block skip through <see cref="OnSkip"/> without exhausting the file.</summary>
    private void _ReportPacketSkip(ScanPacketResult result, long fileOffset)
    {
        (FrameReadErrorKind kind, string message) = PcapInterfaceProperties.DescribeSkip(result);

        OnSkip?.Invoke(new FrameReadErrorEventArgs
        {
            FrameIndex = _Index.Count,
            FileOffset = fileOffset,
            Kind = kind,
            Message = message,
        });
    }

    /// <summary>Raises a skip for a corrupt block or record boundary and stops scanning.</summary>
    private bool _ExhaustCorrupt(string message, FrameReadErrorKind kind, out ScannedFrame frame)
    {
        OnSkip?.Invoke(new FrameReadErrorEventArgs
        {
            FrameIndex = _Index.Count,
            FileOffset = _Offset,
            Kind = kind,
            Message = message,
        });
        _MarkExhausted();
        frame = default;
        return false;
    }

    #endregion
}

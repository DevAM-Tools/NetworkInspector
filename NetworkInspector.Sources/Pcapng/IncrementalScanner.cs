// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Sources.Pcapng.Format;
using NetworkInspector.Sources.Pcapng.Format.Blocks;

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

    /// <summary>Number of captured bytes.</summary>
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
    #region Constants

    /// <summary>
    /// Maximum number of bytes fetched for a single block via
    /// <see cref="DataBackend.GetScanSpan"/>.
    /// Reduced from 512 MiB to 16 MiB. Any block claiming more than
    /// 16 MiB is rejected as corrupt (the maximum practical PCAPNG packet block
    /// is the interface snap length, typically ≤ 65 535 bytes, plus a small header).
    /// 16 MiB is still generous enough to accommodate any real-world block while
    /// preventing a single malformed record from causing an OOM condition.
    /// </summary>
    private const int MaxBlockReadSize = 16 * 1024 * 1024; // 16 MiB

    #endregion

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

    #endregion

    #region Properties

    /// <summary>Gets the frame index built so far.</summary>
    internal FrameIndex Index => _Index;

    /// <summary>Whether scanning is complete (end of file or index full).</summary>
    internal bool IsExhausted => _Exhausted || _Index.IsFull;

    /// <summary>
    /// Whether the index reached its maximum capacity of <see cref="int.MaxValue"/> entries.
    /// When <c>true</c>, the file contains more frames than can be indexed.
    /// </summary>
    internal bool IsIndexFull => _Index.IsFull;

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
        _Index = new FrameIndex();

        // Read the minimum number of bytes needed for format detection.
        ReadOnlySpan<byte> peek = backend.GetScanSpan(0, PcapFormatDetection.MinDetectionBytes);

        if (!PcapFormatDetection.TryDetect(peek, out FormatDetectionResult detection))
        {
            throw new PcapException("Unrecognized capture file format.");
        }

        if (detection.Format == FileFormat.PcapNg)
        {
            _Format = InitializePcapNg(detection.ByteSwapped, fileSize);
        }
        else
        {
            _Format = InitializeLegacyPcap(detection);
        }
    }

    #endregion

    #region Scanning Implementation

    /// <summary>
    /// Reads the initial SHB via the backend and creates the first section.
    /// </summary>
    /// <param name="byteSwapped">Whether the file uses swapped byte order.</param>
    /// <param name="fileSize">Total file size in bytes.</param>
    private PcapNgFormat InitializePcapNg(bool byteSwapped, long fileSize)
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

        if (blockLength < PcapConstants.ShbFixedSize)
        {
            throw new PcapException($"SHB block length {blockLength} is less than minimum {PcapConstants.ShbFixedSize}.");
        }

        // Fetch the complete SHB. Cap to MaxBlockReadSize to guard against corrupt data.
        int readSize = (int)Math.Min((long)blockLength, MaxBlockReadSize);
        ReadOnlySpan<byte> shbData = _Backend.GetScanSpan(0, readSize);

        if (!SectionHeaderBlock.TryParse(shbData, out SectionHeaderBlock shb, out _))
        {
            throw new PcapException("Failed to parse Section Header Block.");
        }

        long sectionLength = reader.Swap(shb.SectionLength.Value);

        // Create the first section
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
    private LegacyPcapFormat InitializeLegacyPcap(FormatDetectionResult detection)
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
    /// Returns true if a frame was found; false if scanning is exhausted.
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
            return NextFramePcapNg(pcapng, out frame);
        }

        return NextFrameLegacy((LegacyPcapFormat)_Format, out frame);
    }

    /// <summary>
    /// PCAPNG block-scanning loop. Reads each block individually from the
    /// backend (windowed I/O), skipping non-packet blocks, processing SHBs
    /// and IDBs, and returning when a packet block is found.
    /// The <c>_Offset</c> field is always a <c>long</c>; no int cast is
    /// performed so files beyond 2 GiB are handled correctly.
    /// </summary>
    private bool NextFramePcapNg(PcapNgFormat format, out ScannedFrame frame)
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

            // Validate block length
            if (blockLength < PcapConstants.MinBlockSize)
            {
                _Exhausted = true;
                frame = default;
                return false;
            }

            // PCAPNG spec requires all block lengths to be a multiple of 4
            // (padding is included in block_total_length). A misaligned value indicates
            // corruption; stop scanning rather than producing bad offsets.
            if ((blockLength & 3) != 0)
            {
                _Exhausted = true;
                frame = default;
                return false;
            }

            // Validate we can read the entire block
            if (_Offset + blockLength > fileSize)
            {
                _Exhausted = true;
                frame = default;
                return false;
            }

            // Reject blocks whose declared length exceeds MaxBlockReadSize.
            // Any block larger than 16 MiB is considered corrupt (even custom block types
            // rarely exceed a few KB; a legitimate EPB is bounded by the snap length).
            // This eliminates the silent-truncation problem: we never read a
            // partial block that appears well-formed but is actually clipped by the cap.
            if (blockLength > MaxBlockReadSize)
            {
                _Exhausted = true;
                frame = default;
                return false;
            }

            // Fetch the full block as a windowed span.
            // blockLength was validated above against MaxBlockReadSize and fileSize,
            // so the cast to int is safe and GetScanSpan must return exactly blockLength bytes.
            int readSize = (int)blockLength;
            ReadOnlySpan<byte> blockData = _Backend.GetScanSpan(_Offset, readSize);

            // Defensive check — GetScanSpan should return exactly readSize bytes
            // when the file size check above passed, but verify to prevent slicing past end.
            if (blockData.Length < readSize)
            {
                _Exhausted = true;
                frame = default;
                return false;
            }

            // Validate that the trailing block_total_length (last 4 bytes of every
            // PCAPNG block) matches the leading block_total_length we used to size the read.
            // A mismatch indicates a corrupt block boundary; stop scanning to prevent the
            // offset arithmetic from producing a cascade of bad frame positions.
            // Note: legacy PCAP records do not have a trailing length field; this check is
            // PCAPNG-only (NextFrameLegacy has no equivalent).
            // The trailing field always uses the same byte order as the rest of the block,
            // which for SHB may differ from the surrounding section. For SHB we apply shbSwap;
            // for all other blocks we apply the current section's ByteSwapped flag.
            {
                bool trailingSwap = blockType == PcapConstants.BlockTypeSHB
                    ? shbSwap
                    : format.CurrentSection.ByteSwapped;
                EndianReader trailingReader = new(trailingSwap);
                uint trailingLength = trailingReader.ReadU32(blockData[(int)(blockLength - 4)..]);
                if (trailingLength != blockLength)
                {
                    _Exhausted = true;
                    frame = default;
                    return false;
                }
            }

            // Advance offset past this block (4-byte aligned)
            long nextOffset = (_Offset + blockLength + 3) & ~3L;

            switch (blockType)
            {
                case PcapConstants.BlockTypeSHB:
                    if (!ProcessSectionHeader(blockData, format, shbSwap, blockLength))
                    {
                        // Section index overflow or parse failure — stop scanning.
                        _Exhausted = true;
                        frame = default;
                        return false;
                    }
                    _Offset = nextOffset;
                    continue;

                case PcapConstants.BlockTypeIDB:
                    ProcessInterfaceDescription(blockData, format, blockLength);
                    _Offset = nextOffset;
                    continue;

                case PcapConstants.BlockTypeEPB:
                    if (TryScanEnhancedPacket(blockData, format, blockLength, out frame))
                    {
                        _Offset = nextOffset;
                        return true;
                    }
                    // Malformed EPB — skip it
                    _Offset = nextOffset;
                    continue;

                case PcapConstants.BlockTypeSPB:
                    if (TryScanSimplePacket(blockData, format, blockLength, out frame))
                    {
                        _Offset = nextOffset;
                        return true;
                    }
                    _Offset = nextOffset;
                    continue;

                case PcapConstants.BlockTypePB:
                    if (TryScanObsoletePacket(blockData, format, blockLength, out frame))
                    {
                        _Offset = nextOffset;
                        return true;
                    }
                    _Offset = nextOffset;
                    continue;

                default:
                    // Unknown block type — skip
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
    private bool NextFrameLegacy(LegacyPcapFormat format, out ScannedFrame frame)
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

        // Parse the 4 packet record header fields (all at relative offsets within packetHeader)
        uint tsSec = reader.ReadU32(packetHeader);
        uint tsFrac = reader.ReadU32(packetHeader[4..]);
        uint inclLen = reader.ReadU32(packetHeader[8..]);
        uint origLen = reader.ReadU32(packetHeader[12..]);

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
        FrameOffset frameOffset = new()
        {
            FileOffset = dataOffset,
            SectionIndex = 0,
            InterfaceId = 0,
            CapturedLength = capturedLength,
        };

        int frameIndex = _Index.Push(frameOffset, timestampNanos);

        // M4: when the index is full, stop scanning to avoid wasting CPU
        if (frameIndex < 0)
        {
            _Exhausted = true;
            frame = default;
            return false;
        }

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

    /// <summary>Processes a Section Header Block — creates a new section.</summary>
    private static bool ProcessSectionHeader(ReadOnlySpan<byte> blockData, PcapNgFormat format, bool swap, uint blockLength)
    {
        // CurrentSectionIndex is a ushort (0–65535); adding another section when
        // there are already 65536 sections would silently wrap the section index to 0,
        // aliasing new interfaces onto the first section. Hard-fail instead.
        if (format.Sections.Count >= ushort.MaxValue)
        {
            return false;
        }

        // Parse the SHB struct to get section length
        if (!SectionHeaderBlock.TryParse(blockData, out SectionHeaderBlock shb, out _))
        {
            return false;
        }

        EndianReader reader = new(swap);
        long sectionLength = reader.Swap(shb.SectionLength.Value);
        SectionInfo section = new(swap, sectionLength, 0);

        // Parse SHB options
        int optionsStart = 24;
        int optionsEnd = (int)blockLength - 4;
        if (optionsEnd > optionsStart && optionsEnd <= blockData.Length)
        {
            section.ParseShbOptions(blockData[optionsStart..optionsEnd]);
        }

        format.Sections.Add(section);
        return true;
    }

    /// <summary>Processes an Interface Description Block — adds an interface to the current section.</summary>
    private static void ProcessInterfaceDescription(ReadOnlySpan<byte> blockData, PcapNgFormat format, uint blockLength)
    {
        SectionInfo section = format.CurrentSection;
        EndianReader reader = new(section.ByteSwapped);

        if (blockData.Length < 16)
        {
            return;
        }

        // Parse IDB fields manually (after block_type and block_total_length at +0 and +4)
        ushort linkType = reader.ReadU16(blockData[8..]);
        // Skip reserved u16 at offset 10
        uint snapLength = reader.ReadU32(blockData[12..]);

        // Parse IDB options (after 16-byte fixed header, before trailing length)
        int optionsStart = 16;
        int optionsEnd = (int)blockLength - 4;
        ReadOnlySpan<byte> optionData = ReadOnlySpan<byte>.Empty;
        if (optionsEnd > optionsStart && optionsEnd <= blockData.Length)
        {
            optionData = blockData[optionsStart..optionsEnd];
        }

        InterfaceInfo info = section.ParseIdbOptions(linkType, snapLength, optionData);
        section.AddInterface(info);
    }

    /// <summary>Scans an Enhanced Packet Block.</summary>
    private bool TryScanEnhancedPacket(ReadOnlySpan<byte> blockData, PcapNgFormat format, uint blockLength, out ScannedFrame frame)
    {
        if (blockLength < PcapConstants.EpbFixedSize || blockData.Length < 28)
        {
            frame = default;
            return false;
        }

        SectionInfo section = format.CurrentSection;
        EndianReader reader = new(section.ByteSwapped);

        // Parse EPB fields (after block_type at +0 and block_total_length at +4)
        uint interfaceId = reader.ReadU32(blockData[8..]);
        uint tsHigh = reader.ReadU32(blockData[12..]);
        uint tsLow = reader.ReadU32(blockData[16..]);
        uint capturedLength = reader.ReadU32(blockData[20..]);

        // Validate interface ID fits in int (section.Interface takes int)
        if (interfaceId > int.MaxValue)
        {
            frame = default;
            return false;
        }

        // Validate interface ID
        InterfaceInfo? iface = section.Interface((int)interfaceId);
        if (iface == null)
        {
            frame = default;
            return false;
        }

        // Validate captured length fits in the block
        // Data starts at offset 28, block ends at blockLength - 4 (trailing length)
        int maxDataLength = (int)blockLength - PcapConstants.EpbFixedSize;
        if (maxDataLength < 0)
        {
            frame = default;
            return false;
        }
        int actualCaptured = (int)Math.Min(capturedLength, maxDataLength);
        if (actualCaptured < 0)
        {
            frame = default;
            return false;
        }

        // Compute timestamp
        ulong rawTimestamp = ((ulong)tsHigh << 32) | tsLow;
        long timestampNanos = iface.TimestampToNanos(rawTimestamp);

        // Data starts at offset 28 from block start
        long dataFileOffset = _Offset + 28;

        FrameOffset frameOffset = new()
        {
            FileOffset = dataFileOffset,
            SectionIndex = format.CurrentSectionIndex,
            InterfaceId = (ushort)interfaceId,
            CapturedLength = actualCaptured,
        };

        int frameIndex = _Index.Push(frameOffset, timestampNanos);

        // When the index is full, stop scanning to avoid wasting CPU.
        // IsIndexFull / IsFrameCountTruncated on PcapSource will surface this condition
        // to callers so they can inform the user that frame count is truncated.
        if (frameIndex < 0)
        {
            _Exhausted = true;
            frame = default;
            return false;
        }

        frame = new ScannedFrame
        {
            FrameIndex = frameIndex,
            FileOffset = dataFileOffset,
            CapturedLength = actualCaptured,
            TimestampNanos = timestampNanos,
            SectionIndex = format.CurrentSectionIndex,
            InterfaceId = (ushort)interfaceId,
            Data = blockData.Slice(28, actualCaptured),
        };

        return true;
    }

    /// <summary>Scans a Simple Packet Block.</summary>
    private bool TryScanSimplePacket(ReadOnlySpan<byte> blockData, PcapNgFormat format, uint blockLength, out ScannedFrame frame)
    {
        if (blockLength < PcapConstants.SpbFixedSize || blockData.Length < 12)
        {
            frame = default;
            return false;
        }

        SectionInfo section = format.CurrentSection;
        EndianReader reader = new(section.ByteSwapped);

        // SPB always uses interface 0
        InterfaceInfo? iface = section.Interface(0);
        if (iface == null)
        {
            frame = default;
            return false;
        }

        uint originalLength = reader.ReadU32(blockData[8..]);

        // Captured length = min(original, block_body_size, snaplen)
        int bodySize = (int)blockLength - PcapConstants.SpbFixedSize;
        int capturedLength = (int)Math.Min(Math.Min(originalLength, (uint)bodySize), iface.SnapLength);
        if (capturedLength < 0)
        {
            frame = default;
            return false;
        }

        // SPB has no timestamp
        long dataFileOffset = _Offset + 12;

        FrameOffset frameOffset = new()
        {
            FileOffset = dataFileOffset,
            SectionIndex = format.CurrentSectionIndex,
            InterfaceId = 0,
            CapturedLength = capturedLength,
        };

        int frameIndex = _Index.Push(frameOffset, 0);

        // M4: when the index is full, stop scanning to avoid wasting CPU
        if (frameIndex < 0)
        {
            _Exhausted = true;
            frame = default;
            return false;
        }

        frame = new ScannedFrame
        {
            FrameIndex = frameIndex,
            FileOffset = dataFileOffset,
            CapturedLength = capturedLength,
            TimestampNanos = 0,
            SectionIndex = format.CurrentSectionIndex,
            InterfaceId = 0,
            Data = blockData.Slice(12, capturedLength),
        };

        return true;
    }

    /// <summary>Scans an Obsolete Packet Block.</summary>
    private bool TryScanObsoletePacket(ReadOnlySpan<byte> blockData, PcapNgFormat format, uint blockLength, out ScannedFrame frame)
    {
        if (blockLength < PcapConstants.PbFixedSize || blockData.Length < 28)
        {
            frame = default;
            return false;
        }

        SectionInfo section = format.CurrentSection;
        EndianReader reader = new(section.ByteSwapped);

        // PB has 16-bit interface ID (unlike EPB's 32-bit)
        ushort interfaceId = reader.ReadU16(blockData[8..]);
        // drops_count at offset 10 (ignored)
        uint tsHigh = reader.ReadU32(blockData[12..]);
        uint tsLow = reader.ReadU32(blockData[16..]);
        uint capturedLength = reader.ReadU32(blockData[20..]);

        InterfaceInfo? iface = section.Interface(interfaceId);
        if (iface == null)
        {
            frame = default;
            return false;
        }

        int maxDataLength = (int)blockLength - PcapConstants.PbFixedSize;
        int actualCaptured = (int)Math.Min(capturedLength, maxDataLength);
        if (actualCaptured < 0)
        {
            frame = default;
            return false;
        }

        ulong rawTimestamp = ((ulong)tsHigh << 32) | tsLow;
        long timestampNanos = iface.TimestampToNanos(rawTimestamp);

        long dataFileOffset = _Offset + 28;

        FrameOffset frameOffset = new()
        {
            FileOffset = dataFileOffset,
            SectionIndex = format.CurrentSectionIndex,
            InterfaceId = interfaceId,
            CapturedLength = actualCaptured,
        };

        int frameIndex = _Index.Push(frameOffset, timestampNanos);

        // M4: when the index is full, stop scanning to avoid wasting CPU
        if (frameIndex < 0)
        {
            _Exhausted = true;
            frame = default;
            return false;
        }

        frame = new ScannedFrame
        {
            FrameIndex = frameIndex,
            FileOffset = dataFileOffset,
            CapturedLength = actualCaptured,
            TimestampNanos = timestampNanos,
            SectionIndex = format.CurrentSectionIndex,
            InterfaceId = interfaceId,
            Data = blockData.Slice(28, actualCaptured),
        };

        return true;
    }

    #endregion
}

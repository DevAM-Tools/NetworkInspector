// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Stream-based frame source for PCAPNG and legacy PCAP data.
/// Implements <see cref="IFrameSource"/> for forward-only sequential reading
/// from any <see cref="Stream"/> (e.g., network streams, pipes, stdin).
/// Packet payload on <see cref="Frame"/> is the captured length. PCAPNG/PCAP <c>orig_len</c>
/// is not kept. Link types that are not defined <see cref="LinkType"/> members skip the
/// packet and raise <see cref="FrameSkipped"/>.
/// <para>
/// Unlike <see cref="PcapSource"/>, this class does not support random access
/// (<see cref="IRandomAccessFrameSource"/>). Frames are read one at a time
/// and their data is copied via <c>ToArray()</c>.
/// </para>
/// </summary>
/// <remarks>
/// All existing format parsing code is reused via span-based APIs:
/// <see cref="PcapFormatDetection"/>, <see cref="EndianReader"/>,
/// <see cref="SectionInfo"/>, <see cref="InterfaceInfo"/>, etc.
/// </remarks>
public sealed class PcapStreamSource : IFrameSource, IErrorTolerantFrameSource
{
    #region Fields

    /// <summary>Underlying data stream.</summary>
    private readonly Stream _Stream;

    /// <summary>User-friendly display name.</summary>
    /// <inheritdoc />
    public string UiName { get; }

    /// <summary>Whether to leave the stream open on Dispose.</summary>
    private readonly bool _LeaveOpen;

    /// <summary>Whether the format has been detected and initial header parsed.</summary>
    private bool _Initialized;

    /// <summary>
    /// Whether the stream is exhausted or stopped. Accessed from multiple threads
    /// (the consumer's <c>NextFrame</c> caller and any thread invoking <c>Stop</c>/<c>Dispose</c>);
    /// every read uses <see cref="System.Threading.Volatile"/> Read and every write uses
    /// <see cref="System.Threading.Volatile"/> Write.
    /// </summary>
    private volatile bool _Exhausted;

    /// <summary>Whether Start() has been called.</summary>
    private volatile bool _Started;

    /// <summary>Atomic dispose latch (0 = live, 1 = disposed).</summary>
    private volatile int _Disposed;

    /// <summary>Sequential frame counter for FrameId assignment.</summary>
    private int _FrameIndex;

    #endregion

    #region Format state

    /// <summary>True if the file is legacy PCAP, false for PCAPNG.</summary>
    private bool _IsLegacy;

    /// <summary>PCAPNG sections (one per SHB encountered).</summary>
    private readonly List<SectionInfo> _Sections = [];

    /// <summary>Legacy PCAP file info (null for PCAPNG).</summary>
    private LegacyPcapInfo? _LegacyInfo;

    #endregion

    #region Interface registration

    /// <summary>Source ID assigned during Start().</summary>
    private FrameSourceId _SourceId;

    /// <summary>Registry for interface registration.</summary>
    private FrameInterfaceRegistry? _Registry;

    /// <summary>Maps (sectionIndex, interfaceId) → FrameInterfaceId.</summary>
    private readonly Dictionary<(ushort, ushort), FrameInterfaceId> _Interfaces = [];

    /// <summary>
    /// Reusable buffer for block reads. Grown as needed.
    /// Avoids repeated allocations for small blocks.
    /// </summary>
    private byte[] _BlockBuffer = new byte[4096];

    /// <summary>Reusable 4-byte buffer for SHB byte-order magic reads.</summary>
    private readonly byte[] _MagicBuf = new byte[4];

    /// <summary>
    /// Reusable 8-byte buffer for PCAPNG block header reads (avoids stackalloc in loop).
    /// </summary>
    private readonly byte[] _PcapNgHeaderBuf = new byte[8];

    #endregion

    #region Error tolerance statistics
    private volatile int _ReadFrameCount;
    private readonly SaturatingVolatileCounter _SkippedFrameCount = new();
    private readonly SaturatingVolatileCounter _ErrorCount = new();

    #endregion

    #region Construction

    private PcapStreamSource(Stream stream, string uiName, bool leaveOpen)
    {
        _Stream = stream;
        UiName = uiName;
        _LeaveOpen = leaveOpen;
    }

    /// <summary>
    /// Creates a new <see cref="PcapStreamSource"/> that reads from the given stream.
    /// The stream must be readable. Format detection occurs on the first call to
    /// <see cref="Start"/>.
    /// </summary>
    /// <param name="stream">A readable stream containing PCAPNG or legacy PCAP data.</param>
    /// <param name="uiName">Display name shown in the UI.</param>
    /// <param name="leaveOpen">
    /// If <c>true</c>, the stream is not disposed when this source is disposed.
    /// </param>
    /// <returns>A new PcapStreamSource ready for <see cref="Start"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not readable.</exception>
    public static PcapStreamSource FromStream(Stream stream, string uiName = "Stream", bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(stream));
        }

        return new PcapStreamSource(stream, uiName, leaveOpen);
    }

    #endregion

    #region IFrameSource

    /// <inheritdoc />
    public string? Description => null;

    /// <inheritdoc />
    /// <remarks>Always <c>null</c> for stream sources — the total is unknown until the stream is exhausted.</remarks>
    public int? EstimatedFrameCount => null;

    /// <inheritdoc />
    public bool IsRunning => _Started && _Disposed == 0;

    // ── IErrorTolerantFrameSource / IFrameSourceStatistics ────────────────────

    /// <inheritdoc/>
    public int ReadFrameCount => _ReadFrameCount;

    /// <inheritdoc/>
    public int SkippedFrameCount => _SkippedFrameCount.Value;

    /// <inheritdoc/>
    public int ErrorCount => _ErrorCount.Value;

    /// <inheritdoc/>
    public bool HasErrors => _ErrorCount.Value > 0;

    /// <inheritdoc/>
    public ErrorToleranceMode ErrorTolerance { get; set; } = ErrorToleranceMode.Tolerant;

    /// <inheritdoc/>
    public event EventHandler<FrameReadErrorEventArgs>? FrameSkipped;

    /// <inheritdoc />
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _SourceId = sourceId;
        _Registry = registry;
        _Started = true;
        _FrameIndex = 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// This method is <b>not</b> thread-safe. It must be called from a single thread only.
    /// All mutable state (stream position, section list, frame index) is accessed without synchronization.
    /// </remarks>
    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);

        if (!_Started)
        {
            throw new InvalidOperationException($"{UiName} has not been started. Call Start() first.");
        }

        if (_Exhausted)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Detect format on first call (deferred so Start() doesn't throw)
        if (!_Initialized)
        {
            bool initialized;
            try
            {
                initialized = _Initialize();
            }
            catch
            {
                // Initialization failed catastrophically: prevent re-entry into the parser
                // with corrupt state when a caller swallows the exception and calls NextFrame()
                // again.
                _Exhausted = true;
                throw;
            }
            if (!initialized)
            {
                _Exhausted = true;
                return null;
            }
            _Initialized = true;
        }

        if (_IsLegacy)
        {
            return _NextFrameLegacy();
        }

        return _NextFramePcapNg();
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        // Clear the registry reference so the session can be GC'd after Dispose().
        _Registry = null;
        if (!_LeaveOpen)
        {
            // Wrapped so that a stream disposal failure does not prevent GC.SuppressFinalize from running.
            try
            {
                _Stream.Dispose();
            }
            catch (ObjectDisposedException) { /* idempotent — stream already disposed */ }
        }

        GC.SuppressFinalize(this);
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Detects the capture format, parses the initial header (SHB or legacy global header),
    /// and registers the first interfaces.
    /// </summary>
    /// <returns>True if initialization succeeded; false if the stream is too short or corrupt.</returns>
    /// <exception cref="PcapException">The stream contains an unrecognized format.</exception>
    private bool _Initialize()
    {
        // Read enough bytes for format detection (at least 12 bytes for SHB detection)
        Span<byte> detectionBuffer = stackalloc byte[PcapFormatDetection.MinDetectionBytes];
        if (!_TryReadExact(detectionBuffer))
        {
            return false;
        }

        if (!PcapFormatDetection.TryDetect(detectionBuffer, out FormatDetectionResult detection))
        {
            throw new PcapException("Unrecognized capture file format in stream.");
        }

        if (detection.Format == FileFormat.PcapNg)
        {
            _IsLegacy = false;
            return _InitializePcapNg(detectionBuffer);
        }

        _IsLegacy = true;
        return _InitializeLegacyPcap(detectionBuffer, detection);
    }

    /// <summary>
    /// Reads the Section Header Block from the stream and creates the first section.
    /// The detection buffer already contains the first 12 bytes (block type + length + byte order magic).
    /// </summary>
    private bool _InitializePcapNg(ReadOnlySpan<byte> detectionBytes)
    {
        // We have the first 12 bytes: block_type(4) + block_total_length(4) + byte_order_magic(4)
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(detectionBytes[8..]);
        bool swap = magic == PcapConstants.PcapngSwappedMagic;

        EndianReader reader = new(swap);
        uint blockLength = reader.ReadU32(detectionBytes[4..]);
        if (!PcapPadding.TryRoundBlockLength(blockLength, out uint roundedLength))
        {
            throw new PcapException($"SHB block length {blockLength} cannot be rounded to a multiple of 4.");
        }

        blockLength = roundedLength;
        if (blockLength < PcapConstants.ShbFixedSize)
        {
            throw new PcapException($"SHB block length {blockLength} is less than minimum {PcapConstants.ShbFixedSize}.");
        }

        // Guard: uint → int cast is undefined for blockLength > int.MaxValue and produces
        // a negative remaining that bypasses _EnsureBuffer's cap check.
        if (blockLength > PcapConstants.MaxBlockSize)
        {
            return false;
        }

        // Read the rest of the SHB block
        // We already have 12 bytes; need blockLength - 12 more
        int remaining = (int)blockLength - PcapFormatDetection.MinDetectionBytes;
        byte[]? shbBuffer = _EnsureBuffer((int)blockLength);
        if (shbBuffer is null)
        {
            return false;
        }

        detectionBytes.CopyTo(shbBuffer);

        if (remaining > 0 && !_TryReadExact(shbBuffer.AsSpan(PcapFormatDetection.MinDetectionBytes, remaining)))
        {
            return false;
        }

        if (!PcapngBlockLayout.TryValidatePcapngBlockLength(
                blockLength, shbBuffer.AsSpan((int)blockLength - 4, 4), swap, out _))
        {
            throw new PcapException("SHB trailing block_total_length does not match the leading length.");
        }

        long startOffset = 0;
        if (_Stream.CanSeek)
        {
            startOffset = _Stream.Position - blockLength;
        }

        // Create section
        SectionInfo section = new(swap, -1, startOffset);

        // Parse SHB options if present (after 24-byte struct, before trailing 4 bytes)
        int optionsStart = 24;
        int optionsEnd = (int)blockLength - 4;
        if (optionsEnd > optionsStart && optionsEnd <= (int)blockLength)
        {
            section.ParseShbOptions(shbBuffer.AsSpan(optionsStart, optionsEnd - optionsStart));
        }

        _Sections.Add(section);
        return true;
    }

    /// <summary>
    /// Parses the legacy PCAP global header (24 bytes).
    /// The detection buffer already contains the first 12 bytes.
    /// </summary>
    private bool _InitializeLegacyPcap(ReadOnlySpan<byte> detectionBytes, FormatDetectionResult detection)
    {
        // Need 24 bytes total; we have 12
        byte[]? headerBuffer = _EnsureBuffer(PcapConstants.PcapGlobalHeaderSize);
        if (headerBuffer is null)
        {
            return false;
        }
        detectionBytes.CopyTo(headerBuffer);

        int remaining = PcapConstants.PcapGlobalHeaderSize - PcapFormatDetection.MinDetectionBytes;
        if (!_TryReadExact(headerBuffer.AsSpan(PcapFormatDetection.MinDetectionBytes, remaining)))
        {
            return false;
        }

        bool swap = detection.ByteSwapped;
        EndianReader reader = new(swap);

        uint rawNetwork = reader.ReadU32(headerBuffer.AsSpan(20));
        uint rawSnapLen = reader.ReadU32(headerBuffer.AsSpan(16));

        _LegacyInfo = new LegacyPcapInfo(swap, detection.NanosecondTimestamps, (ushort)rawNetwork, rawSnapLen);

        // Register the single default interface
        _RegisterLegacyInterface();
        return true;
    }

    #endregion

    #region NextFrame

    /// <summary>
    /// Reads PCAPNG blocks from the stream until a packet block is found.
    /// Processes SHB/IDB blocks inline and skips unknown block types.
    /// </summary>
    private Frame? _NextFramePcapNg()
    {
        Span<byte> headerBytes = _PcapNgHeaderBuf;

        while (true)
        {
            // Step 1: Read block type (4 bytes) + block total length (4 bytes)
            if (!_TryReadExact(headerBytes))
            {
                _Exhausted = true;
                return null;
            }

            uint rawBlockType = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes);

            uint blockType;
            uint blockLength;
            bool swap;

            if (rawBlockType == PcapConstants.BlockTypeSHB)
            {
                // SHB: need to read the byte-order magic to determine endianness
                // Read at least 4 more bytes (magic) before we know the byte order
                Span<byte> magicBuf = _MagicBuf;
                if (!_TryReadExact(magicBuf))
                {
                    // Stream truncated mid-SHB (header read succeeded but magic read failed)
                    _HandleSkip(new FrameReadErrorEventArgs
                    {
                        FrameIndex = _FrameIndex,
                        FileOffset = -1,
                        Kind = FrameReadErrorKind.TruncatedStream,
                        Message = "Stream truncated while reading SHB byte-order magic."
                    });
                    _Exhausted = true;
                    return null;
                }

                uint byteOrderMagic = BinaryPrimitives.ReadUInt32LittleEndian(magicBuf);
                swap = byteOrderMagic == PcapConstants.PcapngSwappedMagic;

                EndianReader shbReader = new(swap);
                blockLength = shbReader.ReadU32(headerBytes[4..]);
                blockType = PcapConstants.BlockTypeSHB;

                // Read the rest of the SHB block (already read 12 bytes: 8 header + 4 magic)
                if (!_ProcessSectionHeaderFromStream(swap, blockLength, headerBytes, magicBuf))
                {
                    // Stream truncated mid-SHB body
                    _HandleSkip(new FrameReadErrorEventArgs
                    {
                        FrameIndex = _FrameIndex,
                        FileOffset = -1,
                        Kind = FrameReadErrorKind.TruncatedStream,
                        Message = $"Stream truncated while reading SHB body (block length {blockLength})."
                    });
                    _Exhausted = true;
                    return null;
                }
                continue;
            }

            // Non-SHB blocks: use current section's byte order
            if (_Sections.Count == 0)
            {
                // A data block arrived before any Section Header Block, which means
                // the stream is corrupt.  Report the skip so ErrorCount is accurate.
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = _FrameIndex,
                    FileOffset = -1,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = "Non-SHB block encountered before any Section Header Block; stream may be corrupt."
                });
                _Exhausted = true;
                return null;
            }

            swap = _Sections[^1].ByteSwapped;
            EndianReader reader = new(swap);
            blockType = reader.Swap(rawBlockType);
            blockLength = reader.ReadU32(headerBytes[4..]);

            if (!PcapPadding.TryRoundBlockLength(blockLength, out uint roundedLength))
            {
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = _FrameIndex,
                    FileOffset = -1,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = $"PCAPNG block length {blockLength} cannot be rounded to a multiple of 4.",
                });
                _Exhausted = true;
                return null;
            }

            blockLength = roundedLength;
            if (blockLength < PcapConstants.MinBlockSize)
            {
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = _FrameIndex,
                    FileOffset = -1,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = $"PCAPNG block length {blockLength} is below the minimum block size.",
                });
                _Exhausted = true;
                return null;
            }

            // Guard: uint → int cast is undefined for blockLength > int.MaxValue and produces
            // a negative bodySize that bypasses _EnsureBuffer's cap check, causing
            // blockBuffer.AsSpan(0, bodySize) to throw ArgumentOutOfRangeException.
            // Use an unsigned comparison to cover both the int-overflow range and valid-but-
            // oversized values that _EnsureBuffer would otherwise handle via its null return.
            if (blockLength > PcapConstants.MaxBlockSize)
            {
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = _FrameIndex,
                    FileOffset = -1,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = $"Block length {blockLength} exceeds the {PcapConstants.MaxBlockSize / (1024 * 1024)} MiB safety cap; the stream data may be corrupt."
                });
                _Exhausted = true;
                return null;
            }

            // Read the remaining block body: blockLength - 8 (header) bytes
            // (body includes the trailing 4-byte block_total_length copy)
            int bodySize = (int)blockLength - 8;
            byte[]? blockBuffer = _EnsureBuffer(bodySize);
            if (blockBuffer is null)
            {
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = _FrameIndex,
                    FileOffset = -1,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = $"Block body size {bodySize} exceeds the {PcapConstants.MaxBlockSize / (1024 * 1024)} MiB safety cap; the stream data may be corrupt."
                });
                _Exhausted = true;
                return null;
            }

            if (!_TryReadExact(blockBuffer.AsSpan(0, bodySize)))
            {
                // Stream truncated mid-block (header read succeeded but body read failed)
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = _FrameIndex,
                    FileOffset = -1,
                    Kind = FrameReadErrorKind.TruncatedStream,
                    Message = $"Stream truncated while reading block body (type=0x{blockType:X}, expected {bodySize} bytes)."
                });
                _Exhausted = true;
                return null;
            }

            ReadOnlySpan<byte> bodySpan = blockBuffer.AsSpan(0, bodySize);
            if (!PcapngBlockLayout.TryValidatePcapngBlockLength(blockLength, bodySpan[^4..], swap, out _))
            {
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = _FrameIndex,
                    FileOffset = -1,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = "PCAPNG trailing block_total_length does not match the leading length.",
                });
                _Exhausted = true;
                return null;
            }

            switch (blockType)
            {
                case PcapConstants.BlockTypeIDB:
                    _ProcessInterfaceDescription(bodySpan, blockLength);
                    continue;

                case PcapConstants.BlockTypeEPB:
                    {
                        ScanPacketResult epbResult = _TryScanEnhancedPacket(bodySpan, blockLength, out Frame? epbFrame);
                        if (epbResult == ScanPacketResult.Frame)
                        {
                            if (epbFrame is not null)
                            {
                                return epbFrame;
                            }

                            if (_Exhausted)
                            {
                                return null;
                            }

                            continue;
                        }

                        _HandlePacketScanSkip(epbResult);
                        if (_Exhausted)
                        {
                            return null;
                        }

                        continue;
                    }

                case PcapConstants.BlockTypeSPB:
                    {
                        ScanPacketResult spbResult = _TryScanSimplePacket(bodySpan, blockLength, out Frame? spbFrame);
                        if (spbResult == ScanPacketResult.Frame)
                        {
                            if (spbFrame is not null)
                            {
                                return spbFrame;
                            }

                            if (_Exhausted)
                            {
                                return null;
                            }

                            continue;
                        }

                        _HandlePacketScanSkip(spbResult);
                        if (_Exhausted)
                        {
                            return null;
                        }

                        continue;
                    }

                case PcapConstants.BlockTypePB:
                    {
                        ScanPacketResult pbResult = _TryScanObsoletePacket(bodySpan, blockLength, out Frame? pbFrame);
                        if (pbResult == ScanPacketResult.Frame)
                        {
                            if (pbFrame is not null)
                            {
                                return pbFrame;
                            }

                            if (_Exhausted)
                            {
                                return null;
                            }

                            continue;
                        }

                        _HandlePacketScanSkip(pbResult);
                        if (_Exhausted)
                        {
                            return null;
                        }

                        continue;
                    }

                default:
                    // Unknown block type — already consumed, skip
                    continue;
            }
        }
    }

    /// <summary>
    /// Processes a Section Header Block that was partially read from the stream.
    /// Creates a new section with the detected byte order.
    /// </summary>
    /// <param name="swap">Whether byte swapping is needed.</param>
    /// <param name="blockLength">Total block length from the header.</param>
    /// <param name="headerBytes">First 8 bytes: block_type + block_total_length.</param>
    /// <param name="magicBytes">4 bytes of byte-order magic.</param>
    private bool _ProcessSectionHeaderFromStream(bool swap, uint blockLength, ReadOnlySpan<byte> headerBytes, ReadOnlySpan<byte> magicBytes)
    {
        if (!PcapPadding.TryRoundBlockLength(blockLength, out uint roundedLength))
        {
            return false;
        }

        blockLength = roundedLength;
        if (blockLength < PcapConstants.ShbFixedSize)
        {
            return false;
        }

        // Guard: uint → int cast is undefined for blockLength > int.MaxValue and produces
        // a negative remaining that bypasses _EnsureBuffer's cap check.
        if (blockLength > PcapConstants.MaxBlockSize)
        {
            return false;
        }

        // Already read: 8 (header) + 4 (magic) = 12 bytes
        // Need: blockLength - 12 more bytes (version, section length, options, trailing length)
        int remaining = (int)blockLength - 12;
        byte[]? shbBuffer = _EnsureBuffer((int)blockLength);
        if (shbBuffer is null)
        {
            return false;
        }

        headerBytes.CopyTo(shbBuffer);
        magicBytes.CopyTo(shbBuffer.AsSpan(8));

        if (remaining > 0 && !_TryReadExact(shbBuffer.AsSpan(12, remaining)))
        {
            return false;
        }

        if (!PcapngBlockLayout.TryValidatePcapngBlockLength(
                blockLength, shbBuffer.AsSpan((int)blockLength - 4, 4), swap, out _))
        {
            return false;
        }

        if (!PcapngPacketBlocks.TryReadShb(
                shbBuffer.AsSpan(0, (int)blockLength),
                swap,
                blockLength,
                out long sectionLength,
                out ReadOnlySpan<byte> optionData))
        {
            return false;
        }

        long startOffset = 0;
        if (_Stream.CanSeek)
        {
            startOffset = _Stream.Position - blockLength;
        }

        SectionInfo section = new(swap, sectionLength, startOffset);
        if (!optionData.IsEmpty)
        {
            section.ParseShbOptions(optionData);
        }

        _Sections.Add(section);
        return true;
    }

    /// <summary>
    /// Processes an Interface Description Block.
    /// The body span starts after the 8-byte block header (contains link_type, reserved, snap_len, options, trailing length).
    /// </summary>
    private void _ProcessInterfaceDescription(ReadOnlySpan<byte> bodySpan, uint blockLength)
    {
        if (_Sections.Count == 0)
        {
            return;
        }

        SectionInfo section = _Sections[^1];
        if (!PcapngPacketBlocks.TryReadIdb(
                bodySpan,
                bodyOmits8ByteHeader: true,
                section.ByteSwapped,
                blockLength,
                out IdbFields idb))
        {
            return;
        }

        InterfaceInfo info = section.ParseIdbOptions(idb.LinkType, idb.SnapLength, idb.OptionData);
        int localId = section.AddInterface(info);

        _RegisterPcapNgInterface(section, (ushort)(_Sections.Count - 1), (ushort)localId, info);
    }

    /// <summary>
    /// Tries to scan an Enhanced Packet Block from the body span.
    /// Body starts after the 8-byte block header. orig_len is ignored.
    /// </summary>
    private ScanPacketResult _TryScanEnhancedPacket(ReadOnlySpan<byte> bodySpan, uint blockLength, out Frame? frame)
    {
        frame = null;
        if (_Sections.Count == 0)
        {
            return ScanPacketResult.SkipMalformed;
        }

        ScanPacketResult result = PcapngPacketBlocks.TryReadEpb(
            bodySpan,
            bodyOmits8ByteHeader: true,
            _Sections[^1].ByteSwapped,
            blockLength,
            _Sections[^1],
            out EpbFields fields);
        if (result != ScanPacketResult.Frame)
        {
            return result;
        }

        frame = _FrameFromPacketFields(fields);
        return ScanPacketResult.Frame;
    }

    /// <summary>Tries to scan a Simple Packet Block from the body span.</summary>
    private ScanPacketResult _TryScanSimplePacket(ReadOnlySpan<byte> bodySpan, uint blockLength, out Frame? frame)
    {
        frame = null;
        if (_Sections.Count == 0)
        {
            return ScanPacketResult.SkipMalformed;
        }

        ScanPacketResult result = PcapngPacketBlocks.TryReadSpb(
            bodySpan,
            bodyOmits8ByteHeader: true,
            _Sections[^1].ByteSwapped,
            blockLength,
            _Sections[^1],
            out EpbFields fields);
        if (result != ScanPacketResult.Frame)
        {
            return result;
        }

        frame = _FrameFromPacketFields(fields);
        return ScanPacketResult.Frame;
    }

    /// <summary>Tries to scan an Obsolete Packet Block from the body span. orig_len is ignored.</summary>
    private ScanPacketResult _TryScanObsoletePacket(ReadOnlySpan<byte> bodySpan, uint blockLength, out Frame? frame)
    {
        frame = null;
        if (_Sections.Count == 0)
        {
            return ScanPacketResult.SkipMalformed;
        }

        ScanPacketResult result = PcapngPacketBlocks.TryReadPb(
            bodySpan,
            bodyOmits8ByteHeader: true,
            _Sections[^1].ByteSwapped,
            blockLength,
            _Sections[^1],
            out EpbFields fields);
        if (result != ScanPacketResult.Frame)
        {
            return result;
        }

        frame = _FrameFromPacketFields(fields);
        return ScanPacketResult.Frame;
    }

    /// <summary>Copies helper packet bytes into a tracked <see cref="Frame"/> (stream payloads cannot alias the reused block buffer).</summary>
    private Frame? _FrameFromPacketFields(EpbFields fields)
    {
        byte[] frameData = fields.PacketData.ToArray();
        ushort sectionIndex = (ushort)(_Sections.Count - 1);
        return _CreateTrackedFrame(sectionIndex, (ushort)fields.InterfaceId, fields.TimestampNanos, frameData);
    }

    /// <summary>Raises <see cref="FrameSkipped"/> for a packet-block parse failure.</summary>
    private void _HandlePacketScanSkip(ScanPacketResult result)
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

        _HandleSkip(new FrameReadErrorEventArgs
        {
            FrameIndex = _FrameIndex,
            FileOffset = -1,
            Kind = kind,
            Message = message,
        });
    }

    #endregion

    #region NextFrame

    /// <summary>
    /// Reads the next legacy PCAP packet record from the stream.
    /// </summary>
    private Frame? _NextFrameLegacy()
    {
        if (_LegacyInfo is null)
        {
            return null;
        }

        Span<byte> headerBuf = stackalloc byte[PcapConstants.PcapPacketHeaderSize];
        if (!_TryReadExact(headerBuf))
        {
            // Natural EOF between frames — no event needed
            _Exhausted = true;
            return null;
        }

        EndianReader reader = new(_LegacyInfo.ByteSwapped);
        uint tsSec = reader.ReadU32(headerBuf);
        uint tsFrac = reader.ReadU32(headerBuf[4..]);
        uint inclLen = reader.ReadU32(headerBuf[8..]);

        long timestampNanos = _LegacyInfo.TimestampToNanos(tsSec, tsFrac);

        // Guard incl_len against values from untrusted file headers before allocating.
        // Values > int.MaxValue cannot fit in a .NET array and indicate corruption.
        // Values exceeding SnapLength violate the PCAP specification (snaplen is the
        // per-packet maximum capture length). Stream position after the header is
        // unknown, so exhaust to avoid further desynchronisation.
        if (inclLen > int.MaxValue)
        {
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.MalformedHeader,
                Message = $"Legacy PCAP incl_len {inclLen} exceeds int.MaxValue; malformed header."
            });
            _Exhausted = true;
            return null;
        }

        uint snapLen = _LegacyInfo.SnapLength;
        // When snapLen is 0 the file declares "no limit". Apply the PCAP specification
        // default (DefaultSnapLength = 262 144 bytes) as an implicit cap to prevent a
        // malicious header from triggering a multi-gigabyte allocation before the OOM
        // guard below. Legitimate captures with very large packets should set snapLen
        // explicitly in the global header.
        uint effectiveSnapLen = snapLen > 0 ? snapLen : PcapConstants.DefaultSnapLength;
        if (inclLen > effectiveSnapLen)
        {
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.MalformedHeader,
                Message = snapLen > 0
                    ? $"Legacy PCAP incl_len {inclLen} exceeds snaplen {snapLen}; malformed header."
                    : $"Legacy PCAP incl_len {inclLen} exceeds the default cap {PcapConstants.DefaultSnapLength};"
                        + " set an explicit snaplen in the global header to allow larger packets."
            });
            _Exhausted = true;
            return null;
        }

        // Wrap the allocation in an OOM-safe path: a malicious or corrupt header with
        // a large incl_len must not destabilise the process. Commit-after-success:
        // frameData is only used once the allocation succeeds.
        byte[] frameData;
        try
        {
            frameData = new byte[inclLen];
        }
        catch (OutOfMemoryException)
        {
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.Other,
                Message = $"OutOfMemoryException allocating {inclLen}-byte buffer for legacy PCAP frame."
            });
            _Exhausted = true;
            return null;
        }

        // Read frame data
        if (!_TryReadExact(frameData))
        {
            // Stream truncated mid-frame (header read succeeded but data read failed)
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.TruncatedStream,
                Message = $"Stream truncated while reading frame data (expected {inclLen} bytes)."
            });
            _Exhausted = true;
            return null;
        }

        return _CreateTrackedFrame(0, 0, timestampNanos, frameData);
    }

    #endregion

    #region Interface registration

    /// <summary>
    /// Registers a PCAPNG interface with the stack and caches the mapping.
    /// </summary>
    private void _RegisterPcapNgInterface(SectionInfo section, ushort sectionIndex, ushort localInterfaceId, InterfaceInfo info)
    {
        if (_Registry is null)
        {
            return;
        }

        (ushort, ushort) key = (sectionIndex, localInterfaceId);
        if (_Interfaces.ContainsKey(key))
        {
            return;
        }

        string name = info.Name ?? $"Interface {localInterfaceId}";
        Dictionary<string, object>? props = _BuildPcapNgProperties(info, section);
        FrameInterfaceId id = _Registry.Register(_SourceId, name, info.Description, info.LinkType, props);
        _Interfaces[key] = id;
    }

    /// <summary>
    /// Registers the single default interface for legacy PCAP.
    /// </summary>
    private void _RegisterLegacyInterface()
    {
        if (_Registry is null || _LegacyInfo is null)
        {
            return;
        }

        (ushort, ushort) key = (0, 0);
        if (_Interfaces.ContainsKey(key))
        {
            return;
        }

        Dictionary<string, object>? props = _BuildLegacyPcapProperties(_LegacyInfo);
        FrameInterfaceId id = _Registry.Register(_SourceId, "Default Interface", null, _LegacyInfo.LinkType, props);
        _Interfaces[key] = id;
    }

    /// <summary>
    /// Builds a properties dictionary from PCAPNG interface and section metadata.
    /// Returns null when no properties are available (avoids empty dictionary allocation).
    /// </summary>
    private static Dictionary<string, object>? _BuildPcapNgProperties(InterfaceInfo info, SectionInfo section)
    {
        // RawLinkType and SnapLength are always available — initialize with them
        Dictionary<string, object> props = new()
        {
            [PcapInterfacePropertyKeys.RawLinkType] = info.RawLinkType,
            [PcapInterfacePropertyKeys.SnapLength] = info.SnapLength,
        };

        // Interface-level metadata (IDB options)
        if (info.Speed.HasValue)
        {
            props[PcapInterfacePropertyKeys.Speed] = info.Speed.Value;
        }
        if (info.FcsLength.HasValue)
        {
            props[PcapInterfacePropertyKeys.FcsLength] = info.FcsLength.Value;
        }
        if (info.Filter is not null)
        {
            props[PcapInterfacePropertyKeys.Filter] = info.Filter;
        }
        if (info.Os is not null)
        {
            props[PcapInterfacePropertyKeys.Os] = info.Os;
        }

        // Section-level metadata (SHB options) — shared across all interfaces in the section
        if (section.Hardware is not null)
        {
            props[PcapInterfacePropertyKeys.CaptureHardware] = section.Hardware;
        }
        if (section.Os is not null)
        {
            props[PcapInterfacePropertyKeys.CaptureOs] = section.Os;
        }
        if (section.UserApplication is not null)
        {
            props[PcapInterfacePropertyKeys.CaptureApplication] = section.UserApplication;
        }

        return props;
    }

    /// <summary>
    /// Builds a properties dictionary from legacy PCAP global header metadata.
    /// Returns null when no properties are available.
    /// </summary>
    private static Dictionary<string, object>? _BuildLegacyPcapProperties(LegacyPcapInfo info)
    {
        // Legacy PCAP has limited metadata — snap length and raw link type
        Dictionary<string, object> props = new()
        {
            [PcapInterfacePropertyKeys.RawLinkType] = info.RawLinkType,
            [PcapInterfacePropertyKeys.SnapLength] = info.SnapLength,
        };

        return props;
    }

    /// <summary>
    /// Resolves the link type and registered interface ID for a frame.
    /// </summary>
    private bool _TryResolveInterface(ushort sectionIndex, ushort interfaceId,
        out LinkType linkType, out FrameInterfaceId frameInterfaceId)
    {
        if (_IsLegacy && _LegacyInfo is not null)
        {
            if (!_LegacyInfo.LinkType.HasValue)
            {
                linkType = default;
                frameInterfaceId = default;
                return false;
            }

            linkType = _LegacyInfo.LinkType.Value;
            return _Interfaces.TryGetValue((0, 0), out frameInterfaceId);
        }

        if (sectionIndex < _Sections.Count)
        {
            SectionInfo section = _Sections[sectionIndex];
            InterfaceInfo? info = section.Interface(interfaceId);
            if (info is not null && info.LinkType.HasValue)
            {
                linkType = info.LinkType.Value;
                return _Interfaces.TryGetValue((sectionIndex, interfaceId), out frameInterfaceId);
            }
        }

        linkType = default;
        frameInterfaceId = default;
        return false;
    }

    #endregion

    #region Error handling

    /// <summary>
    /// Handles a skipped frame by updating statistics and raising the event.
    /// In strict mode, additionally marks the stream as exhausted so subsequent reads return null.
    /// The <see cref="FrameSkipped"/> event is always raised regardless of tolerance mode
    /// so subscribers can log the first offending block even when the source aborts
    /// (per SOURCE_GUIDE.md §12.2).
    /// </summary>
    private void _HandleSkip(FrameReadErrorEventArgs error)
    {
        _SkippedFrameCount.Increment();
        _ErrorCount.Increment();

        // Always signal the error so subscribers can log the first offending block
        // regardless of the tolerance mode. In strict mode the source additionally
        // exhausts itself so the next NextFrame() call returns null.
        FrameSkipped?.Invoke(this, error);

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            _Exhausted = true;
        }
    }

    /// <summary>
    /// Creates a frame from resolved data, tracking statistics.
    /// Returns null if interface resolution or frame creation fails.
    /// </summary>
    private Frame? _CreateTrackedFrame(ushort sectionIndex, ushort interfaceId, long timestampNanos, byte[] frameData)
    {
        // Enforce maximum frame count — FrameId is array-index-based
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(_FrameIndex, "frame");

        if (!_TryResolveInterface(sectionIndex, interfaceId, out LinkType linkType, out FrameInterfaceId frameInterfaceId))
        {
            int skipId = _FrameIndex++;
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = skipId,
                FileOffset = -1,
                Kind = FrameReadErrorKind.UnresolvedInterface,
                Message = $"Unresolved interface: section={sectionIndex}, interface={interfaceId}."
            });
            return null;
        }

        int frameId = _FrameIndex++;
        ParseResult<Frame> result = Frame.Create(
            new FrameId(frameId),
            new Timestamp(timestampNanos),
            frameData,
            linkType,
            frameInterfaceId,
            _Registry!);

        if (result.IsSuccess)
        {
            Interlocked.Increment(ref _ReadFrameCount);
            return result.Value;
        }

        _HandleSkip(new FrameReadErrorEventArgs
        {
            FrameIndex = frameId,
            FileOffset = -1,
            Kind = FrameReadErrorKind.Other,
            Message = $"Frame creation failed for section={sectionIndex}, interface={interfaceId}."
        });
        return null;
    }

    #endregion

    #region Stream I/O helpers

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes from the stream.
    /// Returns false if the stream ended before all bytes could be read (EOF).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _TryReadExact(Span<byte> buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = _Stream.Read(buffer[totalRead..]);
            if (read == 0)
            {
                return false; // EOF
            }
            totalRead += read;
        }
        return true;
    }

    /// <summary>
    /// Ensures the internal block buffer is at least the given size.
    /// Returns the buffer (may be larger than requested), or <c>null</c> when
    /// <paramref name="minSize"/> exceeds <see cref="PcapConstants.MaxBlockSize"/>.
    /// When <c>null</c> is returned the caller must skip the oversized block
    /// via <see cref="_HandleSkip"/> and mark the stream exhausted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[]? _EnsureBuffer(int minSize)
    {
        if ((uint)minSize > PcapConstants.MaxBlockSize)
        {
            // Above the shared block cap. The caller exhausts the stream
            // instead of allocating the declared size.
            return null;
        }

        if (_BlockBuffer.Length < minSize)
        {
            // Grow to the next power of two or the requested size, whichever is larger.
            int cap = (int)PcapConstants.MaxBlockSize;
            int newSize = Math.Min(Math.Max(minSize, _BlockBuffer.Length * 2), cap);
            _BlockBuffer = new byte[newSize];
        }
        return _BlockBuffer;
    }
    #endregion
}

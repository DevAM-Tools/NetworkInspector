// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Stream-based frame source for PCAPNG and legacy PCAP data.
/// Implements <see cref="IFrameSource"/> for forward-only sequential reading
/// from any <see cref="Stream"/> (e.g., network streams, pipes, stdin).
/// Packet payload on <see cref="Frame"/> is the captured length. PCAPNG/PCAP <c>orig_len</c>
/// is not kept. A link type that is not a named <see cref="LinkType"/> member is passed through
/// with its number, as in <see cref="PcapngSource"/>.
/// <para>
/// Unlike <see cref="PcapngSource"/>, this class does not support random access
/// (<see cref="IRandomAccessFrameSource"/>). Frames are read one at a time and their bytes are copied out
/// of the reused block buffer: into one array per frame, or into shared slabs when
/// <see cref="PcapngSourceOptions.FrameSlabSize"/> is set.
/// </para>
/// </summary>
/// <remarks>
/// All existing format parsing code is reused via span-based APIs:
/// <see cref="PcapFormatDetection"/>, <see cref="EndianReader"/>,
/// <see cref="SectionInfo"/>, <see cref="InterfaceInfo"/>, etc.
/// A stream that is neither a <see cref="MemoryStream"/> nor a <see cref="BufferedStream"/> is read through a
/// <see cref="BufferedStream"/> of <see cref="StreamBufferSize"/> bytes, so the small header reads do not become one
/// system call each. With <c>leaveOpen = true</c> the buffer may have read ahead of the last parsed block.
/// </remarks>
public sealed class PcapngStreamSource : IFrameSource, IErrorTolerantFrameSource
{
    #region Fields

    /// <summary>Read buffer for streams that do not buffer themselves: 64 KiB.</summary>
    public const int StreamBufferSize = 64 * 1024;

    /// <summary>Data stream: the caller's stream, or a <see cref="BufferedStream"/> around it.</summary>
    private readonly Stream _Stream;

    /// <summary>Frame slab allocator; null when <see cref="PcapngSourceOptions.FrameSlabSize"/> is 0.</summary>
    private readonly FrameSlabAllocator? _Slabs;

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

    /// <summary>Registered interfaces by (section index, interface index); same table as <see cref="PcapngSource"/>.</summary>
    private readonly PcapInterfaceTable _Interfaces = new();

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

    /// <summary>Reusable 16-byte buffer for legacy PCAP record header reads.</summary>
    private readonly byte[] _LegacyHeaderBuf = new byte[PcapConstants.PcapPacketHeaderSize];

    #endregion

    #region Error tolerance statistics
    private volatile int _ReadFrameCount;
    private readonly SaturatingVolatileCounter _SkippedFrameCount = new();
    private readonly SaturatingVolatileCounter _ErrorCount = new();

    #endregion

    #region Construction

    private PcapngStreamSource(Stream stream, string uiName, bool leaveOpen, PcapngSourceOptions options)
    {
        // Header reads are 4 to 16 bytes; on an unbuffered stream each would be a system call.
        _Stream = stream is MemoryStream or BufferedStream ? stream : new BufferedStream(stream, StreamBufferSize);
        UiName = uiName;
        _LeaveOpen = leaveOpen;
        ErrorTolerance = options.ErrorTolerance;
        _Slabs = FrameSlabAllocator.CreateOrNull(options.FrameSlabSize);
    }

    /// <summary>
    /// Creates a new <see cref="PcapngStreamSource"/> that reads from the given stream.
    /// The stream must be readable. Format detection occurs on the first call to
    /// <see cref="NextFrame"/> (after <see cref="Start"/>).
    /// </summary>
    /// <param name="stream">A readable stream containing PCAPNG or legacy PCAP data.</param>
    /// <param name="uiName">Display name shown in the UI.</param>
    /// <param name="leaveOpen">
    /// If <c>true</c>, the stream is not disposed when this source is disposed.
    /// </param>
    /// <param name="options">
    /// Reader options, or null for defaults. The stream reader applies <see cref="PcapngSourceOptions.ErrorTolerance"/>
    /// (initial <see cref="ErrorTolerance"/>) and <see cref="PcapngSourceOptions.FrameSlabSize"/>; the file-only members
    /// are ignored.
    /// </param>
    /// <returns>A new PcapngStreamSource ready for <see cref="Start"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not readable.</exception>
    public static PcapngStreamSource FromStream(
        Stream stream, string uiName = "Stream", bool leaveOpen = false, PcapngSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(stream));
        }

        return new PcapngStreamSource(stream, uiName, leaveOpen, options ?? new());
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
    /// <remarks>Initialised from <see cref="PcapngSourceOptions.ErrorTolerance"/>; may be changed at any time.</remarks>
    public ErrorToleranceMode ErrorTolerance
    {
        get; set;
    }

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
            // Disposing the BufferedStream wrapper also disposes the caller's stream.
            try
            {
                _Stream.Dispose();
            }
            catch (ObjectDisposedException) { /* idempotent — stream already disposed */ }
        }

        // With leaveOpen a BufferedStream wrapper is simply dropped: disposing it would close the caller's stream.

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
        uint blockLength = new EndianReader(swap).ReadU32(detectionBytes[4..]);

        // A first SHB whose length can never be valid is fatal: nothing can be read without a section.
        if (!PcapPadding.TryRoundBlockLength(blockLength, out uint roundedLength))
        {
            throw new PcapException($"SHB block length {blockLength} cannot be rounded to a multiple of 4.");
        }

        if (roundedLength < PcapConstants.ShbFixedSize)
        {
            throw new PcapException($"SHB block length {roundedLength} is less than minimum {PcapConstants.ShbFixedSize}.");
        }

        // Everything else (cap, body read, trailing length, section length, options) uses the routine that parses every
        // later SHB, so both paths apply one set of checks. A cut or oversized first block ends the stream (false).
        return _ProcessSectionHeaderFromStream(
            swap, blockLength, detectionBytes[..8], detectionBytes[8..PcapFormatDetection.MinDetectionBytes], throwOnBadTrailer: true);
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
    /// <param name="throwOnBadTrailer">
    /// True for the first SHB of the stream: a trailing length that does not match is a <see cref="PcapException"/>
    /// there (the stream is not PCAPNG), while a later SHB only ends the stream.
    /// </param>
    /// <exception cref="PcapException"><paramref name="throwOnBadTrailer"/> is set and the trailing length does not match.</exception>
    private bool _ProcessSectionHeaderFromStream(
        bool swap, uint blockLength, ReadOnlySpan<byte> headerBytes, ReadOnlySpan<byte> magicBytes, bool throwOnBadTrailer = false)
    {
        // Section indexes are ushort; a 65 536th section would wrap onto section 0 (same guard as the file scanner).
        if (_Sections.Count >= ushort.MaxValue)
        {
            return false;
        }

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
            if (throwOnBadTrailer)
            {
                throw new PcapException("SHB trailing block_total_length does not match the leading length.");
            }

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
        if (localId < 0)
        {
            // Section already holds MaxInterfaceCount interfaces: the IDB is ignored, its packets skip as SkipNoInterface.
            return;
        }

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

    /// <summary>
    /// Copies the packet bytes into a tracked <see cref="Frame"/>. Stream payloads cannot alias the reused block buffer,
    /// so they go into a slab region when slabs are on, otherwise into a new array.
    /// </summary>
    private Frame? _FrameFromPacketFields(EpbFields fields)
    {
        ReadOnlyMemory<byte> frameData = _CopyFrameBytes(fields.PacketData);
        ushort sectionIndex = (ushort)(_Sections.Count - 1);
        return _CreateTrackedFrame(sectionIndex, (ushort)fields.InterfaceId, fields.TimestampNanos, frameData);
    }

    /// <summary>Copies frame bytes out of a reused buffer: into the slab when slabs are on, otherwise into a new array.</summary>
    private ReadOnlyMemory<byte> _CopyFrameBytes(ReadOnlySpan<byte> source)
    {
        if (_Slabs is null)
        {
            return source.ToArray();
        }

        Memory<byte> region = _Slabs.Allocate(source.Length);
        source.CopyTo(region.Span);
        return region;
    }

    /// <summary>Raises <see cref="FrameSkipped"/> for a packet-block parse failure.</summary>
    private void _HandlePacketScanSkip(ScanPacketResult result)
    {
        (FrameReadErrorKind kind, string message) = PcapInterfaceProperties.DescribeSkip(result);

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

        Span<byte> headerBuf = _LegacyHeaderBuf;
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

        // Guard incl_len against values from untrusted file headers before allocating: the declared snaplen (or the
        // format default when none is declared) and the shared block cap, with the same rule and text as the file
        // reader. Stream position after the header is unknown, so exhaust to avoid further desynchronisation.
        if (!PcapngBlockLayout.TryValidateLegacyInclLen(inclLen, _LegacyInfo.DeclaredSnapLength, out string? reason))
        {
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.MalformedHeader,
                Message = reason!,
            });
            _Exhausted = true;
            return null;
        }

        // Validated: inclLen <= MaxBlockSize, so the cast is exact and the allocation bounded. The bytes go straight
        // into the frame buffer (a slab region when slabs are on); _TryReadExact fills every byte or reports EOF.
        Memory<byte> frameData = _Slabs is null ? GC.AllocateUninitializedArray<byte>((int)inclLen) : _Slabs.Allocate((int)inclLen);
        if (!_TryReadExact(frameData.Span))
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
    /// Registers a PCAPNG interface with the registry and publishes it in the interface table. A name from the file
    /// that the registry would reject (empty, control characters) falls back to the positional name.
    /// </summary>
    private void _RegisterPcapNgInterface(SectionInfo section, ushort sectionIndex, ushort localInterfaceId, InterfaceInfo info)
    {
        if (_Registry is null || _Interfaces.Contains(sectionIndex, localInterfaceId))
        {
            return;
        }

        string name = info.Name is { } ifName && NameValidation.IsValidUiName(ifName) ? ifName : $"Interface {localInterfaceId}";
        FrameInterfaceId id = _Registry.Register(
            _SourceId, name, info.Description, info.LinkType, PcapInterfaceProperties.ForPcapng(info, section));
        _ = _Interfaces.TryAdd(sectionIndex, localInterfaceId, new PcapInterfaceEntry(id, info.LinkType));
    }

    /// <summary>
    /// Registers the single default interface for legacy PCAP.
    /// </summary>
    private void _RegisterLegacyInterface()
    {
        if (_Registry is null || _LegacyInfo is null || _Interfaces.Contains(0, 0))
        {
            return;
        }

        FrameInterfaceId id = _Registry.Register(
            _SourceId, "Default Interface", null, _LegacyInfo.LinkType, PcapInterfaceProperties.ForLegacy(_LegacyInfo));
        _ = _Interfaces.TryAdd(0, 0, new PcapInterfaceEntry(id, _LegacyInfo.LinkType));
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
    private Frame? _CreateTrackedFrame(ushort sectionIndex, ushort interfaceId, long timestampNanos, ReadOnlyMemory<byte> frameData)
    {
        // Enforce maximum frame count — FrameId is array-index-based
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(_FrameIndex, "frame");

        if (!_Interfaces.TryGet(sectionIndex, interfaceId, out PcapInterfaceEntry iface))
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
            iface.LinkType,
            iface.Id,
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

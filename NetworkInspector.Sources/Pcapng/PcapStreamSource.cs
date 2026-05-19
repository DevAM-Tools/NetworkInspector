// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Sources.Pcapng.Format;
using NetworkInspector.Sources.Pcapng.Format.Blocks;

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Stream-based frame source for PCAPNG and legacy PCAP data.
/// Implements <see cref="IFrameSource"/> for forward-only sequential reading
/// from any <see cref="Stream"/> (e.g., network streams, pipes, stdin).
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
    private readonly string _UiName;

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
    private bool _Exhausted;

    /// <summary>Whether Start() has been called.</summary>
    private bool _Started;

    /// <summary>Whether Dispose() has been called.</summary>
    private bool _Disposed;

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
    private long _ReadFrameCount;
    private long _SkippedFrameCount;
    private long _ErrorCount;

    #endregion

    #region Construction

    private PcapStreamSource(Stream stream, string uiName, bool leaveOpen)
    {
        _Stream = stream;
        _UiName = uiName;
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
    public string UiName => _UiName;

    /// <inheritdoc />
    public string? Description => null;

    /// <inheritdoc />
    /// <remarks>Always <c>null</c> for stream sources — the total is unknown until the stream is exhausted.</remarks>
    public int? EstimatedFrameCount => null;

    /// <inheritdoc />
    public bool IsRunning => Volatile.Read(ref _Started) && !Volatile.Read(ref _Disposed);

    // ── IErrorTolerantFrameSource / IFrameSourceStatistics ────────────────────

    /// <inheritdoc/>
    public long ReadFrameCount => Volatile.Read(ref _ReadFrameCount);

    /// <inheritdoc/>
    public long SkippedFrameCount => Volatile.Read(ref _SkippedFrameCount);

    /// <inheritdoc/>
    public long ErrorCount => Volatile.Read(ref _ErrorCount);

    /// <inheritdoc/>
    public bool HasErrors => Volatile.Read(ref _ErrorCount) > 0;

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
        Volatile.Write(ref _Started, true);
        _FrameIndex = 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// This method is <b>not</b> thread-safe. It must be called from a single thread only.
    /// All mutable state (stream position, section list, frame index) is accessed without synchronization.
    /// </remarks>
    public Frame? NextFrame()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed), this);

        if (!Volatile.Read(ref _Started))
        {
            throw new InvalidOperationException($"{UiName} has not been started. Call Start() first.");
        }

        if (Volatile.Read(ref _Exhausted))
        {
            return null;
        }

        // Detect format on first call (deferred so Start() doesn't throw)
        if (!_Initialized)
        {
            bool initialized;
            try
            {
                initialized = Initialize();
            }
            catch
            {
                // Initialization failed catastrophically: prevent re-entry into the parser
                // with corrupt state when a caller swallows the exception and calls NextFrame()
                // again.
                Volatile.Write(ref _Exhausted, true);
                throw;
            }
            if (!initialized)
            {
                Volatile.Write(ref _Exhausted, true);
                return null;
            }
            _Initialized = true;
        }

        if (_IsLegacy)
        {
            return NextFrameLegacy();
        }

        return NextFramePcapNg();
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (Volatile.Read(ref _Disposed))
        {
            return;
        }

        Volatile.Write(ref _Disposed, true);
        // Clear the registry reference so the session can be GC'd after Dispose().
        _Registry = null;
        if (!_LeaveOpen)
        {
            // Wrapped so that a stream disposal failure does not prevent GC.SuppressFinalize from running.
            try
            {
                _Stream.Dispose();
            }
            catch (Exception) { Interlocked.Increment(ref _ErrorCount); }
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
    private bool Initialize()
    {
        // Read enough bytes for format detection (at least 12 bytes for SHB detection)
        Span<byte> detectionBuffer = stackalloc byte[PcapFormatDetection.MinDetectionBytes];
        if (!TryReadExact(detectionBuffer))
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
            return InitializePcapNg(detectionBuffer);
        }

        _IsLegacy = true;
        return InitializeLegacyPcap(detectionBuffer, detection);
    }

    /// <summary>
    /// Reads the Section Header Block from the stream and creates the first section.
    /// The detection buffer already contains the first 12 bytes (block type + length + byte order magic).
    /// </summary>
    private bool InitializePcapNg(ReadOnlySpan<byte> detectionBytes)
    {
        // We have the first 12 bytes: block_type(4) + block_total_length(4) + byte_order_magic(4)
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(detectionBytes[8..]);
        bool swap = magic == PcapConstants.PcapngSwappedMagic;

        EndianReader reader = new(swap);
        uint blockLength = reader.ReadU32(detectionBytes[4..]);
        if (blockLength < PcapConstants.ShbFixedSize)
        {
            throw new PcapException($"SHB block length {blockLength} is less than minimum {PcapConstants.ShbFixedSize}.");
        }

        // Guard: uint → int cast is undefined for blockLength > int.MaxValue and produces
        // a negative remaining that bypasses EnsureBuffer's cap check.
        if (blockLength > (uint)MaxBufferSize)
        {
            return false;
        }

        // Read the rest of the SHB block
        // We already have 12 bytes; need blockLength - 12 more
        int remaining = (int)blockLength - PcapFormatDetection.MinDetectionBytes;
        byte[]? shbBuffer = EnsureBuffer((int)blockLength);
        if (shbBuffer is null)
        {
            return false;
        }

        detectionBytes.CopyTo(shbBuffer);

        if (remaining > 0 && !TryReadExact(shbBuffer.AsSpan(PcapFormatDetection.MinDetectionBytes, remaining)))
        {
            return false;
        }

        // Create section
        SectionInfo section = new(swap, -1, 0);

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
    private bool InitializeLegacyPcap(ReadOnlySpan<byte> detectionBytes, FormatDetectionResult detection)
    {
        // Need 24 bytes total; we have 12
        byte[]? headerBuffer = EnsureBuffer(PcapConstants.PcapGlobalHeaderSize);
        if (headerBuffer is null)
        {
            return false;
        }
        detectionBytes.CopyTo(headerBuffer);

        int remaining = PcapConstants.PcapGlobalHeaderSize - PcapFormatDetection.MinDetectionBytes;
        if (!TryReadExact(headerBuffer.AsSpan(PcapFormatDetection.MinDetectionBytes, remaining)))
        {
            return false;
        }

        bool swap = detection.ByteSwapped;
        EndianReader reader = new(swap);

        uint rawNetwork = reader.ReadU32(headerBuffer.AsSpan(20));
        uint rawSnapLen = reader.ReadU32(headerBuffer.AsSpan(16));

        _LegacyInfo = new LegacyPcapInfo(swap, detection.NanosecondTimestamps, (ushort)rawNetwork, rawSnapLen);

        // Register the single default interface
        RegisterLegacyInterface();
        return true;
    }

    #endregion

    #region NextFrame

    /// <summary>
    /// Reads PCAPNG blocks from the stream until a packet block is found.
    /// Processes SHB/IDB blocks inline and skips unknown block types.
    /// </summary>
    private Frame? NextFramePcapNg()
    {
        Span<byte> headerBytes = _PcapNgHeaderBuf;

        while (true)
        {
            // Step 1: Read block type (4 bytes) + block total length (4 bytes)
            if (!TryReadExact(headerBytes))
            {
                Volatile.Write(ref _Exhausted, true);
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
                if (!TryReadExact(magicBuf))
                {
                    // Stream truncated mid-SHB (header read succeeded but magic read failed)
                    HandleSkip(new FrameReadErrorEventArgs
                    {
                        FrameIndex = _FrameIndex,
                        FileOffset = -1,
                        Kind = FrameReadErrorKind.TruncatedStream,
                        Message = "Stream truncated while reading SHB byte-order magic."
                    });
                    Volatile.Write(ref _Exhausted, true);
                    return null;
                }

                uint byteOrderMagic = BinaryPrimitives.ReadUInt32LittleEndian(magicBuf);
                swap = byteOrderMagic == PcapConstants.PcapngSwappedMagic;

                EndianReader shbReader = new(swap);
                blockLength = shbReader.ReadU32(headerBytes[4..]);
                blockType = PcapConstants.BlockTypeSHB;

                // Read the rest of the SHB block (already read 12 bytes: 8 header + 4 magic)
                if (!ProcessSectionHeaderFromStream(swap, blockLength, headerBytes, magicBuf))
                {
                    // Stream truncated mid-SHB body
                    HandleSkip(new FrameReadErrorEventArgs
                    {
                        FrameIndex = _FrameIndex,
                        FileOffset = -1,
                        Kind = FrameReadErrorKind.TruncatedStream,
                        Message = $"Stream truncated while reading SHB body (block length {blockLength})."
                    });
                    Volatile.Write(ref _Exhausted, true);
                    return null;
                }
                continue;
            }

            // Non-SHB blocks: use current section's byte order
            if (_Sections.Count == 0)
            {
                // A data block arrived before any Section Header Block, which means
                // the stream is corrupt.  Report the skip so ErrorCount is accurate.
                HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = _FrameIndex,
                    FileOffset = -1,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = "Non-SHB block encountered before any Section Header Block; stream may be corrupt."
                });
                Volatile.Write(ref _Exhausted, true);
                return null;
            }

            swap = _Sections[^1].ByteSwapped;
            EndianReader reader = new(swap);
            blockType = reader.Swap(rawBlockType);
            blockLength = reader.ReadU32(headerBytes[4..]);

            // Validate minimum block size
            if (blockLength < PcapConstants.MinBlockSize)
            {
                // Block length is below the spec-mandated minimum — the stream is corrupt.
                // Report via HandleSkip before exhausting so callers receive the diagnostic.
                HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = _FrameIndex,
                    FileOffset = -1,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = $"Block length {blockLength} is below the minimum allowed ({PcapConstants.MinBlockSize}); stream may be corrupt."
                });
                Volatile.Write(ref _Exhausted, true);
                return null;
            }

            // Guard: uint → int cast is undefined for blockLength > int.MaxValue and produces
            // a negative bodySize that bypasses EnsureBuffer's cap check, causing
            // blockBuffer.AsSpan(0, bodySize) to throw ArgumentOutOfRangeException.
            // Use an unsigned comparison to cover both the int-overflow range and valid-but-
            // oversized values that EnsureBuffer would otherwise handle via its null return.
            if (blockLength > (uint)MaxBufferSize + 8u)
            {
                HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = _FrameIndex,
                    FileOffset = -1,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = $"Block length {blockLength} exceeds the {MaxBufferSize / (1024 * 1024)} MiB safety cap; the stream data may be corrupt."
                });
                Volatile.Write(ref _Exhausted, true);
                return null;
            }

            // Read the remaining block body: blockLength - 8 (header) bytes
            // (body includes the trailing 4-byte block_total_length copy)
            int bodySize = (int)blockLength - 8;
            byte[]? blockBuffer = EnsureBuffer(bodySize);
            if (blockBuffer is null)
            {
                HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = _FrameIndex,
                    FileOffset = -1,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = $"Block body size {bodySize} exceeds the {MaxBufferSize / (1024 * 1024)} MiB safety cap; the stream data may be corrupt."
                });
                Volatile.Write(ref _Exhausted, true);
                return null;
            }

            if (!TryReadExact(blockBuffer.AsSpan(0, bodySize)))
            {
                // Stream truncated mid-block (header read succeeded but body read failed)
                HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = _FrameIndex,
                    FileOffset = -1,
                    Kind = FrameReadErrorKind.TruncatedStream,
                    Message = $"Stream truncated while reading block body (type=0x{blockType:X}, expected {bodySize} bytes)."
                });
                Volatile.Write(ref _Exhausted, true);
                return null;
            }

            ReadOnlySpan<byte> bodySpan = blockBuffer.AsSpan(0, bodySize);

            switch (blockType)
            {
                case PcapConstants.BlockTypeIDB:
                    ProcessInterfaceDescription(bodySpan);
                    continue;

                case PcapConstants.BlockTypeEPB:
                    {
                        Frame? frame = TryScanEnhancedPacket(bodySpan);
                        if (frame.HasValue)
                        {
                            return frame;
                        }
                        // Malformed EPB — skip
                        continue;
                    }

                case PcapConstants.BlockTypeSPB:
                    {
                        Frame? frame = TryScanSimplePacket(bodySpan, blockLength);
                        if (frame.HasValue)
                        {
                            return frame;
                        }
                        continue;
                    }

                case PcapConstants.BlockTypePB:
                    {
                        Frame? frame = TryScanObsoletePacket(bodySpan, blockLength);
                        if (frame.HasValue)
                        {
                            return frame;
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
    private bool ProcessSectionHeaderFromStream(bool swap, uint blockLength, ReadOnlySpan<byte> headerBytes, ReadOnlySpan<byte> magicBytes)
    {
        if (blockLength < PcapConstants.ShbFixedSize)
        {
            return false;
        }

        // Guard: uint → int cast is undefined for blockLength > int.MaxValue and produces
        // a negative remaining that bypasses EnsureBuffer's cap check.
        if (blockLength > (uint)MaxBufferSize)
        {
            return false;
        }

        // Already read: 8 (header) + 4 (magic) = 12 bytes
        // Need: blockLength - 12 more bytes (version, section length, options, trailing length)
        int remaining = (int)blockLength - 12;
        byte[]? shbBuffer = EnsureBuffer((int)blockLength);
        if (shbBuffer is null)
        {
            return false;
        }

        headerBytes.CopyTo(shbBuffer);
        magicBytes.CopyTo(shbBuffer.AsSpan(8));

        if (remaining > 0 && !TryReadExact(shbBuffer.AsSpan(12, remaining)))
        {
            return false;
        }

        // Parse section length from bytes 16..24 of the SHB
        EndianReader reader = new(swap);
        long sectionLength = reader.ReadI64(shbBuffer.AsSpan(16));

        SectionInfo section = new(swap, sectionLength, 0);

        // Parse SHB options (after 24-byte struct, before trailing 4 bytes)
        int optionsStart = 24;
        int optionsEnd = (int)blockLength - 4;
        if (optionsEnd > optionsStart)
        {
            section.ParseShbOptions(shbBuffer.AsSpan(optionsStart, optionsEnd - optionsStart));
        }

        _Sections.Add(section);
        return true;
    }

    /// <summary>
    /// Processes an Interface Description Block.
    /// The body span starts after the 8-byte block header (contains link_type, reserved, snap_len, options, trailing length).
    /// </summary>
    private void ProcessInterfaceDescription(ReadOnlySpan<byte> bodySpan)
    {
        if (_Sections.Count == 0 || bodySpan.Length < 8)
        {
            return;
        }

        SectionInfo section = _Sections[^1];
        EndianReader reader = new(section.ByteSwapped);

        // Body layout (after block_type+block_total_length):
        // link_type (2) + reserved (2) + snap_length (4) + options... + trailing_length (4)
        ushort linkType = reader.ReadU16(bodySpan);
        uint snapLength = reader.ReadU32(bodySpan[4..]);

        // Options start at offset 8, end before trailing 4-byte length
        int optionsStart = 8;
        int optionsEnd = bodySpan.Length - 4;
        ReadOnlySpan<byte> optionData = optionsEnd > optionsStart
            ? bodySpan[optionsStart..optionsEnd]
            : ReadOnlySpan<byte>.Empty;

        InterfaceInfo info = section.ParseIdbOptions(linkType, snapLength, optionData);
        int localId = section.AddInterface(info);

        // Register with the stack
        RegisterPcapNgInterface(section, (ushort)(_Sections.Count - 1), (ushort)localId, info);
    }

    /// <summary>
    /// Tries to scan an Enhanced Packet Block from the body span.
    /// Body starts after the 8-byte block header.
    /// </summary>
    private Frame? TryScanEnhancedPacket(ReadOnlySpan<byte> bodySpan)
    {
        // EPB body layout: interface_id(4) + ts_high(4) + ts_low(4) + captured_len(4)
        //                  + original_len(4) + data + options + trailing_length(4)
        // Minimum body: 20 bytes (fields) + 4 (trailing) = 24
        if (bodySpan.Length < 24)
        {
            return null;
        }

        if (_Sections.Count == 0)
        {
            return null;
        }

        SectionInfo section = _Sections[^1];
        EndianReader reader = new(section.ByteSwapped);

        uint interfaceId = reader.ReadU32(bodySpan);
        uint tsHigh = reader.ReadU32(bodySpan[4..]);
        uint tsLow = reader.ReadU32(bodySpan[8..]);
        uint capturedLength = reader.ReadU32(bodySpan[12..]);

        // Validate interface
        InterfaceInfo? iface = section.Interface((int)interfaceId);
        if (iface == null)
        {
            return null;
        }

        // Validate captured length fits (data starts at offset 20, trailing length at end)
        int maxData = bodySpan.Length - 20 - 4; // minus fields header, minus trailing length
        int actualCaptured = (int)Math.Min(capturedLength, maxData);
        if (actualCaptured < 0)
        {
            return null;
        }

        // Compute timestamp
        ulong rawTimestamp = ((ulong)tsHigh << 32) | tsLow;
        long timestampNanos = iface.TimestampToNanos(rawTimestamp);

        // Copy frame data
        byte[] frameData = bodySpan.Slice(20, actualCaptured).ToArray();

        ushort sectionIndex = (ushort)(_Sections.Count - 1);
        return CreateTrackedFrame(sectionIndex, (ushort)interfaceId, timestampNanos, frameData);
    }

    /// <summary>
    /// Tries to scan a Simple Packet Block from the body span.
    /// </summary>
    private Frame? TryScanSimplePacket(ReadOnlySpan<byte> bodySpan, uint blockLength)
    {
        // SPB body layout: original_packet_len(4) + data + trailing_length(4)
        if (bodySpan.Length < 8 || _Sections.Count == 0)
        {
            return null;
        }

        SectionInfo section = _Sections[^1];
        EndianReader reader = new(section.ByteSwapped);

        // SPB always uses interface 0
        InterfaceInfo? iface = section.Interface(0);
        if (iface == null)
        {
            return null;
        }

        uint originalLength = reader.ReadU32(bodySpan);

        // Captured length = min(original, body_data_size, snaplen)
        // Body data starts at offset 4, trailing length at end
        int bodyDataSize = bodySpan.Length - 4 - 4; // minus originalLength field, minus trailing
        int capturedLength = (int)Math.Min(Math.Min(originalLength, (uint)bodyDataSize), iface.SnapLength);
        if (capturedLength < 0)
        {
            return null;
        }

        byte[] frameData = bodySpan.Slice(4, capturedLength).ToArray();

        ushort sectionIndex = (ushort)(_Sections.Count - 1);
        return CreateTrackedFrame(sectionIndex, 0, 0, frameData); // SPB has no timestamp
    }

    /// <summary>
    /// Tries to scan an Obsolete Packet Block from the body span.
    /// </summary>
    private Frame? TryScanObsoletePacket(ReadOnlySpan<byte> bodySpan, uint blockLength)
    {
        // PB body layout: interface_id(2) + drops_count(2) + ts_high(4) + ts_low(4)
        //                + captured_len(4) + original_len(4) + data + options + trailing(4)
        // Minimum body: 20 bytes fields + 4 trailing = 24
        if (bodySpan.Length < 24 || _Sections.Count == 0)
        {
            return null;
        }

        SectionInfo section = _Sections[^1];
        EndianReader reader = new(section.ByteSwapped);

        ushort interfaceId = reader.ReadU16(bodySpan);
        uint tsHigh = reader.ReadU32(bodySpan[4..]);
        uint tsLow = reader.ReadU32(bodySpan[8..]);
        uint capturedLength = reader.ReadU32(bodySpan[12..]);

        InterfaceInfo? iface = section.Interface(interfaceId);
        if (iface == null)
        {
            return null;
        }

        int maxData = bodySpan.Length - 20 - 4;
        int actualCaptured = (int)Math.Min(capturedLength, maxData);
        if (actualCaptured < 0)
        {
            return null;
        }

        ulong rawTimestamp = ((ulong)tsHigh << 32) | tsLow;
        long timestampNanos = iface.TimestampToNanos(rawTimestamp);

        byte[] frameData = bodySpan.Slice(20, actualCaptured).ToArray();

        ushort sectionIndex = (ushort)(_Sections.Count - 1);
        return CreateTrackedFrame(sectionIndex, interfaceId, timestampNanos, frameData);
    }

    #endregion

    #region NextFrame

    /// <summary>
    /// Reads the next legacy PCAP packet record from the stream.
    /// </summary>
    private Frame? NextFrameLegacy()
    {
        if (_LegacyInfo is null)
        {
            return null;
        }

        Span<byte> headerBuf = stackalloc byte[PcapConstants.PcapPacketHeaderSize];
        if (!TryReadExact(headerBuf))
        {
            // Natural EOF between frames — no event needed
            Volatile.Write(ref _Exhausted, true);
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
            HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.MalformedHeader,
                Message = $"Legacy PCAP incl_len {inclLen} exceeds int.MaxValue; malformed header."
            });
            Volatile.Write(ref _Exhausted, true);
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
            HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.MalformedHeader,
                Message = snapLen > 0
                    ? $"Legacy PCAP incl_len {inclLen} exceeds snaplen {snapLen}; malformed header."
                    : $"Legacy PCAP incl_len {inclLen} exceeds the default cap {PcapConstants.DefaultSnapLength};"
                        + " set an explicit snaplen in the global header to allow larger packets."
            });
            Volatile.Write(ref _Exhausted, true);
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
            HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.Other,
                Message = $"OutOfMemoryException allocating {inclLen}-byte buffer for legacy PCAP frame."
            });
            Volatile.Write(ref _Exhausted, true);
            return null;
        }

        // Read frame data
        if (!TryReadExact(frameData))
        {
            // Stream truncated mid-frame (header read succeeded but data read failed)
            HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = _FrameIndex,
                FileOffset = -1,
                Kind = FrameReadErrorKind.TruncatedStream,
                Message = $"Stream truncated while reading frame data (expected {inclLen} bytes)."
            });
            Volatile.Write(ref _Exhausted, true);
            return null;
        }

        return CreateTrackedFrame(0, 0, timestampNanos, frameData);
    }

    #endregion

    #region Interface registration

    /// <summary>
    /// Registers a PCAPNG interface with the stack and caches the mapping.
    /// </summary>
    private void RegisterPcapNgInterface(SectionInfo section, ushort sectionIndex, ushort localInterfaceId, InterfaceInfo info)
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
        Dictionary<string, object>? props = BuildPcapNgProperties(info, section);
        FrameInterfaceId id = _Registry.Register(_SourceId, name, info.Description, info.LinkType, props);
        _Interfaces[key] = id;
    }

    /// <summary>
    /// Registers the single default interface for legacy PCAP.
    /// </summary>
    private void RegisterLegacyInterface()
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

        Dictionary<string, object>? props = BuildLegacyPcapProperties(_LegacyInfo);
        FrameInterfaceId id = _Registry.Register(_SourceId, "Default Interface", null, _LegacyInfo.LinkType, props);
        _Interfaces[key] = id;
    }

    /// <summary>
    /// Builds a properties dictionary from PCAPNG interface and section metadata.
    /// Returns null when no properties are available (avoids empty dictionary allocation).
    /// </summary>
    private static Dictionary<string, object>? BuildPcapNgProperties(InterfaceInfo info, SectionInfo section)
    {
        // RawLinkType and SnapLength are always available — initialize with them
        Dictionary<string, object> props = new()
        {
            [FrameInterfacePropertyKeys.RawLinkType] = info.RawLinkType,
            [FrameInterfacePropertyKeys.SnapLength] = info.SnapLength,
        };

        // Interface-level metadata (IDB options)
        if (info.Speed.HasValue)
        {
            props[FrameInterfacePropertyKeys.Speed] = info.Speed.Value;
        }
        if (info.FcsLength.HasValue)
        {
            props[FrameInterfacePropertyKeys.FcsLength] = info.FcsLength.Value;
        }
        if (info.Filter is not null)
        {
            props[FrameInterfacePropertyKeys.Filter] = info.Filter;
        }
        if (info.Os is not null)
        {
            props[FrameInterfacePropertyKeys.Os] = info.Os;
        }

        // Section-level metadata (SHB options) — shared across all interfaces in the section
        if (section.Hardware is not null)
        {
            props[FrameInterfacePropertyKeys.CaptureHardware] = section.Hardware;
        }
        if (section.Os is not null)
        {
            props[FrameInterfacePropertyKeys.CaptureOs] = section.Os;
        }
        if (section.UserApplication is not null)
        {
            props[FrameInterfacePropertyKeys.CaptureApplication] = section.UserApplication;
        }

        return props;
    }

    /// <summary>
    /// Builds a properties dictionary from legacy PCAP global header metadata.
    /// Returns null when no properties are available.
    /// </summary>
    private static Dictionary<string, object>? BuildLegacyPcapProperties(LegacyPcapInfo info)
    {
        // Legacy PCAP has limited metadata — snap length and raw link type
        Dictionary<string, object> props = new()
        {
            [FrameInterfacePropertyKeys.RawLinkType] = info.RawLinkType,
            [FrameInterfacePropertyKeys.SnapLength] = info.SnapLength,
        };

        return props;
    }

    /// <summary>
    /// Resolves the link type and registered interface ID for a frame.
    /// </summary>
    private bool TryResolveInterface(ushort sectionIndex, ushort interfaceId,
        out LinkType linkType, out FrameInterfaceId frameInterfaceId)
    {
        if (_IsLegacy && _LegacyInfo is not null)
        {
            linkType = _LegacyInfo.LinkType ?? LinkType.Ethernet;
            return _Interfaces.TryGetValue((0, 0), out frameInterfaceId);
        }

        if (sectionIndex < _Sections.Count)
        {
            SectionInfo section = _Sections[sectionIndex];
            InterfaceInfo? info = section.Interface(interfaceId);
            if (info is not null)
            {
                linkType = info.LinkType ?? LinkType.Ethernet;
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
    private void HandleSkip(FrameReadErrorEventArgs error)
    {
        Interlocked.Increment(ref _SkippedFrameCount);
        Interlocked.Increment(ref _ErrorCount);

        // Always signal the error so subscribers can log the first offending block
        // regardless of the tolerance mode. In strict mode the source additionally
        // exhausts itself so the next NextFrame() call returns null.
        FrameSkipped?.Invoke(this, error);

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            Volatile.Write(ref _Exhausted, true);
        }
    }

    /// <summary>
    /// Creates a frame from resolved data, tracking statistics.
    /// Returns null if interface resolution or frame creation fails.
    /// </summary>
    private Frame? CreateTrackedFrame(ushort sectionIndex, ushort interfaceId, long timestampNanos, byte[] frameData)
    {
        // Enforce maximum frame count — FrameId is int-based
        if (_FrameIndex == int.MaxValue)
        {
            return null;
        }

        if (!TryResolveInterface(sectionIndex, interfaceId, out LinkType linkType, out FrameInterfaceId frameInterfaceId))
        {
            int skipId = _FrameIndex++;
            HandleSkip(new FrameReadErrorEventArgs
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

        HandleSkip(new FrameReadErrorEventArgs
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
    private bool TryReadExact(Span<byte> buffer)
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

    /// <summary>Maximum block body size (256 MiB). Malformed blocks declaring larger sizes
    /// are rejected to prevent unbounded allocation.</summary>
    private const int MaxBufferSize = 256 * 1024 * 1024;

    /// <summary>
    /// Ensures the internal block buffer is at least the given size.
    /// Returns the buffer (may be larger than requested), or <c>null</c> when
    /// <paramref name="minSize"/> exceeds <see cref="MaxBufferSize"/>.
    /// When <c>null</c> is returned the caller must skip the oversized block
    /// via <see cref="HandleSkip"/> and mark the stream exhausted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[]? EnsureBuffer(int minSize)
    {
        if (minSize > MaxBufferSize)
        {
            // Block exceeds the 256 MiB safety cap. Raise a diagnostic so the
            // caller can log the offending block offset, then let the caller
            // exhaust the stream gracefully rather than throwing an exception.
            return null;
        }

        if (_BlockBuffer.Length < minSize)
        {
            // Grow to the next power of two or the requested size, whichever is larger
            int newSize = Math.Min(Math.Max(minSize, _BlockBuffer.Length * 2), MaxBufferSize);
            _BlockBuffer = new byte[newSize];
        }
        return _BlockBuffer;
    }
    #endregion
}

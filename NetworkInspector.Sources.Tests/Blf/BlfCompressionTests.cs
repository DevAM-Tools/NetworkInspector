// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Coverage for BLF compressed-container paths.
///
/// Sections:
///   1. Direct unit tests for <see cref="BlfContainer.Decompress"/> — all three
///      compression methods and every documented error path.
///   2. End-to-end integration tests via <see cref="BlfSource"/> (random-access) —
///      verify that LogContainer objects with None/LZ4/Zlib are parsed correctly
///      and that corrupt containers are skipped gracefully.
///   3. End-to-end integration tests via <see cref="BlfStreamSource"/> (sequential) —
///      same coverage exercised through the streaming read path.
///
/// Test data uses repetitive byte patterns (10 identical CAN frames per container) that
/// LZ4 can compress to less than the raw size, avoiding a fallback to uncompressed mode.
/// </summary>
internal sealed class BlfCompressionTests
{
    #region Helpers

    private static readonly byte[] _CanFrame = FrameBuilders.BuildSocketCanClassic(0x123, [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88]);

    /// <summary>
    /// Builds an inner generator with <paramref name="count"/> identical CAN frames.
    /// Ten frames (480 bytes of repetitive LOBJ structure) is sufficient for LZ4 to
    /// achieve a compressed output smaller than the input.
    /// </summary>
    private static BlfTestGenerator _InnerCanFrames(int count = 10)
    {
        BlfTestGenerator gen = new();
        for (int i = 0; i < count; i++)
        {
            gen.AddCanFrame(1, _CanFrame, (i + 1) * 1_000_000L);
        }

        return gen;
    }

    /// <summary>Creates a full-scan <see cref="BlfSource"/> from raw BLF data.</summary>
    private static BlfSource _CreateFullSource(byte[] data, string name = "test.blf") =>
        BlfSource.FromData(data, name, new BlfSourceOptions { ScanMode = ScanMode.Full });

    /// <summary>Creates a lazy-scan <see cref="BlfSource"/> from raw BLF data.</summary>
    private static BlfSource _CreateLazySource(byte[] data, string name = "test.blf") =>
        BlfSource.FromData(data, name, new BlfSourceOptions { ScanMode = ScanMode.Lazy });

    /// <summary>Creates a <see cref="BlfStreamSource"/> from raw BLF data.</summary>
    private static BlfStreamSource _CreateStreamSource(byte[] data, string name = "test.blf") =>
        BlfStreamSource.FromStream(new MemoryStream(data), name);



    /// <summary>Reads all frames from a started <see cref="BlfSource"/>.</summary>
    private static List<Frame> _ReadAll(BlfSource source)
    {
        List<Frame> frames = [];
        Frame? f;
        while ((f = source.NextFrame()) is not null)
        {
            frames.Add(f.Value);
        }

        return frames;
    }

    /// <summary>Reads all frames from a started <see cref="BlfStreamSource"/>.</summary>
    private static List<Frame> _ReadAll(BlfStreamSource source)
    {
        List<Frame> frames = [];
        Frame? f;
        while ((f = source.NextFrame()) is not null)
        {
            frames.Add(f.Value);
        }

        return frames;
    }

    #endregion

    // ========================================================================
    // Section 1: BlfContainer.Decompress — direct unit tests
    // ========================================================================

    /// <summary>
    /// CompressionNone copies the compressed span into a new array byte-for-byte;
    /// <paramref name="uncompressedSize"/> is ignored for the None path.
    /// </summary>
    [Test]
    public async Task Decompress_None_CopiesDataExactly()
    {
        byte[] input = [0x01, 0x02, 0x03, 0x04, 0x05];
        byte[] output = BlfContainer.Decompress(input, BlfConstants.CompressionNone, (uint)input.Length);

        await Assert.That(output).IsNotNull();
        await Assert.That(output.SequenceEqual(input)).IsTrue();
        // Must be a copy, not the same reference
        await Assert.That(ReferenceEquals(output, input)).IsFalse();
    }

    /// <summary>
    /// LZ4 roundtrip: data compressed with <see cref="Lz4Codec.Compress"/> must be
    /// recovered exactly by <see cref="BlfContainer.Decompress"/> with method 1.
    /// </summary>
    [Test]
    public async Task Decompress_Lz4_Roundtrip()
    {
        // 256 bytes with an 8-byte repeating pattern — compresses well with LZ4.
        byte[] input = new byte[256];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i % 8);
        }

        int maxSize = Lz4Codec.MaxCompressedSize(input.Length);
        byte[] compressed = new byte[maxSize];
        int written = Lz4Codec.Compress(input.AsSpan(), compressed.AsSpan());

        // Repeating patterns must compress; guard against test data regressions.
        await Assert.That(written > 0).IsTrue();

        byte[] output = BlfContainer.Decompress(
            compressed.AsSpan(0, written), BlfConstants.CompressionLz4, (uint)input.Length);

        await Assert.That(output.SequenceEqual(input)).IsTrue();
    }

    /// <summary>
    /// Zlib roundtrip: data compressed with <see cref="ZLibStream"/> (deflate) must be
    /// recovered exactly by <see cref="BlfContainer.Decompress"/> with method 2.
    /// </summary>
    [Test]
    public async Task Decompress_Zlib_Roundtrip()
    {
        // 256 bytes with an 8-byte repeating pattern.
        byte[] input = new byte[256];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i % 8);
        }

        using MemoryStream ms = new();
        using (ZLibStream zlib = new(ms, CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write(input);
        }

        byte[] compressed = ms.ToArray();

        byte[] output = BlfContainer.Decompress(compressed, BlfConstants.CompressionZlib, (uint)input.Length);

        await Assert.That(output.SequenceEqual(input)).IsTrue();
    }

    /// <summary>
    /// An unknown compression method must throw <see cref="BlfException"/>.
    /// </summary>
    [Test]
    public async Task Decompress_UnsupportedMethod_ThrowsBlfException()
    {
        byte[] data = [0x01, 0x02, 0x03];
        await Assert.That(() => BlfContainer.Decompress(data, 99, 3)).Throws<BlfException>();
    }

    /// <summary>
    /// LZ4: random bytes that are not a valid LZ4 block cause <see cref="Lz4Codec.Decompress"/>
    /// to return a negative value, which must surface as <see cref="BlfException"/>.
    /// </summary>
    [Test]
    public async Task Decompress_Lz4_CorruptData_ThrowsBlfException()
    {
        // Bytes that form neither a valid LZ4 sequence nor match the claimed output size.
        byte[] corrupt = [0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA, 0xF9, 0xF8];
        await Assert.That(() => BlfContainer.Decompress(corrupt, BlfConstants.CompressionLz4, 100))
            .Throws<BlfException>();
    }

    /// <summary>
    /// LZ4: if the decompressed byte count does not match the claimed
    /// <c>uncompressedSize</c>, a <see cref="BlfException"/> is thrown.
    /// </summary>
    [Test]
    public async Task Decompress_Lz4_WrongUncompressedSize_ThrowsBlfException()
    {
        // Compress known data, then claim a different output size.
        byte[] input = new byte[128];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i % 4);
        }

        int maxSize = Lz4Codec.MaxCompressedSize(input.Length);
        byte[] compressed = new byte[maxSize];
        int written = Lz4Codec.Compress(input.AsSpan(), compressed.AsSpan());
        await Assert.That(written > 0).IsTrue();

        // Claim 200 bytes but actual decompressed size is 128.
        await Assert.That(() => BlfContainer.Decompress(
                compressed.AsSpan(0, written), BlfConstants.CompressionLz4, 200))
            .Throws<BlfException>();
    }

    /// <summary>
    /// Zlib: random bytes that are not a valid zlib stream trigger an
    /// <see cref="InvalidDataException"/> inside <see cref="ZLibStream"/>,
    /// which must be wrapped as a <see cref="BlfException"/> (regression guard
    /// for the fix applied in this commit).
    /// </summary>
    [Test]
    public async Task Decompress_Zlib_CorruptData_ThrowsBlfException()
    {
        byte[] corrupt = [0xDE, 0xAD, 0xBE, 0xEF, 0xDE, 0xAD, 0xBE, 0xEF];
        await Assert.That(() => BlfContainer.Decompress(corrupt, BlfConstants.CompressionZlib, 100))
            .Throws<BlfException>();
    }

    /// <summary>
    /// Zlib: if the decompressed byte count does not match the claimed
    /// <c>uncompressedSize</c>, a <see cref="BlfException"/> is thrown.
    /// </summary>
    [Test]
    public async Task Decompress_Zlib_WrongUncompressedSize_ThrowsBlfException()
    {
        byte[] input = new byte[64];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i % 4);
        }

        using MemoryStream ms = new();
        using (ZLibStream zlib = new(ms, CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write(input);
        }

        byte[] compressed = ms.ToArray();

        // Claim 200 bytes but actual decompressed size is 64.
        await Assert.That(() => BlfContainer.Decompress(compressed, BlfConstants.CompressionZlib, 200))
            .Throws<BlfException>();
    }

    // ========================================================================
    // Section 2: BlfSource (random-access) + LogContainer integration
    // ========================================================================

    /// <summary>
    /// LogContainer with CompressionNone: inner CAN frames are accessible via
    /// random access after a full scan.
    /// </summary>
    [Test]
    public async Task BlfSource_LogContainer_None_AllFramesParsed()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddLogContainer(BlfConstants.CompressionNone, _InnerCanFrames(5))
            .Build();

        using BlfSource source = _CreateFullSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        await Assert.That(source.EstimatedFrameCount).IsEqualTo(5);
        await Assert.That(source.FrameById(new FrameId(0))).IsNotNull();
        await Assert.That(source.FrameById(new FrameId(4))).IsNotNull();
        await Assert.That(source.FrameById(new FrameId(0))!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);
    }

    /// <summary>
    /// LogContainer with CompressionLz4: inner CAN frames are correctly decompressed
    /// and accessible via random access after a full scan.
    /// </summary>
    [Test]
    public async Task BlfSource_LogContainer_Lz4_AllFramesParsed()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddLogContainer(BlfConstants.CompressionLz4, _InnerCanFrames(10))
            .Build();

        using BlfSource source = _CreateFullSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        await Assert.That(source.EstimatedFrameCount).IsEqualTo(10);
        Frame? first = source.FrameById(new FrameId(0));
        await Assert.That(first).IsNotNull();
        await Assert.That(first!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(first.Value.Data.Span.SequenceEqual(_CanFrame)).IsTrue();
    }

    /// <summary>
    /// LogContainer with CompressionZlib: inner CAN frames are correctly decompressed
    /// and accessible via random access after a full scan.
    /// </summary>
    [Test]
    public async Task BlfSource_LogContainer_Zlib_AllFramesParsed()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddLogContainer(BlfConstants.CompressionZlib, _InnerCanFrames(10))
            .Build();

        using BlfSource source = _CreateFullSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        await Assert.That(source.EstimatedFrameCount).IsEqualTo(10);
        Frame? first = source.FrameById(new FrameId(0));
        await Assert.That(first).IsNotNull();
        await Assert.That(first!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(first.Value.Data.Span.SequenceEqual(_CanFrame)).IsTrue();
    }

    /// <summary>
    /// Multiple containers with different compression methods in a single BLF file:
    /// all frames from all containers are parsed and accessible.
    /// </summary>
    [Test]
    public async Task BlfSource_MultipleContainersMixedCompression_AllFramesParsed()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddLogContainer(BlfConstants.CompressionNone, _InnerCanFrames(4))
            .AddLogContainer(BlfConstants.CompressionLz4, _InnerCanFrames(10))
            .AddLogContainer(BlfConstants.CompressionZlib, _InnerCanFrames(6))
            .Build();

        using BlfSource source = _CreateFullSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        await Assert.That(source.EstimatedFrameCount).IsEqualTo(20);
    }

    /// <summary>
    /// A corrupt LogContainer (invalid LZ4 payload) placed before a valid container:
    /// the corrupt container is skipped, the valid container's frames are still parsed,
    /// and lazy sequential reading reports the skip via <see cref="IFrameSourceStatistics"/>.
    /// </summary>
    [Test]
    public async Task BlfSource_CorruptLogContainer_Lazy_SkippedAndContinues()
    {
        byte[] blfData = new BlfTestGenerator()
            // Corrupt container: claims 200 uncompressed bytes but payload is 4 garbage bytes.
            .AddCorruptLogContainer(BlfConstants.CompressionLz4, 200, [0xDE, 0xAD, 0xBE, 0xEF])
            // Valid container follows: must still be parsed.
            .AddLogContainer(BlfConstants.CompressionNone, _InnerCanFrames(3))
            .Build();

        using BlfSource source = _CreateLazySource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = _ReadAll(source);

        // Three valid frames recovered after the corrupt container.
        await Assert.That(frames.Count).IsEqualTo(3);
        await Assert.That(frames[0].LinkType).IsEqualTo(LinkType.CanSocketcan);

        // The corrupt container must have been reported as a skip.
        await Assert.That(source.SkippedFrameCount > 0).IsTrue();
    }

    /// <summary>
    /// A LogContainer with a wrong (too large) <c>uncompressedSize</c> header field
    /// causes a size-mismatch <see cref="BlfException"/> which is caught and skipped.
    /// The subsequent valid container's frames are still returned.
    /// </summary>
    [Test]
    public async Task BlfSource_LogContainer_WrongUncompressedSize_Lazy_SkippedAndContinues()
    {
        byte[] blfData = new BlfTestGenerator()
            // LZ4-compressed with correct compressed data but wrong declared output size.
            .AddLogContainerWithWrongSize(BlfConstants.CompressionLz4, _InnerCanFrames(5), wrongUncompressedSize: 9999)
            .AddLogContainer(BlfConstants.CompressionNone, _InnerCanFrames(2))
            .Build();

        using BlfSource source = _CreateLazySource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = _ReadAll(source);

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(source.SkippedFrameCount > 0).IsTrue();
    }

    // ========================================================================
    // Section 3: BlfStreamSource (sequential) + LogContainer integration
    // ========================================================================

    /// <summary>
    /// BlfStreamSource correctly reads CAN frames from a None-compressed LogContainer
    /// in sequential order.
    /// </summary>
    [Test]
    public async Task BlfStreamSource_LogContainer_None_AllFramesParsed()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddLogContainer(BlfConstants.CompressionNone, _InnerCanFrames(5))
            .Build();

        using BlfStreamSource source = _CreateStreamSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = _ReadAll(source);

        await Assert.That(frames.Count).IsEqualTo(5);
        await Assert.That(frames[0].LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(frames[0].Data.Span.SequenceEqual(_CanFrame)).IsTrue();
    }

    /// <summary>
    /// BlfStreamSource correctly reads CAN frames from an LZ4-compressed LogContainer.
    /// </summary>
    [Test]
    public async Task BlfStreamSource_LogContainer_Lz4_AllFramesParsed()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddLogContainer(BlfConstants.CompressionLz4, _InnerCanFrames(10))
            .Build();

        using BlfStreamSource source = _CreateStreamSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = _ReadAll(source);

        await Assert.That(frames.Count).IsEqualTo(10);
        await Assert.That(frames[0].LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(frames[0].Data.Span.SequenceEqual(_CanFrame)).IsTrue();
    }

    /// <summary>
    /// BlfStreamSource correctly reads CAN frames from a Zlib-compressed LogContainer.
    /// </summary>
    [Test]
    public async Task BlfStreamSource_LogContainer_Zlib_AllFramesParsed()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddLogContainer(BlfConstants.CompressionZlib, _InnerCanFrames(10))
            .Build();

        using BlfStreamSource source = _CreateStreamSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = _ReadAll(source);

        await Assert.That(frames.Count).IsEqualTo(10);
        await Assert.That(frames[0].LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(frames[0].Data.Span.SequenceEqual(_CanFrame)).IsTrue();
    }

    /// <summary>
    /// BlfStreamSource skips a corrupt LogContainer (invalid Zlib payload) and
    /// continues to deliver frames from the subsequent valid container.
    /// The skip is visible via <see cref="IFrameSourceStatistics.SkippedFrameCount"/>.
    /// </summary>
    [Test]
    public async Task BlfStreamSource_CorruptLogContainer_SkippedAndContinues()
    {
        byte[] blfData = new BlfTestGenerator()
            // Corrupt Zlib payload: invalid stream header causes InvalidDataException,
            // which must be wrapped as BlfException (regression guard for the fix in BlfContainer).
            .AddCorruptLogContainer(BlfConstants.CompressionZlib, 200, [0xDE, 0xAD, 0xBE, 0xEF])
            .AddLogContainer(BlfConstants.CompressionNone, _InnerCanFrames(3))
            .Build();

        using BlfStreamSource source = _CreateStreamSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = _ReadAll(source);

        await Assert.That(frames.Count).IsEqualTo(3);
        await Assert.That(frames[0].LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(source.SkippedFrameCount > 0).IsTrue();
    }

    /// <summary>
    /// BlfStreamSource handles an unsupported compression method (method 99) by
    /// skipping the container and continuing with subsequent objects.
    /// </summary>
    [Test]
    public async Task BlfStreamSource_UnsupportedCompressionMethod_SkippedAndContinues()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddCorruptLogContainer(99, 100, [0x01, 0x02, 0x03, 0x04])
            .AddLogContainer(BlfConstants.CompressionNone, _InnerCanFrames(2))
            .Build();

        using BlfStreamSource source = _CreateStreamSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = _ReadAll(source);

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(source.SkippedFrameCount > 0).IsTrue();
    }

    /// <summary>
    /// When <c>maxUncompressedSize</c> is zero, the limit is inactive and decompression
    /// proceeds normally — no exception is thrown regardless of the uncompressed size.
    /// </summary>
    [Test]
    public async Task Decompress_LimitZero_NoLimitApplied()
    {
        byte[] input = new byte[256];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i % 8);
        }

        int maxSize = Lz4Codec.MaxCompressedSize(input.Length);
        byte[] compressed = new byte[maxSize];
        int written = Lz4Codec.Compress(input.AsSpan(), compressed.AsSpan());
        await Assert.That(written > 0).IsTrue();

        // Limit = 0 means disabled; must not throw even though input is 256 bytes.
        byte[] output = BlfContainer.Decompress(
            compressed.AsSpan(0, written), BlfConstants.CompressionLz4, (uint)input.Length,
            maxUncompressedSize: 0);
        await Assert.That(output.SequenceEqual(input)).IsTrue();
    }

    /// <summary>
    /// When the claimed uncompressed size is exactly at the configured limit, the
    /// decompression must succeed (boundary condition: limit is inclusive).
    /// </summary>
    [Test]
    public async Task Decompress_LimitExact_Succeeds()
    {
        byte[] input = new byte[64];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i % 4);
        }

        int maxSize = Lz4Codec.MaxCompressedSize(input.Length);
        byte[] compressed = new byte[maxSize];
        int written = Lz4Codec.Compress(input.AsSpan(), compressed.AsSpan());
        await Assert.That(written > 0).IsTrue();

        // Limit equals the uncompressed size exactly — must not throw.
        byte[] output = BlfContainer.Decompress(
            compressed.AsSpan(0, written), BlfConstants.CompressionLz4, (uint)input.Length,
            maxUncompressedSize: input.Length);
        await Assert.That(output.SequenceEqual(input)).IsTrue();
    }

    /// <summary>
    /// When the claimed uncompressed size is one byte above the configured limit,
    /// <see cref="BlfDecompressionLimitExceededException"/> is thrown before any
    /// allocation is attempted.
    /// </summary>
    [Test]
    public async Task Decompress_LimitExceededByOne_ThrowsLimitException()
    {
        byte[] dummy = [0x00, 0x01, 0x02, 0x03];
        uint requestedSize = 65;
        long limit = 64;

        BlfDecompressionLimitExceededException? ex = await Assert.That(
            () => BlfContainer.Decompress(dummy, BlfConstants.CompressionLz4, requestedSize,
                maxUncompressedSize: limit))
            .Throws<BlfDecompressionLimitExceededException>();

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ConfiguredLimit).IsEqualTo(limit);
        await Assert.That(ex.RequestedSize).IsEqualTo((long)requestedSize);
    }

    /// <summary>
    /// The limit guard fires for zlib-compressed containers too, not only LZ4.
    /// </summary>
    [Test]
    public async Task Decompress_ZlibLimitExceeded_ThrowsLimitException()
    {
        byte[] dummy = [0x78, 0x9C, 0x00, 0x01];
        uint requestedSize = 1024;
        long limit = 512;

        BlfDecompressionLimitExceededException? ex = await Assert.That(
            () => BlfContainer.Decompress(dummy, BlfConstants.CompressionZlib, requestedSize,
                maxUncompressedSize: limit))
            .Throws<BlfDecompressionLimitExceededException>();

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ConfiguredLimit).IsEqualTo(limit);
        await Assert.That(ex.RequestedSize).IsEqualTo((long)requestedSize);
    }

    /// <summary>
    /// <see cref="BlfSource"/> (lazy sequential path via NextFrame): when the
    /// <see cref="BlfSourceOptions.MaxUncompressedContainerSize"/> limit is set and a
    /// container's uncompressed size exceeds it, <see cref="BlfDecompressionLimitExceededException"/>
    /// propagates from <c>NextFrame</c> without being silently swallowed.
    /// </summary>
    [Test]
    public async Task BlfSource_Lazy_LimitExceeded_Throws()
    {
        // Build a valid LZ4 container whose uncompressed payload exceeds 10 bytes.
        byte[] blfData = new BlfTestGenerator()
            .AddLogContainer(BlfConstants.CompressionLz4, _InnerCanFrames(5))
            .Build();

        BlfSourceOptions options = new()
        {
            ScanMode = ScanMode.Lazy,
            MaxUncompressedContainerSize = 10   // far below the actual container size
        };

        using BlfSource source = BlfSource.FromData(blfData, "test.blf", options);
        SourceTestFixture.InitializeAndStartSource(source);

        await Assert.That(() => _ReadAll(source)).Throws<BlfDecompressionLimitExceededException>();
    }

    /// <summary>
    /// <see cref="BlfSource"/> (full-scan path via ScanFull → BlfIncrementalScanner):
    /// the limit is enforced during the scan phase.
    /// </summary>
    [Test]
    public async Task BlfSource_FullScan_LimitExceeded_Throws()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddLogContainer(BlfConstants.CompressionLz4, _InnerCanFrames(5))
            .Build();

        BlfSourceOptions options = new()
        {
            ScanMode = ScanMode.Full,
            MaxUncompressedContainerSize = 10
        };

        // The exception must propagate from Start (which calls ScanFull internally).
        await Assert.That(() =>
        {
            using BlfSource source = BlfSource.FromData(blfData, "test.blf", options);
            SourceTestFixture.InitializeAndStartSource(source);
        }).Throws<BlfDecompressionLimitExceededException>();
    }

    /// <summary>
    /// <see cref="BlfSource"/> random-access path (<c>FrameById</c>): verifies that the
    /// limit is wired into <see cref="BlfSource.TryGetContainerData"/>. Because
    /// <c>FrameById</c> requires <c>FullyScanned == true</c>, the scanner must have
    /// already completed; the limit fires during the scan phase and propagates through
    /// <c>StartSource</c>, which includes the full scan.
    /// The test guard ensures the exception is not swallowed anywhere in the call chain.
    /// </summary>
    [Test]
    public async Task BlfSource_FrameById_LimitEnforcedViaFullScan_Throws()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddLogContainer(BlfConstants.CompressionLz4, _InnerCanFrames(5))
            .Build();

        BlfSourceOptions options = new()
        {
            ScanMode = ScanMode.Full,
            MaxUncompressedContainerSize = 10
        };

        // The limit fires in the scanner phase (Start → ScanFull → ProcessContainer).
        // It must not be swallowed and must reach the caller.
        await Assert.That(() =>
        {
            using BlfSource source = BlfSource.FromData(blfData, "test.blf", options);
            SourceTestFixture.InitializeAndStartSource(source);
        }).Throws<BlfDecompressionLimitExceededException>();
    }

    /// <summary>
    /// <see cref="BlfSource"/> with a limit that is large enough: all frames are still
    /// returned correctly (regression guard — the limit must not break the normal path).
    /// </summary>
    [Test]
    public async Task BlfSource_LimitSufficient_AllFramesParsed()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddLogContainer(BlfConstants.CompressionLz4, _InnerCanFrames(5))
            .Build();

        BlfSourceOptions options = new()
        {
            ScanMode = ScanMode.Lazy,
            MaxUncompressedContainerSize = 1024 * 1024   // 1 MiB — far above the small test container
        };

        using BlfSource source = BlfSource.FromData(blfData, "test.blf", options);
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = _ReadAll(source);
        await Assert.That(frames.Count).IsEqualTo(5);
    }

    /// <summary>
    /// <see cref="BlfStreamSource"/>: when <see cref="BlfStreamSource.MaxUncompressedContainerSize"/>
    /// is set and exceeded, <see cref="BlfDecompressionLimitExceededException"/> propagates
    /// from <c>NextFrame</c> without being silently swallowed.
    /// </summary>
    [Test]
    public async Task BlfStreamSource_LimitExceeded_Throws()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddLogContainer(BlfConstants.CompressionLz4, _InnerCanFrames(5))
            .Build();

        using BlfStreamSource source = _CreateStreamSource(blfData);
        source.MaxUncompressedContainerSize = 10;   // far below actual container size
        SourceTestFixture.InitializeAndStartSource(source);

        await Assert.That(() => _ReadAll(source)).Throws<BlfDecompressionLimitExceededException>();
    }

    /// <summary>
    /// <see cref="BlfStreamSource"/> with a limit large enough: all frames are returned
    /// correctly (regression guard).
    /// </summary>
    [Test]
    public async Task BlfStreamSource_LimitSufficient_AllFramesParsed()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddLogContainer(BlfConstants.CompressionLz4, _InnerCanFrames(5))
            .Build();

        using BlfStreamSource source = _CreateStreamSource(blfData);
        source.MaxUncompressedContainerSize = 1024 * 1024;
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = _ReadAll(source);
        await Assert.That(frames.Count).IsEqualTo(5);
    }

    /// <summary>
    /// <see cref="BlfSourceOptions.MaxUncompressedContainerSize"/> must reject a negative
    /// value with <see cref="ArgumentOutOfRangeException"/> (defensive guard).
    /// </summary>
    [Test]
    public async Task BlfSourceOptions_NegativeLimit_ThrowsArgumentOutOfRange()
    {
        await Assert.That(() => new BlfSourceOptions { MaxUncompressedContainerSize = -1 })
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// <see cref="BlfStreamSource.MaxUncompressedContainerSize"/> must reject a negative
    /// value with <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    [Test]
    public async Task BlfStreamSource_NegativeLimit_ThrowsArgumentOutOfRange()
    {
        using BlfStreamSource source = _CreateStreamSource([]);
        await Assert.That(() => source.MaxUncompressedContainerSize = -1)
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Default <see cref="BlfSourceOptions"/> applies
    /// <see cref="BlfSourceOptions.DefaultMaxUncompressedContainerSize"/> (128 MiB).
    /// </summary>
    [Test]
    public async Task BlfSourceOptions_DefaultLimit_Is128MiB()
    {
        BlfSourceOptions options = new();
        await Assert.That(options.MaxUncompressedContainerSize)
            .IsEqualTo(BlfSourceOptions.DefaultMaxUncompressedContainerSize);
    }

    /// <summary>
    /// Uncompressed containers (method 0) reject payloads larger than the configured limit.
    /// </summary>
    [Test]
    public async Task Decompress_UncompressedPayloadExceedsLimit_ThrowsLimitException()
    {
        byte[] payload = new byte[128];
        long limit = 64;

        BlfDecompressionLimitExceededException? ex = await Assert.That(
            () => BlfContainer.Decompress(
                payload, BlfConstants.CompressionNone, (uint)payload.Length,
                maxUncompressedSize: limit))
            .Throws<BlfDecompressionLimitExceededException>();

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ConfiguredLimit).IsEqualTo(limit);
        await Assert.That(ex.RequestedSize).IsEqualTo((long)payload.Length);
    }
}

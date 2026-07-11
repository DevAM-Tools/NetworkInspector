// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Pcapng;

/// <summary>
/// Tests for <see cref="PcapStreamSource"/> — stream-based PCAPNG and legacy PCAP reading.
/// Verifies sequential reading, timestamp handling, multi-interface support, and stream lifecycle.
/// </summary>
internal sealed class PcapStreamSourceTests
{
    private static readonly byte[] _SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] _DstMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>Creates a PcapStreamSource from a MemoryStream backed by the given data.</summary>
    private static PcapStreamSource _CreateSource(byte[] data, string uiName = "test.pcapng", bool leaveOpen = false) =>
        PcapStreamSource.FromStream(new MemoryStream(data), uiName, leaveOpen);



    // ========================================================================
    // Single frame
    // ========================================================================

    [Test]
    public async Task SingleFrame_ParsedCorrectly()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0xDE, 0xAD]);

        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.WriteFrame(0, 1_000_000_000, eth);
        byte[] pcapData = writer.Build();

        using PcapStreamSource source = _CreateSource(pcapData);
        // Stream sources always return null for EstimatedFrameCount
        await Assert.That(source.EstimatedFrameCount).IsNull();

        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.Ethernet);
        await Assert.That(frame.Value.Id.Value).IsEqualTo(0);
        await Assert.That(frame.Value.Data.Span.SequenceEqual(eth)).IsTrue();

        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // Multiple frames
    // ========================================================================

    [Test]
    public async Task MultipleFrames_AllReadInOrder()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);

        List<byte[]> expected = [];
        for (int i = 0; i < 5; i++)
        {
            byte[] payload = [(byte)i, (byte)(i + 1), (byte)(i + 2)];
            byte[] eth = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, payload);
            expected.Add(eth);
            writer.WriteFrame(0, (i + 1) * 1_000_000_000L, eth);
        }

        using PcapStreamSource source = _CreateSource(writer.Build());
        SourceTestFixture.InitializeAndStartSource(source);

        for (int i = 0; i < 5; i++)
        {
            Frame? frame = source.NextFrame();
            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.Value.Id.Value).IsEqualTo(i);
            await Assert.That(frame.Value.Data.Span.SequenceEqual(expected[i])).IsTrue();
        }

        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // Timestamps
    // ========================================================================

    [Test]
    public async Task Timestamps_StrictlyIncreasing()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0x00]);

        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);

        long[] timestamps = [1_000_000_000, 2_000_000_000, 3_000_000_000, 4_000_000_000, 5_000_000_000];
        foreach (long ts in timestamps)
        {
            writer.WriteFrame(0, ts, eth);
        }

        using PcapStreamSource source = _CreateSource(writer.Build());
        SourceTestFixture.InitializeAndStartSource(source);

        long prevTs = long.MinValue;
        for (int i = 0; i < 5; i++)
        {
            Frame? frame = source.NextFrame();
            await Assert.That(frame).IsNotNull();
            long ts = frame!.Value.Timestamp.AsNanos;
            await Assert.That(ts > prevTs)
                .IsTrue()
                .Because($"Timestamps must increase: prev={prevTs}, curr={ts}");
            prevTs = ts;
        }
    }

    // ========================================================================
    // Microsecond resolution
    // ========================================================================

    [Test]
    public async Task MicrosecondResolution_TimestampConvertedCorrectly()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0x00]);

        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: false);
        writer.WriteFrame(0, 1_500_000_000, eth);

        using PcapStreamSource source = _CreateSource(writer.Build());
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();

        long ts = frame!.Value.Timestamp.AsNanos;
        // With µs resolution, timestamp should be divisible by 1000
        await Assert.That(ts % 1000).IsEqualTo(0L);
    }

    // ========================================================================
    // Two interfaces
    // ========================================================================

    [Test]
    public async Task TwoInterfaces_FramesDistinguished()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);

        byte[] eth0 = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0x00]);
        byte[] eth1 = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0806, [0x01]);

        writer.WriteFrame(0, 1_000_000_000, eth0);
        writer.WriteFrame(1, 2_000_000_000, eth1);

        using PcapStreamSource source = _CreateSource(writer.Build());

        // Start with fresh registry to verify interface IDs
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);

        Frame? frame0 = source.NextFrame();
        Frame? frame1 = source.NextFrame();

        await Assert.That(frame0).IsNotNull();
        await Assert.That(frame1).IsNotNull();

        // Both should be Ethernet but with different interface IDs
        await Assert.That(frame0!.Value.LinkType).IsEqualTo(LinkType.Ethernet);
        await Assert.That(frame1!.Value.LinkType).IsEqualTo(LinkType.Ethernet);
        await Assert.That(frame0.Value.InterfaceId.Value).IsNotEqualTo(frame1.Value.InterfaceId.Value);

        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // Empty file
    // ========================================================================

    [Test]
    public async Task EmptyFile_NoFrames()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        byte[] pcapData = writer.Build();

        using PcapStreamSource source = _CreateSource(pcapData);
        SourceTestFixture.InitializeAndStartSource(source);

        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // UiName
    // ========================================================================

    [Test]
    public async Task UiName_MatchesProvided()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        byte[] pcapData = writer.Build();

        using PcapStreamSource source = _CreateSource(pcapData, uiName: "MyCapture");
        await Assert.That(source.UiName).IsEqualTo("MyCapture");
    }

    // ========================================================================
    // Stream lifecycle — leaveOpen
    // ========================================================================

    [Test]
    public async Task StreamDisposedWhenNotLeaveOpen()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        byte[] pcapData = writer.Build();

        MemoryStream stream = new(pcapData);
        PcapStreamSource source = PcapStreamSource.FromStream(stream, leaveOpen: false);
        source.Dispose();

        // After disposing with leaveOpen=false, the stream should be disposed
        bool streamDisposed = false;
        try
        {
            stream.ReadByte();
        }
        catch (ObjectDisposedException)
        {
            streamDisposed = true;
        }

        await Assert.That(streamDisposed).IsTrue();
    }

    [Test]
    public async Task StreamNotDisposedWhenLeaveOpen()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        byte[] pcapData = writer.Build();

        using MemoryStream stream = new(pcapData);
        PcapStreamSource source = PcapStreamSource.FromStream(stream, leaveOpen: true);
        source.Dispose();

        // After disposing with leaveOpen=true, the stream should still be usable
        stream.Position = 0;
        bool canRead = stream.ReadByte() >= 0;
        await Assert.That(canRead).IsTrue();
    }

    // ========================================================================
    // Double-Dispose idempotency (C-04 / H-02 regression guard)
    // ========================================================================

    [Test]
    public async Task Dispose_CalledTwice_DoesNotThrow()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        byte[] pcapData = writer.Build();

        PcapStreamSource source = _CreateSource(pcapData);
        SourceTestFixture.InitializeAndStartSource(source);

        source.Dispose();

        // Second Dispose must be idempotent and not throw.
        await Assert.That(() => source.Dispose()).ThrowsNothing();
    }

    // ========================================================================
    // Lifecycle guards — pre-Start contract
    // ========================================================================

    [Test]
    public async Task NextFrame_BeforeStart_ThrowsInvalidOperationException()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        byte[] pcapData = writer.Build();

        using PcapStreamSource source = _CreateSource(pcapData);
        // Calling NextFrame() without Start() must throw — not silently return null.
        await Assert.That(() => source.NextFrame()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task NextFrame_AfterDispose_ThrowsObjectDisposedException()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        byte[] pcapData = writer.Build();

        PcapStreamSource source = _CreateSource(pcapData);
        SourceTestFixture.InitializeAndStartSource(source);
        source.Dispose();

        await Assert.That(() => source.NextFrame()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task IsRunning_FalseBeforeStart()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        byte[] pcapData = writer.Build();

        using PcapStreamSource source = _CreateSource(pcapData);
        await Assert.That(source.IsRunning).IsFalse();
    }

    [Test]
    public async Task IsRunning_TrueAfterStart_FalseAfterDispose()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet);
        byte[] pcapData = writer.Build();

        PcapStreamSource source = _CreateSource(pcapData);
        SourceTestFixture.InitializeAndStartSource(source);

        await Assert.That(source.IsRunning).IsTrue();

        source.Dispose();

        await Assert.That(source.IsRunning).IsFalse();
    }

    // ========================================================================
    // Legacy PCAP — basic read (Finding 3 / Gap A)
    // ========================================================================

    /// <summary>
    /// Builds a minimal well-formed legacy PCAP byte array with a single Ethernet frame.
    /// The global header uses little-endian microsecond timestamps (magic 0xA1B2C3D4).
    /// </summary>
    private static byte[] _BuildLegacyPcap(byte[] frameData, uint snapLen = 65535, bool nanoseconds = false)
    {
        byte[] buf = new byte[24 + 16 + frameData.Length];
        Span<byte> h = buf;

        // Global header (24 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(h, nanoseconds ? 0xA1B2_3C4Du : 0xA1B2_C3D4u); // magic
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], 2);      // major
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], 4);      // minor
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], 0);      // thiszone (GMT)
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], 0);      // sigfigs
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], snapLen); // snaplen
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], 1);      // network = Ethernet

        // Per-packet record header (16 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], 0);                           // ts_sec
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], 0);                           // ts_frac
        BinaryPrimitives.WriteUInt32LittleEndian(h[32..], (uint)frameData.Length);      // incl_len
        BinaryPrimitives.WriteUInt32LittleEndian(h[36..], (uint)frameData.Length);      // orig_len

        // Frame data
        frameData.CopyTo(buf, 40);
        return buf;
    }

    /// <summary>
    /// Builds a PCAPNG stream consisting of a valid SHB + IDB followed by an EPB
    /// whose block_total_length field is set to <paramref name="epbBlockLength"/>
    /// (oversized / corrupt). The stream ends immediately after these 8 EPB header bytes;
    /// no body data is present.
    /// </summary>
    private static byte[] _BuildPcapNgWithOversizedEpb(uint epbBlockLength)
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        byte[] validPart = writer.Build(); // valid SHB + IDB

        // Append only the EPB type (4 bytes) + oversized blockLength (4 bytes).
        // The stream ends here; the source must detect the oversized length before
        // attempting to read the body.
        byte[] result = new byte[validPart.Length + 8];
        validPart.CopyTo(result, 0);
        Span<byte> epb = result.AsSpan(validPart.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(epb, 0x0000_0006u); // EPB block type
        BinaryPrimitives.WriteUInt32LittleEndian(epb[4..], epbBlockLength); // corrupt size
        return result;
    }

    /// <summary>
    /// Builds a raw PCAPNG byte array that looks like the first 12 bytes of an SHB
    /// (type + oversized blockLength + byte-order magic), with no further data.
    /// </summary>
    private static byte[] _BuildOversizedShb(uint blockLength)
    {
        byte[] data = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x0A0D_0D0Au);      // SHB block type
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), blockLength); // oversized
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x1A2B_3C4Du); // BOM (LE)
        return data;
    }

    // ========================================================================
    // OOM guard — PCAPNG block overflow (F1: EPB, F2: SHB)
    // ========================================================================

    /// <summary>
    /// F1 regression: In the unpatched code, an EPB with blockLength > int.MaxValue
    /// (e.g. 3 000 000 000) causes the uint → int cast to produce a negative bodySize
    /// that bypasses EnsureBuffer's cap check and reaches AsSpan(0, negative), throwing
    /// ArgumentOutOfRangeException. The guard must detect the oversized length before
    /// the cast, raise FrameSkipped with CorruptedBlock, and exhaust the source.
    /// </summary>
    [Test]
    public async Task PcapNg_EpbBlockLength_ExceedsIntMaxValue_ExhaustsWithCorruptedBlock()
    {
        // 3 000 000 000 > int.MaxValue (2 147 483 647) and > MaxBufferSize (268 435 456)
        const uint oversizedBlockLength = 3_000_000_000u;
        byte[] data = _BuildPcapNgWithOversizedEpb(oversizedBlockLength);

        using PcapStreamSource source = _CreateSource(data, "corrupt.pcapng");
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;

        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);

        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        // Source must exhaust — no frame returned
        await Assert.That(frame).IsNull();

        // Must have raised exactly one FrameSkipped event with CorruptedBlock
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.CorruptedBlock);
        await Assert.That(source.ErrorCount).IsGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// F1 regression (valid-but-large): An EPB whose blockLength, while within uint range,
    /// exceeds MaxBufferSize + 8 (268 435 464 bytes) must also be rejected with CorruptedBlock
    /// before any allocation attempt.
    /// </summary>
    [Test]
    public async Task PcapNg_EpbBlockLength_ExceedsMaxBufferSize_ExhaustsWithCorruptedBlock()
    {
        // 300 000 000 < int.MaxValue but > MaxBufferSize (268 435 456)
        const uint oversizedBlockLength = 300_000_000u;
        byte[] data = _BuildPcapNgWithOversizedEpb(oversizedBlockLength);

        using PcapStreamSource source = _CreateSource(data, "corrupt.pcapng");
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;

        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);

        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNull();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.CorruptedBlock);
    }

    /// <summary>
    /// F2 regression: An SHB whose block_total_length exceeds MaxBufferSize (268 435 456)
    /// must cause initialization to fail; the first NextFrame() call returns null without
    /// raising any FrameSkipped event.
    /// </summary>
    [Test]
    public async Task PcapNg_ShbBlockLength_ExceedsMaxBufferSize_FailsInitialization()
    {
        // 300 000 000 > MaxBufferSize (268 435 456); well below uint.MaxValue so not an
        // overflow case but an "excessively large" case.
        const uint oversizedBlockLength = 300_000_000u;
        byte[] data = _BuildOversizedShb(oversizedBlockLength);

        using PcapStreamSource source = _CreateSource(data, "corrupt.pcapng");

        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);

        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        // Initialization failed — source exhausted, no frame, no FrameSkipped event
        await Assert.That(frame).IsNull();
        await Assert.That(errors.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Builds a legacy PCAP byte array with a corrupt packet header whose incl_len
    /// is set to the given raw value (not matching the actual payload).
    /// The frame data bytes are padding zeros.
    /// </summary>
    private static byte[] _BuildLegacyPcapWithInclLen(uint inclLen, uint snapLen = 65535)
    {
        byte[] buf = new byte[24 + 16]; // header + packet record header, no payload bytes
        Span<byte> h = buf;

        BinaryPrimitives.WriteUInt32LittleEndian(h, 0xA1B2_C3D4u); // magic (LE, µs)
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], 4);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], snapLen);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], 1); // Ethernet

        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[32..], inclLen); // corrupt incl_len
        BinaryPrimitives.WriteUInt32LittleEndian(h[36..], inclLen);
        return buf;
    }

    [Test]
    public async Task LegacyPcap_SingleFrame_ParsedCorrectly()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0xDE, 0xAD]);
        byte[] data = _BuildLegacyPcap(eth);

        using PcapStreamSource source = _CreateSource(data, "test.pcap");
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.Ethernet);
        await Assert.That(frame.Value.Data.Span.SequenceEqual(eth)).IsTrue();

        await Assert.That(source.NextFrame()).IsNull();
    }

    /// <summary>
    /// Finding 3 / Gap A: incl_len exceeds snaplen — source must fire FrameSkipped
    /// with MalformedHeader and not attempt to allocate incl_len bytes.
    /// </summary>
    [Test]
    public async Task LegacyPcap_InclLen_ExceedsSnapLen_RaisesFrameSkippedAndExhausts()
    {
        // snapLen = 1500; inclLen = 60000 — clearly violates per-packet max.
        const uint snapLen = 1500;
        const uint inclLen = 60000;
        byte[] data = _BuildLegacyPcapWithInclLen(inclLen, snapLen);

        using PcapStreamSource source = _CreateSource(data, "corrupt.pcap");
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;

        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);

        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        // Source must exhaust — no frame returned
        await Assert.That(frame).IsNull();

        // Must have raised exactly one FrameSkipped event with MalformedHeader
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.MalformedHeader);
        await Assert.That(source.ErrorCount).IsGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Finding 3 / Gap A: incl_len > int.MaxValue would cause a negative array-size
    /// cast in the unpatched code. Source must detect this and raise MalformedHeader
    /// before any allocation attempt.
    /// </summary>
    [Test]
    public async Task LegacyPcap_InclLen_ExceedsIntMaxValue_RaisesFrameSkippedAndExhausts()
    {
        // 0x80000001 = 2147483649 > int.MaxValue
        const uint inclLen = 0x8000_0001u;
        byte[] data = _BuildLegacyPcapWithInclLen(inclLen, snapLen: 0); // snapLen=0 → DefaultSnapLength

        using PcapStreamSource source = _CreateSource(data, "corrupt.pcap");
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;

        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);

        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNull();

        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.MalformedHeader);
        await Assert.That(source.ErrorCount).IsGreaterThanOrEqualTo(1);
    }

    // ========================================================================
    // Lifecycle guards — null-registry contract
    // ========================================================================

    [Test]
    public async Task Start_NullRegistry_ThrowsArgumentNullException()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        byte[] pcapData = writer.Build();
        using PcapStreamSource source = _CreateSource(pcapData);
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);

        await Assert.That(() => source.Start(sourceId, null!)).Throws<ArgumentNullException>();
    }
}

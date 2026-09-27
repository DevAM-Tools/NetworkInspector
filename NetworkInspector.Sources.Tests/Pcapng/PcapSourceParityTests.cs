// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Pcapng;

/// <summary>
/// File-source coverage for PCAPNG timestamp offset, captured-length policy, unknown DLT,
/// block alignment, FrameSkipped, packet padding, and section start offsets.
/// </summary>
internal sealed class PcapSourceParityTests
{
    private const ushort _UnknownDlt = 302;

    private static PcapSource _CreateSource(byte[] pcapData) =>
        PcapSource.FromData(pcapData, "test.pcapng");

    [Test]
    public async Task IfTsOffsetSeconds_ShiftsTimestampByWholeSeconds()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: false, timestampOffsetSeconds: 1);
        writer.WriteFrame(0, 0, [0xDE, 0xAD, 0xBE, 0xEF]);

        using PcapSource source = _CreateSource(writer.Build());
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Timestamp.AsNanos).IsEqualTo(1_000_000_000L);
    }

    [Test]
    public async Task BinaryTsResol_ExponentAbove63_IsRejected()
    {
        InterfaceInfo info = new(1, 65535);
        bool supported = info.TrySetTimestampResolution(0x80 | 64);

        await Assert.That(supported).IsFalse();
        await Assert.That(info.HasSupportedTimestampResolution).IsFalse();
    }

    [Test]
    public async Task OrigLenOnDisk_IsIgnored_FrameUsesCapturedBytes()
    {
        byte[] captured = [0x01, 0x02, 0x03, 0x04];
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.WriteFrame(0, 1_000_000_000, captured, originalLength: 512);

        using PcapSource source = _CreateSource(writer.Build());
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Length).IsEqualTo(4);
        await Assert.That(frame.Value.Data.Span.SequenceEqual(captured)).IsTrue();
    }

    [Test]
    public async Task InterfaceName_IfName_BecomesRegistryUiName()
    {
        byte[] payload = [0x00, 0x11, 0x22, 0x33];
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true, name: "eth0");
        writer.WriteFrame(0, 1_000_000_000, payload);

        using PcapSource source = _CreateSource(writer.Build());
        FrameInterfaceRegistry registry = SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        FrameInterfaceInfo? info = registry.Get(frame!.Value.InterfaceId);
        await Assert.That(info).IsNotNull();
        await Assert.That(info!.UiName).IsEqualTo("eth0");
    }

    [Test]
    public async Task UnknownDlt_SkipsFrame_AndRaisesFrameSkipped()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true, rawLinkType: _UnknownDlt);
        writer.WriteFrame(0, 1_000_000_000, [0xAA, 0xBB, 0xCC, 0xDD]);

        using PcapSource source = _CreateSource(writer.Build());
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);

        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNull();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.Other);
        await Assert.That(source.ErrorCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task FirstShb_Length29_ThrowsPcapException()
    {
        byte[] data = PcapNgBlockFactory.TruncatedShbHeader(29);

        await Assert.That(() => PcapSource.FromData(data, "bad-shb.pcapng")).Throws<PcapException>();
    }

    [Test]
    public async Task Epb_BlockLength33_RaisesCorruptedBlockAndExhausts()
    {
        byte[] data = PcapNgBlockFactory.Append(
            PcapNgBlockFactory.ShbAndEthernetIdb(),
            PcapNgBlockFactory.EpbLengthHeader(33));

        using PcapSource source = _CreateSource(data);
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNull();
        await Assert.That(errors.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.CorruptedBlock);
        await Assert.That(source.ErrorCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Epb_TrailingLengthMismatch_RaisesCorruptedBlockAndExhausts()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] epb = PcapNgBlockFactory.EnhancedPacket(0, payload, trailingLength: 32);
        byte[] data = PcapNgBlockFactory.Append(PcapNgBlockFactory.ShbAndEthernetIdb(), epb);

        using PcapSource source = _CreateSource(data);
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNull();
        await Assert.That(errors.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.CorruptedBlock);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task EpbTrailingLengthRoundsUpToLeadingYieldsPacket(int shortfall)
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] sized = PcapNgBlockFactory.EnhancedPacket(0, payload);
        uint leading = BinaryPrimitives.ReadUInt32LittleEndian(sized.AsSpan(4));
        byte[] epb = PcapNgBlockFactory.EnhancedPacket(0, payload, trailingLength: leading - (uint)shortfall);
        byte[] data = PcapNgBlockFactory.Append(PcapNgBlockFactory.ShbAndEthernetIdb(), epb);

        using PcapSource source = _CreateSource(data);
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Length).IsEqualTo(payload.Length);
        await Assert.That(frame.Value.Data.Span.SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task EpbTrailingLengthAboveLeadingExhausts()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] sized = PcapNgBlockFactory.EnhancedPacket(0, payload);
        uint leading = BinaryPrimitives.ReadUInt32LittleEndian(sized.AsSpan(4));
        byte[] epb = PcapNgBlockFactory.EnhancedPacket(0, payload, trailingLength: leading + 4);
        byte[] data = PcapNgBlockFactory.Append(PcapNgBlockFactory.ShbAndEthernetIdb(), epb);

        using PcapSource source = _CreateSource(data);
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNull();
        await Assert.That(errors.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.CorruptedBlock);
    }

    [Test]
    public async Task Epb_UnknownInterface_RaisesFrameSkipped()
    {
        byte[] epb = PcapNgBlockFactory.EnhancedPacket(5, [0x01, 0x02, 0x03, 0x04]);
        byte[] data = PcapNgBlockFactory.Append(PcapNgBlockFactory.ShbAndEthernetIdb(), epb);

        using PcapSource source = _CreateSource(data);
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNull();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.UnresolvedInterface);
        await Assert.That(source.ErrorCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Epb_UnknownInterface_Strict_StopsAfterSkip()
    {
        byte[] epb = PcapNgBlockFactory.EnhancedPacket(5, [0x01, 0x02, 0x03, 0x04]);
        byte[] data = PcapNgBlockFactory.Append(PcapNgBlockFactory.ShbAndEthernetIdb(), epb);

        using PcapSource source = _CreateSource(data);
        source.ErrorTolerance = ErrorToleranceMode.Strict;
        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNull();
        await Assert.That(errors.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UnknownNonPacketBlock_DoesNotRaiseFrameSkipped()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] withDsb = PcapNgBlockFactory.Append(
            PcapNgBlockFactory.ShbAndEthernetIdb(),
            PcapNgBlockFactory.EmptyDsb());
        byte[] data = PcapNgBlockFactory.Append(withDsb, PcapNgBlockFactory.EnhancedPacket(0, payload));

        using PcapSource source = _CreateSource(data);
        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(errors.Count).IsEqualTo(0);
        await Assert.That(frame!.Value.Data.Span.SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task Epb_SixtyOneBytePayload_WithPad_YieldsCapturedLength()
    {
        byte[] payload = new byte[61];
        payload[0] = 0xAB;
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.WriteFrame(0, 1_000_000_000, payload);

        using PcapSource source = _CreateSource(writer.Build());
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Length).IsEqualTo(61);
        await Assert.That(frame.Value.Data.Span[0]).IsEqualTo((byte)0xAB);
    }

    [Test]
    public async Task Epb_CapLenDoesNotFitWithPad_Skips()
    {
        byte[] payload = new byte[61];
        byte[] epb = PcapNgBlockFactory.EnhancedPacket(0, payload, capturedLength: 65);
        byte[] data = PcapNgBlockFactory.Append(PcapNgBlockFactory.ShbAndEthernetIdb(), epb);

        using PcapSource source = _CreateSource(data);
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNull();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.CorruptedBlock);
    }

    [Test]
    public async Task Epb_OptionAfterPad_IsNotIncludedInPayload()
    {
        byte[] payload = new byte[61];
        payload[60] = 0xEE;
        byte[] options = PcapNgBlockFactory.CommentOptionAndEnd("abcd");
        byte[] epb = PcapNgBlockFactory.EnhancedPacket(0, payload, options: options);
        byte[] data = PcapNgBlockFactory.Append(PcapNgBlockFactory.ShbAndEthernetIdb(), epb);

        using PcapSource source = _CreateSource(data);
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Length).IsEqualTo(61);
        await Assert.That(frame.Value.Data.Span[60]).IsEqualTo((byte)0xEE);
        await Assert.That(frame.Value.Data.Span.IndexOf("abcd"u8)).IsEqualTo(-1);
    }

    [Test]
    public async Task SubsequentShb_StartOffset_IsFilePosition()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.WriteFrame(0, 1_000_000_000, [0x01, 0x02, 0x03, 0x04]);
        byte[] firstSection = writer.Build();
        byte[] secondShb = firstSection.AsSpan(0, 28).ToArray();
        byte[] combined = PcapNgBlockFactory.Append(firstSection, secondShb);

        using DataBackend backend = DataBackend.FromMemory(combined);
        IncrementalScanner scanner = new(backend, combined.Length);
        while (true)
        {
            if (scanner.NextFrame(out _))
            {
                continue;
            }

            if (scanner.IsExhausted)
            {
                break;
            }
        }

        PcapNgFormat format = (PcapNgFormat)scanner.Format;
        await Assert.That(format.Sections.Count).IsEqualTo(2);
        await Assert.That(format.Sections[0].StartOffset).IsEqualTo(0L);
        await Assert.That(format.Sections[1].StartOffset).IsEqualTo((long)firstSection.Length);
    }

    [Test]
    public async Task EnhancedPacketBlock_TryParse_ReadsCapLen()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.WriteFrame(0, 1_000_000_000, [0x01, 0x02, 0x03, 0x04]);
        byte[] file = writer.Build();
        uint idbLength = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(32));
        int epbOffset = 28 + (int)idbLength;

        bool parsed = EnhancedPacketBlock.TryParse(file.AsSpan(epbOffset), out EnhancedPacketBlock epb, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(epb.CapturedLength.Value).IsEqualTo(4u);
        await Assert.That(epb.OriginalLength.Value).IsEqualTo(4u);
    }

    [Test]
    public async Task MicrosecondTimestamp_OverflowSaturatesToLongMaxValue()
    {
        InterfaceInfo info = new(1, 65535);

        long overflow = info.TimestampToNanos((ulong)(long.MaxValue / 1000 + 1));
        long boundary = info.TimestampToNanos((ulong)(long.MaxValue / 1000));

        await Assert.That(overflow).IsEqualTo(long.MaxValue);
        await Assert.That(boundary).IsEqualTo((long.MaxValue / 1000) * 1000);
    }

    [Test]
    public async Task TryReadEpb_FullBlockAndBodyOmitHeader_YieldSamePayload()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.WriteFrame(0, 1_000_000_000, payload);
        byte[] file = writer.Build();
        uint idbLength = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(32));
        int epbOffset = 28 + (int)idbLength;
        ReadOnlySpan<byte> epb = file.AsSpan(epbOffset);
        uint blockLength = BinaryPrimitives.ReadUInt32LittleEndian(epb[4..]);

        InterfaceInfo iface = new(1, 65535);
        bool resolutionSupported = iface.TrySetTimestampResolution(9);
        SectionInfo section = new(false, -1, 0);
        section.AddInterface(iface);

        ScanPacketResult fullResult = PcapngPacketBlocks.TryReadEpb(
            epb,
            bodyOmits8ByteHeader: false,
            swap: false,
            blockLength,
            section,
            out EpbFields fullFields);
        int fullCaptured = fullFields.CapturedLength;
        long fullTimestamp = fullFields.TimestampNanos;
        byte[] fullPayload = fullFields.PacketData.ToArray();

        ScanPacketResult bodyResult = PcapngPacketBlocks.TryReadEpb(
            epb[8..],
            bodyOmits8ByteHeader: true,
            swap: false,
            blockLength,
            section,
            out EpbFields bodyFields);
        int bodyCaptured = bodyFields.CapturedLength;
        long bodyTimestamp = bodyFields.TimestampNanos;
        byte[] bodyPayload = bodyFields.PacketData.ToArray();

        await Assert.That(resolutionSupported).IsTrue();
        await Assert.That(fullResult).IsEqualTo(ScanPacketResult.Frame);
        await Assert.That(bodyResult).IsEqualTo(ScanPacketResult.Frame);
        await Assert.That(fullCaptured).IsEqualTo(4);
        await Assert.That(bodyCaptured).IsEqualTo(4);
        await Assert.That(fullTimestamp).IsEqualTo(1_000_000_000L);
        await Assert.That(bodyTimestamp).IsEqualTo(1_000_000_000L);
        await Assert.That(fullPayload.AsSpan().SequenceEqual(payload)).IsTrue();
        await Assert.That(bodyPayload.AsSpan().SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task NanosecondTicksAboveInt64Max_SaturateAtMax()
    {
        InterfaceInfo info = new(1, 65535);
        bool supported = info.TrySetTimestampResolution(9);

        await Assert.That(supported).IsTrue();
        long nanos = info.TimestampToNanos((ulong)long.MaxValue + 1UL);
        await Assert.That(nanos).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task BinaryExponent63_FractionStaysNonNegative()
    {
        InterfaceInfo info = new(1, 65535);
        bool supported = info.TrySetTimestampResolution(0x80 | 63);

        await Assert.That(supported).IsTrue();
        long nanos = info.TimestampToNanos(1UL << 62);
        await Assert.That(nanos).IsEqualTo(500_000_000L);
    }

    [Test]
    public async Task DecimalExponent20_SkipsPacket()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, rawTimestampResolution: 20);
        writer.WriteFrame(0, 0, [0x01, 0x02, 0x03, 0x04]);

        using PcapSource source = _CreateSource(writer.Build());
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNull();
        await Assert.That(errors.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(errors[0].Message).IsEqualTo("unsupported if_tsresol");
    }

    [Test]
    public async Task EpbLeadingLength33_WithPaddedBytes_ReturnsPacketAndTheNextBlock()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] first = PcapNgBlockFactory.EnhancedPacket(0, payload);
        uint shortLength = (uint)first.Length - 3u;
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(4), shortLength);
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(first.Length - 4), shortLength);
        byte[] secondPayload = [0xAA];
        byte[] second = PcapNgBlockFactory.EnhancedPacket(0, secondPayload);
        byte[] data = PcapNgBlockFactory.Append(
            PcapNgBlockFactory.Append(PcapNgBlockFactory.ShbAndEthernetIdb(), first),
            second);

        using PcapSource source = _CreateSource(data);
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();
        Frame? next = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Data.Span.SequenceEqual(payload)).IsTrue();
        await Assert.That(next).IsNotNull();
        await Assert.That(next!.Value.Data.Span.SequenceEqual(secondPayload)).IsTrue();
    }

    [Test]
    public async Task BlockLongerThan16Mib_IsReadByFileAndStream()
    {
        uint maxBlockSize = PcapConstants.MaxBlockSize;
        await Assert.That(maxBlockSize).IsEqualTo(134_348_832u);

        byte[] prefix = PcapNgBlockFactory.ShbAndEthernetIdb();
        const int blockLength = (16 * 1024 * 1024) + 8;
        byte[] file = new byte[prefix.Length + blockLength];
        prefix.CopyTo(file, 0);
        byte[] expected = [0x11, 0x22, 0x33, 0x44];
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(prefix.Length), PcapNgBlockFactory.EpbType);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(prefix.Length + 4), (uint)blockLength);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(prefix.Length + 20), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(prefix.Length + 24), 4);
        expected.CopyTo(file, prefix.Length + 28);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(file.Length - 4), (uint)blockLength);

        using (PcapSource source = PcapSource.FromData(file, "wide.pcapng"))
        {
            SourceTestFixture.InitializeAndStartSource(source);
            Frame? frame = source.NextFrame();
            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.Value.Length).IsEqualTo(4);
            await Assert.That(frame.Value.Data.Span.SequenceEqual(expected)).IsTrue();
        }

        using PcapStreamSource stream = PcapStreamSource.FromStream(new MemoryStream(file), "wide.pcapng");
        SourceTestFixture.InitializeAndStartSource(stream);
        Frame? streamed = stream.NextFrame();
        await Assert.That(streamed).IsNotNull();
        await Assert.That(streamed!.Value.Length).IsEqualTo(4);
    }
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Pcapng;

/// <summary>
/// Stream-source coverage matching the file-source PCAPNG parity cases.
/// </summary>
internal sealed class PcapStreamSourceParityTests
{
    private const ushort _UnknownDlt = 302;

    private static PcapStreamSource _CreateSource(byte[] data) =>
        PcapStreamSource.FromStream(new MemoryStream(data), "test.pcapng");

    [Test]
    public async Task IfTsOffsetSeconds_ShiftsTimestampByWholeSeconds()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: false, timestampOffsetSeconds: 1);
        writer.WriteFrame(0, 0, [0xDE, 0xAD, 0xBE, 0xEF]);

        using PcapStreamSource source = _CreateSource(writer.Build());
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Timestamp.AsNanos).IsEqualTo(1_000_000_000L);
    }

    [Test]
    public async Task OrigLenOnDisk_IsIgnored_FrameUsesCapturedBytes()
    {
        byte[] captured = [0x01, 0x02, 0x03, 0x04];
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        writer.WriteFrame(0, 1_000_000_000, captured, originalLength: 512);

        using PcapStreamSource source = _CreateSource(writer.Build());
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Length).IsEqualTo(4);
        await Assert.That(frame.Value.Data.Span.SequenceEqual(captured)).IsTrue();
    }

    [Test]
    public async Task UnknownDlt_SkipsFrame_DoesNotForgeEthernet()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true, rawLinkType: _UnknownDlt);
        writer.WriteFrame(0, 1_000_000_000, [0xAA, 0xBB, 0xCC, 0xDD]);

        using PcapStreamSource source = _CreateSource(writer.Build());
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
    public async Task FirstShb_Length29_RoundedReadDoesNotFit_ReturnsNull()
    {
        byte[] data = PcapNgBlockFactory.TruncatedShbHeader(29);
        using PcapStreamSource source = _CreateSource(data);
        SourceTestFixture.InitializeAndStartSource(source);

        // 29 rounds to 32. The stream only has the 12-byte prefix, so initialization fails closed.
        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNull();
    }

    [Test]
    public async Task Epb_BlockLength33_RaisesCorruptedBlockAndExhausts()
    {
        byte[] data = PcapNgBlockFactory.Append(
            PcapNgBlockFactory.ShbAndEthernetIdb(),
            PcapNgBlockFactory.EpbLengthHeader(33));

        using PcapStreamSource source = _CreateSource(data);
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNull();
        await Assert.That(errors.Count).IsGreaterThanOrEqualTo(1);
        // 33 rounds to 36. The fixture does not contain those bytes, so the body read ends the stream.
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.TruncatedStream);
        await Assert.That(source.ErrorCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Epb_TrailingLengthMismatch_RaisesCorruptedBlockAndExhausts()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] epb = PcapNgBlockFactory.EnhancedPacket(0, payload, trailingLength: 32);
        byte[] data = PcapNgBlockFactory.Append(PcapNgBlockFactory.ShbAndEthernetIdb(), epb);

        using PcapStreamSource source = _CreateSource(data);
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

        using PcapStreamSource source = _CreateSource(data);
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

        using PcapStreamSource source = _CreateSource(data);
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

        using PcapStreamSource source = _CreateSource(data);
        source.ErrorTolerance = ErrorToleranceMode.Tolerant;
        List<FrameReadErrorEventArgs> errors = [];
        source.FrameSkipped += (_, e) => errors.Add(e);
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNull();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Kind).IsEqualTo(FrameReadErrorKind.UnresolvedInterface);
    }

    [Test]
    public async Task Epb_CapLenDoesNotFitWithPad_Skips()
    {
        byte[] payload = new byte[61];
        byte[] epb = PcapNgBlockFactory.EnhancedPacket(0, payload, capturedLength: 65);
        byte[] data = PcapNgBlockFactory.Append(PcapNgBlockFactory.ShbAndEthernetIdb(), epb);

        using PcapStreamSource source = _CreateSource(data);
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

        using PcapStreamSource source = _CreateSource(data);
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Length).IsEqualTo(61);
        await Assert.That(frame.Value.Data.Span.IndexOf("abcd"u8)).IsEqualTo(-1);
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

        using PcapStreamSource source = _CreateSource(data);
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();
        Frame? next = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Data.Span.SequenceEqual(payload)).IsTrue();
        await Assert.That(next).IsNotNull();
        await Assert.That(next!.Value.Data.Span.SequenceEqual(secondPayload)).IsTrue();
    }

    [Test]
    public async Task InterfaceName_IfName_BecomesRegistryUiName()
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true, name: "eth0");
        writer.WriteFrame(0, 1_000_000_000, [0x00, 0x11, 0x22, 0x33]);

        using PcapStreamSource source = _CreateSource(writer.Build());
        FrameInterfaceRegistry registry = SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        FrameInterfaceInfo? info = registry.Get(frame!.Value.InterfaceId);
        await Assert.That(info).IsNotNull();
        await Assert.That(info!.UiName).IsEqualTo("eth0");
    }
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Blf;

/// <summary>
/// Tests for the <see cref="BlfExporter"/> — validates file header, Ethernet and CAN frames,
/// timestamps, and structural integrity.
/// </summary>
internal sealed class BlfExporterTests
{
    [Test]
    public async Task SingleEthernetFrameProducesValidBlf()
    {
        using MemoryStream stream = new();

        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(stream).Build();

        byte[] frameData = FrameGenerators.BuildEthernetIpv4UdpFrame(32);
        Frame frame = TestHarness.CreateFrame(0, 1_000_000_000, frameData);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.FromBytes(stream.ToArray());
        await Assert.That(verifier.HasValidHeader).IsTrue();
        await Assert.That(verifier.ObjectCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task MultipleFramesAllWritten()
    {
        using MemoryStream stream = new();

        const int count = 20;
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(stream).Build();

        Frame[] frames = PacketGenerators.CreateEthernetFrames(count);
        foreach (Frame frame in frames)
        {
            exporter.OnFrame(frame);
        }

        exporter.OnFinish();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.FromBytes(stream.ToArray());
        await Assert.That(verifier.HasValidHeader).IsTrue();
        // Each frame produces at least one BLF object, plus potentially containers
        await Assert.That(verifier.ObjectCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task CanClassicFrameProducesValidBlf()
    {
        using MemoryStream stream = new();

        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(stream).Build();

        byte[] canData = SocketCanGenerators.BuildCanClassic(0x123, [0x01, 0x02, 0x03, 0x04]);
        Frame frame = TestHarness.CreateFrame(
            0, 1_000_000_000, canData, LinkType.CanSocketcan);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.FromBytes(stream.ToArray());
        await Assert.That(verifier.HasValidHeader).IsTrue();
        await Assert.That(verifier.ObjectCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task CanFdFrameProducesValidBlf()
    {
        using MemoryStream stream = new();

        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(stream).Build();

        byte[] canData = SocketCanGenerators.BuildCanFd(
            0x456, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08], brs: true);
        Frame frame = TestHarness.CreateFrame(
            0, 1_000_000_000, canData, LinkType.CanSocketcan);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.FromBytes(stream.ToArray());
        await Assert.That(verifier.HasValidHeader).IsTrue();
    }

    [Test]
    public async Task FrameCountTracksCorrectly()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();

        const int count = 10;
        Frame[] frames = PacketGenerators.CreateEthernetFrames(count);
        foreach (Frame frame in frames)
        {
            exporter.OnFrame(frame);
        }

        await Assert.That(exporter.FrameCount).IsEqualTo(count);
        exporter.OnFinish();
    }

    [Test]
    public async Task FlexRayFrameProducesValidBlf()
    {
        using MemoryStream stream = new();

        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(stream).Build();

        byte[] frData = FlexRayGenerators.BuildFlexRayFrame(
            0, 10, 3, 0xABCD, [0xDE, 0xAD, 0xBE, 0xEF], sync: true);
        Frame frame = TestHarness.CreateFrame(0, 1_000_000_000, frData, LinkType.Flexray);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.FromBytes(stream.ToArray());
        await Assert.That(verifier.HasValidHeader).IsTrue();
        await Assert.That(verifier.ObjectCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task FlexRayFrameFrameCountTracked()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();

        const int count = 5;
        for (int i = 0; i < count; i++)
        {
            byte[] frData = FlexRayGenerators.BuildFlexRayFrame(
                (byte)(i % 2), (ushort)(i + 1), (byte)i, 0x1234, [0x01, 0x02]);
            Frame frame = TestHarness.CreateFrame(
                i, (long)(i + 1) * 1_000_000_000, frData, LinkType.Flexray);
            exporter.OnFrame(frame);
        }

        await Assert.That(exporter.FrameCount).IsEqualTo(count);
        exporter.OnFinish();
    }

    [Test]
    public async Task LinFrameProducesValidBlf()
    {
        using MemoryStream stream = new();

        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(stream).Build();

        byte[] linData = LinGenerators.BuildLinFrame(0x05, [0x11, 0x22, 0x33], checksum: 0x42);
        Frame frame = TestHarness.CreateFrame(0, 1_000_000_000, linData, LinkType.Lin);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.FromBytes(stream.ToArray());
        await Assert.That(verifier.HasValidHeader).IsTrue();
        await Assert.That(verifier.ObjectCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task LinFrameFrameCountTracked()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();

        const int count = 8;
        for (int i = 0; i < count; i++)
        {
            byte[] linData = LinGenerators.BuildLinFrame((byte)(i % 64), [0xAA, 0xBB]);
            Frame frame = TestHarness.CreateFrame(
                i, (long)(i + 1) * 1_000_000_000, linData, LinkType.Lin);
            exporter.OnFrame(frame);
        }

        await Assert.That(exporter.FrameCount).IsEqualTo(count);
        exporter.OnFinish();
    }

    [Test]
    public async Task MixedFrameTypesAllWritten()
    {
        using MemoryStream stream = new();

        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(stream).Build();

        // Ethernet frame
        byte[] ethData = FrameGenerators.BuildEthernetIpv4UdpFrame(16);
        exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000, ethData));

        // CAN frame
        byte[] canData = SocketCanGenerators.BuildCanClassic(0x100, [0x01, 0x02]);
        exporter.OnFrame(TestHarness.CreateFrame(
            1, 2_000_000_000, canData, LinkType.CanSocketcan));

        // FlexRay frame
        byte[] frData = FlexRayGenerators.BuildFlexRayFrame(
            0, 10, 3, 0xABCD, [0xDE, 0xAD, 0xBE, 0xEF]);
        exporter.OnFrame(TestHarness.CreateFrame(
            2, 3_000_000_000, frData, LinkType.Flexray));

        // LIN frame
        byte[] linData = LinGenerators.BuildLinFrame(0x05, [0x11, 0x22, 0x33], checksum: 0x42);
        exporter.OnFrame(TestHarness.CreateFrame(
            3, 4_000_000_000, linData, LinkType.Lin));

        exporter.OnFinish();

        BlfStructuralVerifier verifier = BlfStructuralVerifier.FromBytes(stream.ToArray());
        await Assert.That(verifier.HasValidHeader).IsTrue();
        // ObjectCount counts top-level BLF objects (containers), not individual frames
        await Assert.That(verifier.ObjectCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(exporter.FrameCount).IsEqualTo(4);
    }

    // ========================================================================
    // UiName / Description
    // ========================================================================

    [Test]
    public async Task EthernetPayloadLongerThanUInt16IsSkippedAsMalformed()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        // dst(6)+src(6)+ethertype(2) + payload larger than ushort.MaxValue
        byte[] oversized = new byte[14 + ushort.MaxValue + 1];
        oversized.AsSpan(12, 2).Clear();
        BinaryPrimitives.WriteUInt16BigEndian(oversized.AsSpan(12), 0x0800);

        Frame frame = TestHarness.CreateFrame(0, 1_000_000_000L, oversized, LinkType.Ethernet);
        bool cont = exporter.OnFrame(frame);
        exporter.OnFinish();

        await Assert.That(cont).IsTrue();
        await Assert.That(exporter.FrameCount).IsEqualTo(0);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(exporter.ErrorCount).IsEqualTo(1);
    }

    [Test]
    public async Task CanXlFrameWritesType139AndReimports()
    {
        using MemoryStream stream = new();

        byte[] xlData = SocketCanGenerators.BuildCanXl(
            0x01, [0xAA, 0xBB, 0xCC, 0xDD], vcid: 0x1A, sdt: 0x05, acceptanceField: 0x11);

        using (BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(stream).Build())
        {
            exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
            bool accepted = exporter.OnFrame(
                TestHarness.CreateFrame(0, 1_000_000_000L, xlData, LinkType.CanSocketcan));
            exporter.OnFinish();

            await Assert.That(accepted).IsTrue();
            await Assert.That(exporter.SkippedCount).IsEqualTo(0);
            await Assert.That(exporter.FrameCount).IsEqualTo(1);
        }

        BlfStructuralVerifier verifier = BlfStructuralVerifier.FromBytes(stream.ToArray());
        await Assert.That(verifier.HasValidHeader).IsTrue();
        await Assert.That(verifier.ObjectCount).IsGreaterThanOrEqualTo(1);

        using BlfSource source = BlfSource.FromData(stream.ToArray(), "output.blf");
        _StartSource(source);
        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(frame.Value.Data.ToArray()).IsEquivalentTo(xlData);
        await Assert.That(source.NextFrame()).IsNull();
    }

    [Test]
    public async Task CanXlFrameStrictModeWritesObject()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(ms).Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        byte[] xlData = SocketCanGenerators.BuildCanXl(0x01, [0xAA, 0xBB, 0xCC, 0xDD]);
        bool accepted = exporter.OnFrame(
            TestHarness.CreateFrame(0, 1_000_000_000L, xlData, LinkType.CanSocketcan));
        exporter.OnFinish();

        await Assert.That(accepted).IsTrue();
        await Assert.That(exporter.HasErrors).IsFalse();
        await Assert.That(exporter.FrameCount).IsEqualTo(1);
        await Assert.That(exporter.IsFinished).IsTrue();
    }

    [Test]
    public async Task CanClassicFrameExtendedIdDoesNotSetFlagsByteEff()
    {
        using MemoryStream stream = new();

        byte[] canData = SocketCanGenerators.BuildCanClassic(0x123, [0x01], extended: true);
        using (BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build())
        {
            exporter.OnFrame(TestHarness.CreateFrame(
                0, 1_000_000_000L, canData, LinkType.CanSocketcan));
            exporter.OnFinish();
        }

        using BlfSource source = BlfSource.FromData(stream.ToArray(), "output.blf");
        _StartSource(source);
        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        byte[] data = frame!.Value.Data.ToArray();
        uint id = BinaryPrimitives.ReadUInt32BigEndian(data);
        await Assert.That(id & 0x8000_0000u).IsEqualTo(0x8000_0000u);
        await Assert.That(id & 0x1FFF_FFFFu).IsEqualTo(0x123u);
    }

    [Test]
    public async Task CanFdFrameWritesType101AndReimports()
    {
        using MemoryStream stream = new();

        byte[] fdData = SocketCanGenerators.BuildCanFd(
            0x456, [0x01, 0x02, 0x03, 0x04], extended: true, brs: true);

        using (BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build())
        {
            exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
            bool accepted = exporter.OnFrame(
                TestHarness.CreateFrame(0, 1_000_000_000L, fdData, LinkType.CanSocketcan));
            exporter.OnFinish();

            await Assert.That(accepted).IsTrue();
            await Assert.That(exporter.SkippedCount).IsEqualTo(0);
            await Assert.That(exporter.FrameCount).IsEqualTo(1);
        }

        // Inner LOBJs sit in an uncompressed LOG_CONTAINER; scan file bytes for Type 101.
        byte[] fileBytes = stream.ToArray();
        bool foundType101 = false;
        for (int i = 0; i + 16 <= fileBytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(i)) != 0x4A424F4C)
            {
                continue;
            }

            uint objectType = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(i + 12));
            if (objectType == BlfConstants.ObjTypeCanFdMessage64)
            {
                foundType101 = true;
                break;
            }
        }

        await Assert.That(foundType101).IsTrue();

        using BlfSource source = BlfSource.FromData(stream.ToArray(), "output.blf");
        _StartSource(source);
        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);
        byte[] data = frame!.Value.Data.ToArray();
        uint id = BinaryPrimitives.ReadUInt32BigEndian(data);
        await Assert.That(id & 0x8000_0000u).IsEqualTo(0x8000_0000u);
        await Assert.That(id & 0x1FFF_FFFFu).IsEqualTo(0x456u);
        await Assert.That(data[4]).IsEqualTo((byte)4);
        await Assert.That(data[5] & 0x04).IsEqualTo((byte)0x04);
        await Assert.That(data.AsSpan(8, 4).ToArray()).IsEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
    }

    [Test]
    public async Task EthernetFrameWithoutChannelPropertyWritesChannelOne()
    {
        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(ms)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build();

        byte[] frameData = FrameGenerators.BuildEthernetIpv4UdpFrame(32);
        Frame frame = TestHarness.CreateFrame(0, 1_000_000_000, frameData);
        exporter.OnFrame(frame);
        exporter.OnFinish();

        using BlfSource source = BlfSource.FromData(
            ms.ToArray(), "ch0.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
        Frame? read = source.NextFrame();

        await Assert.That(read).IsNotNull();
        FrameInterfaceInfo? iface = registry.Get(read!.Value.InterfaceId);
        await Assert.That(iface).IsNotNull();
        await Assert.That((long)iface!.Properties[BlfInterfacePropertyKeys.Channel]).IsEqualTo(1L);
    }

    [Test]
    public async Task EthernetFrameWithNonZeroChannelPreservesChannel()
    {
        Stack stack = TestHarness.GetStack();
        FrameInterfaceRegistry outRegistry = stack.FrameInterfaceRegistry;
        if (outRegistry.SourceCount == 0)
        {
            outRegistry.RegisterSource(TestHarness.CreateNullFrameSource());
        }

        FrameInterfaceId ifId = outRegistry.Register(
            new FrameSourceId(0),
            "eth-ch7",
            null,
            LinkType.Ethernet,
            new Dictionary<string, object> { [BlfInterfacePropertyKeys.Channel] = 7L });
        byte[] frameData = FrameGenerators.BuildEthernetIpv4UdpFrame(32);
        Frame frame = Frame.Create(
            0,
            Timestamp.FromNanos(1_000_000_000),
            frameData,
            LinkType.Ethernet,
            ifId,
            outRegistry).Value;

        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(ms)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build();
        exporter.OnFrame(frame);
        exporter.OnFinish();

        using BlfSource source = BlfSource.FromData(
            ms.ToArray(), "ch7.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });
        FrameInterfaceRegistry inRegistry = new();
        source.Start(inRegistry.RegisterSource(source), inRegistry);
        Frame? read = source.NextFrame();

        await Assert.That(read).IsNotNull();
        FrameInterfaceInfo? iface = inRegistry.Get(read!.Value.InterfaceId);
        await Assert.That(iface).IsNotNull();
        await Assert.That((long)iface!.Properties[BlfInterfacePropertyKeys.Channel]).IsEqualTo(7L);
    }

    [Test]
    public async Task EthernetChannelZeroAndHardwareChannelRoundTrip()
    {
        Stack stack = TestHarness.GetStack();
        FrameInterfaceRegistry outRegistry = stack.FrameInterfaceRegistry;
        if (outRegistry.SourceCount == 0)
        {
            outRegistry.RegisterSource(TestHarness.CreateNullFrameSource());
        }

        FrameInterfaceId ifId = outRegistry.Register(
            new FrameSourceId(0),
            "eth-ch0",
            null,
            LinkType.Ethernet,
            new Dictionary<string, object>
            {
                [BlfInterfacePropertyKeys.Channel] = 0L,
                [BlfInterfacePropertyKeys.HardwareChannel] = (ushort)2,
            });
        byte[] frameData = FrameGenerators.BuildEthernetIpv4UdpFrame(32);
        Frame frame = Frame.Create(
            0,
            Timestamp.FromNanos(1_000_000_000),
            frameData,
            LinkType.Ethernet,
            ifId,
            outRegistry).Value;

        using MemoryStream ms = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(ms)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build();
        exporter.OnFrame(frame);
        exporter.OnFinish();

        using BlfSource source = BlfSource.FromData(
            ms.ToArray(), "ch0hw.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });
        FrameInterfaceRegistry inRegistry = new();
        source.Start(inRegistry.RegisterSource(source), inRegistry);
        Frame? read = source.NextFrame();

        await Assert.That(read).IsNotNull();
        FrameInterfaceInfo? iface = inRegistry.Get(read!.Value.InterfaceId);
        await Assert.That(iface).IsNotNull();
        await Assert.That((long)iface!.Properties[BlfInterfacePropertyKeys.Channel]).IsEqualTo(0L);
        await Assert.That((ushort)iface.Properties[BlfInterfacePropertyKeys.HardwareChannel]).IsEqualTo((ushort)2);
    }

    [Test]
    public async Task LinSleepEventWritesType20()
    {
        using MemoryStream stream = new();
        byte[] linEvent = new byte[12];
        linEvent[0] = 1;
        linEvent[4] = 3 << 2;
        linEvent[8] = 0xB0;
        linEvent[9] = 0xB0;
        linEvent[11] = 0x01;

        using (BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build())
        {
            exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
            bool accepted = exporter.OnFrame(
                TestHarness.CreateFrame(0, 1_000_000_000L, linEvent, LinkType.Lin));
            exporter.OnFinish();

            await Assert.That(accepted).IsTrue();
            await Assert.That(exporter.SkippedCount).IsEqualTo(0);
            await Assert.That(exporter.FrameCount).IsEqualTo(1);
        }

        await Assert.That(_FileContainsObjectType(stream.ToArray(), BlfConstants.ObjTypeLinMessage2)).IsFalse();
        await Assert.That(_FileContainsObjectType(stream.ToArray(), BlfConstants.ObjTypeLinSleep)).IsTrue();

        using BlfSource source = BlfSource.FromData(stream.ToArray(), "output.blf");
        _StartSource(source);
        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Data.Span[11]).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task LinWakeupEventWritesType62()
    {
        using MemoryStream stream = new();
        byte[] linEvent = new byte[12];
        linEvent[0] = 1;
        linEvent[4] = 3 << 2;
        linEvent[8] = 0xB0;
        linEvent[9] = 0xB0;
        linEvent[11] = 0x04;

        using (BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build())
        {
            exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, linEvent, LinkType.Lin));
            exporter.OnFinish();
            await Assert.That(exporter.FrameCount).IsEqualTo(1);
        }

        await Assert.That(_FileContainsObjectType(stream.ToArray(), BlfConstants.ObjTypeLinWakeup2)).IsTrue();

        using BlfSource source = BlfSource.FromData(stream.ToArray(), "output.blf");
        _StartSource(source);
        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Data.Span[11]).IsEqualTo((byte)0x04);
    }

    [Test]
    public async Task LinErrorFrameWritesType60()
    {
        using MemoryStream stream = new();
        byte[] linError = LinGenerators.BuildLinFrame(0x05, [0x11], checksum: 0x42, errors: 0x08);

        using (BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build())
        {
            exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;
            exporter.OnFrame(TestHarness.CreateFrame(
                0, 1_000_000_000L, linError, LinkType.Lin));
            exporter.OnFinish();

            await Assert.That(exporter.SkippedCount).IsEqualTo(0);
            await Assert.That(exporter.FrameCount).IsEqualTo(1);
        }

        await Assert.That(_FileContainsObjectType(stream.ToArray(), BlfConstants.ObjTypeLinMessage2)).IsFalse();
        await Assert.That(_FileContainsObjectType(stream.ToArray(), BlfConstants.ObjTypeLinCrcError2)).IsTrue();

        using BlfSource source = BlfSource.FromData(stream.ToArray(), "output.blf");
        _StartSource(source);
        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Data.Span[7]).IsEqualTo((byte)0x08);
    }

    [Test]
    public async Task CanErrorFrameWritesType2()
    {
        using MemoryStream stream = new();
        byte[] canData = SocketCanGenerators.BuildCanClassic(0x123, [0x01]);
        BinaryPrimitives.WriteUInt32BigEndian(
            canData, BinaryPrimitives.ReadUInt32BigEndian(canData) | BlfConstants.SocketCanErr);

        using (BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build())
        {
            exporter.OnFrame(TestHarness.CreateFrame(
                0, 1_000_000_000L, canData, LinkType.CanSocketcan));
            exporter.OnFinish();
        }

        await Assert.That(_FileContainsObjectType(stream.ToArray(), BlfConstants.ObjTypeCanError)).IsTrue();
        await Assert.That(_FileContainsObjectType(stream.ToArray(), BlfConstants.ObjTypeCanMessage)).IsFalse();

        using BlfSource source = BlfSource.FromData(stream.ToArray(), "output.blf");
        _StartSource(source);
        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        uint id = BinaryPrimitives.ReadUInt32BigEndian(frame!.Value.Data.Span);
        await Assert.That(id & BlfConstants.SocketCanErr).IsEqualTo(BlfConstants.SocketCanErr);
    }

    [Test]
    public async Task EthernetVlanTciZeroRoundTripsViaType120()
    {
        using MemoryStream stream = new();
        byte[] ethernet = new byte[18];
        ethernet.AsSpan(0, 6).Fill(0xFF);
        ethernet.AsSpan(6, 6).Fill(0x11);
        ethernet[12] = 0x81;
        ethernet[13] = 0x00;
        ethernet[14] = 0x00;
        ethernet[15] = 0x00;
        ethernet[16] = 0x08;
        ethernet[17] = 0x00;

        using (BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build())
        {
            exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, ethernet));
            exporter.OnFinish();
        }

        await Assert.That(_FileContainsObjectType(stream.ToArray(), BlfConstants.ObjTypeEthernetFrameEx)).IsTrue();
        await Assert.That(_FileContainsObjectType(stream.ToArray(), BlfConstants.ObjTypeEthernetFrame)).IsFalse();

        using BlfSource source = BlfSource.FromData(stream.ToArray(), "output.blf");
        _StartSource(source);
        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Data.ToArray()).IsEquivalentTo(ethernet);
    }

    [Test]
    public async Task EthernetFrameWithFcsRoundTripsViaType120()
    {
        using MemoryStream stream = new();
        byte[] ethernet = FrameGenerators.BuildEthernetIpv4UdpFrame(8);
        byte[] withFcs = new byte[ethernet.Length + 4];
        ethernet.CopyTo(withFcs, 0);
        withFcs[^4] = 0xDE;
        withFcs[^3] = 0xAD;
        withFcs[^2] = 0xBE;
        withFcs[^1] = 0xEF;

        using (BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build())
        {
            exporter.OnFrame(TestHarness.CreateFrame(0, 1_000_000_000L, withFcs));
            exporter.OnFinish();
        }

        using BlfSource source = BlfSource.FromData(stream.ToArray(), "output.blf");
        _StartSource(source);
        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Data.ToArray()).IsEquivalentTo(withFcs);
    }

    [Test]
    public async Task LinDataFrameWritesObjectVersionOne()
    {
        using MemoryStream stream = new();
        byte[] linData = LinGenerators.BuildLinFrame(0x05, [0x11, 0x22, 0x33], checksum: 0x42);

        using (BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build())
        {
            exporter.OnFrame(TestHarness.CreateFrame(
                0, 1_000_000_000L, linData, LinkType.Lin));
            exporter.OnFinish();
        }

        await Assert.That(_FindObjectVersion(stream.ToArray(), BlfConstants.ObjTypeLinMessage2)).IsEqualTo((ushort)1);
    }

    [Test]
    public async Task FlexRayChannelBRoundTripsHeaderCrc()
    {
        using MemoryStream stream = new();
        byte[] frData = FlexRayGenerators.BuildFlexRayFrame(1, 10, 3, 0x5A3, [0xDE, 0xAD], sync: true);

        using (BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressionLevel(BlfCompressionLevel.None)
            .Build())
        {
            exporter.OnFrame(TestHarness.CreateFrame(
                0, 1_000_000_000L, frData, LinkType.Flexray));
            exporter.OnFinish();
        }

        using BlfSource source = BlfSource.FromData(stream.ToArray(), "output.blf");
        _StartSource(source);
        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Data.ToArray()).IsEquivalentTo(frData);
        bool parsed = FlexRayLinkTypeFrame.TryParseDataFrame(
            frame.Value.Data.Span, out FlexRayLinkTypeFrame.Fields fields, out _);
        await Assert.That(parsed).IsTrue();
        await Assert.That(fields.ChannelB).IsTrue();
        await Assert.That(fields.HeaderCrc).IsEqualTo((ushort)0x5A3);
    }

    /// <summary>Creates a frame on an interface whose channel property is <paramref name="channel"/>.</summary>
    private static Frame _CreateFrameOnChannel(long channel, byte[] data, LinkType linkType)
    {
        Stack stack = TestHarness.GetStack();
        FrameInterfaceRegistry registry = stack.FrameInterfaceRegistry;
        if (registry.SourceCount == 0)
        {
            registry.RegisterSource(TestHarness.CreateNullFrameSource());
        }

        FrameInterfaceId interfaceId = registry.Register(
            new FrameSourceId(0),
            "can-channel",
            null,
            linkType,
            new Dictionary<string, object> { [BlfInterfacePropertyKeys.Channel] = channel });
        return Frame.Create(0, Timestamp.FromNanos(1_000_000_000), data, linkType, interfaceId, registry).Value;
    }

    [Test]
    [Arguments(false, 256L)]
    [Arguments(false, 65535L)]
    [Arguments(true, 256L)]
    [Arguments(true, 65535L)]
    public async Task AnFdOrXlFrameOnAChannelAbove255IsSkippedAsMalformedAndNamesTheLimit(bool xl, long channel)
    {
        byte[] data = xl
            ? SocketCanGenerators.BuildCanXl(0x01, [0xAA])
            : SocketCanGenerators.BuildCanFd(0x123, [0x01, 0x02]);
        Frame frame = _CreateFrameOnChannel(channel, data, LinkType.CanSocketcan);
        List<ExportErrorEventArgs> events = [];
        using MemoryStream stream = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(stream).Build();
        exporter.ItemSkipped += (_, args) => events.Add(args);

        exporter.OnFrame(frame);
        exporter.OnFinish();

        await Assert.That(exporter.FrameCount).IsEqualTo(0);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.MalformedData);
        await Assert.That(events[0].Message).Contains(Invariant($"channel {channel} is above 255"));
    }

    [Test]
    public async Task AnFdFrameOnChannel255IsWritten()
    {
        Frame frame = _CreateFrameOnChannel(255L, SocketCanGenerators.BuildCanFd(0x123, [0x01, 0x02]), LinkType.CanSocketcan);
        using MemoryStream stream = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(stream).Build();

        exporter.OnFrame(frame);
        exporter.OnFinish();

        await Assert.That(exporter.FrameCount).IsEqualTo(1);
        await Assert.That(exporter.SkippedCount).IsEqualTo(0);
    }

    [Test]
    public async Task AnFdFrameThatDeclaresMoreThan64BytesIsSkippedAsMalformedAndNamesTheLimit()
    {
        byte[] data = new byte[8 + 65];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x123);
        data[4] = 65;
        data[5] = 0x04; // FDF
        Frame frame = TestHarness.CreateFrame(0, 1_000_000_000L, data, LinkType.CanSocketcan);
        List<ExportErrorEventArgs> events = [];
        using MemoryStream stream = new();
        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(stream).Build();
        exporter.ItemSkipped += (_, args) => events.Add(args);

        exporter.OnFrame(frame);
        exporter.OnFinish();

        await Assert.That(exporter.FrameCount).IsEqualTo(0);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.MalformedData);
        await Assert.That(events[0].Message).Contains("declares 65 data bytes");
        await Assert.That(events[0].Message).Contains("at most 64");
    }

    [Test]
    [Arguments(-1)]
    [Arguments(2)]
    [Arguments(4)]
    [Arguments(7)]
    public async Task AnUndefinedCompressionLevelIsRefused(int level)
    {
        BlfExporter.Builder builder = BlfExporter.CreateBuilder();

        await Assert.That(() => builder.WithCompressionLevel((BlfCompressionLevel)level)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(BlfCompressionLevel.None)]
    [Arguments(BlfCompressionLevel.Fast)]
    [Arguments(BlfCompressionLevel.Default)]
    [Arguments(BlfCompressionLevel.Best)]
    public async Task EveryDefinedCompressionLevelIsAccepted(BlfCompressionLevel level)
    {
        using MemoryStream stream = new();

        using BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(stream).WithCompressionLevel(level).Build();

        await Assert.That(exporter).IsNotNull();
    }

    private static void _StartSource(BlfSource source)
    {
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
    }

    private static bool _FileContainsObjectType(byte[] fileBytes, uint objectType)
    {
        for (int i = 0; i + 16 <= fileBytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(i)) != 0x4A424F4C)
            {
                continue;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(i + 12)) == objectType)
            {
                return true;
            }
        }

        return false;
    }

    private static ushort _FindObjectVersion(byte[] fileBytes, uint objectType)
    {
        for (int i = 0; i + 24 <= fileBytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(i)) != 0x4A424F4C)
            {
                continue;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(i + 12)) != objectType)
            {
                continue;
            }

            return BinaryPrimitives.ReadUInt16LittleEndian(fileBytes.AsSpan(i + 22));
        }

        return ushort.MaxValue;
    }
}

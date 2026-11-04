// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pcapng;

/// <summary>
/// Tests for the Interface Description Block: the 16-bit link type field, the all-or-nothing write of the block, and the
/// handling of interface metadata that an option cannot hold.
/// </summary>
internal sealed class PcapngInterfaceBlockTests
{
    [Test]
    public async Task LinkTypeAboveSixteenBitsIsRejectedAndNothingIsWritten()
    {
        using MemoryStream stream = new();
        PcapngWriter writer = new(stream);
        try
        {
            await Assert.That(() => writer.WriteInterfaceDescription(
                    (LinkType)0x0001_0001, 65535, PcapngWriter.TsResolNanoseconds, "eth0", null, null, null, []))
                .Throws<ArgumentOutOfRangeException>();
            await Assert.That(stream.Length).IsEqualTo(0L);
        }
        finally
        {
            writer.ReturnBuffers();
        }
    }

    [Test]
    public async Task NegativeLinkTypeIsRejected()
    {
        using MemoryStream stream = new();
        PcapngWriter writer = new(stream);
        try
        {
            await Assert.That(() => writer.WriteInterfaceDescription(
                    (LinkType)(-1), 65535, PcapngWriter.TsResolNanoseconds, "eth0", null, null, null, []))
                .Throws<ArgumentOutOfRangeException>();
        }
        finally
        {
            writer.ReturnBuffers();
        }
    }

    [Test]
    public async Task LargestSixteenBitLinkTypeIsWrittenUnchanged()
    {
        using MemoryStream stream = new();
        PcapngWriter writer = new(stream);
        try
        {
            writer.WriteInterfaceDescription(
                (LinkType)ushort.MaxValue, 65535, PcapngWriter.TsResolNanoseconds, "eth0", null, null, null, []);
        }
        finally
        {
            writer.ReturnBuffers();
        }

        ushort linkType = BinaryPrimitives.ReadUInt16LittleEndian(stream.ToArray().AsSpan(8));

        await Assert.That(linkType).IsEqualTo(ushort.MaxValue);
    }

    [Test]
    public async Task FrameWithAnUnrepresentableLinkTypeIsSkippedAsSerializationError()
    {
        using MemoryStream stream = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);
        Frame frame = TestHarness.CreateFrame(
            0, 1_000_000_000L, new byte[20], (LinkType)0x0001_0001);

        bool accepted = exporter.OnFrame(frame);

        await Assert.That(accepted).IsTrue();
        await Assert.That(exporter.FrameCount).IsEqualTo(0);
        await Assert.That(exporter.SkippedCount).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
    }

    [Test]
    public async Task AnOverlongInterfaceNameIsLeftOutAndReportedOnce()
    {
        using MemoryStream stream = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);
        (Frame first, Frame second) = _CreateTwoFramesOnOneInterface(new string('n', 70_000), null, null);

        bool firstAccepted = exporter.OnFrame(first);
        bool secondAccepted = exporter.OnFrame(second);
        exporter.OnFinish();

        PcapngVerifier verifier = PcapngVerifier.FromData(stream.ToArray());
        await Assert.That(firstAccepted).IsTrue();
        await Assert.That(secondAccepted).IsTrue();
        await Assert.That(exporter.FrameCount).IsEqualTo(2);
        await Assert.That(exporter.SkippedCount).IsEqualTo(0);
        await Assert.That(exporter.ErrorCount).IsEqualTo(0);
        await Assert.That(verifier.FrameCount).IsEqualTo(2);
        await Assert.That(verifier.InterfaceCount).IsEqualTo(1);
        await Assert.That(verifier.Interfaces[0].Name).IsNull();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(events[0].Message).Contains("if_name");
        await Assert.That(events[0].Message).Contains("65535");
    }

    [Test]
    public async Task InterfaceMetadataThatFitsRaisesNoEvent()
    {
        using MemoryStream stream = new();
        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToStream(stream).Build();
        List<ExportErrorEventArgs> events = [];
        exporter.ItemSkipped += (_, args) => events.Add(args);
        Dictionary<string, object> properties = new()
        {
            [PcapInterfacePropertyKeys.Speed] = 1_000_000_000UL,
            [PcapInterfacePropertyKeys.Filter] = "port 80",
        };
        (Frame first, _) = _CreateTwoFramesOnOneInterface("eth0", "first interface", properties);

        exporter.OnFrame(first);
        exporter.OnFinish();

        PcapngVerifier verifier = PcapngVerifier.FromData(stream.ToArray());
        await Assert.That(events.Count).IsEqualTo(0);
        await Assert.That(verifier.Interfaces[0].Name).IsEqualTo("eth0");
    }

    [Test]
    public async Task InterfaceNameAtTheOptionLimitIsKeptAndOneAboveIsLeftOut()
    {
        List<string> omittedAtLimit = [];
        List<string> omittedAbove = [];
        PcapngVerifier atLimit = _WriteInterface(new string('a', 65535), null, null, omittedAtLimit);
        PcapngVerifier above = _WriteInterface(new string('a', 65536), null, null, omittedAbove);

        await Assert.That(omittedAtLimit.Count).IsEqualTo(0);
        await Assert.That(atLimit.Interfaces[0].Name!.Length).IsEqualTo(65535);
        await Assert.That(omittedAbove.Count).IsEqualTo(1);
        await Assert.That(omittedAbove[0]).IsEqualTo("if_name");
        await Assert.That(above.Interfaces[0].Name).IsNull();
    }

    [Test]
    public async Task AnOverlongDescriptionAndUnusableTableOptionsAreLeftOutAndNamed()
    {
        Dictionary<string, object> properties = new()
        {
            [PcapInterfacePropertyKeys.Speed] = -1L,
            [PcapInterfacePropertyKeys.Filter] = 5,
            [PcapInterfacePropertyKeys.Os] = new string('o', 70_000),
            [PcapInterfacePropertyKeys.Hardware] = string.Empty,
            [PcapInterfacePropertyKeys.TxSpeed] = 100UL,
        };
        List<string> omitted = [];

        PcapngVerifier verifier = _WriteInterface("eth0", new string('d', 65_536), properties, omitted);

        // Every unusable value is named once; the empty text is nothing to store, and the valid number is written.
        await Assert.That(omitted.Count).IsEqualTo(4);
        await Assert.That(omitted).Contains("if_description");
        await Assert.That(omitted).Contains(PcapInterfacePropertyKeys.Speed);
        await Assert.That(omitted).Contains(PcapInterfacePropertyKeys.Filter);
        await Assert.That(omitted).Contains(PcapInterfacePropertyKeys.Os);
        await Assert.That(verifier.Interfaces[0].Name).IsEqualTo("eth0");
    }

    [Test]
    public async Task ANullOmittedListIsRejectedAndNothingIsWritten()
    {
        using MemoryStream stream = new();
        PcapngWriter writer = new(stream);
        try
        {
            await Assert.That(() => writer.WriteInterfaceDescription(
                    LinkType.Ethernet, 65535, PcapngWriter.TsResolNanoseconds, "eth0", null, null, null, null!))
                .Throws<ArgumentNullException>();
            await Assert.That(stream.Length).IsEqualTo(0L);
        }
        finally
        {
            writer.ReturnBuffers();
        }
    }

    /// <summary>Writes one IDB and returns the parsed result; <paramref name="omitted"/> receives what the writer left out.</summary>
    private static PcapngVerifier _WriteInterface(
        string? name,
        string? description,
        IReadOnlyDictionary<string, object>? properties,
        List<string> omitted)
    {
        using MemoryStream stream = new();
        PcapngWriter writer = new(stream);
        try
        {
            writer.WriteInterfaceDescription(
                LinkType.Ethernet, 65535, PcapngWriter.TsResolNanoseconds, name, null, description, properties, omitted);
        }
        finally
        {
            writer.ReturnBuffers();
        }

        return PcapngVerifier.FromData(stream.ToArray());
    }

    /// <summary>Registers one interface with the given metadata and creates two frames on it.</summary>
    private static (Frame First, Frame Second) _CreateTwoFramesOnOneInterface(
        string name,
        string? description,
        Dictionary<string, object>? properties)
    {
        Stack stack = TestHarness.GetStack();
        FrameInterfaceRegistry registry = stack.FrameInterfaceRegistry;
        if (registry.SourceCount == 0)
        {
            registry.RegisterSource(TestHarness.CreateNullFrameSource());
        }

        FrameInterfaceId interfaceId = registry.Register(new FrameSourceId(0), name, description, LinkType.Ethernet, properties);
        Frame first = Frame.Create(0, Timestamp.FromNanos(1_000_000_000), new byte[20], LinkType.Ethernet, interfaceId, registry).Value;
        Frame second = Frame.Create(1, Timestamp.FromNanos(2_000_000_000), new byte[20], LinkType.Ethernet, interfaceId, registry).Value;
        return (first, second);
    }
}

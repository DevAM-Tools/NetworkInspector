// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces.Tests;

/// <summary>The recipes: argument rules, determinism, and what the application decodes from each trace.</summary>
internal sealed class TraceRecipeTests
{
    #region Arguments

    [Test]
    public async Task RecipeConstructorsRefuseBadCountsAndSizes()
    {
        await Assert.That(() => new MixedEthernetRecipe(0, 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new MixedEthernetRecipe(-1, 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new MixedEthernetRecipe(TraceRecipe.MaxFrames + 1, 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new CanSignalsRecipe(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new CanSignalsRecipe(TraceRecipe.MaxFrames + 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new JumboFramesRecipe(0, 9000)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new JumboFramesRecipe(10, 1_499)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new JumboFramesRecipe(10, 65_001)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new OutOfOrderRecipe(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RepeatedFailureRecipe(TraceRecipe.MaxFrames + 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SampleProtocolRecipe(10, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SampleProtocolRecipe(0, 40000)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new LargeRandomRecipe(0, 1, RandomFrameMode.UdpIPv4)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new LargeRandomRecipe(LargeRandomRecipe.MaxLargeFrames + 1, 1, RandomFrameMode.UdpIPv4))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new LargeRandomRecipe(10, 1, (RandomFrameMode)999)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => OutOfOrderRecipe.BackwardIndices(-1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task TheLargeRecipeAllowsMoreFramesThanTheOthers()
    {
        LargeRandomRecipe large = new(TraceRecipe.MaxFrames + 1, 1, RandomFrameMode.UdpIPv4);

        await Assert.That(large.FrameCount).IsEqualTo(TraceRecipe.MaxFrames + 1);
    }

    #endregion

    #region Mixed Ethernet

    [Test]
    public async Task TheMixedTraceHasEachFrameKindAndTheDatagramsHaveTheirFixedSize()
    {
        using TempFolder folder = new("recipe-mixed");
        TraceFile trace = await new TraceKit(folder.Path).GetAsync(new MixedEthernetRecipe(500, 4), CancellationToken.None);
        using Stack stack = TraceReading.BuildStack(withSample: false);

        List<TraceReading.ParsedFrame> frames = TraceReading.ReadAndParse(trace.Path, stack);

        int dns = 0;
        int tcp = 0;
        int arp = 0;
        int datagrams = 0;
        Timestamp previous = default;
        foreach (TraceReading.ParsedFrame parsed in frames)
        {
            // The kind of a packet is told by the fields of its protocols: a protocol that did not take part has no fields.
            await Assert.That(TraceReading.Text(stack, parsed.Packet, "packet.error")).IsNull();
            if (TraceReading.U64(stack, parsed.Packet, "dns.id") is not null)
            {
                dns++;
            }
            else if (TraceReading.U64(stack, parsed.Packet, "tcp.dstport") is not null)
            {
                tcp++;
            }
            else if (TraceReading.U64(stack, parsed.Packet, "arp.hw.type") is not null)
            {
                arp++;
            }
            else if (TraceReading.U64(stack, parsed.Packet, "udp.dstport") == MixedEthernetRecipe.DatagramPort)
            {
                datagrams++;
                await Assert.That(TraceReading.U64(stack, parsed.Packet, "udp.length")).IsEqualTo(8UL + MixedEthernetRecipe.DatagramPayloadBytes);
            }

            if (parsed.Frame.Id.Value > 0)
            {
                await Assert.That(parsed.Frame.Timestamp).IsGreaterThan(previous);
            }

            previous = parsed.Frame.Timestamp;
        }

        // The generator draws each of four kinds with the same chance; 15 % of 500 is far below the expected 25 %.
        await Assert.That(dns).IsGreaterThanOrEqualTo(75);
        await Assert.That(tcp).IsGreaterThanOrEqualTo(75);
        await Assert.That(arp).IsGreaterThanOrEqualTo(75);
        await Assert.That(datagrams).IsGreaterThanOrEqualTo(75);
        await Assert.That(dns + tcp + arp + datagrams).IsEqualTo(500);
    }

    #endregion

    #region CAN signals

    [Test]
    public async Task TheCanTraceDecodesWithTheWrittenConfigurationIncludingTheSpecialValues()
    {
        using TempFolder folder = new("recipe-can");
        TraceFile trace = await new TraceKit(folder.Path).GetAsync(new CanSignalsRecipe(300), CancellationToken.None);
        string configPath = trace.ProfileSettings[CanSignalTrace.ConfigFileSetting];

        // The configuration loads without a warning and describes the three signals with their value names.
        using (FileStream stream = File.OpenRead(configPath))
        {
            await Assert.That(SignalMessageRegistration.TryLoadConfig(stream, out SignalMessagesConfig? config, out SettingsLoadWarning? warning)).IsTrue();
            await Assert.That(warning).IsNull();
            SignalMessageConfig message = config!.Messages.Single();
            await Assert.That(message.Name).IsEqualTo(CanSignalTrace.MessageName);
            SignalFieldConfig gear = message.Signals.Single(static signal => signal.Name == CanSignalTrace.GearField);
            await Assert.That(gear.ValueNames!.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => pair.Value).ToArray())
                .IsEquivalentTo(["P", "R", "N", "D", "S"], CollectionOrdering.Matching);
        }

        using Stack stack = TraceReading.BuildStack(withSample: false, trace.ProfileSettings, folder.Path);
        List<TraceReading.ParsedFrame> frames = TraceReading.ReadAndParse(trace.Path, stack);

        HashSet<ulong> speeds = [];
        HashSet<ulong> gears = [];
        foreach (TraceReading.ParsedFrame parsed in frames)
        {
            int index = (int)parsed.Frame.Id.Value;
            double? speed = TraceReading.Number(stack, parsed.Packet, CanSignalTrace.VehicleSpeedField);
            double? gear = TraceReading.Number(stack, parsed.Packet, CanSignalTrace.GearField);
            await Assert.That(speed).IsEqualTo((double)CanSignalsRecipe.RawSpeedFor(index));
            await Assert.That(gear).IsEqualTo((double)(index % 5));
            speeds.Add((ulong)speed!.Value);
            gears.Add((ulong)gear!.Value);
        }

        await Assert.That(frames.Count).IsEqualTo(300);
        await Assert.That(speeds.Contains(254UL)).IsTrue();
        await Assert.That(speeds.Contains(255UL)).IsTrue();
        await Assert.That(speeds.Count(static value => value <= 253)).IsGreaterThan(100);
        await Assert.That(gears.OrderBy(static value => value).ToArray()).IsEquivalentTo([0UL, 1UL, 2UL, 3UL, 4UL], CollectionOrdering.Matching);
        await Assert.That(stack.GetFieldId(CanSignalTrace.WheelSpeedField)).IsNotNull();
    }

    [Test]
    public async Task TheWheelSpeedIsTheRawValueTimesTheFactor()
    {
        using TempFolder folder = new("recipe-can-wheel");
        TraceFile trace = await new TraceKit(folder.Path).GetAsync(new CanSignalsRecipe(40), CancellationToken.None);
        using Stack stack = TraceReading.BuildStack(withSample: false, trace.ProfileSettings, folder.Path);

        List<TraceReading.ParsedFrame> frames = TraceReading.ReadAndParse(trace.Path, stack);

        FieldId wheel = stack.GetFieldId(CanSignalTrace.WheelSpeedField)!.Value;
        foreach (TraceReading.ParsedFrame parsed in frames)
        {
            await Assert.That(parsed.Packet.TryGetFieldValue(wheel, out FieldValue value, materialize: true)).IsTrue();
            await Assert.That(value.Data.TryGetAsF64(out double physical)).IsTrue();
            await Assert.That(physical).IsEqualTo(CanSignalsRecipe.RawWheelSpeedFor((int)parsed.Frame.Id.Value) * 0.01).Within(1e-9);
        }
    }

    #endregion

    #region Jumbo, out of order, failures

    [Test]
    public async Task JumboFramesReopenIntact()
    {
        using TempFolder folder = new("recipe-jumbo");
        TraceFile trace = await new TraceKit(folder.Path).GetAsync(new JumboFramesRecipe(20, 9000), CancellationToken.None);
        using Stack stack = TraceReading.BuildStack(withSample: false);

        List<TraceReading.ParsedFrame> frames = TraceReading.ReadAndParse(trace.Path, stack);

        await Assert.That(frames.Count).IsEqualTo(20);
        foreach (TraceReading.ParsedFrame parsed in frames)
        {
            int index = (int)parsed.Frame.Id.Value;
            ReadOnlyMemory<byte> data = parsed.Frame.Data;
            await Assert.That(data.Length).IsEqualTo(9042);
            await Assert.That(TraceReading.U64(stack, parsed.Packet, "udp.length")).IsEqualTo(9008UL);
            await Assert.That(TraceReading.U64(stack, parsed.Packet, "udp.dstport")).IsEqualTo((ulong)JumboFramesRecipe.Port);
            bool intact = true;
            for (int j = 0; j < 9000; j++)
            {
                intact &= data.Span[42 + j] == (byte)((index + j) & 0xFF);
            }

            await Assert.That(intact).IsTrue();
        }
    }

    [Test]
    public async Task OutOfOrderTimestampsGoBackwardsAtTheSevenPeriodPositions()
    {
        using TempFolder folder = new("recipe-unordered");
        TraceFile trace = await new TraceKit(folder.Path).GetAsync(new OutOfOrderRecipe(100), CancellationToken.None);
        using Stack stack = TraceReading.BuildStack(withSample: false);

        List<Frame> frames = TraceReading.ReadFrames(trace.Path, stack);

        List<int> backward = [];
        for (int i = 1; i < frames.Count; i++)
        {
            long delta = frames[i].Timestamp.AsNanos - frames[i - 1].Timestamp.AsNanos;
            if (i % 7 == 6)
            {
                await Assert.That(delta).IsEqualTo(-5_000_000L);
                backward.Add(i);
            }
            else
            {
                await Assert.That(delta).IsEqualTo(1_000_000L);
            }
        }

        await Assert.That(frames.Count).IsEqualTo(100);
        await Assert.That(OutOfOrderRecipe.BackwardIndices(100).ToArray()).IsEquivalentTo(backward.ToArray(), CollectionOrdering.Matching);
        await Assert.That(OutOfOrderRecipe.BackwardIndices(100).Count).IsEqualTo(14);
    }

    [Test]
    public async Task EveryPacketOfTheFailureTraceFailsWithTheSameText()
    {
        using TempFolder folder = new("recipe-failure");
        TraceFile trace = await new TraceKit(folder.Path).GetAsync(new RepeatedFailureRecipe(50), CancellationToken.None);
        using Stack stack = TraceReading.BuildStack(withSample: false);

        List<TraceReading.ParsedFrame> frames = TraceReading.ReadAndParse(trace.Path, stack);

        string[] errors = [.. frames.Select(parsed => TraceReading.Text(stack, parsed.Packet, "packet.error") ?? string.Empty)];
        await Assert.That(errors.Length).IsEqualTo(50);
        await Assert.That(errors.All(static error => error.Length > 0)).IsTrue();
        await Assert.That(errors.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(1);

        // Core's text names the missing bytes, not the protocol: the UDP header is eight bytes and four are there.
        await Assert.That(errors[0]).Contains("expected 8 bytes, got 4");
    }

    #endregion

    #region Sample protocol

    [Test]
    public async Task TheSampleTraceEqualsTheSampleWriterOverUdpAndTcp()
    {
        using TempFolder folder = new("recipe-sample");
        TraceKit kit = new(folder.Path);
        TraceFile udp = await kit.GetAsync(new SampleProtocolRecipe(30, 40000), CancellationToken.None);
        TraceFile tcp = await kit.GetAsync(new SampleProtocolRecipe(30, 41234, tcp: true), CancellationToken.None);

        // One stack per trace: both traces number their packets from 0, and a stack treats an id it has already parsed as a
        // replay of the first parse, so the TCP segments would be shown as recorded (no payload dispatch) instead of parsed.
        using Stack stack = TraceReading.BuildStack(withSample: true);
        using Stack tcpStack = TraceReading.BuildStack(withSample: true);

        List<TraceReading.ParsedFrame> udpFrames = TraceReading.ReadAndParse(udp.Path, stack);
        List<TraceReading.ParsedFrame> tcpFrames = TraceReading.ReadAndParse(tcp.Path, tcpStack);

        await Assert.That(udpFrames.Count).IsEqualTo(30);
        await Assert.That(tcpFrames.Count).IsEqualTo(30);
        for (int i = 0; i < 30; i++)
        {
            byte gear = (byte)(i % 5);
            await Assert.That(udpFrames[i].Frame.Data.ToArray())
                .IsEquivalentTo(SampleFrameWriter.Write((ulong)i, gear, SampleFrameWriter.SpeedFor(i), 40000), CollectionOrdering.Matching);
            await Assert.That(tcpFrames[i].Frame.Data.ToArray())
                .IsEquivalentTo(SampleFrameWriter.WriteTcp((ulong)i, gear, SampleFrameWriter.SpeedFor(i), 41234, (uint)(1 + (14 * i))),
                    CollectionOrdering.Matching);
            await Assert.That(TraceReading.U64(stack, udpFrames[i].Packet, "sample.counter")).IsEqualTo((ulong)i);
            await Assert.That(TraceReading.U64(stack, udpFrames[i].Packet, "sample.speed")).IsEqualTo((ulong)SampleFrameWriter.SpeedFor(i));
            await Assert.That(TraceReading.U64(tcpStack, tcpFrames[i].Packet, "sample.counter")).IsEqualTo((ulong)i);
        }
    }

    [Test]
    public async Task TheSampleSpeedPatternHasItsSpecialValues()
    {
        using TempFolder folder = new("recipe-sample-speed");
        TraceFile trace = await new TraceKit(folder.Path).GetAsync(new SampleProtocolRecipe(200, 40000), CancellationToken.None);
        using Stack stack = TraceReading.BuildStack(withSample: true);

        List<TraceReading.ParsedFrame> frames = TraceReading.ReadAndParse(trace.Path, stack);

        int[] errors = [0, 50, 100, 150];
        int[] unavailable = [.. Enumerable.Range(70, 7), .. Enumerable.Range(170, 7)];
        foreach (int index in errors)
        {
            await Assert.That(TraceReading.U64(stack, frames[index].Packet, "sample.speed")).IsEqualTo(254UL);
        }

        foreach (int index in unavailable)
        {
            await Assert.That(TraceReading.U64(stack, frames[index].Packet, "sample.speed")).IsEqualTo(255UL);
        }

        await Assert.That(Enumerable.Range(0, 200).All(index => SampleProtocolRecipe.SpeedFor(index) == SampleFrameWriter.SpeedFor(index))).IsTrue();
    }

    #endregion

    #region Large random

    [Test]
    public async Task TheLargeRecipeWritesTheSameBytesInTwoCacheFolders()
    {
        using TempFolder first = new("recipe-large-a");
        using TempFolder second = new("recipe-large-b");
        LargeRandomRecipe recipe = new(10_000, 9, RandomFrameMode.UdpIPv4);

        TraceFile a = await new TraceKit(first.Path).GetAsync(recipe, CancellationToken.None);
        TraceFile b = await new TraceKit(second.Path).GetAsync(recipe, CancellationToken.None);
        TraceFile other = await new TraceKit(first.Path).GetAsync(new LargeRandomRecipe(10_000, 10, RandomFrameMode.UdpIPv4), CancellationToken.None);

        await Assert.That(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Path))))
            .IsEqualTo(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(b.Path))));
        await Assert.That(Path.GetFileName(other.Path)).IsNotEqualTo(Path.GetFileName(a.Path));
        using Stack stack = TraceReading.BuildStack(withSample: false);
        await Assert.That(TraceReading.ReadFrames(a.Path, stack).Count).IsEqualTo(10_000);
    }

    #endregion
}

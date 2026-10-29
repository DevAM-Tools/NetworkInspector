// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using PacketIndex = NetworkInspector.Core.Index.PacketIndex;

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// SOME/IP service discovery: the container, the entries and options with their names and annotations, every diagnostic, the
/// packet summary, and the guarantee that the eager index agrees with the lazily expanded tree.
/// </summary>
internal sealed class SomeIpSdTests
{
    /// <summary>The full shipped example: service 0xF232 version 2 "ClimateControl" with the eventgroup 1 "ClimateEvents".</summary>
    private static string _FullExample => File.ReadAllText(ShippedConfigFixture.ExamplePath("someip", "full_service.json"));

    private static readonly byte[] _Ipv4 = [192, 168, 1, 100];

    private static readonly byte[] _Ipv6 = [0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];

    private const byte _Udp = 17;

    private const byte _Tcp = 6;

    private static T[] _Items<T>(params T[] values) => values;

    #region Container and flags

    [Test]
    public async Task ADiscoveryMessageIsExpandedLazilyAndHasNoRawPayloadField()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(new SomeIpSdBuilder().Offer(0x1234, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Udp, 30512).BuildFrame());

        await Assert.That(s.Count(packet, "someip_sd")).IsEqualTo(1);
        await Assert.That(s.Count(packet, "someip.payload")).IsEqualTo(0);
    }

    [Test]
    public async Task TheContainerShowsTheFixedPartAndBothArraysWithTheirLengths()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(new SomeIpSdBuilder().Offer(0x1234, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Udp, 30512).BuildFrame());

        await Assert.That(s.U64s(packet, "someip_sd.flags")).IsEquivalentTo(_Items(0xC0UL));
        await Assert.That(s.U64s(packet, "someip_sd.reserved")).IsEquivalentTo(_Items(0UL));
        await Assert.That(s.U64s(packet, "someip_sd.entries_length")).IsEquivalentTo(_Items(16UL));
        await Assert.That(s.Count(packet, "someip_sd.entries")).IsEqualTo(1);
        await Assert.That(s.Count(packet, "someip_sd.entry")).IsEqualTo(1);
        await Assert.That(s.U64s(packet, "someip_sd.options_length")).IsEquivalentTo(_Items(12UL));
        await Assert.That(s.Count(packet, "someip_sd.options")).IsEqualTo(1);
        await Assert.That(s.Count(packet, "someip_sd.option")).IsEqualTo(1);
    }

    [Test]
    [Arguments((byte)0xE0, true, true, true)]
    [Arguments((byte)0x00, false, false, false)]
    [Arguments((byte)0x1F, false, false, false)]
    [Arguments((byte)0x40, false, true, false)]
    public async Task TheFlagsByteIsSplitIntoRebootUnicastAndInitialEvents(byte flags, bool reboot, bool unicast, bool initial)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(new SomeIpSdBuilder { Flags = flags }.Offer(0x1234, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Udp, 30512).BuildFrame());

        await Assert.That(s.U64s(packet, "someip_sd.flags")[0]).IsEqualTo(flags);
        await ProtocolTestHelper.AssertBoolField(s.Stack, packet, "someip_sd.flags.reboot", reboot);
        await ProtocolTestHelper.AssertBoolField(s.Stack, packet, "someip_sd.flags.unicast", unicast);
        await ProtocolTestHelper.AssertBoolField(s.Stack, packet, "someip_sd.flags.initial_events", initial);
    }

    [Test]
    public async Task TheUnicastFlagDecidesTheContainerTextAndTheSummaryWord()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet unicast = s.Parse(new SomeIpSdBuilder { Flags = 0x40 }.Offer(1, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Udp, 1).BuildFrame());
        Packet multicast = s.Parse(new SomeIpSdBuilder { Flags = 0x00 }.Offer(1, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Udp, 1).BuildFrame());

        await ProtocolTestHelper.AssertDisplayText(s.Stack, unicast, "someip_sd", "SOME/IP-SD (unicast)");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, multicast, "someip_sd", "SOME/IP-SD (multicast)");
        await Assert.That(unicast.Info).Contains("(unicast)");
        await Assert.That(multicast.Info).Contains("(multicast)");
    }

    #endregion

    #region Entries

    [Test]
    [Arguments((byte)0x01, 5u, "Offer Service (0x01)")]
    [Arguments((byte)0x01, 0u, "Stop Offer Service (0x01)")]
    [Arguments((byte)0x06, 100u, "Subscribe Eventgroup (0x06)")]
    [Arguments((byte)0x06, 0u, "Stop Subscribe Eventgroup (0x06)")]
    [Arguments((byte)0x07, 0u, "Subscribe Eventgroup Nack (0x07)")]
    [Arguments((byte)0x07, 3u, "Subscribe Eventgroup Ack (0x07)")]
    [Arguments((byte)0x00, 3u, "Find Service (0x00)")]
    public async Task EntryTypesAreNamedAndATtlOfZeroTurnsThemIntoStops(byte type, uint ttl, string expected)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(new SomeIpSdBuilder().Entry(type, 0x1234, 1, 1, ttl, 0, count1: 1).Ipv4Option(_Ipv4, _Udp, 1).BuildFrame());

        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip_sd.entry.type", expected);
        await Assert.That(s.Count(packet, "someip_sd.error.entry_unknown")).IsEqualTo(0);
    }

    [Test]
    public async Task AnUnknownEntryTypeIsShownAsHexAndFlagged()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(new SomeIpSdBuilder().Entry(0x3F, 0x1234, 1, 1, 3, 0).BuildFrame());

        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip_sd.entry.type", "0x3F");
        await Assert.That(s.Count(packet, "someip_sd.error.entry_unknown")).IsEqualTo(1);
        await Assert.That(s.Count(packet, "someip_sd.entry.minorver")).IsEqualTo(0);
    }

    [Test]
    public async Task EveryEntryFieldMatchesItsBytes()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(new SomeIpSdBuilder().Entry(0x01, 0x1234, 2, 3, 0x010203, 5, index1: 3, index2: 5, count1: 2, count2: 1)
            .Ipv4Option(_Ipv4, _Udp, 1).BuildFrame());

        await Assert.That(s.U64s(packet, "someip_sd.entry.index1")).IsEquivalentTo(_Items(3UL));
        await Assert.That(s.U64s(packet, "someip_sd.entry.index2")).IsEquivalentTo(_Items(5UL));
        await Assert.That(s.U64s(packet, "someip_sd.entry.n_opt_1")).IsEquivalentTo(_Items(2UL));
        await Assert.That(s.U64s(packet, "someip_sd.entry.n_opt_2")).IsEquivalentTo(_Items(1UL));
        await Assert.That(s.U64s(packet, "someip_sd.entry.serviceid")).IsEquivalentTo(_Items(0x1234UL));
        await Assert.That(s.U64s(packet, "someip_sd.entry.instanceid")).IsEquivalentTo(_Items(2UL));
        await Assert.That(s.U64s(packet, "someip_sd.entry.majorver")).IsEquivalentTo(_Items(3UL));
        await Assert.That(s.U64s(packet, "someip_sd.entry.ttl")).IsEquivalentTo(_Items(66051UL));
        await Assert.That(s.U64s(packet, "someip_sd.entry.minorver")).IsEquivalentTo(_Items(5UL));
    }

    [Test]
    public async Task ALastWordIsAMinorVersionForServiceEntriesAndEventgroupFieldsForSubscribes()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet offer = s.Parse(new SomeIpSdBuilder().Offer(0x1234, 1, 1, 7, 3).Ipv4Option(_Ipv4, _Udp, 1).BuildFrame());
        Packet subscribe = s.Parse(new SomeIpSdBuilder().Entry(0x06, 0x1234, 1, 1, 3, 0x00851234).BuildFrame());

        await Assert.That(s.U64s(offer, "someip_sd.entry.minorver")).IsEquivalentTo(_Items(7UL));
        await Assert.That(s.Count(offer, "someip_sd.entry.eventgroupid")).IsEqualTo(0);

        await Assert.That(s.U64s(subscribe, "someip_sd.entry.reserved")).IsEquivalentTo(_Items(0UL));
        await ProtocolTestHelper.AssertBoolField(s.Stack, subscribe, "someip_sd.entry.initial_events", true);
        await Assert.That(s.U64s(subscribe, "someip_sd.entry.counter")).IsEquivalentTo(_Items(5UL));
        await Assert.That(s.U64s(subscribe, "someip_sd.entry.eventgroupid")).IsEquivalentTo(_Items(0x1234UL));
        await Assert.That(s.Count(subscribe, "someip_sd.entry.minorver")).IsEqualTo(0);
    }

    [Test]
    [Arguments((byte)0, (byte)3, (byte)5, (byte)2, "0-2,5-6")]
    [Arguments((byte)0, (byte)0, (byte)5, (byte)2, "5-6")]
    [Arguments((byte)0, (byte)0, (byte)0, (byte)0, "None")]
    [Arguments((byte)4, (byte)1, (byte)0, (byte)0, "4-4")]
    public async Task TheOptionsAnEntryReferencesAreShownAsRuns(byte index1, byte count1, byte index2, byte count2, string expected)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(new SomeIpSdBuilder().Entry(0x00, 1, 1, 1, 3, 0, index1: index1, index2: index2, count1: count1, count2: count2).BuildFrame());

        await Assert.That(s.Strings(packet, "someip_sd.entry.options_referenced")).IsEquivalentTo(_Items(expected));
    }

    [Test]
    public async Task ConfiguredNamesAppearOnServiceAndEventgroupAndAnUnknownServiceIsMarked()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_FullExample);
        Packet offer = s.Parse(new SomeIpSdBuilder().Offer(0xF232, 1, 2, 0, 3).Ipv4Option(_Ipv4, _Udp, 1).BuildFrame());
        Packet subscribe = s.Parse(new SomeIpSdBuilder().Subscribe(0xF232, 1, 2, 3, 1).BuildFrame());
        Packet unknown = s.Parse(new SomeIpSdBuilder().Offer(0x2222, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Udp, 1).BuildFrame());

        await ProtocolTestHelper.AssertDisplayText(s.Stack, offer, "someip_sd.entry.serviceid", "0xF232 (ClimateControl)");
        await Assert.That(s.Strings(offer, "someip_sd.entry.service.name")).IsEquivalentTo(_Items("ClimateControl"));
        await ProtocolTestHelper.AssertDisplayText(s.Stack, subscribe, "someip_sd.entry.eventgroupid", "0x0001 (ClimateEvents)");
        await Assert.That(s.Strings(subscribe, "someip_sd.entry.eventgroup.name")).IsEquivalentTo(_Items("ClimateEvents"));
        await ProtocolTestHelper.AssertDisplayText(s.Stack, unknown, "someip_sd.entry.serviceid", "0x2222 (unknown)");
        await Assert.That(s.Count(unknown, "someip_sd.entry.service.name")).IsEqualTo(0);
    }

    [Test]
    public async Task WildcardsAreAnnotatedAsAnyAndTheOtherServiceIdIsNamed()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet find = s.Parse(new SomeIpSdBuilder().Find(0xFFFF, 0xFFFF, 0xFF, 0xFFFFFFFF, 3).BuildFrame());
        Packet subscribe = s.Parse(new SomeIpSdBuilder().Subscribe(0x1234, 1, 1, 3, 0xFFFF).BuildFrame());
        Packet other = s.Parse(new SomeIpSdBuilder().Offer(0xFFFE, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Udp, 1).BuildFrame());

        await ProtocolTestHelper.AssertDisplayText(s.Stack, find, "someip_sd.entry.serviceid", "0xFFFF (ANY)");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, find, "someip_sd.entry.instanceid", "0xFFFF (ANY)");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, find, "someip_sd.entry.majorver", "255 (ANY)");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, find, "someip_sd.entry.minorver", "4294967295 (ANY)");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, subscribe, "someip_sd.entry.eventgroupid", "0xFFFF (ANY)");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, other, "someip_sd.entry.serviceid", "0xFFFE (other service)");
    }

    [Test]
    [Arguments(0xFFFFFFu, "16777215 (infinite)")]
    [Arguments(3u, "3")]
    public async Task AnInfiniteTimeToLiveIsAnnotated(uint ttl, string expected)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, ttl).Ipv4Option(_Ipv4, _Udp, 1).BuildFrame());

        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip_sd.entry.ttl", expected);
    }

    #endregion

    #region Options

    [Test]
    public async Task OptionsHaveExactBoundariesAndLeaveNoBytesBehind()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3, count1: 2).Ipv4Option(_Ipv4, _Udp, 30512).ConfigurationOption("a=1").BuildFrame());

        await Assert.That(s.ByteLengths(packet, "someip_sd.option")).IsEquivalentTo(_Items(12, 9));
        await Assert.That(s.Count(packet, "someip_sd.error.options_bytes_left")).IsEqualTo(0);
        await Assert.That(s.Count(packet, "someip_sd.error.options_truncated")).IsEqualTo(0);
    }

    [Test]
    [Arguments((byte)0x01, "Configuration (0x01)")]
    [Arguments((byte)0x02, "Load Balancing (0x02)")]
    [Arguments((byte)0x04, "IPv4 Endpoint (0x04)")]
    [Arguments((byte)0x06, "IPv6 Endpoint (0x06)")]
    [Arguments((byte)0x14, "IPv4 Multicast (0x14)")]
    [Arguments((byte)0x16, "IPv6 Multicast (0x16)")]
    [Arguments((byte)0x24, "IPv4 SD Endpoint (0x24)")]
    [Arguments((byte)0x26, "IPv6 SD Endpoint (0x26)")]
    public async Task DefinedOptionTypesAreNamed(byte type, string expected)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        SomeIpSdBuilder builder = new();
        _ = builder.Offer(1, 1, 1, 0, 3);
        _ = type switch
        {
            0x01 => builder.ConfigurationOption("a=1"),
            0x02 => builder.LoadBalancingOption(1, 2),
            0x04 or 0x14 or 0x24 => builder.Ipv4Option(_Ipv4, _Udp, 1, type),
            _ => builder.Ipv6Option(_Ipv6, _Udp, 1, type),
        };

        Packet packet = s.Parse(builder.BuildFrame());

        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip_sd.option.type", expected);
    }

    [Test]
    public async Task AnUnknownOptionTypeShowsItsBytesAndTheNextOptionStillDecodes()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3, count1: 2).Option(0x7F, 9, 8, 7).Ipv4Option(_Ipv4, _Udp, 30512).BuildFrame());

        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip_sd.option.type", "0x7F");
        await Assert.That(s.Count(packet, "someip_sd.option.data")).IsEqualTo(1);
        await Assert.That(s.Count(packet, "someip_sd.error.option_unknown")).IsEqualTo(1);
        await Assert.That(s.U64s(packet, "someip_sd.option.port")).IsEquivalentTo(_Items(30512UL));
    }

    [Test]
    public async Task EndpointOptionsShowAddressProtocolPortAndASummary()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet v4 = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Udp, 30512).BuildFrame());
        Packet v6 = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3).Ipv6Option(_Ipv6, _Tcp, 30600, 0x26).BuildFrame());

        await ProtocolTestHelper.AssertIPv4Field(s.Stack, v4, "someip_sd.option.ipv4", "192.168.1.100");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, v4, "someip_sd.option.proto", "UDP (17)");
        await ProtocolTestHelper.AssertU64Field(s.Stack, v4, "someip_sd.option.port", 30512);
        await ProtocolTestHelper.AssertDisplayText(s.Stack, v4, "someip_sd.option", "IPv4 Endpoint (192.168.1.100:30512 (UDP))");

        await Assert.That(s.Count(v6, "someip_sd.option.ipv6")).IsEqualTo(1);
        await ProtocolTestHelper.AssertDisplayText(s.Stack, v6, "someip_sd.option.proto", "TCP (6)");
        await ProtocolTestHelper.AssertU64Field(s.Stack, v6, "someip_sd.option.port", 30600);

        // The endpoint of the service discovery (0x26) with TCP is a datagram endpoint that names a stream protocol.
        await Assert.That(s.Count(v6, "someip_sd.error.l4_unsupported")).IsEqualTo(1);
    }

    [Test]
    public async Task ConfigurationItemsAreSplitAtTheFirstEqualsSign()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3).ConfigurationOption("a=1", "key").BuildFrame());

        await Assert.That(s.Strings(packet, "someip_sd.option.config")).IsEquivalentTo(_Items("a=1, key"));
        await Assert.That(s.Count(packet, "someip_sd.option.config.item")).IsEqualTo(2);
        await Assert.That(s.Strings(packet, "someip_sd.option.config.item.key")).IsEquivalentTo(_Items("a", "key"));
        await Assert.That(s.Strings(packet, "someip_sd.option.config.item.value")).IsEquivalentTo(_Items("1"));
    }

    [Test]
    public async Task ConfigurationItemsEscapeControlCharactersFromTheWire()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();

        // The second item is an ESC sequence that would clear a console that prints the tree.
        Packet packet = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3).ConfigurationOption("abc=1", "\u001B[2J").BuildFrame());

        await Assert.That(s.Strings(packet, "someip_sd.option.config")).IsEquivalentTo(_Items("abc=1, \\x1B[2J"));
        await Assert.That(s.Strings(packet, "someip_sd.option.config.item.key")).IsEquivalentTo(_Items("abc", "\\x1B[2J"));
        await Assert.That(s.Strings(packet, "someip_sd.option.config.item.value")).IsEquivalentTo(_Items("1"));
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip_sd.option", "Configuration (abc=1, \\x1B[2J)");
    }

    [Test]
    public async Task TheReservedBitsOfAnEventgroupEntryAndTheReservedByteOfAnEndpointOptionAreShown()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();

        // Byte 13 is 0xF5: initial events (bit 7), reserved (bits 4-6) all set, counter 5. The option has 0x01 before the protocol.
        Packet packet = s.Parse(new SomeIpSdBuilder().Entry(0x06, 0x1234, 1, 1, 3, 0x00F51234)
            .Option(0x04, 192, 168, 1, 100, 0x01, _Udp, 0x77, 0x30).BuildFrame());

        await Assert.That(s.U64s(packet, "someip_sd.entry.reserved2")).IsEquivalentTo(_Items(7UL));
        await Assert.That(s.U64s(packet, "someip_sd.entry.counter")).IsEquivalentTo(_Items(5UL));
        await ProtocolTestHelper.AssertBoolField(s.Stack, packet, "someip_sd.entry.initial_events", true);
        await Assert.That(s.U64s(packet, "someip_sd.option.reserved2")).IsEquivalentTo(_Items(1UL));
        await Assert.That(s.U64s(packet, "someip_sd.option.port")).IsEquivalentTo(_Items(0x7730UL));
        await Assert.That(s.Count(packet, "someip_sd.error.option_wrong_length")).IsEqualTo(0);
    }

    [Test]
    public async Task TheReservedByteOfAnIpv6EndpointOptionIsShown()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] data = [.. _Ipv6, 0x02, _Udp, 0x77, 0x30];
        Packet packet = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3).Option(0x06, data).BuildFrame());

        await Assert.That(s.U64s(packet, "someip_sd.option.reserved2")).IsEquivalentTo(_Items(2UL));
        await Assert.That(s.Count(packet, "someip_sd.option.ipv6")).IsEqualTo(1);
    }

    [Test]
    public async Task AConfigurationItemLongerThanTheOptionStopsTheItemsAndIsFlagged()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();

        // Option: length 4, type 1, reserved 0, then an item announcing 9 bytes while only 2 follow.
        Packet packet = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3).RawOptions(0x00, 0x04, 0x01, 0x00, 0x09, 0x41, 0x42).BuildFrame());

        await Assert.That(s.Count(packet, "someip_sd.error.config_malformed")).IsEqualTo(1);
        await Assert.That(s.Count(packet, "someip_sd.option.config.item")).IsEqualTo(0);
    }

    [Test]
    public async Task ALoadBalancingOptionShowsPriorityAndWeight()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3).LoadBalancingOption(0x0102, 0x0304).BuildFrame());

        await Assert.That(s.U64s(packet, "someip_sd.option.lb_priority")).IsEquivalentTo(_Items(258UL));
        await Assert.That(s.U64s(packet, "someip_sd.option.lb_weight")).IsEquivalentTo(_Items(772UL));
    }

    #endregion

    #region Anomalies

    [Test]
    public async Task AnEntriesLengthBeyondThePayloadDecodesWholeEntriesShowsTheRestRawAndIsFlagged()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        SomeIpSdBuilder builder = new() { EntriesLengthOverride = 64, OmitOptions = true };
        _ = builder.Offer(1, 1, 1, 0, 3, count1: 0).Offer(2, 1, 1, 0, 3, count1: 0).RawEntries(1, 2, 3, 4, 5, 6, 7, 8);
        Packet packet = s.ParseIndexed(builder.BuildFrame(), out PacketIndex index);

        await Assert.That(s.Count(packet, "someip_sd.entry")).IsEqualTo(2);
        await Assert.That(s.ByteLengths(packet, "someip_sd.entries.raw")).IsEquivalentTo(_Items(8));
        await Assert.That(s.Count(packet, "someip_sd.error.truncated")).IsEqualTo(1);
        await Assert.That(s.HasGroup(index, packet, "someipSd.error.truncated")).IsTrue();
        await Assert.That(s.Count(packet, "someip_sd.options")).IsEqualTo(0);
    }

    [Test]
    public async Task AnOptionsLengthBeyondThePayloadDecodesTheFittingOptionsAndIsFlagged()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        SomeIpSdBuilder builder = new() { OptionsLengthOverride = 1000 };
        _ = builder.Offer(1, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Udp, 30512);
        Packet packet = s.Parse(builder.BuildFrame());

        await Assert.That(s.Count(packet, "someip_sd.option")).IsEqualTo(1);
        await Assert.That(s.Count(packet, "someip_sd.error.options_truncated")).IsEqualTo(1);
    }

    [Test]
    public async Task AnOfferWithoutAnOptionIsFlaggedButAStopOfferAndAnOfferWithAnOptionAreNot()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet bare = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3, count1: 0).BuildFrame());
        Packet withOption = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Udp, 1).BuildFrame());
        Packet stop = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 0, count1: 0).BuildFrame());

        await Assert.That(s.Count(bare, "someip_sd.error.offer_without_endpoint")).IsEqualTo(1);
        await Assert.That(s.Count(withOption, "someip_sd.error.offer_without_endpoint")).IsEqualTo(0);
        await Assert.That(s.Count(stop, "someip_sd.error.offer_without_endpoint")).IsEqualTo(0);
    }

    [Test]
    public async Task AnEntriesLengthThatIsNotAMultipleOfSixteenShowsTheSurplusRawAndZeroIsFlagged()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        SomeIpSdBuilder malformed = new() { OmitOptions = true };
        _ = malformed.Offer(1, 1, 1, 0, 3, count1: 0).RawEntries(9, 9, 9, 9);
        Packet packet = s.Parse(malformed.BuildFrame());
        Packet empty = s.Parse(new SomeIpSdBuilder().BuildFrame());

        await Assert.That(s.Count(packet, "someip_sd.entry")).IsEqualTo(1);
        await Assert.That(s.ByteLengths(packet, "someip_sd.entries.raw")).IsEquivalentTo(_Items(4));
        await Assert.That(s.Count(packet, "someip_sd.error.entries_malformed")).IsEqualTo(1);
        await Assert.That(s.Count(empty, "someip_sd.error.entries_empty")).IsEqualTo(1);
        await Assert.That(s.Count(empty, "someip_sd.entries")).IsEqualTo(0);
    }

    [Test]
    public async Task ASubscribeDirectlyAfterItsStopIsFlaggedButOnlyForTheSameTuple()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet same = s.Parse(new SomeIpSdBuilder().Subscribe(0x1234, 1, 1, 0, 2).Subscribe(0x1234, 1, 1, 3, 2).BuildFrame());
        Packet other = s.Parse(new SomeIpSdBuilder().Subscribe(0x1234, 1, 1, 0, 2).Subscribe(0x1234, 1, 1, 3, 3).BuildFrame());
        Packet apart = s.Parse(new SomeIpSdBuilder().Subscribe(0x1234, 1, 1, 0, 2).Offer(0x1234, 1, 1, 0, 3).Subscribe(0x1234, 1, 1, 3, 2)
            .Ipv4Option(_Ipv4, _Udp, 1).BuildFrame());

        await Assert.That(s.Count(same, "someip_sd.error.subscribe_after_stop")).IsEqualTo(1);
        await Assert.That(s.Count(other, "someip_sd.error.subscribe_after_stop")).IsEqualTo(0);
        await Assert.That(s.Count(apart, "someip_sd.error.subscribe_after_stop")).IsEqualTo(0);
    }

    [Test]
    public async Task OptionAnomaliesAreReportedWhereTheyBelong()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet wrongLength = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3).Option(0x04, 1, 2, 3, 4).BuildFrame());
        Packet multicastTcp = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Tcp, 1, 0x14).BuildFrame());
        Packet bytesLeft = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Udp, 1).RawOptions(1, 2).BuildFrame());

        await Assert.That(s.Count(wrongLength, "someip_sd.error.option_wrong_length")).IsEqualTo(1);
        await Assert.That(s.Count(wrongLength, "someip_sd.option.data")).IsEqualTo(1);
        await Assert.That(s.Count(multicastTcp, "someip_sd.error.l4_unsupported")).IsEqualTo(1);
        await Assert.That(s.Count(bytesLeft, "someip_sd.error.options_bytes_left")).IsEqualTo(1);
        await Assert.That(s.ByteLengths(bytesLeft, "someip_sd.options.raw")).IsEquivalentTo(_Items(2));
    }

    [Test]
    [Arguments(271, 271, false)]
    [Arguments(272, 271, true)]
    public async Task OptionsBeyondWhatAnEntryCanReferenceAreRawAndFlagged(int optionCount, int expectedDecoded, bool flagged)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        SomeIpSdBuilder builder = new();
        _ = builder.Offer(1, 1, 1, 0, 3, count1: 1);

        // The minimal configuration option: length 2, type 1, reserved, one terminating zero byte (5 bytes).
        for (int i = 0; i < optionCount; i++)
        {
            _ = builder.ConfigurationOption();
        }

        // 272 minimal options of 5 bytes (1,360 bytes) still fit one Ethernet frame together with the headers.
        Packet packet = s.Parse(FrameBuilderTestExtensions.WrapRawUdp(builder.BuildMessage(), 30490, 30490));

        await Assert.That(s.Count(packet, "someip_sd.option")).IsEqualTo(expectedDecoded);
        int expectedErrors = flagged
            ? 1
            : 0;
        await Assert.That(s.Count(packet, "someip_sd.error.too_many_options")).IsEqualTo(expectedErrors);
    }

    [Test]
    public async Task APayloadShorterThanTheFixedPartHasNoContainerAndKeepsItsBytes()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] message = SomeIpBytes.Message(0xFFFF, 0x8100, [1, 2, 3], messageType: 0x02);
        Packet packet = s.Parse(FrameBuilderTestExtensions.WrapRawUdp(message, 30490, 30490));

        await Assert.That(s.Count(packet, "someip_sd")).IsEqualTo(0);
        await Assert.That(s.Count(packet, "someip_sd.error.truncated")).IsEqualTo(1);
        await Assert.That(s.ByteLengths(packet, "someip.payload")).IsEquivalentTo(_Items(3));
    }

    #endregion

    #region Summary, laziness and the index

    [Test]
    public async Task ThePacketInfoListsTheKindsOfTheEntries()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet offers = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3).Offer(2, 1, 1, 0, 3).Find(3, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Udp, 1).BuildFrame());
        Packet stops = s.Parse(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 0, count1: 0).SubscribeAck(1, 1, 1, 0, 2).BuildFrame());

        await Assert.That(offers.Info).IsEqualTo("SOME/IP-SD [Find] [Offer] (unicast)");
        await Assert.That(stops.Info).IsEqualTo("SOME/IP-SD [StopOffer] [SubscribeNack] (unicast)");
    }

    [Test]
    public async Task TheSubtreeIsBuiltOnDemandAndTheIndexAnswersWithoutIt()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.ParseIndexed(new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3, count1: 0).BuildFrame(), out PacketIndex index);

        // Nothing was expanded yet, but the index already knows the diagnostic.
        await Assert.That(packet.HasUnpopulatedLazyFields).IsTrue();
        await Assert.That(s.HasGroup(index, packet, "someipSd.error.offerWithoutEndpoint")).IsTrue();
        await Assert.That(s.HasGroup(index, packet, "someipSd.options")).IsFalse();

        packet.MaterializeAll();
        await Assert.That(packet.HasUnpopulatedLazyFields).IsFalse();
        await Assert.That(s.Count(packet, "someip_sd.entry")).IsEqualTo(1);
    }

    [Test]
    public async Task TheScanAllocatesNothing()
    {
        byte[] payload = new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3).Subscribe(2, 1, 1, 3, 4)
            .Ipv4Option(_Ipv4, _Udp, 30512).ConfigurationOption("a=1").BuildPayload();
        for (int i = 0; i < 100; i++)
        {
            _ = SomeIpSdDecoder.Scan(payload);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        SomeIpSdScan last = default;
        for (int i = 0; i < 1000; i++)
        {
            last = SomeIpSdDecoder.Scan(payload);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(last.HasEntries).IsTrue();
        await Assert.That(allocated).IsEqualTo(0).Because($"the scan allocated {allocated} bytes");
    }

    public static IEnumerable<Func<(string, byte[])>> IndexCorpus()
    {
        yield return static () => ("container", new SomeIpSdBuilder().Offer(0x1234, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Udp, 30512).BuildFrame());
        yield return static () => ("offer-without-endpoint", new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3, count1: 0).BuildFrame());
        yield return static () => ("entries-truncated",
            new SomeIpSdBuilder { EntriesLengthOverride = 64, OmitOptions = true }.Offer(1, 1, 1, 0, 3, count1: 0).RawEntries(1, 2, 3).BuildFrame());
        yield return static () => ("entries-malformed",
            new SomeIpSdBuilder { OmitOptions = true }.Offer(1, 1, 1, 0, 3, count1: 0).RawEntries(9, 9, 9, 9).BuildFrame());
        yield return static () => ("entries-empty", new SomeIpSdBuilder().BuildFrame());
        yield return static () => ("options-anomalies",
            new SomeIpSdBuilder().Offer(1, 1, 1, 0, 3).Option(0x04, 1, 2, 3, 4).Ipv4Option(_Ipv4, _Tcp, 1, 0x14).Option(0x7F, 1).RawOptions(1, 2).BuildFrame());
        yield return static () => ("options-truncated",
            new SomeIpSdBuilder { OptionsLengthOverride = 1000 }.Offer(1, 1, 1, 0, 3).Ipv4Option(_Ipv4, _Udp, 1).RawOptions(0, 50, 4).BuildFrame());
        yield return static () => ("subscribe-after-stop", new SomeIpSdBuilder().Subscribe(1, 1, 1, 0, 2).Subscribe(1, 1, 1, 3, 2).BuildFrame());
        yield return static () => ("short-payload", FrameBuilderTestExtensions.WrapRawUdp(SomeIpBytes.Message(0xFFFF, 0x8100, [1, 2, 3], messageType: 0x02), 30490, 30490));
    }

    [Test]
    [MethodDataSource(nameof(IndexCorpus))]
    public async Task TheEagerIndexEqualsTheGroupsOfTheMaterializedTree(string name, byte[] frame)
    {
        _ = name;
        await Index.EagerIndexGroupRegistrationTests.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
    }

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Neighbor Discovery tests for the flag bytes, the options beyond the five basic ones, the length-aware link-layer
/// address option, malformed option lists and the packet info text. The basic messages and options are covered by
/// <see cref="Icmpv6NdpTests"/>.
/// </summary>
internal sealed class Icmpv6NdpOptionTests
{
    private static readonly byte[] _Target = [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];
    private static readonly byte[] _Mac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];

    /// <summary>An option whose length byte is zero: invalid, and the cause of an endless loop in a naive option walk.</summary>
    private static readonly byte[] _ZeroLengthOption = [1, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>Builds a generic option of <paramref name="units"/> 8-byte units with the given content from byte 2 on.</summary>
    private static byte[] _Option(byte type, byte units, params byte[] content)
    {
        byte[] option = new byte[units * 8];
        option[0] = type;
        option[1] = units;
        content.CopyTo(option, 2);
        return option;
    }

    /// <summary>Encodes a domain name in uncompressed DNS wire format: each label with its length byte, ended by a zero byte.</summary>
    private static byte[] _DnsName(params string[] labels)
    {
        List<byte> name = [];
        foreach (string label in labels)
        {
            name.Add((byte)label.Length);
            name.AddRange(Encoding.ASCII.GetBytes(label));
        }

        name.Add(0);
        return [.. name];
    }

    /// <summary>Builds a Router Advertisement frame whose flags byte is <paramref name="flags"/> and whose options are <paramref name="options"/>.</summary>
    private static byte[] _RouterAdvertisementFrame(byte flags, params byte[][] options)
    {
        byte[] body = Icmpv6PacketBuilder.BuildRouterAdvertisementBody(64, false, false, 1800, 0, 0, [.. options.SelectMany(static o => o)]);
        body[1] = flags;
        return Icmpv6PacketBuilder.Build(134, 0, body);
    }

    private static byte[] _RouterSolicitationFrame(params byte[][] options) =>
        Icmpv6PacketBuilder.Build(133, 0, Icmpv6PacketBuilder.BuildRouterSolicitationBody([.. options.SelectMany(static o => o)]));

    #region Flag bytes

    [Test]
    public async Task RouterAdvertisementFlagsDecodePreferenceAndHomeAgent()
    {
        // 0xA8: M, H and preference 01 (High).
        byte[] frame = _RouterAdvertisementFrame(0xA8);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.nd.ra.flag.m", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.nd.ra.flag.o", false).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.nd.ra.flag.h", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.ra.flag.prf", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.nd.ra.flag.prf", "High (1)").ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.nd.ra.flag", "[M, H, Prf=High]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task RouterAdvertisementFlagsDecodeProxySnacAndReservedBit()
    {
        byte[] frame = _RouterAdvertisementFrame(0x07);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.nd.ra.flag.p", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.nd.ra.flag.s", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.ra.flag.rsv", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.nd.ra.flag.prf", 0).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task PrefixInformationFlagsDecodeRouterAddressAndPFlag()
    {
        byte[] prefix = Icmpv6PacketBuilder.BuildPrefixInformationOption(64, true, true, 86_400, 14_400, _Target);
        prefix[3] = 0xF5; // L, A, R, P and reserved 0x5
        byte[] frame = _RouterAdvertisementFrame(0, prefix);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.opt.prefix.flag.r", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.opt.prefix.flag.p", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.prefix.flag.reserved", 5).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task InfinityLifetimeHasItsOwnDisplayText()
    {
        byte[] prefix = Icmpv6PacketBuilder.BuildPrefixInformationOption(64, true, true, 0xFFFFFFFF, 3600, _Target);
        byte[] frame = _RouterAdvertisementFrame(0, prefix);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.opt.prefix.valid_lifetime", "Infinity").ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.opt.prefix.preferred_lifetime", "3600 seconds").ConfigureAwait(false);
        }
    }

    #endregion

    #region Further options

    [Test]
    public async Task RouteInformationOptionShowsPrefixPreferenceAndLifetime()
    {
        // Length 3 (24 bytes): prefix length 48, preference High, lifetime 1800, prefix 2001:db8:1::/48 zero-extended.
        byte[] option = _Option(24, 3, 48, 0x08, 0, 0, 0x07, 0x08);
        byte[] prefixBytes = [0x20, 0x01, 0x0d, 0xb8, 0, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        prefixBytes.CopyTo(option, 8);
        byte[] frame = _RouterAdvertisementFrame(0, option);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.route_info.prefix_length", 48).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.route_info.flag.route_preference", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.route_lifetime", 1800).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.opt.route_info.prefix", "2001:db8:1::").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task RouteInformationOptionWithoutPrefixBytesShowsTheZeroPrefix()
    {
        // Length 1 (8 bytes): a default route; the prefix is the unspecified address.
        byte[] frame = _RouterAdvertisementFrame(0, _Option(24, 1, 0, 0x10, 0, 0, 0x01, 0x2C));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.opt.route_info.prefix", "::").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.route_info.flag.route_preference", 2).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task RouteInformationOptionOfAnInvalidLengthEmitsNoContent()
    {
        // RFC 4191 allows 1, 2 or 3 units; 4 units is malformed and shows only the generic option fields.
        byte[] frame = _RouterAdvertisementFrame(0, _Option(24, 4, 48, 0x08));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.type", 24).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.opt.route_info.prefix_length").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task DnsSearchListOptionShowsLifetimeAndEveryDomain()
    {
        // Lifetime 600, then "example.org" and "a.io" as uncompressed DNS names, zero padded to 32 bytes.
        byte[] names = [.. _DnsName("example", "org"), .. _DnsName("a", "io")];
        byte[] option = _Option(31, 4, 0, 0, 0, 0, 0x02, 0x58);
        names.CopyTo(option, 8);
        byte[] frame = _RouterAdvertisementFrame(0, option);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.dnssl.lifetime", 600).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "icmpv6.opt.dnssl", "example.org").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task DnsSearchListWithAnOverlongLabelStopsBeforeTheBrokenName()
    {
        // A label length of 70 is invalid (the maximum is 63): the lifetime stays, no domain is invented.
        byte[] option = _Option(31, 2, 0, 0, 0, 0, 0x02, 0x58, 70, (byte)'x');
        byte[] frame = _RouterAdvertisementFrame(0, option);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.dnssl.lifetime", 600).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.opt.dnssl").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Pref64OptionShowsScaledLifetimePrefixLengthCodeAndPrefix()
    {
        // Scaled lifetime 600 (= 4800 s) with code 0 (/96); the prefix 64:ff9b::/96 is 12 bytes.
        ushort word = (ushort)((600 << 3) | 0);
        byte[] option = _Option(38, 2, (byte)(word >> 8), (byte)(word & 0xFF), 0x00, 0x64, 0xFF, 0x9B);
        byte[] frame = _RouterAdvertisementFrame(0, option);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.pref64.scaled_lifetime", 600).ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.opt.pref64.scaled_lifetime", "4800 seconds").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.pref64.plc", 0).ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.opt.pref64.plc", "96 bits (0)").ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.opt.pref64.prefix", "64:ff9b::").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task RedirectedHeaderOptionShowsTheQuotedPacket()
    {
        // Length 2: 8 option bytes (type, length, 6 reserved) plus 8 bytes of the redirected packet.
        byte[] option = _Option(4, 2);
        byte[] quoted = [1, 2, 3, 4, 5, 6, 7, 8];
        quoted.CopyTo(option, 8);
        byte[] frame = Icmpv6PacketBuilder.Build(137, 0, Icmpv6PacketBuilder.BuildRedirectBody(_Target, _Target, option));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmpv6.opt.redirected_packet", quoted).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task RedirectedHeaderOptionWithoutAPacketHasNoContentField()
    {
        byte[] frame = Icmpv6PacketBuilder.Build(137, 0, Icmpv6PacketBuilder.BuildRedirectBody(_Target, _Target, _Option(4, 1)));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.opt.redirected_packet").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task AdvertisementIntervalHomeAgentNonceFlagsExtensionAndCaptivePortalAreDecoded()
    {
        byte[] interval = _Option(7, 1, 0, 0, 0, 0, 0x27, 0x10); // 10000 ms
        byte[] homeAgent = _Option(8, 1, 0, 0, 0, 5, 0x03, 0xE8); // preference 5, lifetime 1000 s
        byte[] nonce = _Option(14, 1, 0xCA, 0xFE, 0xBA, 0xBE, 0x01, 0x02);
        byte[] flagsExtension = _Option(26, 1, 0x80, 0, 0, 0, 0, 0);
        byte[] portal = _Option(37, 3, [.. System.Text.Encoding.UTF8.GetBytes("https://portal.example")]);
        byte[] frame = _RouterAdvertisementFrame(0, interval, homeAgent, nonce, flagsExtension, portal);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.advertisement_interval", 10_000).ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.opt.advertisement_interval", "10000 ms").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.home_agent_preference", 5).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.home_agent_lifetime", 1000).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmpv6.opt.nonce", [0xCA, 0xFE, 0xBA, 0xBE, 0x01, 0x02]).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmpv6.opt.efo", [0x80, 0, 0, 0, 0, 0]).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "icmpv6.opt.captive_portal", "https://portal.example").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task RdnssOptionListsEveryAddressAndIgnoresTrailingBytes()
    {
        // Length 4 (32 bytes): header unit, one 16-byte address and 8 trailing bytes that do not form an address.
        byte[] option = _Option(25, 4, 0, 0, 0, 0, 0x0E, 0x10);
        _Target.CopyTo(option, 8);
        byte[] frame = _RouterAdvertisementFrame(0, option);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.rdnss.lifetime", 3600).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.opt.rdnss", "2001:db8::1").ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments((byte)6, "NBMA Shortcut Limit Option (6)")]
    [Arguments((byte)31, "DNS Search List (31)")]
    [Arguments((byte)144, "Encrypted DNS Option (144)")]
    [Arguments((byte)200, "Unknown Option (200)")]
    public async Task OptionNamesCoverTheRegisteredAndTheUnknownTypes(byte type, string expected) =>
        await Assert.That(DisplayTables.GetNdpOptionDisplayText(type)).IsEqualTo(expected);

    [Test]
    [Arguments((byte)1, "1 (8 bytes)")]
    [Arguments((byte)4, "4 (32 bytes)")]
    [Arguments((byte)255, "255 (2040 bytes)")]
    public async Task OptionLengthTextShowsTheByteCount(byte units, string expected) =>
        await Assert.That(DisplayTables.GetNdpOptionLengthDisplayText(units)).IsEqualTo(expected);

    [Test]
    public async Task OptionOfAnUndecodedTypeShowsTypeAndLengthOnly()
    {
        byte[] frame = _RouterAdvertisementFrame(0, _Option(11, 2));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.type", 11).ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.opt", "CGA (11)").ConfigureAwait(false);
        }
    }

    #endregion

    #region Link-layer address option

    [Test]
    public async Task LinkLayerOptionOfLength16ShowsAnEui64()
    {
        // Length 2: the eight address bytes 00:11:22:ff:fe:33:44:55 followed by six zero pad bytes.
        byte[] option = _Option(1, 2, 0x00, 0x11, 0x22, 0xFF, 0xFE, 0x33, 0x44, 0x55);
        byte[] frame = _RouterSolicitationFrame(option);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            FieldId? id = stack.GetFieldId("icmpv6.opt.linkaddr_eui64");
            await Assert.That(id).IsNotNull();
            bool found = packet.TryGetFieldValue(id!.Value, out FieldValue value, materialize: true);
            await Assert.That(found).IsTrue();
            await Assert.That(value.Data.TryGetAsEui64(out Eui64 eui64)).IsTrue();
            await Assert.That(eui64.ToString(null, CultureInfo.InvariantCulture)).IsEqualTo("00:11:22:FF:FE:33:44:55");
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.opt.linkaddr").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task LinkLayerOptionOf16BytesWithANonZeroTailShowsRawBytes()
    {
        byte[] option = _Option(1, 2, 0x00, 0x11, 0x22, 0xFF, 0xFE, 0x33, 0x44, 0x55, 0, 0, 0, 0, 0, 0x01);
        byte[] frame = _RouterSolicitationFrame(option);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmpv6.opt.linkaddr_raw", option[2..]).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.opt.linkaddr_eui64").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task LinkLayerOptionOfAnotherLengthShowsRawBytesNotAnInventedMac()
    {
        byte[] option = _Option(2, 3, 1, 2, 3, 4, 5, 6, 7, 8);
        byte[] frame = _RouterSolicitationFrame(option);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmpv6.opt.linkaddr_raw", option[2..]).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.opt.linkaddr").ConfigureAwait(false);
        }
    }

    #endregion

    #region Malformed option lists

    [Test]
    public async Task ZeroLengthOptionIsReportedAsAnError()
    {
        // The RA fixed part ends at message offset 16, where the broken option starts.
        byte[] frame = _RouterAdvertisementFrame(0, _ZeroLengthOption);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "icmpv6.nd.error.option_length", "Invalid option length (zero) at offset 16").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.opt").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task OptionLongerThanTheMessageKeepsEarlierOptionsAndReportsTheExcess()
    {
        // An MTU option (complete), then a Prefix Information option that declares 32 bytes but only 16 are present.
        byte[] mtu = Icmpv6PacketBuilder.BuildMtuOption(1500);
        byte[] cutPrefix = Icmpv6PacketBuilder.BuildPrefixInformationOption(64, true, true, 100, 50, _Target)[..16];
        byte[] frame = _RouterAdvertisementFrame(0, mtu, cutPrefix);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.opt.mtu", 1500).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.opt.prefix.length").ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "icmpv6.nd.error.option_length", "Option at offset 24 exceeds the message by 16 bytes").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task WellFormedOptionListHasNoErrorField()
    {
        byte[] frame = _RouterAdvertisementFrame(0, Icmpv6PacketBuilder.BuildMtuOption(1500));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.nd.error.option_length").ConfigureAwait(false);
        }
    }

    /// <summary>Patches the hop limit of the IPv6 header of an Ethernet + IPv6 frame (byte 7 of the IPv6 header).</summary>
    private static byte[] _WithHopLimit(byte[] frame, byte hopLimit)
    {
        byte[] copy = (byte[])frame.Clone();
        copy[14 + 7] = hopLimit;
        return copy;
    }

    [Test]
    [Arguments((byte)133)]
    [Arguments((byte)134)]
    [Arguments((byte)135)]
    [Arguments((byte)136)]
    [Arguments((byte)137)]
    public async Task NeighborDiscoveryMessageWithAnotherHopLimitThanTheRequiredIsFlagged(byte type)
    {
        byte[] body = type switch
        {
            133 => Icmpv6PacketBuilder.BuildRouterSolicitationBody([]),
            134 => Icmpv6PacketBuilder.BuildRouterAdvertisementBody(64, false, false, 1800, 0, 0, []),
            135 => Icmpv6PacketBuilder.BuildNeighborSolicitationBody(_Target, []),
            136 => Icmpv6PacketBuilder.BuildNeighborAdvertisementBody(false, true, true, _Target, []),
            _ => Icmpv6PacketBuilder.BuildRedirectBody(_Target, _Target, []),
        };
        byte[] frame = _WithHopLimit(Icmpv6PacketBuilder.Build(type, 0, body), 64);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "icmpv6.nd.error.hop_limit", "IPv6 hop limit must be 255 for this message (found 64)");
        }
    }

    [Test]
    public async Task NeighborDiscoveryMessageWithHopLimit255HasNoHopLimitError()
    {
        byte[] frame = _WithHopLimit(Icmpv6PacketBuilder.Build(135, 0, Icmpv6PacketBuilder.BuildNeighborSolicitationBody(_Target, [])), 255);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.nd.error.hop_limit");
        }
    }

    [Test]
    public async Task TruncatedNeighborMessageWithAWrongHopLimitIsStillFlagged()
    {
        // A Neighbor Solicitation body of 19 bytes cannot be decoded, but the sender is wrong all the same.
        byte[] frame = _WithHopLimit(Icmpv6PacketBuilder.Build(135, 0, new byte[19]), 1);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "icmpv6.nd.error.hop_limit", "IPv6 hop limit must be 255 for this message (found 1)");
        }
    }

    [Test]
    public async Task EchoRequestIsNotJudgedByItsHopLimit()
    {
        byte[] frame = _WithHopLimit(Icmpv6PacketBuilder.Build(128, 0, [0x12, 0x34, 0x00, 0x01]), 3);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.nd.error.hop_limit");
        }
    }

    [Test]
    public async Task TruncatedNeighborSolicitationRecordsNoTargetGroup()
    {
        // 19 body bytes: one short of the 20 a Neighbor Solicitation needs, so neither the eager index nor the lazy parse
        // may announce or emit a target.
        byte[] frame = Icmpv6PacketBuilder.Build(135, 0, new byte[19]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.nd.ns.target_address").ConfigureAwait(false);
        }
    }

    #endregion

    #region Address text and info

    [Test]
    public async Task TargetAddressTextCarriesTheLabelAndTheFormattedAddress()
    {
        byte[] frame = Icmpv6PacketBuilder.Build(135, 0, Icmpv6PacketBuilder.BuildNeighborSolicitationBody(_Target, []));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.nd.ns.target_address", "Target Address: 2001:DB8::1").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task NeighborSolicitationInfoNamesTheTargetAndTheSender()
    {
        byte[] option = Icmpv6PacketBuilder.BuildLinkLayerAddressOption(1, _Mac);
        byte[] frame = Icmpv6PacketBuilder.Build(135, 0, Icmpv6PacketBuilder.BuildNeighborSolicitationBody(_Target, option));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.Info).IsEqualTo("Neighbor Solicitation (135) for 2001:DB8::1 from 00:11:22:33:44:55");
        }
    }

    [Test]
    public async Task NeighborAdvertisementInfoShowsTheTargetFlagsAndTheAddress()
    {
        byte[] option = Icmpv6PacketBuilder.BuildLinkLayerAddressOption(2, _Mac);
        byte[] frame = Icmpv6PacketBuilder.Build(136, 0, Icmpv6PacketBuilder.BuildNeighborAdvertisementBody(false, true, true, _Target, option));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.Info).IsEqualTo("Neighbor Advertisement (136) 2001:DB8::1 (sol, ovr) is at 00:11:22:33:44:55");
        }
    }

    [Test]
    public async Task RouterAdvertisementInfoNamesTheSenderAddress()
    {
        byte[] frame = _RouterAdvertisementFrame(0, Icmpv6PacketBuilder.BuildLinkLayerAddressOption(1, _Mac));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.Info).IsEqualTo("Router Advertisement (134) from 00:11:22:33:44:55");
        }
    }

    [Test]
    public async Task RouterSolicitationWithoutOptionsInfoIsTheTypeName()
    {
        byte[] frame = _RouterSolicitationFrame();

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.Info).IsEqualTo("Router Solicitation (133)");
        }
    }

    #endregion

    #region Concurrency

    [Test]
    public async Task ConcurrentMaterializationYieldsTheSameFieldsAsASingleThread()
    {
        byte[] dnssl = _Option(31, 3, 0, 0, 0, 0, 0x02, 0x58, 3, (byte)'a', (byte)'b', (byte)'c', 0);
        await ConcurrentMaterialization.AssertMatchesSingleThreadedResult(
            _RouterAdvertisementFrame(0xA8, Icmpv6PacketBuilder.BuildMtuOption(1500), dnssl, Icmpv6PacketBuilder.BuildLinkLayerAddressOption(1, _Mac)),
            Icmpv6PacketBuilder.Build(
                135, 0, Icmpv6PacketBuilder.BuildNeighborSolicitationBody(_Target, Icmpv6PacketBuilder.BuildLinkLayerAddressOption(1, _Mac))),
            _RouterAdvertisementFrame(0, _ZeroLengthOption)).ConfigureAwait(false);
    }

    #endregion
}

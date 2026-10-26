// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;
using UdpConnectionKey = NetworkInspector.Protocols.Udp.UdpConnectionKey;
using UdpStreamTracker = NetworkInspector.Protocols.Udp.UdpStreamTracker;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for UDP parsing of crafted input (a length below the header size, a zero checksum over IPv6) and for the stream
/// table limit <c>udp.max_tracked_streams</c> (secure default 10000, same behavior as <c>tcp.max_tracked_streams</c>).
/// </summary>
internal sealed class UdpHardeningTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv6Address _V6Src = IPv6Address.FromBytes([0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]);
    private static readonly IPv6Address _V6Dst = IPv6Address.FromBytes([0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02]);

    private const int _EthernetIpv4UdpOffset = 14 + 20;
    private const int _EthernetIpv6UdpOffset = 14 + 40;

    private static byte[] _Ipv4Frame(ushort srcPort = 40000, ushort dstPort = 4000, uint srcIp = 0xAC100164, uint dstIp = 0xAC100101)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(srcIp), new IPv4Address(dstIp));
        UdpLayer udp = new(srcPort, dstPort);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame([1, 2, 3, 4, 5, 6, 7, 8]);
    }

    private static byte[] _Ipv6Frame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(_V6Src, _V6Dst);
        UdpLayer udp = new(40000, 4000);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame([1, 2, 3, 4, 5, 6, 7, 8]);
    }

    /// <summary>
    /// Parses an IPv6 frame as packet 1, right after an IPv4 frame as packet 0. The IP protocols keep per-thread address caches
    /// keyed by packet id only, and the tests of one run share threads: without the IPv4 frame, an IPv4 pair left behind for
    /// packet id 1 by another test could be mistaken for the enclosing layer of this IPv6 packet. The IPv4 frame replaces that
    /// cache entry with one for id 0, which does not match.
    /// </summary>
    private static Packet _ParseIpv6Frame(Stack stack, byte[] ipv6Frame)
    {
        _ = ProtocolTestHelper.ParseFrame(stack, _Ipv4Frame(), 0, Timestamp.FromMillis(0));
        return ProtocolTestHelper.ParseFrame(stack, ipv6Frame, 1, Timestamp.FromMillis(1));
    }

    #region Length and checksum

    [Test]
    [Arguments(0)]
    [Arguments(3)]
    [Arguments(7)]
    public async Task UdpLengthBelowTheHeaderSizeIsReported(int length)
    {
        byte[] frame = _Ipv4Frame();
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(_EthernetIpv4UdpOffset + 4), (ushort)length);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "udp.length", (ulong)length).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "udp.error.bad_length", "8").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "udp.payload").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task UdpLengthOfExactlyTheHeaderSizeIsNotAnError()
    {
        byte[] frame = _Ipv4Frame();
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(_EthernetIpv4UdpOffset + 4), 8);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "udp.error.bad_length").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ZeroChecksumOverIPv6IsBadWhenVerificationIsOn()
    {
        byte[] frame = _Ipv6Frame();
        frame[_EthernetIpv6UdpOffset + 6] = 0;
        frame[_EthernetIpv6UdpOffset + 7] = 0;

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame, ("udp.verify_checksum", SettingValue.Bool(true)))
            .ConfigureAwait(false);
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(("udp.verify_checksum", SettingValue.Bool(true)));
        Packet packet = _ParseIpv6Frame(stack, frame);

        await ProtocolTestHelper.AssertStringField(stack, packet, "udp.checksum.status", "[Bad]").ConfigureAwait(false);
    }

    [Test]
    public async Task ZeroChecksumOverIPv4IsNotVerified()
    {
        byte[] frame = _Ipv4Frame();
        frame[_EthernetIpv4UdpOffset + 6] = 0;
        frame[_EthernetIpv4UdpOffset + 7] = 0;

        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(("udp.verify_checksum", SettingValue.Bool(true)));
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "udp.checksum.status").ConfigureAwait(false);
    }

    [Test]
    public async Task ValidChecksumOverIPv6IsGood()
    {
        byte[] frame = _Ipv6Frame();

        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(("udp.verify_checksum", SettingValue.Bool(true)));
        Packet packet = _ParseIpv6Frame(stack, frame);

        await ProtocolTestHelper.AssertStringField(stack, packet, "udp.checksum.status", "[Good]").ConfigureAwait(false);
    }

    #endregion

    #region Stream limit

    [Test]
    public async Task StreamLimitSettingsHaveASecureDefaultOf10000()
    {
        SettingsManager? captured = null;
        using Stack stack = ProtocolTestHelper.BuildStack(settings => captured = settings);

        Setting? udp = captured?.GetSetting("udp.max_tracked_streams");
        Setting? tcp = captured?.GetSetting("tcp.max_tracked_streams");

        await Assert.That(udp).IsNotNull();
        await Assert.That(tcp).IsNotNull();
        await Assert.That(udp!.Value.TryGetAsU64(out ulong udpValue)).IsTrue();
        await Assert.That(tcp!.Value.TryGetAsU64(out ulong tcpValue)).IsTrue();
        await Assert.That(udpValue).IsEqualTo(10_000UL);
        await Assert.That(tcpValue).IsEqualTo(10_000UL);
    }

    [Test]
    public async Task NewTupleBeyondTheLimitKeepsParsingWithoutAStreamAndNamesTheLimit()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(("udp.max_tracked_streams", SettingValue.U64(1)));

        Packet first = ProtocolTestHelper.ParseFrame(stack, _Ipv4Frame(srcPort: 40000), 0, Timestamp.FromMillis(0));
        Packet second = ProtocolTestHelper.ParseFrame(stack, _Ipv4Frame(srcPort: 40001), 1, Timestamp.FromMillis(1));

        await ProtocolTestHelper.AssertU64Field(stack, first, "udp.stream", 0).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, first, "udp.error.stream_limit").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, second, "udp.stream").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringFieldContains(stack, second, "udp.error.stream_limit", "udp.max_tracked_streams")
            .ConfigureAwait(false);

        // Parsing goes on: the datagram itself is still decoded.
        await ProtocolTestHelper.AssertU64Field(stack, second, "udp.srcport", 40001).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, second, "udp.payload").ConfigureAwait(false);
    }

    [Test]
    public async Task KnownTupleKeepsItsStreamWhenTheTableIsFull()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(("udp.max_tracked_streams", SettingValue.U64(1)));

        _ = ProtocolTestHelper.ParseFrame(stack, _Ipv4Frame(srcPort: 40000), 0, Timestamp.FromMillis(0));
        _ = ProtocolTestHelper.ParseFrame(stack, _Ipv4Frame(srcPort: 40001), 1, Timestamp.FromMillis(1));
        Packet again = ProtocolTestHelper.ParseFrame(stack, _Ipv4Frame(srcPort: 40000), 2, Timestamp.FromMillis(2));

        await ProtocolTestHelper.AssertU64Field(stack, again, "udp.stream", 0).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, again, "udp.error.stream_limit").ConfigureAwait(false);
    }

    [Test]
    public async Task StreamLimitOutcomeIsReplayedOnAReparse()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(("udp.max_tracked_streams", SettingValue.U64(1)));
        byte[] limited = _Ipv4Frame(srcPort: 40001);
        _ = ProtocolTestHelper.ParseFrame(stack, _Ipv4Frame(srcPort: 40000), 0, Timestamp.FromMillis(0));
        _ = ProtocolTestHelper.ParseFrame(stack, limited, 1, Timestamp.FromMillis(1));

        Packet reparsed = ProtocolTestHelper.ParseFrame(stack, limited, 1, Timestamp.FromMillis(1));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, reparsed, "udp.stream").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, reparsed, "udp.error.stream_limit").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, reparsed, "packet.error").ConfigureAwait(false);
    }

    [Test]
    public async Task StreamLimitFieldIsIndexedExactly()
    {
        // A limit of 0 refuses the very first tuple, so one frame is enough to exercise the limit path in the exactness check.
        byte[] frame = _Ipv4Frame(srcPort: 40001);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame, ("udp.max_tracked_streams", SettingValue.U64(0)))
            .ConfigureAwait(false);
    }

    [Test]
    public async Task TrackerRefusesANewKeyAtTheLimitAndStillKnowsOldKeys()
    {
        UdpStreamTracker tracker = new();
        UdpConnectionKey a = UdpConnectionKey.FromIPv4(1, 2, 10, 20);
        UdpConnectionKey b = UdpConnectionKey.FromIPv4(1, 2, 11, 20);

        bool first = tracker.TryGetOrCreateStreamIndex(in a, 1, out uint indexA);
        bool refused = tracker.TryGetOrCreateStreamIndex(in b, 1, out uint indexB);
        bool known = tracker.TryGetOrCreateStreamIndex(in a, 1, out uint indexAgain);

        await Assert.That(first).IsTrue();
        await Assert.That(indexA).IsEqualTo(0u);
        await Assert.That(refused).IsFalse();
        await Assert.That(indexB).IsEqualTo(0u);
        await Assert.That(known).IsTrue();
        await Assert.That(indexAgain).IsEqualTo(0u);
        await Assert.That(tracker.Count).IsEqualTo(1);
    }

    #endregion
}

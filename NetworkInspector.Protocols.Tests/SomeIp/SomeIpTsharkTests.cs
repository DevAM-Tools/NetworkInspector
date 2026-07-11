// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Symmetric tshark cross-validation tests for the SOME/IP dissector (Plan §3.3.12).
/// Covers the base 16-byte header, SOME/IP-TP segment fields, and SOME/IP-SD
/// OfferService entry.
/// </summary>
/// <remarks>
/// <para>
/// tshark does <b>not</b> auto-dissect port 30490 as SOME/IP without an explicit
/// Decode-As rule. All calls therefore pass <c>decodeAs: _SomeIpDecodeAs</c>
/// (<c>"-d udp.port==30490,someip"</c>) to force dissection.
/// </para>
/// <para>
/// Comparison uses <see cref="TsharkAssert.AssertEquivalentMany"/> which in turn uses
/// <see cref="TsharkEquivalence.AreEquivalent"/> for semantic (not literal) equality.
/// Bool-typed NI fields ("True"/"False") are automatically bridged to tshark's "1"/"0"
/// representation.
/// </para>
/// <para><b>Thread safety:</b> Stateless tests; no shared mutable state.</para>
/// </remarks>
internal sealed class SomeIpTsharkTests
{
    /// <summary>Decode-As rule that forces tshark to dissect UDP port 30490 as SOME/IP.</summary>
    /// <remarks>
    /// Without this rule tshark does not recognise port 30490 as SOME/IP, so all
    /// SOME/IP-specific fields are absent from tshark output and every assertion fails.
    /// </remarks>
    private const string _SomeIpDecodeAs = "udp.port==30490,someip";

    #region Shared addresses

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);
    private static readonly IPv4Address _SrcIp = new(0xC0A80101); // 192.168.1.1
    private static readonly IPv4Address _DstIp = new(0xC0A80102); // 192.168.1.2

    #endregion

    #region Frame builders

    /// <summary>
    /// Wraps a <see cref="SomeIpLayer"/> in a standard Eth+IPv4+UDP frame on port 30490
    /// so tshark's SOME/IP heuristic triggers without UAT configuration.
    /// </summary>
    private static byte[] _BuildSomeIpUdp(SomeIpLayer someIp, ReadOnlySpan<byte> payload)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_SrcIp, _DstIp);
        UdpLayer udp = new(srcPort: 12345, dstPort: (ushort)SomeIpProtocol.UdpPortKey);
        return FrameStack.Start(eth).Then(ip).Then(udp).Then(someIp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>
    /// Wraps a <see cref="SomeIpTpLayer"/> in a standard Eth+IPv4+UDP frame on port 30490.
    /// </summary>
    private static byte[] _BuildSomeIpTpUdp(SomeIpTpLayer tp, ReadOnlySpan<byte> payload)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_SrcIp, _DstIp);
        UdpLayer udp = new(srcPort: 12345, dstPort: (ushort)SomeIpProtocol.UdpPortKey);
        return FrameStack.Start(eth).Then(ip).Then(udp).Then(tp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>
    /// Builds a minimal SOME/IP-SD OfferService payload (28 bytes, no options).
    /// Wire format:
    /// <code>
    /// [0]     flags (0xC0 = reboot+unicast)
    /// [1-3]   reserved
    /// [4-7]   entries array length (big-endian uint32) = 16
    /// [8]     entry type  = 0x01 (OfferService)
    /// [9]     index1      = 0
    /// [10]    index2      = 0
    /// [11]    numOpt      = 0
    /// [12-13] serviceId   (big-endian)
    /// [14-15] instanceId  (big-endian)
    /// [16]    majorVersion
    /// [17-19] TTL         (24-bit big-endian)
    /// [20-23] minorVersion (big-endian)
    /// [24-27] options array length = 0
    /// </code>
    /// </summary>
    private static byte[] _BuildSdOfferServicePayload(
        byte flags,
        ushort serviceId,
        ushort instanceId,
        byte majorVer,
        uint ttl)
    {
        byte[] sd = new byte[28];
        sd[0] = flags;
        // bytes 1-3: reserved (zero)
        BinaryPrimitives.WriteUInt32BigEndian(sd.AsSpan(4, 4), 16u); // entries array length = 16
        // Entry (16 bytes starting at offset 8)
        sd[8] = 0x01; // OfferService
        sd[9] = 0;    // index1
        sd[10] = 0;   // index2
        sd[11] = 0;   // numOpt = 0
        BinaryPrimitives.WriteUInt16BigEndian(sd.AsSpan(12, 2), serviceId);
        BinaryPrimitives.WriteUInt16BigEndian(sd.AsSpan(14, 2), instanceId);
        sd[16] = majorVer;
        // TTL is a 24-bit big-endian field at bytes 17-19
        sd[17] = (byte)((ttl >> 16) & 0xFF);
        sd[18] = (byte)((ttl >> 8) & 0xFF);
        sd[19] = (byte)(ttl & 0xFF);
        // minorVersion bytes 20-23: leave zero
        // options array length bytes 24-27: leave zero
        return sd;
    }

    #endregion

    #region Base header tests

    /// <summary>
    /// Builds a SOME/IP Request frame and verifies that all seven base header fields
    /// reported by NI match tshark's PDML output exactly.
    /// </summary>
    [Test]
    public async Task Tshark_RequestMessage_AllHeaderFields_MatchNi()
    {
        SomeIpLayer someIp = new(
            serviceId: 0x1234,
            methodId: 0x0042,
            clientId: 0x0001,
            sessionId: 0x0010,
            messageType: SomeIpMessageType.Request);
        byte[] frame = _BuildSomeIpUdp(someIp, [0xDE, 0xAD, 0xBE, 0xEF]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, 1, null, _SomeIpDecodeAs,
                ("someip.serviceid", "someip.serviceid"),
                ("someip.methodid", "someip.methodid"),
                ("someip.length", "someip.length"),
                // NI field: someip.msgtype — tshark 4.6 field: someip.messagetype
                ("someip.msgtype", "someip.messagetype"),
                ("someip.returncode", "someip.returncode"),
                ("someip.clientid", "someip.clientid"),
                ("someip.sessionid", "someip.sessionid")).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that a Response message type (0x80) is reported identically by NI
    /// and tshark.
    /// </summary>
    [Test]
    public async Task Tshark_ResponseMessage_MessageType_MatchNi()
    {
        SomeIpLayer someIp = new(
            serviceId: 0xABCD,
            methodId: 0x0001,
            messageType: SomeIpMessageType.Response);
        byte[] frame = _BuildSomeIpUdp(someIp, []);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, 1, null, _SomeIpDecodeAs,
                // NI field: someip.msgtype — tshark 4.6 field: someip.messagetype
                ("someip.msgtype", "someip.messagetype")).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that an Error message (0x81) with a non-zero return code is reported
    /// identically by NI and tshark.
    /// </summary>
    [Test]
    public async Task Tshark_ErrorMessage_ReturnCode_MatchNi()
    {
        SomeIpLayer someIp = new(
            serviceId: 0x1111,
            methodId: 0x0002,
            messageType: SomeIpMessageType.Error,
            returnCode: 0x05);
        byte[] frame = _BuildSomeIpUdp(someIp, []);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, 1, null, _SomeIpDecodeAs,
                // NI field: someip.msgtype — tshark 4.6 field: someip.messagetype
                ("someip.msgtype", "someip.messagetype"),
                ("someip.returncode", "someip.returncode")).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that a Notification message type (0x02) is reported identically by NI
    /// and tshark.
    /// </summary>
    [Test]
    public async Task Tshark_Notification_MessageType_MatchNi()
    {
        SomeIpLayer someIp = new(
            serviceId: 0x4242,
            methodId: 0x8001,
            messageType: SomeIpMessageType.Notification);
        byte[] frame = _BuildSomeIpUdp(someIp, [1, 2, 3]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, 1, null, _SomeIpDecodeAs,
                // NI field: someip.msgtype — tshark 4.6 field: someip.messagetype
                ("someip.msgtype", "someip.messagetype")).ConfigureAwait(false);
        }
    }

    #endregion

    #region SOME/IP-TP segment tests

    /// <summary>
    /// Builds the first TP segment (offset = 0, More Segments = true) and verifies that
    /// the TP flag, segment offset, and More Segments indicator match tshark's output.
    /// </summary>
    [Test]
    public async Task Tshark_TpFirstSegment_TpFlagAndOffset_MatchNi()
    {
        SomeIpTpLayer tp = new(
            serviceId: 0x1234,
            methodId: 0x0042,
            tpOffsetIn16Bytes: 0,
            moreSegments: true);
        // Payload must be a multiple of the 16-byte fragment alignment.
        byte[] payload = new byte[32];
        byte[] frame = _BuildSomeIpTpUdp(tp, payload);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, 1, null, _SomeIpDecodeAs,
                // NI field: someip.msgtype.tp — tshark 4.6 field: someip.messagetype.tp
                ("someip.msgtype.tp", "someip.messagetype.tp"),
                ("someip.tp.offset", "someip.tp.offset"),
                // NI field: someip.tp.more — tshark 4.6 field: someip.tp.flags.more_segments
                ("someip.tp.more", "someip.tp.flags.more_segments")).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the last TP segment (offset = 2 × 16 bytes = 32 bytes, More Segments = false)
    /// and verifies that the More Segments flag and the byte offset match tshark's output.
    /// </summary>
    [Test]
    public async Task Tshark_TpLastSegment_MoreFalse_MatchNi()
    {
        // tpOffsetIn16Bytes=2 → byte offset 32 reported in someip.tp.offset
        SomeIpTpLayer tp = new(
            serviceId: 0x1234,
            methodId: 0x0042,
            tpOffsetIn16Bytes: 2,
            moreSegments: false);
        byte[] payload = new byte[32];
        byte[] frame = _BuildSomeIpTpUdp(tp, payload);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, 1, null, _SomeIpDecodeAs,
                // NI field: someip.tp.more — tshark 4.6 field: someip.tp.flags.more_segments
                ("someip.tp.more", "someip.tp.flags.more_segments"),
                ("someip.tp.offset", "someip.tp.offset")).ConfigureAwait(false);
        }
    }

    #endregion

    #region SOME/IP-SD test

    /// <summary>
    /// Builds a SOME/IP-SD OfferService message and verifies the reboot flag, unicast
    /// flag, entry type, and service ID using <see cref="TsharkAssert.AssertEquivalentMany"/>.
    /// <para>
    /// The message ID 0xFFFF8100 (Service 0xFFFF, Method 0x8100) triggers tshark's
    /// SOME/IP-SD sub-dissector. The NI field paths use the <c>someip_sd.*</c> prefix
    /// which the SOME/IP protocol parser registers via <c>SomeIpSdParser</c>.
    /// </para>
    /// </summary>
    [Test]
    public async Task Tshark_ServiceDiscovery_OfferService_MatchNi()
    {
        SomeIpLayer someIp = new(
            serviceId: 0xFFFF,
            methodId: 0x8100,
            messageType: SomeIpMessageType.Notification,
            sessionId: 1);
        byte[] sdPayload = _BuildSdOfferServicePayload(
            flags: 0xC0,       // reboot=1, unicast=1
            serviceId: 0x0042,
            instanceId: 0x0001,
            majorVer: 1,
            ttl: 3);
        byte[] frame = _BuildSomeIpUdp(someIp, sdPayload);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, 1, null, _SomeIpDecodeAs,
                // NI uses someip_sd.* prefix; tshark 4.6 uses someipsd.* (no underscore)
                ("someip_sd.flags.reboot", "someipsd.flags.reboot"),
                ("someip_sd.flags.unicast", "someipsd.flags.unicast"),
                ("someip_sd.entry.type", "someipsd.entry.type"),
                ("someip_sd.entry.serviceid", "someipsd.entry.serviceid")).ConfigureAwait(false);
        }
    }

    #endregion
}

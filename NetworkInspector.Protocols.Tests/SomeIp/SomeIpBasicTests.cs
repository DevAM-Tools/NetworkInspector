// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// SOME/IP happy-path tests against the new cons-list FrameBuilder API.
/// Covers the 16-byte header (service/method/length/client/session/version/
/// msgtype/return-code) over UDP.
/// </summary>
/// <remarks>
/// Happy-path header field verification for the 16-byte base SOME/IP header.
/// SOME/IP-TP tshark cross-validation and segment tests live in <c>SomeIpTsharkTests</c>.
/// </remarks>
internal sealed class SomeIpBasicTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);
    private static readonly IPv4Address _SrcIp = IPv4Address.FromBytes([192, 168, 1, 1]);
    private static readonly IPv4Address _DstIp = IPv4Address.FromBytes([192, 168, 1, 2]);

    private static byte[] _BuildSomeIpUdp(SomeIpLayer someIp, byte[] payload)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_SrcIp, _DstIp);
        UdpLayer udp = new(srcPort: 12345, dstPort: 30490);
        return FrameStack.Start(eth).Then(ip).Then(udp).Then(someIp).CreateWithFixedValues().EmitFrame(payload);
    }

    [Test]
    public async Task Parse_RequestMessage_HeaderFields()
    {
        SomeIpLayer someIp = new(
            serviceId: 0x1234,
            methodId: 0x0042,
            clientId: 0x0001,
            sessionId: 0x0010,
            messageType: SomeIpMessageType.Request);
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] frame = _BuildSomeIpUdp(someIp, payload);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "someip.serviceid", 0x1234).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "someip.methodid", 0x0042).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "someip.clientid", 0x0001).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "someip.sessionid", 0x0010).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "someip.msgtype", SomeIpMessageType.Request).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "someip.returncode", 0).ConfigureAwait(false);
            // Length covers everything after the length field itself: 8 bytes header tail + 4 bytes payload = 12.
            await ProtocolTestHelper.AssertU64Field(stack, packet, "someip.length", 12).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ResponseMessage_MessageTypeFlags()
    {
        SomeIpLayer someIp = new(
            serviceId: 0x1234, methodId: 0x0042,
            clientId: 0, sessionId: 0,
            messageType: SomeIpMessageType.Response, returnCode: 0);
        byte[] frame = _BuildSomeIpUdp(someIp, []);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "someip.msgtype", SomeIpMessageType.Response).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ErrorMessage_ReturnCodePresent()
    {
        SomeIpLayer someIp = new(
            serviceId: 0xABCD, methodId: 0x0001,
            clientId: 0, sessionId: 0,
            messageType: SomeIpMessageType.Error, returnCode: 0x05);
        byte[] frame = _BuildSomeIpUdp(someIp, []);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "someip.msgtype", SomeIpMessageType.Error).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "someip.returncode", 0x05).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_NotificationMessage()
    {
        SomeIpLayer someIp = new(
            serviceId: 0x4242, methodId: 0x8001,
            clientId: 0, sessionId: 0,
            messageType: SomeIpMessageType.Notification);
        byte[] frame = _BuildSomeIpUdp(someIp, [1, 2, 3]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "someip.msgtype", SomeIpMessageType.Notification).ConfigureAwait(false);
        }
    }
}

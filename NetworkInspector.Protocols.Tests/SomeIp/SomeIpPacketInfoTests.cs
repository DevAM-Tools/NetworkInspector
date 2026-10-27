// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// The packet-list text of a SOME/IP message: protocol name, service id with a single <c>0x</c> prefix, message type.
/// </summary>
internal sealed class SomeIpPacketInfoTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    [Test]
    public async Task PacketInfoShowsTheServiceIdWithOneHexPrefix()
    {
        byte[] frame = FrameStack
            .Start(new EthernetLayer(_DstMac, _SrcMac))
            .Then(new IPv4Layer(IPv4Address.FromBytes([192, 168, 1, 1]), IPv4Address.FromBytes([192, 168, 1, 2])))
            .Then(new UdpLayer(srcPort: 12345, dstPort: 30490))
            .Then(new SomeIpLayer(
                serviceId: 0x1234, methodId: 0x0042, clientId: 0x0001, sessionId: 0x0010, messageType: SomeIpMessageType.Request))
            .CreateWithFixedValues()
            .EmitFrame([0xDE, 0xAD, 0xBE, 0xEF]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Ethernet);
        using (stack)
        {
            string info = packet.Info;

            // The message-type wording comes from a display table; this test pins only the prefix and the single "0x".
            await Assert.That(info.StartsWith("SOME/IP 0x1234 ", StringComparison.Ordinal)).IsTrue();
            await Assert.That(info.Contains("0x0x", StringComparison.Ordinal)).IsFalse();
        }
    }
}

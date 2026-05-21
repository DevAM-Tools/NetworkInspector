// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure;

/// <summary>
/// Small Ethernet-over-IPv4-over-UDP frame builders reused by PDU-Transport and Signal-PDU tests.
/// </summary>
internal static class AutomotiveEthUdpFrames
{
    /// <summary>
    /// Emits Ethernet → IPv4 → UDP carrying a PDU-Transport header patched by FrameBuilder FixPhase,
    /// then a Signal-PDU payload emitted by <paramref name="signalPdu"/>.
    /// </summary>
    internal static byte[] EncapsulatePduTransportSignal(
        ushort udpSrcPort,
        ushort udpDstPort,
        PduTransportConfigFb pduFb,
        uint pduWireId,
        SignalPduLayer signalPdu)
    {
        EthernetLayer eth = TestEthernet();
        IPv4Layer ip = TestIpv4();

        /*
         Destination port selects the PDU-Transport dissector whenever
         pdu_transport.udp_dispatch_port matches.
        */
        UdpLayer udp = new(udpSrcPort, udpDstPort);
        return FrameStack.Start(eth).Then(ip).Then(udp)
            .Then(PduTransportLayer.Single(pduFb, pduWireId))
            .Then(signalPdu)
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    internal static EthernetLayer TestEthernet()
    {
        MacAddress dst = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress src = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

        return new EthernetLayer(dst, src);
    }

    internal static IPv4Layer TestIpv4() =>
        new(new IPv4Address(0xAC100164), new IPv4Address(0xAC100101));
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;
using InternetChecksum = NetworkInspector.Protocols.Helpers.InternetChecksum;


/// <summary>
/// Tests for TCP checksum verification inside an IP tunnel: an IPv6 packet carried in an IPv4 packet (protocol 41). The
/// checksum covers the pseudo-header of the IP layer that directly encloses TCP, never the outer one.
/// </summary>
internal sealed class TcpTunnelChecksumTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv6Address _InnerSrc = IPv6Address.FromBytes([0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]);
    private static readonly IPv6Address _InnerDst = IPv6Address.FromBytes([0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02]);

    /// <summary>A stack in which IPv6 is also reachable through the IPv4 protocol table (protocol 41) and checksums are verified.</summary>
    private static Stack _BuildTunnelStack()
    {
        using SettingsManager settings = new();
        settings.PreloadValue("tcp.verify_checksum", SettingValue.Bool(true));
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        builder.RegisterParserInU64TableByName(IPv4Protocol.IpProtoTableName, 41, builder.GetProtocolId("ipv6")!.Value);
        return builder.Build();
    }

    private static byte[] _BuildTunnelFrame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer outer = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002), protocol: 41);
        IPv6Layer inner = new(_InnerSrc, _InnerDst);
        TcpLayer tcp = new(49152, 8080, seqNum: 1000, ackNum: 0, flags: TcpFlags.Syn);
        byte[] frame = FrameStack.Start(eth).Then(outer).Then(inner).Then(tcp).CreateWithFixedValues().EmitFrame([]);

        // Set the TCP checksum from the pseudo-header of the inner IPv6 layer, independent of what the frame builder wrote:
        // Ethernet (14) + IPv4 (20) + IPv6 (40) precede the segment, and the checksum sits at offset 16 of the TCP header.
        const int segmentStart = 14 + 20 + 40;
        Span<byte> segment = frame.AsSpan(segmentStart);
        segment[16] = 0;
        segment[17] = 0;
        ulong pseudoSum = InternetChecksum.ComputeIPv6PseudoHeaderSum(
            _InnerSrc.High, _InnerSrc.Low, _InnerDst.High, _InnerDst.Low, 6, (uint)segment.Length);
        BinaryPrimitives.WriteUInt16BigEndian(segment[16..], InternetChecksum.ComputeWithPseudoHeader(segment, pseudoSum));
        return frame;
    }

    [Test]
    public async Task ChecksumInsideIPv6InIPv4TunnelIsVerifiedAgainstTheInnerAddresses()
    {
        using Stack stack = _BuildTunnelStack();
        byte[] frame = _BuildTunnelFrame();

        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertFieldExists(stack, packet, "ipv6").ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, packet, "tcp.checksum.status", 1).ConfigureAwait(false);
    }
}

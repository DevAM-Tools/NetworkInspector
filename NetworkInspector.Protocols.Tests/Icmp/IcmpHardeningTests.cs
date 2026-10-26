// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests that the index groups recorded for ICMP error messages equal the fields the lazy populator emits: the quoted
/// bytes of an error message are shown as an embedded IPv4 header (or not at all), never as a data field.
/// </summary>
internal sealed class IcmpHardeningTests
{
    private static byte[] _DestUnreachableFrame(ReadOnlySpan<byte> quoted)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
            MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        IcmpV4DestUnreachLayer icmp = new();
        return FrameStack.Start(eth).Then(ip).Then(icmp).CreateWithFixedValues().EmitFrame(quoted);
    }

    /// <summary>A quoted IPv4 header (20 bytes, no options, protocol UDP) followed by 8 bytes of the original datagram.</summary>
    private static byte[] _QuotedIpv4Datagram(byte versionAndHeaderLength)
    {
        byte[] quoted = new byte[28];
        quoted[0] = versionAndHeaderLength;
        BinaryPrimitives.WriteUInt16BigEndian(quoted.AsSpan(2), 28);
        quoted[8] = 64;
        quoted[9] = 17;
        BinaryPrimitives.WriteUInt32BigEndian(quoted.AsSpan(12), 0x0A000001);
        BinaryPrimitives.WriteUInt32BigEndian(quoted.AsSpan(16), 0x0A000002);
        return quoted;
    }

    [Test]
    public async Task ErrorMessageWithAQuotedIpv4HeaderRecordsOnlyTheEmbeddedHeaderGroup()
    {
        byte[] frame = _DestUnreachableFrame(_QuotedIpv4Datagram(0x45));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "icmp.resp_in_ip.src", "10.0.0.1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.data").ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(12)]
    [Arguments(19)]
    public async Task ErrorMessageQuotingLessThanAnIpv4HeaderRecordsNoEmbeddedHeaderAndNoDataGroup(int quotedLength)
    {
        byte[] quoted = _QuotedIpv4Datagram(0x45).AsSpan(0, quotedLength).ToArray();
        byte[] frame = _DestUnreachableFrame(quoted);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.resp_in_ip").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.data").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ErrorMessageQuotingExactlyTheMinimumIpv4HeaderShowsTheEmbeddedHeader()
    {
        byte[] quoted = _QuotedIpv4Datagram(0x45).AsSpan(0, 20).ToArray();
        byte[] frame = _DestUnreachableFrame(quoted);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "icmp.resp_in_ip").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ErrorMessageQuotingANonIpv4DatagramRecordsNoEmbeddedHeaderGroup()
    {
        byte[] frame = _DestUnreachableFrame(_QuotedIpv4Datagram(0x65));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.resp_in_ip").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task EchoRequestWithPayloadStillRecordsTheDataGroup()
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
            MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        IcmpV4EchoLayer icmp = new(identifier: 1, sequenceNumber: 1);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(icmp).CreateWithFixedValues().EmitFrame([1, 2, 3, 4]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
    }
}

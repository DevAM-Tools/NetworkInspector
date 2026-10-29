// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the quoted datagram of ICMP error messages: the index groups recorded for it equal the fields the lazy
/// populator emits, the transport fields behind the quoted header are found even with IPv4 options, and quoted bytes
/// that do not start with an IPv4 header are shown as data instead of being dropped.
/// </summary>
internal sealed class IcmpHardeningTests
{
    private static byte[] _DestUnreachableFrame(ReadOnlySpan<byte> quoted) =>
        IcmpBasicTests.BuildIcmpFrame(type: 3, code: 3, secondWord: 0, quoted);

    /// <summary>
    /// A quoted IPv4 header (20 bytes, no options, protocol UDP) followed by 8 bytes of the original datagram: the UDP
    /// source port 53 and destination port 40000 and filler for length and checksum.
    /// </summary>
    internal static byte[] QuotedIpv4Datagram(byte versionAndHeaderLength)
    {
        byte[] quoted = new byte[28];
        quoted[0] = versionAndHeaderLength;
        BinaryPrimitives.WriteUInt16BigEndian(quoted.AsSpan(2), 28);
        quoted[8] = 64;
        quoted[9] = 17;
        BinaryPrimitives.WriteUInt32BigEndian(quoted.AsSpan(12), 0x0A000001);
        BinaryPrimitives.WriteUInt32BigEndian(quoted.AsSpan(16), 0x0A000002);
        BinaryPrimitives.WriteUInt16BigEndian(quoted.AsSpan(20), 53);
        BinaryPrimitives.WriteUInt16BigEndian(quoted.AsSpan(22), 40000);
        return quoted;
    }

    [Test]
    public async Task ErrorMessageWithAQuotedIpv4HeaderRecordsTheOriginalHeaderGroupAndNoDataGroup()
    {
        byte[] frame = _DestUnreachableFrame(QuotedIpv4Datagram(0x45));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "icmp.orig_ip.src", "10.0.0.1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.data").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task PortUnreachableShowsTheQuotedPorts()
    {
        byte[] frame = _DestUnreachableFrame(QuotedIpv4Datagram(0x45));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.orig_ip.proto", 17).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.orig_ip.ttl", 64).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.orig_ip.srcport", 53).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.orig_ip.dstport", 40000).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task QuotedHeaderWithOptionsStillFindsThePorts()
    {
        // IHL 6: a 24-byte header, so the ports sit at offset 24, not 20.
        byte[] quoted = new byte[32];
        QuotedIpv4Datagram(0x46).AsSpan(0, 20).CopyTo(quoted);
        quoted[0] = 0x46;
        BinaryPrimitives.WriteUInt16BigEndian(quoted.AsSpan(24), 1234);
        BinaryPrimitives.WriteUInt16BigEndian(quoted.AsSpan(26), 5678);
        byte[] frame = _DestUnreachableFrame(quoted);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.orig_ip.srcport", 1234).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.orig_ip.dstport", 5678).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task QuotedFragmentOtherThanTheFirstShowsNoPorts()
    {
        // A non-zero fragment offset means the bytes behind the header are payload, not a transport header.
        byte[] quoted = QuotedIpv4Datagram(0x45);
        BinaryPrimitives.WriteUInt16BigEndian(quoted.AsSpan(6), 0x00B9);
        byte[] frame = _DestUnreachableFrame(quoted);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.orig_ip.srcport").ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments(20)]
    [Arguments(23)]
    public async Task QuoteTooShortForThePortsShowsTheHeaderOnly(int quotedLength)
    {
        byte[] quoted = QuotedIpv4Datagram(0x45).AsSpan(0, quotedLength).ToArray();
        byte[] frame = _DestUnreachableFrame(quoted);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "icmp.orig_ip").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.orig_ip.srcport").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task QuotedEchoRequestShowsTypeIdentifierAndSequence()
    {
        // A traceroute probe is an echo request; the error quotes it with its identifier and sequence number.
        byte[] quoted = QuotedIpv4Datagram(0x45);
        quoted[9] = 1;
        quoted[20] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(quoted.AsSpan(24), 0xBEEF);
        BinaryPrimitives.WriteUInt16BigEndian(quoted.AsSpan(26), 7);
        byte[] frame = IcmpBasicTests.BuildIcmpFrame(type: 11, code: 0, secondWord: 0, quoted);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.orig_ip.icmp_type", 8).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.orig_ip.ident", 0xBEEF).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.orig_ip.seq", 7).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.orig_ip.srcport").ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(12)]
    [Arguments(19)]
    public async Task QuoteShorterThanAnIpv4HeaderIsShownAsData(int quotedLength)
    {
        byte[] quoted = QuotedIpv4Datagram(0x45).AsSpan(0, quotedLength).ToArray();
        byte[] frame = _DestUnreachableFrame(quoted);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.orig_ip").ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmp.data", quoted).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task QuoteOfANonIpv4DatagramIsShownAsData()
    {
        byte[] quoted = QuotedIpv4Datagram(0x65);
        byte[] frame = _DestUnreachableFrame(quoted);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.orig_ip").ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmp.data", quoted).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ErrorMessageWithoutAQuoteHasNeitherOriginalHeaderNorData()
    {
        byte[] frame = _DestUnreachableFrame(ReadOnlySpan<byte>.Empty);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.orig_ip").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.data").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task EchoRequestWithPayloadRecordsTheDataGroup()
    {
        byte[] frame = FrameStack.Start(new EthernetLayer(
                MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
                MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB])))
            .Then(new IPv4Layer(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102)))
            .Then(new IcmpV4EchoLayer(identifier: 1, sequenceNumber: 1))
            .CreateWithFixedValues().EmitFrame([1, 2, 3, 4]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
    }

    [Test]
    public async Task ConcurrentMaterializationYieldsTheSameFieldsAsASingleThread()
    {
        // The lazy populator is shared by all packets of the stack; materialising different ICMP packets on several
        // threads must give the same fields as one thread does.
        await ConcurrentMaterialization.AssertMatchesSingleThreadedResult(
            _DestUnreachableFrame(QuotedIpv4Datagram(0x45)),
            IcmpBasicTests.BuildIcmpFrame(type: 3, code: 4, secondWord: 1400, QuotedIpv4Datagram(0x45)),
            IcmpBasicTests.BuildIcmpFrame(type: 12, code: 0, secondWord: 0x14000000, QuotedIpv4Datagram(0x45)),
            IcmpBasicTests.BuildIcmpFrame(type: 13, code: 0, secondWord: 0, [1, 2, 3, 4])).ConfigureAwait(false);
    }
}

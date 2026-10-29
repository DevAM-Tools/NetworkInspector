// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for ICMP protocol parsing (RFC 792): echo, the error messages and their second header word, the checksum
/// status, the display tables and the packet info text. Frames are built with <see cref="IcmpV4Layer"/> and
/// <see cref="IcmpV4EchoLayer"/> on top of <see cref="FrameStack"/>.
/// </summary>
internal sealed class IcmpBasicTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    /// <summary>Builds Ethernet + IPv4 + a generic ICMP header (type, code, second word) + payload.</summary>
    internal static byte[] BuildIcmpFrame(byte type, byte code, uint secondWord, ReadOnlySpan<byte> payload)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        return FrameStack.Start(eth).Then(ip).Then(new IcmpV4Layer(type, code, secondWord)).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Builds an echo request or reply with the given identifier, sequence number and payload.</summary>
    private static byte[] _BuildEchoFrame(bool isReply, ushort identifier, ushort sequence, ReadOnlySpan<byte> payload)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        IcmpV4EchoLayer icmp = new(identifier: identifier, sequenceNumber: sequence, isReply: isReply);
        return FrameStack.Start(eth).Then(ip).Then(icmp).CreateWithFixedValues().EmitFrame(payload);
    }

    #region Echo

    [Test]
    public async Task ParseIcmpEchoRequestType()
    {
        byte[] frame = _BuildEchoFrame(isReply: false, identifier: 1, sequence: 1, [0xAB, 0xCD, 0xEF, 0x01]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // ICMP type 8 = Echo Request, code 0
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.type", 8).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.code", 0).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ParseIcmpEchoReplyType()
    {
        byte[] frame = _BuildEchoFrame(isReply: true, identifier: 1, sequence: 1, ReadOnlySpan<byte>.Empty);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.type", 0).ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments((ushort)0x0001, (ulong)0x0100)]
    [Arguments((ushort)0x1234, (ulong)0x3412)]
    [Arguments((ushort)0x0000, (ulong)0x0000)]
    public async Task EchoShowsBothByteOrdersOfIdentifierAndSequence(ushort value, ulong swapped)
    {
        byte[] frame = _BuildEchoFrame(isReply: false, identifier: value, sequence: value, ReadOnlySpan<byte>.Empty);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.ident", value).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.ident_le", swapped).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.seq", value).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.seq_le", swapped).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task EchoPayloadIsTheDataField()
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] frame = _BuildEchoFrame(isReply: false, identifier: 1, sequence: 1, payload);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmp.data", payload).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task EchoWithoutPayloadHasNoDataField()
    {
        byte[] frame = _BuildEchoFrame(isReply: false, identifier: 1, sequence: 1, ReadOnlySpan<byte>.Empty);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.data").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task EchoRequestInfoShowsIdentifierAndBothSequenceOrders()
    {
        byte[] frame = _BuildEchoFrame(isReply: false, identifier: 0x1234, sequence: 1, ReadOnlySpan<byte>.Empty);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.Info).IsEqualTo("Echo (ping) request (8) id=0x1234, seq=1/256");
        }
    }

    #endregion

    #region Checksum

    [Test]
    public async Task ChecksumStatusIsGoodAndBadWhenVerificationIsOn()
    {
        byte[] good = _BuildEchoFrame(isReply: false, identifier: 1, sequence: 1, [1, 2, 3, 4]);
        byte[] bad = (byte[])good.Clone();

        // The ICMP header starts after Ethernet (14) and IPv4 (20); the checksum is at offset 2 of the header. 0xFFFF
        // cannot be the right sum for this message, so the corrupted frame must be reported bad.
        BinaryPrimitives.WriteUInt16BigEndian(bad.AsSpan(14 + 20 + 2, 2), 0xFFFF);

        (Stack stack, Packet goodPacket) = ProtocolTestHelper.BuildAndParse(good, static s => s.PreloadValue("icmp.verify_checksum", SettingValue.Bool(true)));
        using (stack)
        {
            Packet badPacket = ProtocolTestHelper.ParseFrame(stack, bad, packetIndex: 1, Timestamp.FromMillis(1));
            await ProtocolTestHelper.AssertStringField(stack, goodPacket, "icmp.checksum.status", "[Good]").ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, badPacket, "icmp.checksum.status", "[Bad]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ChecksumStatusIsAbsentWhenVerificationIsOff()
    {
        byte[] frame = _BuildEchoFrame(isReply: false, identifier: 1, sequence: 1, [1, 2, 3, 4]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.checksum.status").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ChecksumStatusIndexGroupIsRecordedWhenVerificationIsOn()
    {
        byte[] frame = _BuildEchoFrame(isReply: false, identifier: 1, sequence: 1, [1, 2, 3, 4]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame, ("icmp.verify_checksum", SettingValue.Bool(true))).ConfigureAwait(false);
    }

    [Test]
    public async Task MessageCutByTheCaptureLengthIsUnverified()
    {
        // The IPv4 header declares 16 payload bytes more than the frame holds: the capture length cut the message, so the
        // sum cannot be checked and the status must not blame the sender.
        byte[] frame = _BuildEchoFrame(isReply: false, identifier: 1, sequence: 1, new byte[32]);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(14 + 2, 2), (ushort)(20 + 8 + 32 + 16));
        byte[] cut = frame[..^16];

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(cut, ("icmp.verify_checksum", SettingValue.Bool(true)));
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(cut, static s => s.PreloadValue("icmp.verify_checksum", SettingValue.Bool(true)));
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "icmp.checksum.status", "[Unverified]");
        }
    }

    [Test]
    public async Task EthernetPaddingAfterTheMessageDoesNotMakeItUnverified()
    {
        // Bytes after the declared end (link-layer padding) are not part of the message and not a cut.
        byte[] frame = _BuildEchoFrame(isReply: false, identifier: 1, sequence: 1, [1, 2, 3, 4]);
        byte[] padded = [.. frame, .. new byte[10]];

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(padded, static s => s.PreloadValue("icmp.verify_checksum", SettingValue.Bool(true)));
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "icmp.checksum.status", "[Good]");
        }
    }

    #endregion

    #region Error messages: second header word

    [Test]
    public async Task FragmentationNeededShowsTheNextHopMtu()
    {
        byte[] frame = BuildIcmpFrame(type: 3, code: 4, secondWord: 1400, IcmpHardeningTests.QuotedIpv4Datagram(0x45));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.mtu", 1400).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.unused").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task PortUnreachableHasNoMtuAndShowsTheUnusedWord()
    {
        byte[] frame = BuildIcmpFrame(type: 3, code: 3, secondWord: 0, IcmpHardeningTests.QuotedIpv4Datagram(0x45));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.mtu").ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmp.unused", [0, 0, 0, 0]).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ParameterProblemShowsThePointer()
    {
        byte[] frame = BuildIcmpFrame(type: 12, code: 0, secondWord: 0x14000000, IcmpHardeningTests.QuotedIpv4Datagram(0x45));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.pointer", 20).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.length").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task TimeExceededWithRfc4884LengthShowsTheLengthInBytes()
    {
        // Byte 5 of the header is the length in 32-bit words: 5 words are 20 bytes.
        byte[] frame = BuildIcmpFrame(type: 11, code: 0, secondWord: 0x00050000, IcmpHardeningTests.QuotedIpv4Datagram(0x45));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.length", 5).ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmp.length", "20 bytes").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.unused").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task SourceQuenchIsAnErrorTypeThatQuotesTheDatagram()
    {
        byte[] frame = BuildIcmpFrame(type: 4, code: 0, secondWord: 0, IcmpHardeningTests.QuotedIpv4Datagram(0x45));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "icmp.orig_ip.src", "10.0.0.1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.data").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task RedirectShowsTheGatewayAndTheQuotedHeader()
    {
        byte[] frame = BuildIcmpFrame(type: 5, code: 1, secondWord: 0x0A000001, IcmpHardeningTests.QuotedIpv4Datagram(0x45));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "icmp.redir_gw", "10.0.0.1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "icmp.orig_ip.dst", "10.0.0.2").ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments((byte)10)]
    [Arguments((byte)15)]
    [Arguments((byte)16)]
    public async Task InformationalTypesWithoutALayoutShowTheirBodyAsData(byte type)
    {
        byte[] frame = BuildIcmpFrame(type, code: 0, secondWord: 0, [1, 2, 3, 4]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmp.data", [1, 2, 3, 4]).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.ident").ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments((byte)13)]
    [Arguments((byte)14)]
    public async Task TimestampMessagesShowIdentifierSequenceAndTheThreeTimestamps(byte type)
    {
        byte[] timestamps = [0, 0x36, 0xEE, 0x80, 0, 0x36, 0xEE, 0x85, 0, 0x36, 0xEE, 0x86];
        byte[] frame = BuildIcmpFrame(type, code: 0, secondWord: 0x12340001, timestamps);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.ident", 0x1234).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.seq", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.originate_timestamp", 3600000).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.receive_timestamp", 3600005).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.transmit_timestamp", 3600006).ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmp.originate_timestamp", "3600000 ms").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.data").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ATimestampMessageCutInsideTheSecondTimestampStopsAtTheLastCompleteOneAndKeepsTheRestAsData()
    {
        // Originate is complete, receive has two of four bytes: the partial value is not shown, its bytes stay visible as data.
        byte[] frame = BuildIcmpFrame(type: 13, code: 0, secondWord: 0, [0, 0, 0, 7, 0xAA, 0xBB]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.originate_timestamp", 7).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.receive_timestamp").ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmp.data", [0xAA, 0xBB]).ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments((byte)17)]
    [Arguments((byte)18)]
    public async Task AddressMaskMessagesShowIdentifierSequenceAndTheMask(byte type)
    {
        byte[] frame = BuildIcmpFrame(type, code: 0, secondWord: 0x00050006, [255, 255, 255, 0]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.ident", 5).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.seq", 6).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "icmp.address_mask", "255.255.255.0").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.data").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task AnAddressMaskRequestWithoutTheMaskShowsNoMaskField()
    {
        byte[] frame = BuildIcmpFrame(type: 17, code: 0, secondWord: 0x00050006, ReadOnlySpan<byte>.Empty);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.address_mask").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.ident", 5).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ARouterAdvertisementShowsTheCountTheEntrySizeTheLifetimeAndEveryEntry()
    {
        // Two entries of two words: 10.0.0.1 with preference 0 and 10.0.0.2 with preference -1 (0xFFFFFFFF), lifetime 1800 s.
        byte[] entries = [10, 0, 0, 1, 0, 0, 0, 0, 10, 0, 0, 2, 0xFF, 0xFF, 0xFF, 0xFF];
        byte[] frame = BuildIcmpFrame(type: 9, code: 0, secondWord: 0x02020708, entries);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.num_addrs", 2).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.addr_entry_size", 2).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.lifetime", 0x0708).ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmp.lifetime", "1800 s").ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "icmp.router_address", "10.0.0.1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertI64Field(stack, packet, "icmp.pref_level", 0).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.data").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ARouterAdvertisementWithMoreEntriesAnnouncedThanPresentShowsOnlyTheCompleteOnes()
    {
        // The count says 3, one whole entry and four more bytes are present: the entry is shown, the four bytes stay as data.
        byte[] frame = BuildIcmpFrame(type: 9, code: 0, secondWord: 0x03020708, [10, 0, 0, 1, 0, 0, 0, 5, 10, 0, 0, 2]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.num_addrs", 3).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv4Field(stack, packet, "icmp.router_address", "10.0.0.1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertI64Field(stack, packet, "icmp.pref_level", 5).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmp.data", [10, 0, 0, 2]).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ARouterAdvertisementWithAnotherEntrySizeLeavesTheEntriesAsData()
    {
        byte[] frame = BuildIcmpFrame(type: 9, code: 0, secondWord: 0x01030708, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmp.addr_entry_size", 3).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmp.router_address").ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmp.data", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]).ConfigureAwait(false);
        }
    }

    #endregion

    #region Display text and info

    [Test]
    [Arguments((byte)3, (byte)3, "Port unreachable (3)")]
    [Arguments((byte)11, (byte)0, "TTL exceeded in transit (0)")]
    [Arguments((byte)8, (byte)0, "0")]
    public async Task CodeHasDisplayText(byte type, byte code, string expected)
    {
        byte[] frame = BuildIcmpFrame(type, code, secondWord: 0, IcmpHardeningTests.QuotedIpv4Datagram(0x45));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmp.code", expected).ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments((byte)6, (byte)0, "Alternate address for host (0)")]
    [Arguments((byte)9, (byte)16, "Does not route common traffic (16)")]
    [Arguments((byte)40, (byte)3, "Decryption Failed (3)")]
    [Arguments((byte)43, (byte)2, "No Such Interface (2)")]
    [Arguments((byte)3, (byte)200, "200")]
    public async Task CodeTablesNameTheRegisteredValues(byte type, byte code, string expected) =>
        await Assert.That(DisplayTables.GetIcmpCodeDisplayText(type, code)).IsEqualTo(expected);

    [Test]
    [Arguments((byte)6, "Alternate host address (6)")]
    [Arguments((byte)31, "Datagram Conversion Error (31)")]
    [Arguments((byte)39, "SKIP (39)")]
    [Arguments((byte)41, "Experimental mobility protocols (41)")]
    [Arguments((byte)250, "250")]
    public async Task TypeTableNamesTheRegisteredValues(byte type, string expected) =>
        await Assert.That(DisplayTables.GetIcmpTypeDisplayText(type)).IsEqualTo(expected);

    [Test]
    public async Task ErrorInfoNamesTheCode()
    {
        byte[] frame = BuildIcmpFrame(type: 3, code: 3, secondWord: 0, IcmpHardeningTests.QuotedIpv4Datagram(0x45));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.Info).IsEqualTo("Destination unreachable (3): Port unreachable (3)");
        }
    }

    [Test]
    public async Task InformationalInfoIsTheTypeName()
    {
        byte[] frame = BuildIcmpFrame(type: 13, code: 0, secondWord: 0, [1, 2, 3, 4]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.Info).IsEqualTo("Timestamp request (13)");
        }
    }

    /// <summary>
    /// Measures the bytes the display-table lookups of the ICMP, ICMPv6 and Neighbor Discovery parsers allocate. A
    /// synchronous method so that no async state machine allocation is counted.
    /// </summary>
    private static long _MeasureTableLookupAllocations()
    {
        // Warm up: the first call runs the type initialiser, which builds the tables once.
        _ = DisplayTables.GetIcmpContainerTitle(8);
        _ = DisplayTables.GetIcmpv6ContainerTitle(128);
        _ = DisplayTables.GetByteCountDisplayText(64);
        _ = DisplayTables.GetNdpOptionDisplayText(3);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            byte b = (byte)(i & 0xFF);
            _ = DisplayTables.GetIcmpContainerTitle(b);
            _ = DisplayTables.GetIcmpv6ContainerTitle(b);
            _ = DisplayTables.GetByteCountDisplayText(i * 2);
            _ = DisplayTables.GetIcmpCodeDisplayText(8, 0);
            _ = DisplayTables.GetIcmpv6CodeDisplayText(128, b);
            _ = DisplayTables.GetNdpOptionDisplayText(b);
            _ = DisplayTables.GetNdpOptionLengthDisplayText(b);
            _ = DisplayTables.GetDecimalText(b);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public async Task TableLookupsOfTheEagerAndPopulatePathsAllocateNothing()
    {
        long allocated = _MeasureTableLookupAllocations();

        await Assert.That(allocated).IsEqualTo(0L);
    }

    [Test]
    public async Task ContainerTitleNamesTheTypeFromASharedTable()
    {
        byte[] frame = _BuildEchoFrame(isReply: false, identifier: 1, sequence: 1, ReadOnlySpan<byte>.Empty);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmp", "Internet Control Message Protocol, Echo (ping) request (8)");
            await Assert.That(ReferenceEquals(DisplayTables.GetIcmpContainerTitle(8), DisplayTables.GetIcmpContainerTitle(8))).IsTrue();
        }
    }

    [Test]
    public async Task ContainerTextNamesTheStoredMessageLength()
    {
        // 8 header bytes plus a 4-byte payload: the representation must say 12, not the fixed header size.
        byte[] frame = _BuildEchoFrame(isReply: false, identifier: 1, sequence: 1, [1, 2, 3, 4]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            FieldId? id = stack.GetFieldId("icmp");
            await Assert.That(id).IsNotNull();
            bool found = packet.TryGetFieldValue(id!.Value, out FieldValue value, materialize: false);
            await Assert.That(found).IsTrue();
            await Assert.That(value.CustomRepresentation.AsString).IsEqualTo("12 bytes");
        }
    }

    [Test]
    [Arguments(0, "0 bytes")]
    [Arguments(8, "8 bytes")]
    [Arguments(1500, "1500 bytes")]
    [Arguments(2048, "2048 bytes")]
    [Arguments(2049, "2049 bytes")]
    [Arguments(-1, "-1 bytes")]
    public async Task ByteCountTextCoversTheTableAndTheFallback(int count, string expected) =>
        await Assert.That(DisplayTables.GetByteCountDisplayText(count)).IsEqualTo(expected);

    [Test]
    public async Task ByteCountTextInsideTheTableIsAShared​String() =>
        await Assert.That(ReferenceEquals(DisplayTables.GetByteCountDisplayText(64), DisplayTables.GetByteCountDisplayText(64))).IsTrue();

    #endregion

    #region Robustness

    [Test]
    public async Task TsharkCrossValidationIcmpType()
    {
        byte[] frame = _BuildEchoFrame(isReply: false, identifier: 1, sequence: 1, ReadOnlySpan<byte>.Empty);

        string? tsharkValue = TsharkVerifier.GetFieldValue(frame, "icmp.type");
        if (tsharkValue is null)
        {
            return;
        }

        await Assert.That(tsharkValue).IsEqualTo("8");
    }

    [Test]
    public async Task ShortIcmpMessageDoesNotCrash()
    {
        // Ethernet (14) + IPv4 (20) + only 2 bytes of ICMP: shorter than the 8-byte header.
        byte[] shortFrame = new byte[36];
        shortFrame[12] = 0x08;
        shortFrame[13] = 0x00;
        shortFrame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(shortFrame.AsSpan(16), 22);
        shortFrame[23] = 1;

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(shortFrame);
        using (stack)
        {
            await Assert.That(packet.IsFinalized).IsTrue();
            await ProtocolTestHelper.AssertProtocolNotPresent(stack, packet, "icmp").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task IcmpFrameIsRecordedInTheProtocolAndFieldIndex()
    {
        byte[] frame = _BuildEchoFrame(isReply: false, identifier: 0x1234, sequence: 1, [1, 2, 3, 4]);

        using Stack stack = ProtocolTestHelper.BuildStack();
        NetworkInspector.Core.Index.PacketIndex index = new(stack);
        Frame parsedFrame = Frame.Create(
            new FrameId(0), Timestamp.FromSecs(0), frame, LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new(index: index);
        bool parsed = Packet.TryParse(new PacketId(0), stack, parsedFrame, in options, out Packet? packet, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(packet).IsNotNull();
        ProtocolId? icmpId = stack.GetProtocolId("icmp");
        await Assert.That(icmpId).IsNotNull();
        await Assert.That(index.GetProtocolBitmap(icmpId!.Value).Contains(0)).IsTrue();
        FieldId? typeId = stack.GetFieldId("icmp.type");
        await Assert.That(index.GetFieldBitmap(typeId!.Value).Contains(0)).IsTrue();
    }

    #endregion
}

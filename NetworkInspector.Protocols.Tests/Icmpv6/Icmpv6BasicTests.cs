// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// ICMPv6 happy-path tests (RFC 4443) — Echo Request/Reply, the four error messages with their second header word and
/// quoted datagram, the code and type display text and the packet info text.
/// Frames are constructed via <see cref="IcmpV6Layer"/> on top of <see cref="FrameStack"/>.
/// </summary>
internal sealed class Icmpv6BasicTests
{
    internal static byte[] BuildIcmpV6Frame(byte type, byte code, ReadOnlySpan<byte> body)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
            MacAddress.FromBytes([0x33, 0x33, 0x00, 0x00, 0x00, 0x01]));
        IPv6Layer ip = new(
            IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]),
            IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]));
        return FrameStack.Start(eth).Then(ip).Then(new IcmpV6Layer(type, code)).CreateWithFixedValues().EmitFrame(body);
    }

    /// <summary>
    /// The body of an error message: the 4-byte word at offset 4 followed by a quoted IPv6 datagram (40-byte header, next
    /// header UDP) and the first eight bytes of the original UDP datagram (ports 53 and 40000).
    /// </summary>
    internal static byte[] ErrorBody(uint word, byte nextHeader = 17, int quotedLength = 48)
    {
        byte[] body = new byte[4 + 48];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), word);
        Span<byte> quote = body.AsSpan(4);
        quote[0] = 0x60; // version 6
        quote[6] = nextHeader;
        quote[7] = 64; // hop limit
        quote[8] = 0x20; // source 2001:db8::1
        quote[9] = 0x01;
        quote[10] = 0x0d;
        quote[11] = 0xb8;
        quote[23] = 0x01;
        quote[24] = 0x20; // destination 2001:db8::2
        quote[25] = 0x01;
        quote[26] = 0x0d;
        quote[27] = 0xb8;
        quote[39] = 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(quote[40..], 53);
        BinaryPrimitives.WriteUInt16BigEndian(quote[42..], 40000);

        // The quote is built at full length first so the writes above are always in range; shorter quotes are cut here.
        return body[..(4 + quotedLength)];
    }

    #region Echo

    [Test]
    public async Task ParseEchoRequestTypeAndIdentifier()
    {
        // Echo Request body = identifier(2) + sequence(2) + payload
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(0, 2), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2, 2), 0x0001);

        byte[] frame = BuildIcmpV6Frame(type: 128, code: 0, body);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.type", 128).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.code", 0).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.echo.identifier", 0x1234).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.echo.sequence_number", 1).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ParseEchoReplyType()
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(0, 2), 0x4242);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2, 2), 0x0007);

        byte[] frame = BuildIcmpV6Frame(type: 129, code: 0, body);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.type", 129).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.echo.sequence_number", 7).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task EchoRequestWithPayloadShowsData()
    {
        byte[] body = [0x12, 0x34, 0x00, 0x01, 0xCA, 0xFE, 0xBA, 0xBE];
        byte[] frame = BuildIcmpV6Frame(type: 128, code: 0, body);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmpv6.data", [0xCA, 0xFE, 0xBA, 0xBE]).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task EchoRequestWithoutPayloadHasNoDataField()
    {
        byte[] frame = BuildIcmpV6Frame(type: 128, code: 0, [0x12, 0x34, 0x00, 0x01]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.data").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task EchoInfoShowsIdentifierAndSequence()
    {
        byte[] frame = BuildIcmpV6Frame(type: 128, code: 0, [0x12, 0x34, 0x00, 0x07, 0xAA]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.Info).IsEqualTo("Echo (ping) request (128) id=0x1234, seq=7");
        }
    }

    [Test]
    public async Task ParseChecksumFieldPresentAsHex()
    {
        byte[] frame = BuildIcmpV6Frame(type: 128, code: 0, new byte[4]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // The exact checksum depends on the payload; check that the field exists and is non-zero.
            FieldId? id = stack.GetFieldId("icmpv6.checksum");
            await Assert.That(id).IsNotNull();
            bool found = packet.TryGetFieldValue(id!.Value, out FieldValue value, materialize: true);
            await Assert.That(found).IsTrue();
            await Assert.That(value.Data.TryGetAsU64(out ulong cs)).IsTrue();
            await Assert.That(cs).IsNotEqualTo((ulong)0).Because("the frame builder patched the checksum");
        }
    }

    #endregion

    #region Error messages

    [Test]
    public async Task PacketTooBigShowsTheMtu()
    {
        byte[] frame = BuildIcmpV6Frame(type: 2, code: 0, ErrorBody(1280));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.type", 2).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mtu", 1280).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.pointer").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.reserved").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ParameterProblemShowsThePointer()
    {
        byte[] frame = BuildIcmpV6Frame(type: 4, code: 1, ErrorBody(40));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.pointer", 40).ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.code", "Unrecognized Next Header type encountered (1)").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task DestinationUnreachableWithoutRfc4884LengthShowsFourReservedBytes()
    {
        byte[] frame = BuildIcmpV6Frame(type: 1, code: 4, ErrorBody(0));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmpv6.reserved", [0, 0, 0, 0]).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.length").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task DestinationUnreachableWithRfc4884LengthShowsLengthInBytes()
    {
        // Byte 4 = 5 units of 8 bytes = 40 bytes; the remaining three bytes are reserved.
        byte[] frame = BuildIcmpV6Frame(type: 1, code: 4, ErrorBody(0x05000000));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.length", 5).ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.length", "40 bytes").ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmpv6.reserved", [0, 0, 0]).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task TimeExceededShowsTheQuotedHeaderAndPorts()
    {
        byte[] frame = BuildIcmpV6Frame(type: 3, code: 0, ErrorBody(0));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.orig_ipv6.src", "2001:db8::1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.orig_ipv6.dst", "2001:db8::2").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.orig_ipv6.nxt", 17).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.orig_ipv6.hlim", 64).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.orig_ipv6.srcport", 53).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.orig_ipv6.dstport", 40000).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.data").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task QuotedDatagramWithAnExtensionHeaderShowsNoPorts()
    {
        // Next header 0 (hop-by-hop options): the transport header is not directly behind the fixed header.
        byte[] frame = BuildIcmpV6Frame(type: 3, code: 0, ErrorBody(0, nextHeader: 0));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "icmpv6.orig_ipv6").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.orig_ipv6.srcport").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task QuotedDatagramShorterThanThePortsShowsTheHeaderOnly()
    {
        byte[] frame = BuildIcmpV6Frame(type: 3, code: 0, ErrorBody(0, quotedLength: 42));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "icmpv6.orig_ipv6").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.orig_ipv6.srcport").ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments(8)]
    [Arguments(39)]
    public async Task QuoteShorterThanAnIpv6HeaderIsShownAsData(int quotedLength)
    {
        byte[] body = ErrorBody(0, quotedLength: quotedLength);
        byte[] frame = BuildIcmpV6Frame(type: 1, code: 0, body);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.orig_ipv6").ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmpv6.data", body[4..]).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task QuoteOfAnIpv4DatagramIsShownAsData()
    {
        byte[] body = ErrorBody(0);
        body[4] = 0x45; // version nibble 4
        byte[] frame = BuildIcmpV6Frame(type: 1, code: 0, body);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.orig_ipv6").ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmpv6.data", body[4..]).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ErrorWithoutAQuoteHasNoDataAndNoOriginalHeader()
    {
        byte[] frame = BuildIcmpV6Frame(type: 1, code: 0, new byte[4]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.data").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.orig_ipv6").ConfigureAwait(false);
        }
    }

    #endregion

    #region Display text and info

    [Test]
    public async Task DestinationUnreachablePortUnreachableHasCodeText()
    {
        byte[] frame = BuildIcmpV6Frame(type: 1, code: 4, ErrorBody(0));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.code", "Port unreachable (4)").ConfigureAwait(false);
            await Assert.That(packet.Info).IsEqualTo("Destination Unreachable (1): Port unreachable (4)");
        }
    }

    [Test]
    [Arguments((byte)1, (byte)0, "No route to destination (0)")]
    [Arguments((byte)1, (byte)8, "Headers too long (8)")]
    [Arguments((byte)3, (byte)1, "Fragment reassembly time exceeded (1)")]
    [Arguments((byte)4, (byte)10, "Option too big (10)")]
    [Arguments((byte)138, (byte)255, "Sequence number reset (255)")]
    [Arguments((byte)139, (byte)2, "Query subject = IPv4 addresses (2)")]
    [Arguments((byte)140, (byte)1, "Refused (1)")]
    [Arguments((byte)161, (byte)4, "Multiple Interfaces Satisfy Query (4)")]
    [Arguments((byte)128, (byte)0, "0")]
    [Arguments((byte)1, (byte)99, "99")]
    public async Task CodeTableNamesTheRegisteredValues(byte type, byte code, string expected) =>
        await Assert.That(DisplayTables.GetIcmpv6CodeDisplayText(type, code)).IsEqualTo(expected);

    [Test]
    [Arguments((byte)100, "Private experimentation (100)")]
    [Arguments((byte)144, "Home Agent Address Discovery Request (144)")]
    [Arguments((byte)160, "Extended Echo request (160)")]
    [Arguments((byte)255, "Reserved for expansion of ICMPv6 informational messages (255)")]
    [Arguments((byte)50, "50")]
    public async Task TypeTableNamesTheRegisteredValues(byte type, string expected) =>
        await Assert.That(DisplayTables.GetIcmpv6TypeDisplayText(type)).IsEqualTo(expected);

    [Test]
    public async Task ContainerTitleNamesTheTypeFromASharedTable()
    {
        byte[] frame = BuildIcmpV6Frame(type: 128, code: 0, [0x12, 0x34, 0x00, 0x01]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6", "Internet Control Message Protocol v6, Echo (ping) request (128)");
            await Assert.That(ReferenceEquals(DisplayTables.GetIcmpv6ContainerTitle(128), DisplayTables.GetIcmpv6ContainerTitle(128))).IsTrue();
        }
    }

    [Test]
    public async Task ContainerTextNamesTheStoredMessageLength()
    {
        byte[] frame = BuildIcmpV6Frame(type: 128, code: 0, [0x12, 0x34, 0x00, 0x01, 0xAA, 0xBB]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            FieldId? id = stack.GetFieldId("icmpv6");
            bool found = packet.TryGetFieldValue(id!.Value, out FieldValue value, materialize: false);
            await Assert.That(found).IsTrue();
            await Assert.That(value.CustomRepresentation.AsString).IsEqualTo("10 bytes");
        }
    }

    #endregion

    #region Index and robustness

    [Test]
    public async Task Icmpv6FrameIsRecordedInTheProtocolAndFieldIndex()
    {
        byte[] frame = BuildIcmpV6Frame(type: 128, code: 0, [0x12, 0x34, 0x00, 0x01, 0xAA]);

        using Stack stack = ProtocolTestHelper.BuildStack();
        NetworkInspector.Core.Index.PacketIndex index = new(stack);
        Frame parsedFrame = Frame.Create(
            new FrameId(0), Timestamp.FromSecs(0), frame, LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new(index: index);
        bool parsed = Packet.TryParse(new PacketId(0), stack, parsedFrame, in options, out Packet? packet, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(packet).IsNotNull();
        ProtocolId? icmpv6Id = stack.GetProtocolId("icmpv6");
        await Assert.That(index.GetProtocolBitmap(icmpv6Id!.Value).Contains(0)).IsTrue();
        FieldId? typeId = stack.GetFieldId("icmpv6.type");
        await Assert.That(index.GetFieldBitmap(typeId!.Value).Contains(0)).IsTrue();
    }

    [Test]
    public async Task MessageShorterThanTheEightByteMinimumIsNotParsedAsIcmpv6()
    {
        // 4 header bytes and nothing else: below the 8-byte minimum, so no ICMPv6 container may appear.
        byte[] frame = BuildIcmpV6Frame(type: 128, code: 0, ReadOnlySpan<byte>.Empty);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.IsFinalized).IsTrue();
            await ProtocolTestHelper.AssertProtocolNotPresent(stack, packet, "icmpv6");
        }
    }

    #endregion

    #region Concurrency

    [Test]
    public async Task ConcurrentMaterializationYieldsTheSameFieldsAsASingleThread() =>
        await ConcurrentMaterialization.AssertMatchesSingleThreadedResult(
            BuildIcmpV6Frame(type: 128, code: 0, [0x12, 0x34, 0x00, 0x01, 0xAA]),
            BuildIcmpV6Frame(type: 2, code: 0, ErrorBody(1280)),
            BuildIcmpV6Frame(type: 1, code: 4, ErrorBody(0x05000000)),
            BuildIcmpV6Frame(type: 4, code: 0, ErrorBody(40))).ConfigureAwait(false);

    #endregion
}

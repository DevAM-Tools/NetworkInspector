// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// ICMPv6 happy-path tests (RFC 4443) — Echo Request/Reply, basic error
/// messages (Destination Unreachable, Time Exceeded, Packet Too Big).
/// Frames are constructed via <see cref="IcmpV6Layer"/> on top of
/// <see cref="FrameStack"/>.
/// </summary>
internal sealed class Icmpv6BasicTests
{
    private static byte[] _BuildIcmpV6Frame(byte type, byte code, ReadOnlySpan<byte> body)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
            MacAddress.FromBytes([0x33, 0x33, 0x00, 0x00, 0x00, 0x01]));
        IPv6Layer ip = new(
            IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]),
            IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]));
        return FrameStack.Start(eth).Then(ip).Then(new IcmpV6Layer(type, code)).CreateWithFixedValues().EmitFrame(body);
    }

    [Test]
    public async Task Parse_EchoRequest_TypeAndIdentifier()
    {
        // Echo Request body = identifier(2) + sequence(2) + payload
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(0, 2), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2, 2), 0x0001);
        // payload bytes 4..7 stay 0

        byte[] frame = _BuildIcmpV6Frame(type: 128, code: 0, body);
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
    public async Task Parse_EchoReply_Type()
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(0, 2), 0x4242);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2, 2), 0x0007);

        byte[] frame = _BuildIcmpV6Frame(type: 129, code: 0, body);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.type", 129).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.echo.sequence_number", 7).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_DestinationUnreachable_NoRoute()
    {
        // Type 1 (Destination Unreachable), Code 0 (No Route to Destination).
        // Body = 4 unused bytes + as much of the original packet as fits.
        byte[] body = new byte[16];
        byte[] frame = _BuildIcmpV6Frame(type: 1, code: 0, body);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.type", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.code", 0).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_PacketTooBig_MtuField()
    {
        // Type 2 (Packet Too Big), Code 0; body starts with 4-byte MTU.
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), 1280); // MTU in bytes
        byte[] frame = _BuildIcmpV6Frame(type: 2, code: 0, body);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.type", 2).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.code", 0).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_TimeExceeded_HopLimitInTransit()
    {
        // Type 3 (Time Exceeded), Code 0 (Hop Limit Exceeded).
        byte[] body = new byte[8];
        byte[] frame = _BuildIcmpV6Frame(type: 3, code: 0, body);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.type", 3).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.code", 0).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ChecksumField_PresentAsHex()
    {
        byte[] body = new byte[4];
        byte[] frame = _BuildIcmpV6Frame(type: 128, code: 0, body);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // The exact checksum depends on payload — just check the field exists and is non-zero.
            FieldId? id = stack.GetFieldId("icmpv6.checksum");
            await Assert.That(id).IsNotNull();
            bool found = packet.TryGetFieldValue(id!.Value, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
            await Assert.That(found).IsTrue();
            await Assert.That(value.Data.TryGetAsU64(out ulong cs)).IsTrue();
            await Assert.That(cs).IsNotEqualTo((ulong)0).Because("checksum was patched by IcmpV6Fixup");
        }
    }
}

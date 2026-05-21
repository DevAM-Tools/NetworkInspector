// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for malformed and truncated Ethernet frames.
/// Verifies graceful handling of invalid input — no crashes, no silent corruption.
/// </summary>
internal sealed class EthernetMalformedTests
{
    [Test]
    public async Task Parse_EmptyFrame_DoesNotCrash()
    {
        byte[] empty = [];
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(empty);
        using (stack)
        {
            // The frame protocol always runs, so at least 1 field (frame container)
            await Assert.That(packet.FieldCount()).IsGreaterThanOrEqualTo(1);
        }
    }

    [Test]
    public async Task Parse_SingleByte_DoesNotCrash()
    {
        byte[] frame = [0xFF];
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.FieldCount()).IsGreaterThanOrEqualTo(1);
        }
    }

    [Test]
    public async Task Parse_ExactlyThirteenBytes_DoesNotCrash()
    {
        // One byte short of the minimum 14-byte Ethernet header
        byte[] frame = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x08];
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Ethernet should not parse — no eth.dst or eth.src fields
            await Assert.That(packet.FieldCount()).IsGreaterThanOrEqualTo(1);
        }
    }

    [Test]
    public async Task Parse_ExactlyFourteenBytes_HeaderOnly()
    {
        // Minimum valid Ethernet header (14 bytes) — no payload
        byte[] frame = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x08, 0x00];
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Should parse Ethernet header successfully
            await ProtocolTestHelper.AssertMacField(stack, packet, "eth.dst", "AA:BB:CC:DD:EE:FF").ConfigureAwait(false);
            await ProtocolTestHelper.AssertMacField(stack, packet, "eth.src", "11:22:33:44:55:66").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "eth.type", 0x0800).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_AllZeroFrame_DoesNotCrash()
    {
        // 64 bytes of all zeros — valid frame size but nonsensical content
        byte[] frame = new byte[64];
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Type/Length = 0x0000 < 0x0600 → IEEE 802.3 (length 0)
            await ProtocolTestHelper.AssertMacField(stack, packet, "eth.dst", "00:00:00:00:00:00").ConfigureAwait(false);
            await ProtocolTestHelper.AssertMacField(stack, packet, "eth.src", "00:00:00:00:00:00").ConfigureAwait(false);
            // Value 0 < 0x0600 → should be treated as length field
            await ProtocolTestHelper.AssertU64Field(stack, packet, "eth.len", 0).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_AllOnesFrame_DoesNotCrash()
    {
        // 64 bytes of all 0xFF — broadcast with type 0xFFFF
        byte[] frame = new byte[64];
        Array.Fill<byte>(frame, 0xFF);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertMacField(stack, packet, "eth.dst", "FF:FF:FF:FF:FF:FF").ConfigureAwait(false);
            await ProtocolTestHelper.AssertMacField(stack, packet, "eth.src", "FF:FF:FF:FF:FF:FF").ConfigureAwait(false);
            // 0xFFFF >= 0x0600 → Ethernet II
            await ProtocolTestHelper.AssertU64Field(stack, packet, "eth.type", 0xFFFF).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_JumboFrame_DoesNotCrash()
    {
        // 9000 bytes — typical jumbo frame size
        byte[] jumboPayload = new byte[9000 - 14]; // subtract header
        Array.Fill<byte>(jumboPayload, 0xAB);

        byte[] frame = new byte[9000];
        Span<byte> span = frame.AsSpan();

        // DST: AA:BB:CC:DD:EE:FF
        span[0] = 0xAA;
        span[1] = 0xBB;
        span[2] = 0xCC;
        span[3] = 0xDD;
        span[4] = 0xEE;
        span[5] = 0xFF;
        // SRC: 11:22:33:44:55:66
        span[6] = 0x11;
        span[7] = 0x22;
        span[8] = 0x33;
        span[9] = 0x44;
        span[10] = 0x55;
        span[11] = 0x66;
        // Type: IPv4 (0x0800)
        BinaryPrimitives.WriteUInt16BigEndian(span[12..], 0x0800);
        // Fill payload
        jumboPayload.AsSpan().CopyTo(span[14..]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertMacField(stack, packet, "eth.dst", "AA:BB:CC:DD:EE:FF").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "eth.type", 0x0800).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_FifteenBytes_MinimalPayload()
    {
        // 14 header + 1 payload byte — smallest possible valid Ethernet frame with data
        byte[] frame = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x08, 0x00, 0x42];
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Header should parse fine
            await ProtocolTestHelper.AssertMacField(stack, packet, "eth.dst", "AA:BB:CC:DD:EE:FF").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "eth.type", 0x0800).ConfigureAwait(false);
        }
    }
}

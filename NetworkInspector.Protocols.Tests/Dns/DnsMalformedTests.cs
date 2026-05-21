// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// DNS edge cases — name compression and malformed packets. Verifies the
/// parser handles pointer-based decompression correctly and refuses to spin
/// on infinite-loop pointers.
/// Frames are constructed via the <see cref="FrameStack"/> directly.
/// </summary>
internal sealed class DnsMalformedTests
{
    private static byte[] WrapDnsUdp(ReadOnlySpan<byte> dnsPayload)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(dnsPayload);
    }

    /// <summary>Writes a 12-byte DNS header into <paramref name="dst"/> at offset 0.</summary>
    private static void WriteDnsHeader(
        Span<byte> dst, ushort id, ushort flags,
        ushort qdCount, ushort anCount, ushort nsCount, ushort arCount)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst[0..], id);
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..], flags);
        BinaryPrimitives.WriteUInt16BigEndian(dst[4..], qdCount);
        BinaryPrimitives.WriteUInt16BigEndian(dst[6..], anCount);
        BinaryPrimitives.WriteUInt16BigEndian(dst[8..], nsCount);
        BinaryPrimitives.WriteUInt16BigEndian(dst[10..], arCount);
    }

    [Test]
    public async Task Parse_TruncatedHeader_DoesNotThrow()
    {
        // Only 6 bytes of DNS header — must be tolerated without crash.
        byte[] payload = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), 0xBEEF);
        byte[] frame = WrapDnsUdp(payload);
        (Stack stack, _) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        { /* parse succeeded if we got here */
        }
    }

    [Test]
    public async Task Parse_NamePointerLoop_DoesNotInfiniteLoop()
    {
        // Hand-craft a payload where the question name is a pointer to itself.
        // Header(12) + ptr(2) [points back to offset 12 = itself] + qtype(2) + qclass(2)
        byte[] payload = new byte[20];
        WriteDnsHeader(payload, id: 1, flags: 0x0100,
            qdCount: 1, anCount: 0, nsCount: 0, arCount: 0);
        // Self-referential pointer at offset 12.
        byte[] ptr = DnsLayer.EncodeNamePointer(12);
        ptr.CopyTo(payload.AsSpan(12));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(14, 2), DnsLayer.DnsType.A);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(16, 2), 1);

        byte[] frame = WrapDnsUdp(payload);
        // Must terminate quickly — TUnit times out otherwise.
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Header still parses correctly.
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dns.id", 1).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ValidNamePointer_ResolvesCorrectly()
    {
        // Build a response where the answer's name is a pointer to the question name.
        // BuildResponseSingleRR already does this — ensure the pointer is followed correctly.
        byte[] rdata = [1, 2, 3, 4];
        DnsLayer dns = DnsLayer.BuildResponseSingleRR(
            1, "alpha.beta.example.com", DnsLayer.DnsType.A, rdata, ttlSeconds: 1);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(5353, 53);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(
                stack, packet, "dns.resp.name", "alpha.beta.example.com").ConfigureAwait(false);
        }
    }
}

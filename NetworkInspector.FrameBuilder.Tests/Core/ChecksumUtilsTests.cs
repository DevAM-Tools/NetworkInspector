// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Tests for <see cref="ChecksumUtils"/> — verifying RFC 1071 one's complement,
/// IPv4 header checksum, and pseudo-header checksum computations.
/// </summary>
internal sealed class ChecksumUtilsTests
{
    [Test]
    public async Task OnesComplement_EmptyData_ReturnsFFFF()
    {
        ushort result = ChecksumUtils.OnesComplement(ReadOnlySpan<byte>.Empty);
        // One's complement of 0 = ~0 = 0xFFFF
        await Assert.That(result).IsEqualTo((ushort)0xFFFF);
    }

    [Test]
    public async Task OnesComplement_SingleByte_TreatsAsHighByte()
    {
        // Single byte 0x01 → sum = 0x0100, ~sum = 0xFEFF
        byte[] data = [0x01];
        ushort result = ChecksumUtils.OnesComplement(data);
        await Assert.That(result).IsEqualTo((ushort)0xFEFF);
    }

    [Test]
    public async Task OnesComplement_RFC1071Example()
    {
        // RFC 1071 example: checksum of "00 01 f2 03 f4 f5 f6 f7" = 0x220D
        byte[] data = [0x00, 0x01, 0xF2, 0x03, 0xF4, 0xF5, 0xF6, 0xF7];
        ushort result = ChecksumUtils.OnesComplement(data);
        await Assert.That(result).IsEqualTo((ushort)0x220D);
    }

    [Test]
    public async Task IPv4Header_KnownGoodPacket_ChecksumCorrect()
    {
        // A known IPv4 header (20 bytes) with checksum zeroed:
        // Version=4, IHL=5, DSCP/ECN=0, TotalLen=40, Id=0x1234,
        // Flags=0x4000(DF), TTL=64, Proto=6(TCP), Checksum=0x0000,
        // Src=192.168.1.1 (0xC0A80101), Dst=10.0.0.1 (0x0A000001)
        byte[] header =
        [
            0x45, 0x00, 0x00, 0x28, // Ver+IHL, DSCP/ECN, TotalLength=40
            0x12, 0x34, 0x40, 0x00, // ID=0x1234, Flags=DF, FragOff=0
            0x40, 0x06, 0x00, 0x00, // TTL=64, Proto=TCP, Checksum=0
            0xC0, 0xA8, 0x01, 0x01, // Src=192.168.1.1
            0x0A, 0x00, 0x00, 0x01, // Dst=10.0.0.1
        ];

        ushort checksum = ChecksumUtils.IPv4Header(header);

        // Write the checksum back and recalculate — should be 0
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10, 2), checksum);
        ushort verification = ChecksumUtils.IPv4Header(header);
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task PseudoHeaderIPv4_KnownValues_ProducesValidChecksum()
    {
        byte[] srcIp = [192, 168, 1, 1];
        byte[] dstIp = [192, 168, 1, 2];

        // Minimal UDP segment: SrcPort=1234, DstPort=80, Length=8, Checksum=0
        byte[] segment =
        [
            0x04, 0xD2, 0x00, 0x50, // SrcPort=1234, DstPort=80
            0x00, 0x08, 0x00, 0x00, // Length=8, Checksum=0
        ];

        ushort checksum = ChecksumUtils.PseudoHeaderIPv4(srcIp, dstIp, IpProtocols.Udp, segment);

        // Write checksum back and verify: recompute should be 0
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(6, 2), checksum);
        ushort verification = ChecksumUtils.PseudoHeaderIPv4(srcIp, dstIp, IpProtocols.Udp, segment);
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task PseudoHeaderIPv6_Produces16ByteAddressChecksum()
    {
        byte[] srcIp = [0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1];
        byte[] dstIp = [0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2];

        // Minimal UDP segment
        byte[] segment =
        [
            0x04, 0xD2, 0x00, 0x50,
            0x00, 0x08, 0x00, 0x00,
        ];

        ushort checksum = ChecksumUtils.PseudoHeaderIPv6(srcIp, dstIp, IpProtocols.Udp, segment);

        // Verify by re-computing after writing checksum
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(6, 2), checksum);
        ushort verification = ChecksumUtils.PseudoHeaderIPv6(srcIp, dstIp, IpProtocols.Udp, segment);
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task OnesComplement_OddLength_HandlesTrailingByte()
    {
        // 3 bytes: 0xFF, 0xFF, 0x01 → sum = 0xFFFF + 0x0100 = 0x100FF, fold = 0x0100, ~0x0100 = 0xFEFF
        byte[] data = [0xFF, 0xFF, 0x01];
        ushort result = ChecksumUtils.OnesComplement(data);
        await Assert.That(result).IsEqualTo((ushort)0xFEFF);
    }

    /// <summary>
    /// Verifies that the SIMD-accelerated checksum produces identical results to
    /// a reference scalar implementation across a range of data lengths.
    /// This exercises:
    /// - Lengths below the Vector128 threshold (pure scalar path)
    /// - Lengths that exactly fill Vector128 iterations (16-byte aligned)
    /// - Lengths that exactly fill Vector256 iterations (32-byte aligned)
    /// - Lengths with scalar tail bytes after SIMD processing
    /// - Odd-length inputs requiring trailing byte handling
    /// </summary>
    [Test]
    [Arguments(0)]    // empty — scalar only
    [Arguments(1)]    // single byte — odd length, scalar only
    [Arguments(2)]    // one word — scalar only
    [Arguments(7)]    // below 8-byte unroll threshold, odd
    [Arguments(8)]    // exactly one scalar unroll iteration
    [Arguments(15)]   // below Vector128 threshold, odd
    [Arguments(16)]   // exactly one Vector128 iteration (if available)
    [Arguments(17)]   // one Vector128 iteration + 1 scalar tail byte, odd
    [Arguments(24)]   // one Vector128 iteration + 8 scalar tail bytes
    [Arguments(31)]   // below Vector256 threshold, odd
    [Arguments(32)]   // exactly one Vector256 iteration (if available)
    [Arguments(33)]   // one Vector256 iteration + 1 scalar tail byte, odd
    [Arguments(48)]   // one Vector256 iteration + one Vector128 tail
    [Arguments(63)]   // just under two Vector256 iterations, odd
    [Arguments(64)]   // two Vector256 iterations
    [Arguments(100)]  // mixed SIMD + scalar tail
    [Arguments(255)]  // large odd-length, exercises carry folding
    [Arguments(1500)] // typical MTU size — realistic segment
    public async Task OnesComplement_MatchesReferenceScalar_AllLengths(int length)
    {
        // Generate deterministic test data with a known pattern
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            // Use a pattern that produces non-trivial checksums with carries
            data[i] = (byte)((i * 0x9D + 0x3B) & 0xFF);
        }

        // Compute with the production method (may use SIMD)
        ushort actual = ChecksumUtils.OnesComplement(data);

        // Compute with a known-correct reference (pure scalar, no SIMD)
        ushort expected = ReferenceOnesComplement(data);

        await Assert.That(actual).IsEqualTo(expected);
    }

    /// <summary>
    /// Verifies that checksum of data followed by its own checksum yields 0x0000.
    /// This is the fundamental self-check property of one's complement checksums.
    /// Tests with sizes that exercise all SIMD paths.
    /// </summary>
    [Test]
    [Arguments(20)]   // IPv4 header size — scalar only
    [Arguments(32)]   // Vector256 path
    [Arguments(60)]   // IPv4 header with max options
    [Arguments(1500)] // typical MTU
    public async Task OnesComplement_SelfCheck_ZeroAfterInsertion(int payloadSize)
    {
        byte[] data = new byte[payloadSize + 2]; // +2 for checksum field
        for (int i = 0; i < payloadSize; i++)
        {
            data[i] = (byte)(i & 0xFF);
        }

        // data[payloadSize] and data[payloadSize+1] are zero (checksum placeholder).
        // Compute checksum over the whole buffer (including zero checksum field).
        ushort checksum = ChecksumUtils.OnesComplement(data);

        // Write the checksum into the placeholder field
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(payloadSize, 2), checksum);

        // Re-compute: the checksum of (data + correct checksum) must be 0
        ushort verification = ChecksumUtils.OnesComplement(data);
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    /// <summary>
    /// Verifies that all-0xFF data (maximum carry) produces the correct checksum.
    /// This exercises the fold/carry logic extensively.
    /// </summary>
    [Test]
    [Arguments(2)]
    [Arguments(16)]
    [Arguments(32)]
    [Arguments(64)]
    public async Task OnesComplement_AllOnes_HandlesMaxCarry(int length)
    {
        byte[] data = new byte[length];
        Array.Fill(data, (byte)0xFF);

        ushort actual = ChecksumUtils.OnesComplement(data);
        ushort expected = ReferenceOnesComplement(data);

        await Assert.That(actual).IsEqualTo(expected);
    }

    /// <summary>
    /// Reference scalar implementation of RFC 1071. No SIMD, no unrolling.
    /// Used to validate the SIMD-accelerated production method.
    /// </summary>
    private static ushort ReferenceOnesComplement(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;

        while (i + 1 < data.Length)
        {
            sum += (uint)(data[i] << 8 | data[i + 1]);
            i += 2;
        }

        if (i < data.Length)
        {
            sum += (uint)(data[i] << 8);
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }
}

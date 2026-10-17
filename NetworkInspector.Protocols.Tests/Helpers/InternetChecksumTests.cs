// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using InternetChecksum = NetworkInspector.Protocols.Helpers.InternetChecksum;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// The checksum equals the RFC 1071 reference on every hardware path: network-order words, every carry kept.
/// </summary>
internal sealed class InternetChecksumTests
{
    #region Helpers

    /// <summary>RFC 1071 reference: big-endian words, an odd last byte padded on the right, full end-around carry.</summary>
    private static ushort _Reference(ReadOnlySpan<byte> data, ulong pseudoHeaderSum = 0)
    {
        UInt128 sum = pseudoHeaderSum;
        for (int offset = 0; offset < data.Length; offset += 2)
        {
            int high = data[offset] << 8;
            int low = offset + 1 < data.Length ? data[offset + 1] : 0;
            sum += (uint)(high | low);
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum >> 16) + (sum & 0xFFFF);
        }

        return (ushort)~(ushort)sum;
    }

    private static byte[] _Pattern(int length)
    {
        byte[] data = new byte[length];
        uint state = 0x1234_5678;
        for (int i = 0; i < length; i++)
        {
            state = (state * 1_103_515_245) + 12_345;
            data[i] = (byte)(state >> 16);
        }

        return data;
    }

    #endregion

    #region Tests

    [Test]
    public async Task ComputeMatchesRfc1071ForEveryLengthUpTo200()
    {
        byte[] data = _Pattern(200);
        for (int length = 0; length <= data.Length; length++)
        {
            ReadOnlySpan<byte> slice = data.AsSpan(0, length);
            ushort expected = _Reference(slice);
            ushort actual = InternetChecksum.Compute(slice);
            if (actual != expected)
            {
                await Assert.That(actual).IsEqualTo(expected).Because($"length {length}");
            }
        }
    }

    [Test]
    public async Task ComputeKeepsLaneCarriesAboveOneMebibyte()
    {
        // All-0xFF words make every vector lane grow fastest; 2 MiB would wrap a u32 lane without the flush.
        byte[] data = new byte[2 * 1024 * 1024 + 6];
        data.AsSpan().Fill(0xFF);
        data[^1] = 0x01;

        await Assert.That(InternetChecksum.Compute(data)).IsEqualTo(_Reference(data));
    }

    [Test]
    public async Task FoldKeepsCarriesOfLargePseudoHeaderSums()
    {
        byte[] data = _Pattern(70);
        ulong[] pseudoSums = [0, 0xFFFF, 0x1_0000_0000, 0xFFFF_FFFF_FFFF, 0x0001_0000_0000_0000, ulong.MaxValue - 0x1_0000];

        foreach (ulong pseudoSum in pseudoSums)
        {
            await Assert.That(InternetChecksum.ComputeWithPseudoHeader(data, pseudoSum)).IsEqualTo(_Reference(data, pseudoSum));
        }
    }

    #endregion
}

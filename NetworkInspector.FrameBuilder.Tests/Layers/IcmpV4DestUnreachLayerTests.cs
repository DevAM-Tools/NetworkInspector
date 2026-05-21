// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests.Layers;

/// <summary>
/// Tests for <see cref="IcmpV4DestUnreachLayer"/> — verifies Type=3, correct code values,
/// and that the Next-Hop MTU for Code 4 (Fragmentation Needed) is serialized in bytes 6–7.
/// </summary>
internal sealed class IcmpV4DestUnreachLayerTests
{
    private static byte[] WriteHeader(IcmpV4DestUnreachLayer layer)
    {
        byte[] buf = new byte[layer.HeaderSize];
        layer.WriteHeader(buf.AsSpan());
        return buf;
    }

    #region Header layout

    [Test]
    public async Task WriteHeader_TypeByteIsAlways3()
    {
        // RFC 792: Destination Unreachable is ICMP type 3.
        IcmpV4DestUnreachLayer layer = new();
        byte[] buf = WriteHeader(layer);

        await Assert.That(buf[0]).IsEqualTo((byte)3);
    }

    [Test]
    public async Task WriteHeader_DefaultCode_IsPortUnreachable()
    {
        // The default constructor uses CodePortUnreachable (3).
        IcmpV4DestUnreachLayer layer = new();
        byte[] buf = WriteHeader(layer);

        await Assert.That(buf[1]).IsEqualTo(IcmpV4DestUnreachLayer.CodePortUnreachable);
    }

    [Test]
    [Arguments(IcmpV4DestUnreachLayer.CodeNetUnreachable, 0)]
    [Arguments(IcmpV4DestUnreachLayer.CodeHostUnreachable, 1)]
    [Arguments(IcmpV4DestUnreachLayer.CodeProtocolUnreachable, 2)]
    [Arguments(IcmpV4DestUnreachLayer.CodePortUnreachable, 3)]
    [Arguments(IcmpV4DestUnreachLayer.CodeFragmentationNeeded, 4)]
    [Arguments(IcmpV4DestUnreachLayer.CodeSourceRouteFailed, 5)]
    public async Task WriteHeader_AllCodes_StoredInByteOne(byte code, int expectedValue)
    {
        // Each named code constant must equal its documented numeric value and be written to byte 1.
        IcmpV4DestUnreachLayer layer = new(code);
        byte[] buf = WriteHeader(layer);

        await Assert.That((int)buf[1]).IsEqualTo(expectedValue)
            .Because($"code byte must be {expectedValue} for constant value {code}");
    }

    #endregion

    #region Next-Hop MTU for Code 4 (Fragmentation Needed)

    [Test]
    public async Task WriteHeader_CodeFragmentationNeeded_NextHopMtuInBytes6And7()
    {
        // RFC 1191: for code 4 the Next-Hop MTU occupies bytes 6–7 in big-endian order.
        // The IcmpV4DestUnreachLayer stores nextHopMtu in the low 16 bits of data4,
        // which WriteHeader writes as a big-endian uint32 to bytes 4–7.
        const ushort mtu = 1480;
        IcmpV4DestUnreachLayer layer = new(IcmpV4DestUnreachLayer.CodeFragmentationNeeded, nextHopMtu: mtu);
        byte[] buf = WriteHeader(layer);

        ushort actual = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6, 2));
        await Assert.That(actual).IsEqualTo(mtu);
    }

    [Test]
    public async Task WriteHeader_CodeFragmentationNeeded_Bytes4And5AreZero()
    {
        // Per RFC 792, bytes 4–5 (unused/padding) must be zero even for code 4.
        IcmpV4DestUnreachLayer layer = new(IcmpV4DestUnreachLayer.CodeFragmentationNeeded, nextHopMtu: 1500);
        byte[] buf = WriteHeader(layer);

        await Assert.That(buf[4]).IsEqualTo((byte)0).Because("byte 4 is the unused high byte");
        await Assert.That(buf[5]).IsEqualTo((byte)0).Because("byte 5 is the unused high byte");
    }

    [Test]
    public async Task WriteHeader_OtherCode_NextHopMtuBytes6And7AreZero()
    {
        // For codes other than 4, the data field must be all zeros.
        IcmpV4DestUnreachLayer layer = new(IcmpV4DestUnreachLayer.CodePortUnreachable);
        byte[] buf = WriteHeader(layer);

        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6, 2))).IsEqualTo((ushort)0)
            .Because("Next-Hop MTU field must be zero when nextHopMtu is not specified");
    }

    #endregion

    #region HeaderSize

    [Test]
    public async Task HeaderSize_Is8()
    {
        IcmpV4DestUnreachLayer layer = new();

        await Assert.That(layer.HeaderSize).IsEqualTo(8);
    }

    #endregion
}

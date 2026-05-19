// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder.Tests.Layers;

/// <summary>
/// Tests for <see cref="IcmpV4TimeExceededLayer"/> — verifies Type=11, both code values,
/// and both checksum paths (auto-computed and explicit).
/// </summary>
internal sealed class IcmpV4TimeExceededLayerTests
{
    private static byte[] WriteHeader(IcmpV4TimeExceededLayer layer)
    {
        byte[] buf = new byte[layer.HeaderSize];
        layer.WriteHeader(buf.AsSpan());
        return buf;
    }

    #region Header layout

    [Test]
    public async Task WriteHeader_TypeByteIsAlways11()
    {
        // RFC 792: Time Exceeded is ICMP type 11.
        IcmpV4TimeExceededLayer layer = new();
        byte[] buf = WriteHeader(layer);

        await Assert.That(buf[0]).IsEqualTo((byte)11);
    }

    [Test]
    public async Task WriteHeader_DefaultCode_IsTtlExceeded()
    {
        // The default code is CodeTtlExceeded (0).
        IcmpV4TimeExceededLayer layer = new();
        byte[] buf = WriteHeader(layer);

        await Assert.That(buf[1]).IsEqualTo(IcmpV4TimeExceededLayer.CodeTtlExceeded);
    }

    [Test]
    public async Task WriteHeader_CodeReassemblyTimeout_ByteOneIsOne()
    {
        // CodeReassemblyTimeout = 1; must be serialized to byte 1.
        IcmpV4TimeExceededLayer layer = new(IcmpV4TimeExceededLayer.CodeReassemblyTimeout);
        byte[] buf = WriteHeader(layer);

        await Assert.That(buf[1]).IsEqualTo(IcmpV4TimeExceededLayer.CodeReassemblyTimeout);
    }

    [Test]
    public async Task WriteHeader_Bytes4To7_AreAlwaysZero()
    {
        // RFC 792: the unused field (bytes 4–7) must be zero for Time Exceeded.
        IcmpV4TimeExceededLayer layer = new();
        byte[] buf = WriteHeader(layer);

        await Assert.That(buf[4]).IsEqualTo((byte)0);
        await Assert.That(buf[5]).IsEqualTo((byte)0);
        await Assert.That(buf[6]).IsEqualTo((byte)0);
        await Assert.That(buf[7]).IsEqualTo((byte)0);
    }

    #endregion

    #region Checksum path

    [Test]
    public async Task WriteHeader_ChecksumBytesAreZeroBeforePostFix()
    {
        // WriteHeader always leaves the checksum field at zero; it is patched later
        // by ApplyPostFix so that the computation covers the complete ICMP message.
        IcmpV4TimeExceededLayer layer = new();
        byte[] buf = WriteHeader(layer);

        await Assert.That(buf[2]).IsEqualTo((byte)0).Because("checksum high byte must be 0 before PostFix");
        await Assert.That(buf[3]).IsEqualTo((byte)0).Because("checksum low byte must be 0 before PostFix");
    }

    [Test]
    public async Task ApplyPostFix_AutoChecksum_ComputesOnesComplementOverFullMessage()
    {
        // After ApplyPostFix with InnerChecksum phase, the checksum field must contain
        // the one's complement of the full ICMP header.
        // Expected checksum for [0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]:
        //   Sum = 0x0B00 + 0x0000 + 0x0000 + 0x0000 = 0x0B00
        //   One's complement = ~0x0B00 = 0xF4FF
        IcmpV4TimeExceededLayer layer = new();
        byte[] frame = new byte[8];
        layer.WriteHeader(frame.AsSpan());
        PostFixContext ctx = default;

        layer.ApplyPostFix(FixPhase.InnerChecksum, frame.AsSpan(), myOffset: 0, myLength: 8, ref ctx);

        ushort checksum = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2, 2));
        await Assert.That(checksum).IsEqualTo((ushort)0xF4FF)
            .Because("one's complement of [0x0B00,0x0000,0x0000,0x0000] = 0xF4FF");
    }

    [Test]
    public async Task ApplyPostFix_ExplicitChecksum_WritesExplicitValueVerbatim()
    {
        // When an explicit checksum is supplied, ApplyPostFix must write it without
        // computing anything — this allows crafting intentionally invalid packets in tests.
        IcmpV4TimeExceededLayer layer = new(checksum: FB.Auto<ushort>.Explicit(0xDEAD));
        byte[] frame = new byte[8];
        layer.WriteHeader(frame.AsSpan());
        PostFixContext ctx = default;

        layer.ApplyPostFix(FixPhase.InnerChecksum, frame.AsSpan(), myOffset: 0, myLength: 8, ref ctx);

        ushort checksum = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2, 2));
        await Assert.That(checksum).IsEqualTo((ushort)0xDEAD)
            .Because("explicit checksum must be written verbatim without recomputation");
    }

    [Test]
    public async Task ApplyPostFix_NonChecksumPhase_DoesNotModifyFrame()
    {
        // ApplyPostFix must be a no-op for all phases other than InnerChecksum.
        IcmpV4TimeExceededLayer layer = new();
        byte[] frame = new byte[8];
        layer.WriteHeader(frame.AsSpan());
        byte[] snapshot = frame.ToArray();
        PostFixContext ctx = default;

        layer.ApplyPostFix(FixPhase.Length, frame.AsSpan(), myOffset: 0, myLength: 8, ref ctx);

        await Assert.That(frame.SequenceEqual(snapshot)).IsTrue()
            .Because("non-checksum phases must not modify any bytes in the frame");
    }

    #endregion

    #region HeaderSize

    [Test]
    public async Task HeaderSize_Is8()
    {
        IcmpV4TimeExceededLayer layer = new();

        await Assert.That(layer.HeaderSize).IsEqualTo(8);
    }

    #endregion
}

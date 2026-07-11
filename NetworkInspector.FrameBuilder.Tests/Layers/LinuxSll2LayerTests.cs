// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests.Layers;

/// <summary>
/// Tests for <see cref="LinuxSll2Layer"/> — verifies that the 20-byte SLL2 header is written
/// correctly for all address-length variants (0, 6, 8, &gt;8), that the interface index is
/// serialized at the correct offset, and that EtherType patching respects the auto vs. explicit flag.
/// </summary>
internal sealed class LinuxSll2LayerTests
{
    #region HeaderSize

    [Test]
    public async Task HeaderSize_IsAlways20()
    {
        LinuxSll2Layer layer = new();

        await Assert.That(layer.HeaderSize).IsEqualTo(20);
    }

    #endregion

    #region srcAddress length clamping and zero-padding

    [Test]
    public async Task WriteHeader_EmptyAddress_HaLenIsZeroAndAddressBytesAreAllZero()
    {
        // A zero-length address span must produce HaLen=0 at byte 11 and zero all 8 address bytes.
        LinuxSll2Layer layer = new(srcAddress: ReadOnlySpan<byte>.Empty);
        byte[] buf = new byte[20];

        layer.WriteHeader(buf.AsSpan());

        await Assert.That(buf[11]).IsEqualTo((byte)0).Because("HaLen (byte 11) must be 0");
        await Assert.That(buf.Skip(12).Take(8).All(static b => b == 0)).IsTrue()
            .Because("all address slots must be zero-padded when srcAddress is empty");
    }

    [Test]
    public async Task WriteHeader_SixByteAddress_HaLenIsSixAndFirstSixBytesMatch()
    {
        // Standard Ethernet MAC (6 bytes) — HaLen=6, bytes 12–17 carry the address,
        // bytes 18–19 must remain zero.
        byte[] addr = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
        LinuxSll2Layer layer = new(srcAddress: addr);
        byte[] buf = new byte[20];

        layer.WriteHeader(buf.AsSpan());

        await Assert.That(buf[11]).IsEqualTo((byte)6);
        await Assert.That(buf[12]).IsEqualTo(addr[0]);
        await Assert.That(buf[13]).IsEqualTo(addr[1]);
        await Assert.That(buf[14]).IsEqualTo(addr[2]);
        await Assert.That(buf[15]).IsEqualTo(addr[3]);
        await Assert.That(buf[16]).IsEqualTo(addr[4]);
        await Assert.That(buf[17]).IsEqualTo(addr[5]);
        await Assert.That(buf[18]).IsEqualTo((byte)0).Because("unused address slot must be zero");
        await Assert.That(buf[19]).IsEqualTo((byte)0).Because("unused address slot must be zero");
    }

    [Test]
    public async Task WriteHeader_EightByteAddress_AllEightBytesWritten()
    {
        // An 8-byte address fills all available address slots; HaLen=8.
        byte[] addr = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        LinuxSll2Layer layer = new(srcAddress: addr);
        byte[] buf = new byte[20];

        layer.WriteHeader(buf.AsSpan());

        await Assert.That(buf[11]).IsEqualTo((byte)8);
        await Assert.That(buf.Skip(12).Take(8).SequenceEqual(addr)).IsTrue()
            .Because("all 8 address bytes must be copied verbatim");
    }

    [Test]
    public async Task WriteHeader_NineByteAddress_ClampedToEightBytes()
    {
        // Addresses longer than 8 bytes must be silently truncated; HaLen is clamped to 8.
        byte[] addr = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09];
        LinuxSll2Layer layer = new(srcAddress: addr);
        byte[] buf = new byte[20];

        layer.WriteHeader(buf.AsSpan());

        await Assert.That(buf[11]).IsEqualTo((byte)8).Because("HaLen is capped at 8");
        await Assert.That(buf.Skip(12).Take(8).SequenceEqual(addr.Take(8))).IsTrue()
            .Because("only the first 8 address bytes are written");
    }

    #endregion

    #region Interface index serialization

    [Test]
    public async Task WriteHeader_IfIndex_WrittenBigEndianAtOffset4()
    {
        // Interface index occupies bytes 4–7 in big-endian order.
        LinuxSll2Layer layer = new(ifIndex: 42);
        byte[] buf = new byte[20];

        layer.WriteHeader(buf.AsSpan());

        uint ifIndex = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4, 4));
        await Assert.That(ifIndex).IsEqualTo(42u);
    }

    [Test]
    public async Task WriteHeader_IfIndexMaxValue_SerializedCorrectly()
    {
        // Verify big-endian encoding at the boundary of uint.MaxValue.
        LinuxSll2Layer layer = new(ifIndex: uint.MaxValue);
        byte[] buf = new byte[20];

        layer.WriteHeader(buf.AsSpan());

        uint ifIndex = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4, 4));
        await Assert.That(ifIndex).IsEqualTo(uint.MaxValue);
    }

    #endregion

    #region EtherType — explicit vs auto

    [Test]
    public async Task WriteHeader_ExplicitEtherType_WrittenAtOffset0()
    {
        // SLL2 places EtherType at bytes 0–1; an explicit value must appear there after WriteHeader.
        LinuxSll2Layer layer = new(etherType: FB.Auto.Explicit((ushort)0x0800));
        byte[] buf = new byte[20];

        layer.WriteHeader(buf.AsSpan());

        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(0, 2));
        await Assert.That(etherType).IsEqualTo((ushort)0x0800);
    }

    [Test]
    public async Task PatchNextProtocol_AutoEtherType_WritesValueAtOffset0()
    {
        // When EtherType is auto, PatchNextProtocol must write the supplied value at frame[myOffset+0].
        LinuxSll2Layer layer = new(); // default: auto EtherType
        byte[] frame = new byte[32];

        layer.PatchNextProtocol(frame.AsSpan(), myOffset: 0, nextProtocol: 0x86DD);

        ushort patched = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(0, 2));
        await Assert.That(patched).IsEqualTo((ushort)0x86DD);
    }

    [Test]
    public async Task PatchNextProtocol_ExplicitEtherType_DoesNotOverwrite()
    {
        // An explicit EtherType must not be overwritten by PatchNextProtocol.
        LinuxSll2Layer layer = new(etherType: FB.Auto.Explicit((ushort)0x0800));
        byte[] frame = new byte[32];

        layer.WriteHeader(frame.AsSpan());
        layer.PatchNextProtocol(frame.AsSpan(), myOffset: 0, nextProtocol: 0x86DD);

        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(0, 2));
        await Assert.That(etherType).IsEqualTo((ushort)0x0800)
            .Because("explicit EtherType must never be patched over");
    }

    #endregion
}

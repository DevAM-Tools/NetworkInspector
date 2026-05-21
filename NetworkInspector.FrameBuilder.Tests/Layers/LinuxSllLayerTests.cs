// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests.Layers;

/// <summary>
/// Tests for <see cref="LinuxSllLayer"/> — verifies that the 16-byte SLL header is written
/// correctly for all address-length variants (0, 6, 8, &gt;8) and that EtherType patching
/// respects the auto vs. explicit flag.
/// </summary>
internal sealed class LinuxSllLayerTests
{
    #region HeaderSize

    [Test]
    public async Task HeaderSize_IsAlways16()
    {
        LinuxSllLayer layer = new();

        await Assert.That(layer.HeaderSize).IsEqualTo(16);
    }

    #endregion

    #region srcAddress length clamping and zero-padding

    [Test]
    public async Task WriteHeader_EmptyAddress_HaLenIsZeroAndAddressBytesAreAllZero()
    {
        // A zero-length address span must produce HaLen=0 and all 8 address bytes zeroed.
        LinuxSllLayer layer = new(srcAddress: ReadOnlySpan<byte>.Empty);
        byte[] buf = new byte[16];

        layer.WriteHeader(buf.AsSpan());

        ushort haLen = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4, 2));
        await Assert.That(haLen).IsEqualTo((ushort)0);
        await Assert.That(buf.Skip(6).Take(8).All(static b => b == 0)).IsTrue()
            .Because("all address slots must be zero-padded when srcAddress is empty");
    }

    [Test]
    public async Task WriteHeader_SixByteAddress_HaLenIsSixAndFirstSixBytesMatch()
    {
        // Standard Ethernet MAC (6 bytes) — HaLen=6, bytes 6–11 carry the address,
        // bytes 12–13 must remain zero.
        byte[] addr = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
        LinuxSllLayer layer = new(srcAddress: addr);
        byte[] buf = new byte[16];

        layer.WriteHeader(buf.AsSpan());

        ushort haLen = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4, 2));
        await Assert.That(haLen).IsEqualTo((ushort)6);
        await Assert.That(buf[6]).IsEqualTo(addr[0]);
        await Assert.That(buf[7]).IsEqualTo(addr[1]);
        await Assert.That(buf[8]).IsEqualTo(addr[2]);
        await Assert.That(buf[9]).IsEqualTo(addr[3]);
        await Assert.That(buf[10]).IsEqualTo(addr[4]);
        await Assert.That(buf[11]).IsEqualTo(addr[5]);
        await Assert.That(buf[12]).IsEqualTo((byte)0).Because("unused address slot must be zero");
        await Assert.That(buf[13]).IsEqualTo((byte)0).Because("unused address slot must be zero");
    }

    [Test]
    public async Task WriteHeader_EightByteAddress_AllEightBytesWritten()
    {
        // An 8-byte address fills all available address slots; HaLen=8.
        byte[] addr = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        LinuxSllLayer layer = new(srcAddress: addr);
        byte[] buf = new byte[16];

        layer.WriteHeader(buf.AsSpan());

        ushort haLen = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4, 2));
        await Assert.That(haLen).IsEqualTo((ushort)8);
        await Assert.That(buf.Skip(6).Take(8).SequenceEqual(addr)).IsTrue()
            .Because("all 8 address bytes must be copied verbatim");
    }

    [Test]
    public async Task WriteHeader_NineByteAddress_ClampedToEightBytes()
    {
        // Addresses longer than 8 bytes must be silently truncated; HaLen is clamped to 8.
        byte[] addr = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09];
        LinuxSllLayer layer = new(srcAddress: addr);
        byte[] buf = new byte[16];

        layer.WriteHeader(buf.AsSpan());

        ushort haLen = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4, 2));
        await Assert.That(haLen).IsEqualTo((ushort)8).Because("HaLen is capped at 8");
        await Assert.That(buf.Skip(6).Take(8).SequenceEqual(addr.Take(8))).IsTrue()
            .Because("only the first 8 address bytes are written");
    }

    #endregion

    #region EtherType — explicit vs auto

    [Test]
    public async Task WriteHeader_ExplicitEtherType_WrittenAtOffset14()
    {
        // An explicit EtherType must appear in bytes 14–15 immediately after WriteHeader.
        LinuxSllLayer layer = new(etherType: FB.Auto<ushort>.Explicit(0x0800));
        byte[] buf = new byte[16];

        layer.WriteHeader(buf.AsSpan());

        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(14, 2));
        await Assert.That(etherType).IsEqualTo((ushort)0x0800);
    }

    [Test]
    public async Task PatchNextProtocol_AutoEtherType_WritesValueAtOffset14()
    {
        // When EtherType is auto, PatchNextProtocol must write the supplied value at frame[myOffset+14].
        LinuxSllLayer layer = new(); // default: auto EtherType
        byte[] frame = new byte[32];

        layer.PatchNextProtocol(frame.AsSpan(), myOffset: 0, next: 0x86DD); // IPv6

        ushort patched = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14, 2));
        await Assert.That(patched).IsEqualTo((ushort)0x86DD);
    }

    [Test]
    public async Task PatchNextProtocol_ExplicitEtherType_DoesNotOverwrite()
    {
        // An explicit EtherType must not be overwritten by PatchNextProtocol; the outer layer
        // may call this unconditionally and the SLL layer is responsible for ignoring the patch.
        LinuxSllLayer layer = new(etherType: FB.Auto<ushort>.Explicit(0x0800));
        byte[] frame = new byte[32];

        layer.WriteHeader(frame.AsSpan());
        layer.PatchNextProtocol(frame.AsSpan(), myOffset: 0, next: 0x86DD);

        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14, 2));
        await Assert.That(etherType).IsEqualTo((ushort)0x0800)
            .Because("explicit EtherType must never be patched over");
    }

    #endregion
}

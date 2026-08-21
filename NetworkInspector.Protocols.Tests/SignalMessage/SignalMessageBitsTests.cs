// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalMessage;

/// <summary>Unit tests for <see cref="SignalMessageBits"/> span, extraction, and scaling helpers.</summary>
internal sealed class SignalMessageBitsTests
{
    [Test]
    public async Task GetEndByteExclusive_LittleEndian_Contiguous()
    {
        // bits 0..15 → bytes 0..1 → end exclusive 2
        await Assert.That(SignalMessageBits.GetEndByteExclusive(0, 16, bigEndian: false)).IsEqualTo(2);
        // bits 8..15 → byte 1 → end 2
        await Assert.That(SignalMessageBits.GetEndByteExclusive(8, 8, bigEndian: false)).IsEqualTo(2);
    }

    [Test]
    public async Task GetEndByteExclusive_BigEndian_MatchesWalk()
    {
        // start_bit 7, length 8: entire byte 0 (bits 7..0) → end exclusive 1
        await Assert.That(SignalMessageBits.GetEndByteExclusive(7, 8, bigEndian: true)).IsEqualTo(1);
    }

    [Test]
    public async Task MaxRawForBitLength_SpecialCases()
    {
        await Assert.That(SignalMessageBits.MaxRawForBitLength(4)).IsEqualTo(15UL);
        await Assert.That(SignalMessageBits.MaxRawForBitLength(64)).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task ExtractRawUnchecked_LittleEndian_Uint16()
    {
        // bytes [0x64, 0x00] → LE u16 = 100 at start_bit 0
        byte[] data = [0x64, 0x00, 0x00, 0x00];
        SignalInfo signal = _Make(startBit: 0, bitLength: 16, bigEndian: false, factor: 1, offset: 0);
        ulong raw = SignalMessageBits.ExtractRawUnchecked(data, in signal);
        await Assert.That(raw).IsEqualTo(100UL);
        await Assert.That(SignalMessageBits.ToPhysical(raw, in signal)).IsEqualTo(100.0);
    }

    [Test]
    public async Task ToPhysical_AppliesFactorOffset()
    {
        SignalInfo signal = _Make(0, 16, bigEndian: false, factor: 0.25, offset: 100);
        double phys = SignalMessageBits.ToPhysical(100UL, in signal);
        await Assert.That(phys).IsEqualTo(125.0);
    }

    [Test]
    public async Task ToPhysical_TreatsRawAsUnsigned()
    {
        // 8-bit raw 0xFF stays 255 (no signed reinterpretation).
        SignalInfo signal = _Make(0, 8, bigEndian: false, factor: 1, offset: 0);
        double phys = SignalMessageBits.ToPhysical(0xFFUL, in signal);
        await Assert.That(phys).IsEqualTo(255.0);
    }

    [Test]
    public async Task ExtractRawUnchecked_BigEndian_ByteAligned()
    {
        // Motorola: start_bit 7 = MSB of byte0; 8 bits → value of byte0 with MSB-first packing.
        byte[] data = [0xA5, 0x00];
        SignalInfo signal = _Make(7, 8, bigEndian: true, 1, 0);
        ulong raw = SignalMessageBits.ExtractRawUnchecked(data, in signal);
        await Assert.That(raw).IsEqualTo(0xA5UL);
    }

    [Test]
    public async Task GetEndByteExclusive_NonPositiveBitLength_ReturnsZero()
    {
        await Assert.That(SignalMessageBits.GetEndByteExclusive(0, 0, bigEndian: false)).IsEqualTo(0);
        await Assert.That(SignalMessageBits.GetEndByteExclusive(0, -1, bigEndian: true)).IsEqualTo(0);
    }

    [Test]
    public async Task MaxRawForBitLength_NonPositive_ReturnsZero()
    {
        await Assert.That(SignalMessageBits.MaxRawForBitLength(0)).IsEqualTo(0UL);
        await Assert.That(SignalMessageBits.MaxRawForBitLength(-3)).IsEqualTo(0UL);
    }

    [Test]
    public async Task ExtractRawUnchecked_LittleEndian_Aligned64_AllOnes()
    {
        byte[] data = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        SignalInfo signal = _Make(0, 64, bigEndian: false, 1, 0);
        ulong raw = SignalMessageBits.ExtractRawUnchecked(data, in signal);
        await Assert.That(raw).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task ExtractRawUnchecked_LittleEndian_Unaligned64_AllOnes()
    {
        // startBit=1, 64 bits → 9 bytes of 0xFF → every extracted bit is 1.
        byte[] data = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        SignalInfo signal = _Make(1, 64, bigEndian: false, 1, 0);
        ulong raw = SignalMessageBits.ExtractRawUnchecked(data, in signal);
        await Assert.That(raw).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task ExtractRawUnchecked_LittleEndian_Unaligned64_KnownPattern()
    {
        // Byte0 = 0x02 (bit 1 set), rest zero → 64-bit LE starting at bit 1 yields 1.
        byte[] data = [0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        SignalInfo signal = _Make(1, 64, bigEndian: false, 1, 0);
        ulong raw = SignalMessageBits.ExtractRawUnchecked(data, in signal);
        await Assert.That(raw).IsEqualTo(1UL);
    }

    [Test]
    public async Task ExtractRawUnchecked_LittleEndian_Aligned8And32()
    {
        byte[] data = [0xAB, 0x78, 0x56, 0x34, 0x12];
        SignalInfo u8Signal = _Make(0, 8, bigEndian: false, 1, 0);
        ulong u8 = SignalMessageBits.ExtractRawUnchecked(data, in u8Signal);
        await Assert.That(u8).IsEqualTo(0xABUL);

        SignalInfo u32Signal = _Make(8, 32, bigEndian: false, 1, 0);
        ulong u32 = SignalMessageBits.ExtractRawUnchecked(data, in u32Signal);
        await Assert.That(u32).IsEqualTo(0x12345678UL);
    }

    [Test]
    public async Task ExtractRawUnchecked_LittleEndian_UnalignedOddWidth()
    {
        // 12 bits starting at bit 4 of 0xF0, 0x0D → bits 4..15 = 0xDF0 >> 4 = 0xDF.
        byte[] data = [0xF0, 0x0D];
        SignalInfo signal = _Make(4, 12, bigEndian: false, 1, 0);
        ulong raw = SignalMessageBits.ExtractRawUnchecked(data, in signal);
        await Assert.That(raw).IsEqualTo(0xDFUL);
    }

    [Test]
    public async Task ComputeRequiredByteLength_TakesMaxOverSignals()
    {
        SignalInfo[] signals =
        [
            _Make(0, 8, bigEndian: false, 1, 0),
            _Make(16, 16, bigEndian: false, 1, 0),
        ];
        await Assert.That(SignalMessageBits.ComputeRequiredByteLength(signals)).IsEqualTo(4);
    }

    private static SignalInfo _Make(
        ushort startBit,
        byte bitLength,
        bool bigEndian,
        double factor,
        double offset)
        => new(
            startBit,
            bitLength,
            bigEndian,
            factor,
            offset,
            SignalFieldId: FieldId.Invalid,
            RawFieldId: FieldId.Invalid,
            EnumFieldId: FieldId.Invalid,
            Name: "S",
            UiName: "S",
            Unit: string.Empty,
            Enums: SignalEnumTable.None,
            CustomTextByRaw: null);
}

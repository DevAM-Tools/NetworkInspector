// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Format;

/// <summary>
/// The handwritten BLF and PCAPNG header parsers read every field from its on-disk offset. The input is the byte
/// sequence 01 02 03 … so every field holds a different little-endian value; the expectation is computed with plain
/// shifts (not with <see cref="BinaryPrimitives"/>) so an offset or width mistake cannot cancel itself out. One byte
/// short must fail with a default value.
/// </summary>
internal sealed class HeaderParseTests
{
    #region Helpers

    /// <summary>Bytes 01 02 03 … of the given length.</summary>
    private static byte[] _Pattern(int size)
    {
        byte[] bytes = new byte[size];
        for (int index = 0; index < size; index++)
        {
            bytes[index] = (byte)(index + 1);
        }

        return bytes;
    }

    private static ushort _U16(byte[] bytes, int offset) => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static uint _U32(byte[] bytes, int offset) => _U16(bytes, offset) | ((uint)_U16(bytes, offset + 2) << 16);

    private static ulong _U64(byte[] bytes, int offset) => _U32(bytes, offset) | ((ulong)_U32(bytes, offset + 4) << 32);

    #endregion

    #region BLF

    [Test]
    public async Task BlfBlockHeaderParsesEveryField()
    {
        byte[] bytes = _Pattern(BlfBlockHeader.Size);

        bool ok = BlfBlockHeader.TryParse(bytes, out BlfBlockHeader value);
        bool shortOk = BlfBlockHeader.TryParse(bytes.AsSpan(0, BlfBlockHeader.Size - 1), out BlfBlockHeader shortValue);

        await Assert.That(bytes.Length).IsEqualTo(16);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.Signature).IsEqualTo(_U32(bytes, 0));
        await Assert.That(value.HeaderSize).IsEqualTo(_U16(bytes, 4));
        await Assert.That(value.HeaderType).IsEqualTo(_U16(bytes, 6));
        await Assert.That(value.ObjectLength).IsEqualTo(_U32(bytes, 8));
        await Assert.That(value.ObjectType).IsEqualTo(_U32(bytes, 12));
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(BlfBlockHeader));
    }

    [Test]
    public async Task BlfContainerHeaderParsesEveryField()
    {
        byte[] bytes = _Pattern(BlfContainerHeader.Size);

        bool ok = BlfContainerHeader.TryParse(bytes, out BlfContainerHeader value);
        bool shortOk = BlfContainerHeader.TryParse(bytes.AsSpan(0, BlfContainerHeader.Size - 1), out BlfContainerHeader shortValue);

        await Assert.That(bytes.Length).IsEqualTo(16);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.CompressionMethod).IsEqualTo(_U16(bytes, 0));
        await Assert.That(value.Reserved1A).IsEqualTo(_U16(bytes, 2));
        await Assert.That(value.Reserved1B).IsEqualTo(_U32(bytes, 4));
        await Assert.That(value.UncompressedSize).IsEqualTo(_U32(bytes, 8));
        await Assert.That(value.Reserved2).IsEqualTo(_U32(bytes, 12));
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(BlfContainerHeader));
    }

    [Test]
    public async Task BlfDateParsesEveryField()
    {
        byte[] bytes = _Pattern(BlfDate.Size);

        bool ok = BlfDate.TryParse(bytes, out BlfDate value);
        bool shortOk = BlfDate.TryParse(bytes.AsSpan(0, BlfDate.Size - 1), out BlfDate shortValue);

        await Assert.That(bytes.Length).IsEqualTo(16);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.Year).IsEqualTo(_U16(bytes, 0));
        await Assert.That(value.Month).IsEqualTo(_U16(bytes, 2));
        await Assert.That(value.DayOfWeek).IsEqualTo(_U16(bytes, 4));
        await Assert.That(value.Day).IsEqualTo(_U16(bytes, 6));
        await Assert.That(value.Hour).IsEqualTo(_U16(bytes, 8));
        await Assert.That(value.Minute).IsEqualTo(_U16(bytes, 10));
        await Assert.That(value.Second).IsEqualTo(_U16(bytes, 12));
        await Assert.That(value.Millisecond).IsEqualTo(_U16(bytes, 14));
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(BlfDate));
    }

    [Test]
    public async Task BlfFileHeaderPrefixParsesEveryField()
    {
        byte[] bytes = _Pattern(BlfFileHeaderPrefix.Size);

        bool ok = BlfFileHeaderPrefix.TryParse(bytes, out BlfFileHeaderPrefix value);
        bool shortOk = BlfFileHeaderPrefix.TryParse(bytes.AsSpan(0, BlfFileHeaderPrefix.Size - 1), out BlfFileHeaderPrefix shortValue);

        await Assert.That(bytes.Length).IsEqualTo(12);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.Signature).IsEqualTo(_U32(bytes, 0));
        await Assert.That(value.HeaderSize).IsEqualTo(_U32(bytes, 4));
        await Assert.That(value.ApiVersion).IsEqualTo(_U32(bytes, 8));
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(BlfFileHeaderPrefix));
    }

    [Test]
    public async Task BlfLogObjectHeaderV1ParsesEveryField()
    {
        byte[] bytes = _Pattern(BlfLogObjectHeaderV1.Size);

        bool ok = BlfLogObjectHeaderV1.TryParse(bytes, out BlfLogObjectHeaderV1 value);
        bool shortOk = BlfLogObjectHeaderV1.TryParse(bytes.AsSpan(0, BlfLogObjectHeaderV1.Size - 1), out BlfLogObjectHeaderV1 shortValue);

        await Assert.That(bytes.Length).IsEqualTo(16);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.Flags).IsEqualTo(_U32(bytes, 0));
        await Assert.That(value.ClientIndex).IsEqualTo(_U16(bytes, 4));
        await Assert.That(value.ObjectVersion).IsEqualTo(_U16(bytes, 6));
        await Assert.That(value.Timestamp).IsEqualTo(_U64(bytes, 8));
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(BlfLogObjectHeaderV1));
    }

    [Test]
    public async Task BlfLogObjectHeaderV2ParsesEveryField()
    {
        byte[] bytes = _Pattern(BlfLogObjectHeaderV2.Size);

        bool ok = BlfLogObjectHeaderV2.TryParse(bytes, out BlfLogObjectHeaderV2 value);
        bool shortOk = BlfLogObjectHeaderV2.TryParse(bytes.AsSpan(0, BlfLogObjectHeaderV2.Size - 1), out BlfLogObjectHeaderV2 shortValue);

        await Assert.That(bytes.Length).IsEqualTo(24);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.Flags).IsEqualTo(_U32(bytes, 0));
        await Assert.That(value.TimestampStatusAndReserved).IsEqualTo(_U16(bytes, 4));
        await Assert.That(value.ObjectVersion).IsEqualTo(_U16(bytes, 6));
        await Assert.That(value.Timestamp).IsEqualTo(_U64(bytes, 8));
        await Assert.That(value.OriginalTimestamp).IsEqualTo(_U64(bytes, 16));
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(BlfLogObjectHeaderV2));
    }

    [Test]
    public async Task BlfLogObjectHeaderV3ParsesEveryField()
    {
        byte[] bytes = _Pattern(BlfLogObjectHeaderV3.Size);

        bool ok = BlfLogObjectHeaderV3.TryParse(bytes, out BlfLogObjectHeaderV3 value);
        bool shortOk = BlfLogObjectHeaderV3.TryParse(bytes.AsSpan(0, BlfLogObjectHeaderV3.Size - 1), out BlfLogObjectHeaderV3 shortValue);

        await Assert.That(bytes.Length).IsEqualTo(16);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.Flags).IsEqualTo(_U32(bytes, 0));
        await Assert.That(value.StaticSize).IsEqualTo(_U16(bytes, 4));
        await Assert.That(value.ObjectVersion).IsEqualTo(_U16(bytes, 6));
        await Assert.That(value.Timestamp).IsEqualTo(_U64(bytes, 8));
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(BlfLogObjectHeaderV3));
    }

    #endregion

    #region PCAPNG

    [Test]
    public async Task EnhancedPacketBlockParsesEveryField()
    {
        byte[] bytes = _Pattern(EnhancedPacketBlock.Size);

        bool ok = EnhancedPacketBlock.TryParse(bytes, out EnhancedPacketBlock value);
        bool shortOk = EnhancedPacketBlock.TryParse(bytes.AsSpan(0, EnhancedPacketBlock.Size - 1), out EnhancedPacketBlock shortValue);

        await Assert.That(bytes.Length).IsEqualTo(28);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.BlockType).IsEqualTo(_U32(bytes, 0));
        await Assert.That(value.BlockTotalLength).IsEqualTo(_U32(bytes, 4));
        await Assert.That(value.InterfaceId).IsEqualTo(_U32(bytes, 8));
        await Assert.That(value.TimestampHigh).IsEqualTo(_U32(bytes, 12));
        await Assert.That(value.TimestampLow).IsEqualTo(_U32(bytes, 16));
        await Assert.That(value.CapturedLength).IsEqualTo(_U32(bytes, 20));
        await Assert.That(value.OriginalLength).IsEqualTo(_U32(bytes, 24));
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(EnhancedPacketBlock));
    }

    [Test]
    public async Task InterfaceDescriptionBlockParsesEveryField()
    {
        byte[] bytes = _Pattern(InterfaceDescriptionBlock.Size);

        bool ok = InterfaceDescriptionBlock.TryParse(bytes, out InterfaceDescriptionBlock value);
        bool shortOk = InterfaceDescriptionBlock.TryParse(bytes.AsSpan(0, InterfaceDescriptionBlock.Size - 1), out InterfaceDescriptionBlock shortValue);

        await Assert.That(bytes.Length).IsEqualTo(16);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.BlockType).IsEqualTo(_U32(bytes, 0));
        await Assert.That(value.BlockTotalLength).IsEqualTo(_U32(bytes, 4));
        await Assert.That(value.LinkType).IsEqualTo(_U16(bytes, 8));
        await Assert.That(value.Reserved).IsEqualTo(_U16(bytes, 10));
        await Assert.That(value.SnapLength).IsEqualTo(_U32(bytes, 12));
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(InterfaceDescriptionBlock));
    }

    [Test]
    public async Task ObsoletePacketBlockParsesEveryField()
    {
        byte[] bytes = _Pattern(ObsoletePacketBlock.Size);

        bool ok = ObsoletePacketBlock.TryParse(bytes, out ObsoletePacketBlock value);
        bool shortOk = ObsoletePacketBlock.TryParse(bytes.AsSpan(0, ObsoletePacketBlock.Size - 1), out ObsoletePacketBlock shortValue);

        await Assert.That(bytes.Length).IsEqualTo(28);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.BlockType).IsEqualTo(_U32(bytes, 0));
        await Assert.That(value.BlockTotalLength).IsEqualTo(_U32(bytes, 4));
        await Assert.That(value.InterfaceId).IsEqualTo(_U16(bytes, 8));
        await Assert.That(value.DropsCount).IsEqualTo(_U16(bytes, 10));
        await Assert.That(value.TimestampHigh).IsEqualTo(_U32(bytes, 12));
        await Assert.That(value.TimestampLow).IsEqualTo(_U32(bytes, 16));
        await Assert.That(value.CapturedLength).IsEqualTo(_U32(bytes, 20));
        await Assert.That(value.OriginalLength).IsEqualTo(_U32(bytes, 24));
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(ObsoletePacketBlock));
    }

    [Test]
    public async Task OptionHeaderParsesEveryField()
    {
        byte[] bytes = _Pattern(OptionHeader.Size);

        bool ok = OptionHeader.TryParse(bytes, out OptionHeader value);
        bool shortOk = OptionHeader.TryParse(bytes.AsSpan(0, OptionHeader.Size - 1), out OptionHeader shortValue);

        await Assert.That(bytes.Length).IsEqualTo(4);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.Code).IsEqualTo(_U16(bytes, 0));
        await Assert.That(value.Length).IsEqualTo(_U16(bytes, 2));
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(OptionHeader));
    }

    [Test]
    public async Task PcapGlobalHeaderParsesEveryField()
    {
        byte[] bytes = _Pattern(PcapGlobalHeader.Size);

        bool ok = PcapGlobalHeader.TryParse(bytes, out PcapGlobalHeader value);
        bool shortOk = PcapGlobalHeader.TryParse(bytes.AsSpan(0, PcapGlobalHeader.Size - 1), out PcapGlobalHeader shortValue);

        await Assert.That(bytes.Length).IsEqualTo(24);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.Magic).IsEqualTo(_U32(bytes, 0));
        await Assert.That(value.VersionMajor).IsEqualTo(_U16(bytes, 4));
        await Assert.That(value.VersionMinor).IsEqualTo(_U16(bytes, 6));
        await Assert.That(value.ThisZone).IsEqualTo(_U32(bytes, 8));
        await Assert.That(value.SigFigs).IsEqualTo(_U32(bytes, 12));
        await Assert.That(value.SnapLen).IsEqualTo(_U32(bytes, 16));
        await Assert.That(value.Network).IsEqualTo(_U32(bytes, 20));
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(PcapGlobalHeader));
    }

    [Test]
    public async Task PcapPacketHeaderParsesEveryField()
    {
        byte[] bytes = _Pattern(PcapPacketHeader.Size);

        bool ok = PcapPacketHeader.TryParse(bytes, out PcapPacketHeader value);
        bool shortOk = PcapPacketHeader.TryParse(bytes.AsSpan(0, PcapPacketHeader.Size - 1), out PcapPacketHeader shortValue);

        await Assert.That(bytes.Length).IsEqualTo(16);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.TsSec).IsEqualTo(_U32(bytes, 0));
        await Assert.That(value.TsFrac).IsEqualTo(_U32(bytes, 4));
        await Assert.That(value.InclLen).IsEqualTo(_U32(bytes, 8));
        await Assert.That(value.OrigLen).IsEqualTo(_U32(bytes, 12));
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(PcapPacketHeader));
    }

    [Test]
    public async Task SectionHeaderBlockParsesEveryFieldIncludingNegativeSectionLength()
    {
        byte[] bytes = _Pattern(SectionHeaderBlock.Size);
        bytes[SectionHeaderBlock.Size - 1] = 0xFF; // top byte set: the section length is signed and must come out negative

        bool ok = SectionHeaderBlock.TryParse(bytes, out SectionHeaderBlock value);
        bool shortOk = SectionHeaderBlock.TryParse(bytes.AsSpan(0, SectionHeaderBlock.Size - 1), out SectionHeaderBlock shortValue);

        await Assert.That(bytes.Length).IsEqualTo(24);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.BlockType).IsEqualTo(_U32(bytes, 0));
        await Assert.That(value.BlockTotalLength).IsEqualTo(_U32(bytes, 4));
        await Assert.That(value.ByteOrderMagic).IsEqualTo(_U32(bytes, 8));
        await Assert.That(value.MajorVersion).IsEqualTo(_U16(bytes, 12));
        await Assert.That(value.MinorVersion).IsEqualTo(_U16(bytes, 14));
        await Assert.That(value.SectionLength).IsEqualTo(unchecked((long)_U64(bytes, 16)));
        await Assert.That(value.SectionLength < 0).IsTrue();
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(SectionHeaderBlock));
    }

    [Test]
    public async Task SimplePacketBlockParsesEveryField()
    {
        byte[] bytes = _Pattern(SimplePacketBlock.Size);

        bool ok = SimplePacketBlock.TryParse(bytes, out SimplePacketBlock value);
        bool shortOk = SimplePacketBlock.TryParse(bytes.AsSpan(0, SimplePacketBlock.Size - 1), out SimplePacketBlock shortValue);

        await Assert.That(bytes.Length).IsEqualTo(12);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.BlockType).IsEqualTo(_U32(bytes, 0));
        await Assert.That(value.BlockTotalLength).IsEqualTo(_U32(bytes, 4));
        await Assert.That(value.OriginalPacketLength).IsEqualTo(_U32(bytes, 8));
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortValue).IsEqualTo(default(SimplePacketBlock));
    }

    #endregion
}

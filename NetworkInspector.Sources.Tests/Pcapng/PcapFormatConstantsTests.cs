// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Pcapng;

/// <summary>Exit-point coverage for PCAPNG format constants and padding helpers.</summary>
internal sealed class PcapFormatConstantsTests
{
    [Test]
    [Arguments(PcapConstants.BlockTypeSHB, true)]
    [Arguments(PcapConstants.BlockTypeEPB, true)]
    [Arguments(0xDEADBEEFu, false)]
    public async Task IsKnownBlockType_ClassifiesBlockTypes(uint blockType, bool expected)
    {
        await Assert.That(PcapConstants.IsKnownBlockType(blockType)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(PcapConstants.BlockTypeEPB, true)]
    [Arguments(PcapConstants.BlockTypeSPB, true)]
    [Arguments(PcapConstants.BlockTypePB, true)]
    [Arguments(PcapConstants.BlockTypeSHB, false)]
    public async Task IsPacketBlock_ClassifiesPacketBlocks(uint blockType, bool expected)
    {
        await Assert.That(PcapConstants.IsPacketBlock(blockType)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(PcapConstants.BlockTypeSHB, "Section Header Block")]
    [Arguments(PcapConstants.BlockTypeIDB, "Interface Description Block")]
    [Arguments(PcapConstants.BlockTypePB, "Packet Block (obsolete)")]
    [Arguments(PcapConstants.BlockTypeSPB, "Simple Packet Block")]
    [Arguments(PcapConstants.BlockTypeNRB, "Name Resolution Block")]
    [Arguments(PcapConstants.BlockTypeISB, "Interface Statistics Block")]
    [Arguments(PcapConstants.BlockTypeEPB, "Enhanced Packet Block")]
    [Arguments(PcapConstants.BlockTypeITB, "IRIG Timestamp Block")]
    [Arguments(PcapConstants.BlockTypeArinc429, "ARINC 429 Block")]
    [Arguments(PcapConstants.BlockTypeDSB, "Decryption Secrets Block")]
    [Arguments(PcapConstants.BlockTypeCBCopy, "Custom Block (copyable)")]
    [Arguments(PcapConstants.BlockTypeCBNoCopy, "Custom Block (non-copyable)")]
    [Arguments(0x12345678u, "Unknown Block (0x12345678)")]
    public async Task BlockTypeName_ReturnsReadableName(uint blockType, string expected)
    {
        await Assert.That(PcapConstants.BlockTypeName(blockType)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0, 4)]
    [Arguments(1, 8)]
    [Arguments(5, 12)]
    public async Task OptionSize_ComputesTlvSize(int valueLength, int expected)
    {
        await Assert.That(PcapPadding.OptionSize(valueLength)).IsEqualTo(expected);
    }

    [Test]
    public async Task OptionSize_NegativeLength_ThrowsArgumentOutOfRangeException()
    {
        await Assert.That(() => PcapPadding.OptionSize(-1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task TryPacketDataBounds_CapLenPlusPadMustFit()
    {
        await Assert.That(PcapngBlockLayout.TryPacketDataBounds(28, 96, 61, out int captured, out int pad)).IsTrue();
        await Assert.That(captured).IsEqualTo(61);
        await Assert.That(pad).IsEqualTo(3);

        await Assert.That(PcapngBlockLayout.TryPacketDataBounds(28, 96, 65, out _, out _)).IsFalse();
    }

    [Test]
    public async Task OptionIterator_StopsWhenPaddedTlvDoesNotFit()
    {
        // code=1, length=1, one value byte, missing 3 pad bytes
        byte[] truncated = [1, 0, 1, 0, 0x41];
        PcapOptionIterator iter = new(truncated);
        bool hasOption = iter.TryGetNext(out _);

        await Assert.That(hasOption).IsFalse();
    }
}

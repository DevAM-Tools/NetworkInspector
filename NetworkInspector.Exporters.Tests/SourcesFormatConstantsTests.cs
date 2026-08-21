// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests;

/// <summary>
/// Exit-point coverage for format helpers still linked into <see cref="NetworkInspector.Exporters"/>
/// (<c>BlfConstants</c>, <c>PcapConstants</c>, <c>PcapPadding</c>).
/// </summary>
internal sealed class SourcesFormatConstantsTests
{
    [Test]
    public async Task BlfConstants_FileMagicBytes_MatchesExpectedValue()
    {
        byte[] magic = BlfConstants.FileMagicBytes.ToArray();

        await Assert.That(magic.Length).IsEqualTo(4);
        await Assert.That(magic[0]).IsEqualTo((byte)'L');
        await Assert.That(magic[1]).IsEqualTo((byte)'O');
        await Assert.That(magic[2]).IsEqualTo((byte)'G');
        await Assert.That(magic[3]).IsEqualTo((byte)'G');
    }

    [Test]
    public async Task BlfConstants_ObjectMagicBytes_MatchesExpectedValue()
    {
        byte[] magic = BlfConstants.ObjectMagicBytes.ToArray();

        await Assert.That(magic.Length).IsEqualTo(4);
        await Assert.That(magic[0]).IsEqualTo((byte)'L');
        await Assert.That(magic[1]).IsEqualTo((byte)'O');
        await Assert.That(magic[2]).IsEqualTo((byte)'B');
        await Assert.That(magic[3]).IsEqualTo((byte)'J');
    }

    [Test]
    public async Task BlfConstants_CanFdPayloadLengthToDlc_TableIsPopulated()
    {
        byte[] table = BlfConstants.CanFdPayloadLengthToDlc.ToArray();

        await Assert.That(table.Length).IsEqualTo(65);
        await Assert.That(table[0]).IsEqualTo(BlfConstants.GetCanFdDlcFromPayloadByteCount(0));
        await Assert.That(table[64]).IsEqualTo(BlfConstants.GetCanFdDlcFromPayloadByteCount(64));
    }

    [Test]
    [Arguments(BlfConstants.ObjTypeCanMessage, true)]
    [Arguments(BlfConstants.ObjTypeCanFdMessage64, true)]
    [Arguments(BlfConstants.ObjTypeEthernetFrameEx, true)]
    [Arguments(0xFFFFFFFFu, false)]
    public async Task BlfConstants_IsFrameProducingType_ClassifiesObjectTypes(uint objectType, bool expected)
    {
        await Assert.That(BlfConstants.IsFrameProducingType(objectType)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(PcapConstants.BlockTypeSHB, true)]
    [Arguments(PcapConstants.BlockTypeEPB, true)]
    [Arguments(0xDEADBEEFu, false)]
    public async Task PcapConstants_IsKnownBlockType_ClassifiesBlockTypes(uint blockType, bool expected)
    {
        await Assert.That(PcapConstants.IsKnownBlockType(blockType)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(PcapConstants.BlockTypeEPB, true)]
    [Arguments(PcapConstants.BlockTypeSPB, true)]
    [Arguments(PcapConstants.BlockTypePB, true)]
    [Arguments(PcapConstants.BlockTypeSHB, false)]
    public async Task PcapConstants_IsPacketBlock_ClassifiesPacketBlocks(uint blockType, bool expected)
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
    public async Task PcapConstants_BlockTypeName_ReturnsReadableName(uint blockType, string expected)
    {
        await Assert.That(PcapConstants.BlockTypeName(blockType)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(1, 8)]
    [Arguments(5, 12)]
    public async Task PcapPadding_OptionSize_ComputesTlvSize(int valueLength, int expected)
    {
        await Assert.That(PcapPadding.OptionSize(valueLength)).IsEqualTo(expected);
    }
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Unit tests for <see cref="AscLineClassifier.Classify"/> covering all
/// known ASC line type classifications and edge cases.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class AscLineClassifierTests
{
    // ========================================================================
    // Comments
    // ========================================================================

    [Test]
    public async Task EmptyLine_ClassifiedAsComment()
    {
        AscLineType result = AscLineClassifier.Classify(""u8);

        await Assert.That(result).IsEqualTo(AscLineType.Comment);
    }

    [Test]
    public async Task SemicolonLine_ClassifiedAsComment()
    {
        AscLineType result = AscLineClassifier.Classify("; this is a comment"u8);

        await Assert.That(result).IsEqualTo(AscLineType.Comment);
    }

    [Test]
    public async Task DoubleSlashLine_ClassifiedAsComment()
    {
        AscLineType result = AscLineClassifier.Classify("// another comment"u8);

        await Assert.That(result).IsEqualTo(AscLineType.Comment);
    }

    // ========================================================================
    // Header lines
    // ========================================================================

    [Test]
    public async Task DateLine_ClassifiedAsHeader()
    {
        AscLineType result = AscLineClassifier.Classify("date Sun Nov 24 11:44:00 AM 2019"u8);

        await Assert.That(result).IsEqualTo(AscLineType.Header);
    }

    [Test]
    public async Task BaseLine_ClassifiedAsHeader()
    {
        AscLineType result = AscLineClassifier.Classify("base hex timestamps absolute"u8);

        await Assert.That(result).IsEqualTo(AscLineType.Header);
    }

    [Test]
    public async Task InternalEventsLine_ClassifiedAsHeader()
    {
        AscLineType result = AscLineClassifier.Classify("internal events logged"u8);

        await Assert.That(result).IsEqualTo(AscLineType.Header);
    }

    // ========================================================================
    // Trigger blocks
    // ========================================================================

    [Test]
    public async Task BeginTriggerblock_Classified()
    {
        AscLineType result = AscLineClassifier.Classify("Begin Triggerblock"u8);

        await Assert.That(result).IsEqualTo(AscLineType.TriggerBlockBegin);
    }

    [Test]
    public async Task EndTriggerBlock_Classified()
    {
        AscLineType result = AscLineClassifier.Classify("End TriggerBlock"u8);

        await Assert.That(result).IsEqualTo(AscLineType.TriggerBlockEnd);
    }

    // ========================================================================
    // Start of measurement
    // ========================================================================

    [Test]
    public async Task StartOfMeasurement_Classified()
    {
        AscLineType result = AscLineClassifier.Classify("Start of measurement"u8);

        await Assert.That(result).IsEqualTo(AscLineType.StartOfMeasurement);
    }

    // ========================================================================
    // CAN messages
    // ========================================================================

    [Test]
    public async Task CanMessage_Classified()
    {
        AscLineType result = AscLineClassifier.Classify("0.100000 1 123 Rx d 8 AA BB CC DD EE FF 00 11"u8);

        await Assert.That(result).IsEqualTo(AscLineType.CanMessage);
    }

    [Test]
    public async Task CanFdMessage_Classified()
    {
        AscLineType result = AscLineClassifier.Classify("0.200000 CANFD 1 Rx 200 1 0 8 8 01 02 03 04 05 06 07 08"u8);

        await Assert.That(result).IsEqualTo(AscLineType.CanFdMessage);
    }

    // ========================================================================
    // LIN messages
    // ========================================================================

    [Test]
    public async Task LinMessage_Classified()
    {
        AscLineType result = AscLineClassifier.Classify("0.300000 Li 3C Rx 8 01 02 03 04 05 06 07 08 checksum = F0"u8);

        await Assert.That(result).IsEqualTo(AscLineType.LinMessage);
    }

    // ========================================================================
    // FlexRay messages
    // ========================================================================

    [Test]
    public async Task FlexRayMessage_Classified()
    {
        AscLineType result = AscLineClassifier.Classify("0.400000 Fr 1 V9 0A 4 0 0 1234 x 8 0102030405060708"u8);

        await Assert.That(result).IsEqualTo(AscLineType.FlexRayMessage);
    }

    // ========================================================================
    // Ethernet packets
    // ========================================================================

    [Test]
    public async Task EthPacket_Classified()
    {
        AscLineType result = AscLineClassifier.Classify("0.500000 ETH 1 Rx 14:001122334455667788990A0B0C0D"u8);

        await Assert.That(result).IsEqualTo(AscLineType.EthernetPacket);
    }

    [Test]
    public async Task AfdxPacketIsEthernet()
    {
        AscLineType bytes = AscLineClassifier.Classify("0.600000 AFDX 1 Rx 1 0 0 4:01020304"u8);
        AscLineType chars = AscLineClassifier.Classify("0.600000 AFDX 1 Rx 1 0 0 4:01020304".AsSpan());

        await Assert.That(bytes).IsEqualTo(AscLineType.EthernetPacket);
        await Assert.That(chars).IsEqualTo(AscLineType.EthernetPacket);
    }

    [Test]
    public async Task LinChannel1TokenIsLi()
    {
        AscLineType li = AscLineClassifier.Classify("0.073973 Li 2d Tx 1 aa checksum = 70"u8);
        AscLineType wildcard = AscLineClassifier.Classify("0.073973 L* 2d Tx 1 aa"u8);

        await Assert.That(li).IsEqualTo(AscLineType.LinMessage);
        await Assert.That(wildcard).IsEqualTo(AscLineType.Unknown);
    }

    [Test]
    public async Task ChannelStatisticIsNotCan()
    {
        AscLineType result = AscLineClassifier.Classify("1.0100 1 Statistic: D 0 R 0 XD 0 XR 0 E 0 O 0 B 0.0%"u8);

        await Assert.That(result).IsEqualTo(AscLineType.CanBusStatistics);
    }

    [Test]
    public async Task FlexRayStatusIsUnknown()
    {
        AscLineType result = AscLineClassifier.Classify("0.003022 Fr SE 0 0 1 3 255 5 2 15 0 0 0 0"u8);

        await Assert.That(result).IsEqualTo(AscLineType.Unknown);
    }

    [Test]
    public async Task EthStatIsUnknown()
    {
        AscLineType result = AscLineClassifier.Classify("0.100000 ETH 1 STAT 0"u8);

        await Assert.That(result).IsEqualTo(AscLineType.Unknown);
    }

    [Test]
    public async Task EthRxErIsUnknown()
    {
        AscLineType result = AscLineClassifier.Classify("0.200000 ETH 2 RxEr e:0011"u8);

        await Assert.That(result).IsEqualTo(AscLineType.Unknown);
    }

    [Test]
    public async Task CanXlTokenIsCanXlMessage()
    {
        AscLineType result = AscLineClassifier.Classify("0.100000 CANXL 1 Rx XLFF 0 0 123"u8);

        await Assert.That(result).IsEqualTo(AscLineType.CanXlMessage);
    }

    [Test]
    public async Task FlexRayNameContainingCanFdStaysFlexRay()
    {
        AscLineType result = AscLineClassifier.Classify("0.400000 Fr 1 CANFD 0A 4 0 0 1234 x 8 0102030405060708"u8);

        await Assert.That(result).IsEqualTo(AscLineType.FlexRayMessage);
    }

    // ========================================================================
    // Error / overload frames
    // ========================================================================

    [Test]
    public async Task ErrorFrame_Classified()
    {
        AscLineType result = AscLineClassifier.Classify("0.700000 1 ErrorFrame"u8);

        await Assert.That(result).IsEqualTo(AscLineType.CanErrorFrame);
    }

    // ========================================================================
    // Environment variables
    // ========================================================================

    [Test]
    public async Task EnvironmentVariable_Classified()
    {
        AscLineType result = AscLineClassifier.Classify("0.800000 EnvVar: MyVar = 42"u8);

        await Assert.That(result).IsEqualTo(AscLineType.EnvironmentVariable);
    }

    // ========================================================================
    // System variables
    // ========================================================================

    [Test]
    public async Task SystemVariable_Classified()
    {
        AscLineType result = AscLineClassifier.Classify("0.900000 SV: MySystem.Var = 1"u8);

        await Assert.That(result).IsEqualTo(AscLineType.SystemVariable);
    }
}

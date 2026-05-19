// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Sources.Asc.Format;

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
        AscLineType result = AscLineClassifier.Classify("0.300000 L1 3C Rx 8 01 02 03 04 05 06 07 08 checksum = F0"u8);

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
    public async Task AfdxPacket_ClassifiedAsEthernet()
    {
        AscLineType result = AscLineClassifier.Classify("0.600000 AFDX 1 Rx 14:AABBCCDDEEFF112233445566ABCD"u8);

        await Assert.That(result).IsEqualTo(AscLineType.EthernetPacket);
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

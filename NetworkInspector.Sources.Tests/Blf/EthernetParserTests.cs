// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Direct tests for <see cref="EthernetParser"/> object layouts (Type 71 / 120 / 102).
/// </summary>
internal sealed class EthernetParserTests
{
    [Test]
    public async Task Type71_EthertypeStoredLittleEndian_ReconstructsWireBigEndian()
    {
        byte[] payload = new byte[32 + 4];
        payload.AsSpan(0, 6).Fill(0x11);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), 1);
        payload.AsSpan(8, 6).Fill(0x22);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), 0x0800);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 4);
        payload[32] = 0xDE;
        payload[33] = 0xAD;
        payload[34] = 0xBE;
        payload[35] = 0xEF;

        bool ok = EthernetParser.TryParseType71(payload, out byte[] frame, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)1);
        await Assert.That(frame[12]).IsEqualTo((byte)0x08);
        await Assert.That(frame[13]).IsEqualTo((byte)0x00);
        await Assert.That(frame.AsSpan(14, 4).ToArray()).IsEquivalentTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
    }

    [Test]
    public async Task Type71_TpidWithoutTci_DoesNotInsertVlan()
    {
        byte[] payload = new byte[32 + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), 0x0800);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18), 0x8100);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 2);

        bool ok = EthernetParser.TryParseType71(payload, out byte[] frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[12]).IsEqualTo((byte)0x08);
        await Assert.That(frame[13]).IsEqualTo((byte)0x00);
        await Assert.That(frame.Length).IsEqualTo(16);
    }

    [Test]
    public async Task Type71_TpidAndTci_InsertsVlanTag()
    {
        byte[] payload = new byte[32 + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), 0x0800);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18), 0x8100);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20), 0x0064);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 2);

        bool ok = EthernetParser.TryParseType71(payload, out byte[] frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[12]).IsEqualTo((byte)0x81);
        await Assert.That(frame[13]).IsEqualTo((byte)0x00);
        await Assert.That(frame[14]).IsEqualTo((byte)0x00);
        await Assert.That(frame[15]).IsEqualTo((byte)0x64);
        await Assert.That(frame[16]).IsEqualTo((byte)0x08);
        await Assert.That(frame[17]).IsEqualTo((byte)0x00);
        await Assert.That(frame.Length).IsEqualTo(20);
    }

    [Test]
    public async Task Type120_FrameLengthAtOffset22_DataAt32()
    {
        byte[] payload = new byte[32 + 14];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 5);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 14);
        payload.AsSpan(32, 6).Fill(0xAA);
        payload.AsSpan(38, 6).Fill(0xBB);
        payload[44] = 0x08;
        payload[45] = 0x00;

        bool ok = EthernetParser.TryParseType120(payload, out ReadOnlyMemory<byte> frame, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)5);
        await Assert.That(frame.Length).IsEqualTo(14);
        await Assert.That(frame.Span[12]).IsEqualTo((byte)0x08);
        await Assert.That(frame.Span[13]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task Type120_PayloadMemory_AliasesExistingArray()
    {
        byte[] payload = new byte[32 + 14];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 5);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 14);
        payload.AsSpan(32, 6).Fill(0xAA);
        payload.AsSpan(38, 6).Fill(0xBB);
        payload[44] = 0x08;
        payload[45] = 0x00;

        bool ok = EthernetParser.TryParseType120(
            payload, payload, out ReadOnlyMemory<byte> frame, out _);

        await Assert.That(ok).IsTrue();
        bool gotArray = MemoryMarshal.TryGetArray(frame, out ArraySegment<byte> segment);
        await Assert.That(gotArray).IsTrue();
        await Assert.That(segment.Array).IsSameReferenceAs(payload);
        await Assert.That(segment.Offset).IsEqualTo(32);
        await Assert.That(segment.Count).IsEqualTo(14);
    }

    [Test]
    public async Task Type120_ShortHeader_ReturnsFalse()
    {
        bool ok = EthernetParser.TryParseType120(new byte[31], out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Type102_ChannelAtOffset2_DataAt20()
    {
        byte[] payload = new byte[20 + 14];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 14);
        payload.AsSpan(20, 6).Fill(0x01);
        payload.AsSpan(26, 6).Fill(0x02);
        payload[32] = 0x08;
        payload[33] = 0x00;

        bool ok = EthernetParser.TryParseType102(payload, out ReadOnlyMemory<byte> frame, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)7);
        await Assert.That(frame.Length).IsEqualTo(14);
        await Assert.That(frame.Span[0]).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task Type102_ShortHeader_ReturnsFalse()
    {
        bool ok = EthernetParser.TryParseType102(new byte[19], out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Type102_PayloadMemory_AliasesExistingArray()
    {
        byte[] payload = new byte[20 + 14];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 14);
        payload.AsSpan(20, 6).Fill(0x01);
        payload.AsSpan(26, 6).Fill(0x02);
        payload[32] = 0x08;
        payload[33] = 0x00;

        bool ok = EthernetParser.TryParseType102(
            payload, payload, out ReadOnlyMemory<byte> frame, out _);

        await Assert.That(ok).IsTrue();
        bool gotArray = MemoryMarshal.TryGetArray(frame, out ArraySegment<byte> segment);
        await Assert.That(gotArray).IsTrue();
        await Assert.That(segment.Array).IsSameReferenceAs(payload);
        await Assert.That(segment.Offset).IsEqualTo(20);
        await Assert.That(segment.Count).IsEqualTo(14);
    }
}

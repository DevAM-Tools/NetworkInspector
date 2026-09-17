// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Scan-path channel peek coverage: indexers must read channel without reconstructing frames.
/// </summary>
internal sealed class BlfChannelPeekTests
{
    [Test]
    [Arguments(BlfConstants.ObjTypeCanMessage, 16, 0, (ushort)9)]
    [Arguments(BlfConstants.ObjTypeCanFdMessage, 20, 0, (ushort)9)]
    [Arguments(BlfConstants.ObjTypeEthernetFrame, 32, 6, (ushort)9)]
    [Arguments(BlfConstants.ObjTypeEthernetFrameEx, 32, 4, (ushort)9)]
    [Arguments(BlfConstants.ObjTypeFlexRayData, 12, 0, (ushort)9)]
    [Arguments(BlfConstants.ObjTypeLinMessage, 12, 0, (ushort)9)]
    public async Task TryGetChannel_MinSizePayload_ReadsChannel(
        uint objectType, int minSize, int channelOffset, ushort expected)
    {
        byte[] payload = new byte[minSize];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(channelOffset), expected);

        bool ok = BlfFrameDispatcher.TryGetChannel(objectType, payload, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo(expected);
    }

    [Test]
    public async Task TryGetChannel_UnknownType_ReturnsFalse()
    {
        bool ok = BlfFrameDispatcher.TryGetChannel(0xFFFF_FFFFu, new byte[64], out ushort channel);

        await Assert.That(ok).IsFalse();
        await Assert.That(channel).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task TryGetChannel_ShortPayload_ReturnsFalse()
    {
        bool ok = BlfFrameDispatcher.TryGetChannel(BlfConstants.ObjTypeCanMessage, new byte[15], out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryGetChannel_CanFd64_ReadsByte0()
    {
        byte[] payload = new byte[40];
        payload[0] = 7;

        bool ok = BlfFrameDispatcher.TryGetChannel(BlfConstants.ObjTypeCanFdMessage64, payload, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)7);
    }

    [Test]
    public async Task TryGetChannel_LinMessage2_ReadsOffset12()
    {
        byte[] payload = new byte[121];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 11);

        bool ok = BlfFrameDispatcher.TryGetChannel(BlfConstants.ObjTypeLinMessage2, payload, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)11);
    }

    [Test]
    public async Task TryGetChannel_EthernetRxError_ReadsOffset2()
    {
        byte[] payload = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 6);

        bool ok = BlfFrameDispatcher.TryGetChannel(BlfConstants.ObjTypeEthernetRxError, payload, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)6);
    }

    [Test]
    [Arguments(BlfConstants.ObjTypeCanError, 8)]
    [Arguments(BlfConstants.ObjTypeCanOverload, 4)]
    [Arguments(BlfConstants.ObjTypeCanErrorExt, 24)]
    [Arguments(BlfConstants.ObjTypeCanMessage2, 16)]
    [Arguments(BlfConstants.ObjTypeLinCrcError, 4)]
    [Arguments(BlfConstants.ObjTypeLinSleep, 4)]
    [Arguments(BlfConstants.ObjTypeLinWakeup, 4)]
    [Arguments(BlfConstants.ObjTypeFlexRayMessage, 32)]
    [Arguments(BlfConstants.ObjTypeFlexRayRcvMessage, 44)]
    [Arguments(BlfConstants.ObjTypeFlexRayRcvMessageEx, 84)]
    public async Task TryGetChannel_U16AtOffset0(uint objectType, int minSize)
    {
        byte[] payload = new byte[minSize];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 13);

        bool ok = BlfFrameDispatcher.TryGetChannel(objectType, payload, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)13);
    }

    [Test]
    [Arguments(BlfConstants.ObjTypeCanFdError64, 44)]
    [Arguments(BlfConstants.ObjTypeCanXlChannelFrame, 104)]
    public async Task TryGetChannel_U8AtOffset0(uint objectType, int minSize)
    {
        byte[] payload = new byte[minSize];
        payload[0] = 4;

        bool ok = BlfFrameDispatcher.TryGetChannel(objectType, payload, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)4);
    }

    [Test]
    [Arguments(BlfConstants.ObjTypeLinCrcError2, 38)]
    [Arguments(BlfConstants.ObjTypeLinWakeup2, 20)]
    public async Task TryGetChannel_LinV2_U16AtOffset12(uint objectType, int minSize)
    {
        byte[] payload = new byte[minSize];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 8);

        bool ok = BlfFrameDispatcher.TryGetChannel(objectType, payload, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)8);
    }

    [Test]
    [Arguments(BlfConstants.ObjTypeLinRcvError)]
    [Arguments(BlfConstants.ObjTypeLinSndError)]
    [Arguments(BlfConstants.ObjTypeLinRcvError2)]
    [Arguments(BlfConstants.ObjTypeLinSndError2)]
    public async Task TryGetChannel_RemainingLinErrorTypes(uint objectType)
    {
        int minSize;
        int offset;
        if (objectType is BlfConstants.ObjTypeLinRcvError2 or BlfConstants.ObjTypeLinSndError2)
        {
            minSize = 38;
            offset = 12;
        }
        else
        {
            minSize = 4;
            offset = 0;
        }

        byte[] payload = new byte[minSize];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), 3);

        bool ok = BlfFrameDispatcher.TryGetChannel(objectType, payload, out ushort channel);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo((ushort)3);
    }

    [Test]
    [Arguments(BlfConstants.ObjTypeCanMessage, 15)]
    [Arguments(BlfConstants.ObjTypeCanMessage2, 15)]
    [Arguments(BlfConstants.ObjTypeCanError, 7)]
    [Arguments(BlfConstants.ObjTypeCanOverload, 3)]
    [Arguments(BlfConstants.ObjTypeCanErrorExt, 23)]
    [Arguments(BlfConstants.ObjTypeCanFdMessage, 19)]
    [Arguments(BlfConstants.ObjTypeCanFdMessage64, 39)]
    [Arguments(BlfConstants.ObjTypeCanFdError64, 43)]
    [Arguments(BlfConstants.ObjTypeCanXlChannelFrame, 103)]
    [Arguments(BlfConstants.ObjTypeEthernetFrame, 31)]
    [Arguments(BlfConstants.ObjTypeEthernetFrameEx, 31)]
    [Arguments(BlfConstants.ObjTypeEthernetRxError, 19)]
    [Arguments(BlfConstants.ObjTypeLinMessage, 11)]
    [Arguments(BlfConstants.ObjTypeLinCrcError, 3)]
    [Arguments(BlfConstants.ObjTypeLinSleep, 3)]
    [Arguments(BlfConstants.ObjTypeLinWakeup, 3)]
    [Arguments(BlfConstants.ObjTypeLinMessage2, 120)]
    [Arguments(BlfConstants.ObjTypeLinCrcError2, 37)]
    [Arguments(BlfConstants.ObjTypeLinWakeup2, 19)]
    [Arguments(BlfConstants.ObjTypeFlexRayData, 11)]
    [Arguments(BlfConstants.ObjTypeFlexRayMessage, 31)]
    [Arguments(BlfConstants.ObjTypeFlexRayRcvMessage, 43)]
    [Arguments(BlfConstants.ObjTypeFlexRayRcvMessageEx, 83)]
    public async Task TryGetChannel_BelowMinSize_ReturnsFalse(uint objectType, int shortSize)
    {
        bool ok = BlfFrameDispatcher.TryGetChannel(objectType, new byte[shortSize], out ushort channel);

        await Assert.That(ok).IsFalse();
        await Assert.That(channel).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task TryGetChannel_ParserUnknownType_ReturnsFalse()
    {
        bool can = CanParser.TryGetChannel(0xFFFF_FFFFu, new byte[64], out _);
        bool lin = LinParser.TryGetChannel(0xFFFF_FFFFu, new byte[64], out _);
        bool flex = FlexRayParser.TryGetChannel(0xFFFF_FFFFu, new byte[64], out _);

        await Assert.That(can).IsFalse();
        await Assert.That(lin).IsFalse();
        await Assert.That(flex).IsFalse();
    }
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// A classic CAN frame (EDL clear) must come out as the same SocketCAN bytes whichever BLF object type carried it: Type 1,
/// Type 100 (CAN FD message) or Type 101 (CAN FD message 64). Also the "set on success" contract of the AppText parser.
/// </summary>
internal sealed class BlfClassicFrameConsistencyTests
{
    #region Helpers

    private static readonly byte[] _Data = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];

    private static byte[] _Type1(byte flags, byte dlc, uint rawId)
    {
        byte[] payload = new byte[16];
        payload[0] = 1;
        payload[2] = flags;
        payload[3] = dlc;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), rawId);
        _Data.CopyTo(payload.AsSpan(8));
        return payload;
    }

    private static byte[] _Type100(byte flags, byte dlc, uint rawId, byte canFdFlags, int validDataBytes)
    {
        byte[] payload = new byte[20 + _Data.Length];
        payload[0] = 1;
        payload[2] = flags;
        payload[3] = dlc;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), rawId);
        payload[13] = canFdFlags;
        payload[14] = (byte)validDataBytes;
        _Data.CopyTo(payload.AsSpan(20));
        return payload;
    }

    private static byte[] _Type101(uint flags, byte dlc, uint rawId, int validDataBytes)
    {
        byte[] payload = new byte[40 + _Data.Length];
        payload[0] = 1;
        payload[1] = dlc;
        payload[2] = (byte)validDataBytes;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), rawId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), flags);
        _Data.CopyTo(payload.AsSpan(40));
        return payload;
    }

    #endregion

    #region Classic frames in CAN FD objects

    [Test]
    [Arguments(5, 5)]
    [Arguments(8, 8)]
    [Arguments(12, 8)]
    [Arguments(15, 8)]
    public async Task ClassicRemoteFrameInType100KeepsTheRequestedDlcLikeType1(int dlc, int expectedLenByte)
    {
        bool type1 = CanParser.TryParseCanMessage(_Type1(0x80, (byte)dlc, 0x123), out byte[] expected, out _);
        bool type100 = CanParser.TryParseCanFdMessage(_Type100(0x80, (byte)dlc, 0x123, 0, 0), out byte[] actual, out _);

        await Assert.That(type1).IsTrue();
        await Assert.That(type100).IsTrue();
        await Assert.That(actual).IsEquivalentTo(expected);
        await Assert.That((int)actual[4]).IsEqualTo(expectedLenByte);
        await Assert.That(actual.Length).IsEqualTo(8);
    }

    [Test]
    [Arguments(5, 5, 0)]
    [Arguments(8, 8, 0)]
    [Arguments(9, 8, 9)]
    [Arguments(15, 8, 15)]
    public async Task ClassicDataFrameInType100KeepsDlcAbove8InByte7LikeType1(int dlc, int expectedLen, int expectedLen8Dlc)
    {
        bool type1 = CanParser.TryParseCanMessage(_Type1(0, (byte)dlc, 0x80000123), out byte[] expected, out _);
        bool type100 = CanParser.TryParseCanFdMessage(
            _Type100(0, (byte)dlc, 0x80000123, 0, expectedLen), out byte[] actual, out _);

        await Assert.That(type1).IsTrue();
        await Assert.That(type100).IsTrue();
        await Assert.That(actual).IsEquivalentTo(expected);
        await Assert.That((int)actual[4]).IsEqualTo(expectedLen);
        await Assert.That((int)actual[7]).IsEqualTo(expectedLen8Dlc);
    }

    [Test]
    [Arguments(5, 5)]
    [Arguments(12, 8)]
    public async Task ClassicRemoteFrameInType101KeepsTheRequestedDlcLikeType1(int dlc, int expectedLenByte)
    {
        bool type1 = CanParser.TryParseCanMessage(_Type1(0x80, (byte)dlc, 0x123), out byte[] expected, out _);
        bool type101 = CanParser.TryParseCanFdMessage64(_Type101(0x10, (byte)dlc, 0x123, 0), out byte[] actual, out _);

        await Assert.That(type1).IsTrue();
        await Assert.That(type101).IsTrue();
        await Assert.That(actual).IsEquivalentTo(expected);
        await Assert.That((int)actual[4]).IsEqualTo(expectedLenByte);
    }

    [Test]
    [Arguments(6, 6, 0)]
    [Arguments(11, 8, 11)]
    public async Task ClassicDataFrameInType101KeepsDlcAbove8InByte7LikeType1(int dlc, int expectedLen, int expectedLen8Dlc)
    {
        bool type1 = CanParser.TryParseCanMessage(_Type1(0, (byte)dlc, 0x123), out byte[] expected, out _);
        bool type101 = CanParser.TryParseCanFdMessage64(_Type101(0, (byte)dlc, 0x123, expectedLen), out byte[] actual, out _);

        await Assert.That(type1).IsTrue();
        await Assert.That(type101).IsTrue();
        await Assert.That(actual).IsEquivalentTo(expected);
        await Assert.That((int)actual[7]).IsEqualTo(expectedLen8Dlc);
    }

    [Test]
    public async Task FdFrameStillStoresTheByteCountAndLeavesByte7Zero()
    {
        byte[] payload = new byte[20 + 64];
        payload[3] = 15;
        payload[13] = 0x01;
        payload[14] = 64;

        bool ok = CanParser.TryParseCanFdMessage(payload, out byte[] frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That((int)frame[4]).IsEqualTo(64);
        await Assert.That((int)frame[7]).IsEqualTo(0);
        await Assert.That(frame.Length).IsEqualTo(8 + 64);
    }

    [Test]
    public async Task ClassicRemoteFrameInType100ReachesTheReaderWithItsDlc()
    {
        byte[] file = new BlfCaptureBuilder()
            .AddRawObject(BlfConstants.ObjTypeCanFdMessage, 1_000_000, _Type100(0x80, 6, 0x123, 0, 0))
            .Build();
        using BlfSource source = BlfSource.FromData(file, "rtr.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });
        _ = SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That((int)frame!.Value.Data.Span[4]).IsEqualTo(6);
    }

    #endregion

    #region AppText out parameters

    [Test]
    public async Task AppTextRejectedForItsTextLengthLeavesTheOutParametersAtDefaults()
    {
        // source 1, channel 3, bus type 5, textLength 99 but only 4 text bytes follow.
        byte[] payload = new byte[16 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, BlfConstants.AppTextSourceChannel);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (3u << 8) | (5u << 16));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 99);

        bool ok = AppTextParser.TryParseChannelName(payload, out byte channel, out byte busType, out string? name);

        await Assert.That(ok).IsFalse();
        await Assert.That((int)channel).IsEqualTo(0);
        await Assert.That((int)busType).IsEqualTo(0);
        await Assert.That(name).IsNull();
    }

    [Test]
    public async Task AppTextWithoutAnInterfaceNameLeavesTheOutParametersAtDefaults()
    {
        byte[] text = Encoding.UTF8.GetBytes("OnlyOneToken");
        byte[] payload = new byte[16 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, BlfConstants.AppTextSourceChannel);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (3u << 8) | (5u << 16));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)text.Length);
        text.CopyTo(payload.AsSpan(16));

        bool ok = AppTextParser.TryParseChannelName(payload, out byte channel, out byte busType, out string? name);

        await Assert.That(ok).IsFalse();
        await Assert.That((int)channel).IsEqualTo(0);
        await Assert.That((int)busType).IsEqualTo(0);
        await Assert.That(name).IsNull();
    }

    [Test]
    public async Task AppTextWithANameSetsChannelAndBusType()
    {
        byte[] text = Encoding.UTF8.GetBytes("Path;Powertrain;extra");
        byte[] payload = new byte[16 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, BlfConstants.AppTextSourceChannel);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (3u << 8) | (5u << 16));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)text.Length);
        text.CopyTo(payload.AsSpan(16));

        bool ok = AppTextParser.TryParseChannelName(payload, out byte channel, out byte busType, out string? name);

        await Assert.That(ok).IsTrue();
        await Assert.That((int)channel).IsEqualTo(3);
        await Assert.That((int)busType).IsEqualTo(5);
        await Assert.That(name).IsEqualTo("Powertrain");
    }

    #endregion
}

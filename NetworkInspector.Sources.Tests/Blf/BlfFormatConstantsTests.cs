// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>Exit-point coverage for BLF format constants and timestamp helpers.</summary>
internal sealed class BlfFormatConstantsTests
{
    [Test]
    public async Task FileMagicBytes_MatchesExpectedValue()
    {
        byte[] magic = BlfConstants.FileMagicBytes.ToArray();

        await Assert.That(magic.Length).IsEqualTo(4);
        await Assert.That(magic[0]).IsEqualTo((byte)'L');
        await Assert.That(magic[1]).IsEqualTo((byte)'O');
        await Assert.That(magic[2]).IsEqualTo((byte)'G');
        await Assert.That(magic[3]).IsEqualTo((byte)'G');
    }

    [Test]
    public async Task ObjectMagicBytes_MatchesExpectedValue()
    {
        byte[] magic = BlfConstants.ObjectMagicBytes.ToArray();

        await Assert.That(magic.Length).IsEqualTo(4);
        await Assert.That(magic[0]).IsEqualTo((byte)'L');
        await Assert.That(magic[1]).IsEqualTo((byte)'O');
        await Assert.That(magic[2]).IsEqualTo((byte)'B');
        await Assert.That(magic[3]).IsEqualTo((byte)'J');
    }

    [Test]
    public async Task CanFdPayloadLengthToDlc_TableIsPopulated()
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
    [Arguments(BlfConstants.ObjTypeCanXlChannelFrame, true)]
    [Arguments(BlfConstants.ObjTypeCanXlChannelErrorFrame, false)]
    [Arguments(0xFFFFFFFFu, false)]
    public async Task IsFrameProducingType_ClassifiesObjectTypes(uint objectType, bool expected)
    {
        await Assert.That(BlfConstants.IsFrameProducingType(objectType)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(100UL, BlfConstants.TimestampResolution10Us, 1_000_000L)]
    [Arguments(42UL, BlfConstants.TimestampResolution1Ns, 42L)]
    [Arguments(100UL, 0x11u, 0L)]
    [Arguments(99UL, 0xFFu, 0L)]
    [Arguments(5UL, 0u, 0L)]
    public async Task ToNanoseconds_ConvertsByResolution(ulong raw, uint flags, long expected)
    {
        await Assert.That(BlfTimestamp.ToNanoseconds(raw, flags)).IsEqualTo(expected);
    }

    [Test]
    public async Task ToNanoseconds_10UsOverflow_SaturatesToLongMax()
    {
        long nanos = BlfTimestamp.ToNanoseconds(ulong.MaxValue, BlfConstants.TimestampResolution10Us);

        await Assert.That(nanos).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task ToNanoseconds_1NsAboveLongMax_SaturatesToLongMax()
    {
        long nanos = BlfTimestamp.ToNanoseconds((ulong)long.MaxValue + 1UL, BlfConstants.TimestampResolution1Ns);

        await Assert.That(nanos).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task DateToUnixNanoseconds_ValidUtcDate_ReturnsExpectedNanoseconds()
    {
        BlfDate date = new()
        {
            Year = 2024,
            Month = 1,
            Day = 2,
            Hour = 3,
            Minute = 4,
            Second = 5,
            Millisecond = 6,
        };

        long nanos = BlfTimestamp.DateToUnixNanoseconds(date, TimeZoneInfo.Utc);

        await Assert.That(nanos).IsGreaterThan(0L);
    }

    [Test]
    public async Task DateToUnixNanoseconds_InvalidDate_ReturnsZero()
    {
        BlfDate date = default;

        long nanos = BlfTimestamp.DateToUnixNanoseconds(date, TimeZoneInfo.Utc);

        await Assert.That(nanos).IsEqualTo(0L);
    }

    [Test]
    public async Task DateToUnixNanoseconds_InvalidCalendarDate_ReturnsZero()
    {
        BlfDate date = new()
        {
            Year = 2024,
            Month = 2,
            Day = 31,
            Hour = 0,
            Minute = 0,
            Second = 0,
            Millisecond = 0,
        };

        long nanos = BlfTimestamp.DateToUnixNanoseconds(date, TimeZoneInfo.Utc);

        await Assert.That(nanos).IsEqualTo(0L);
    }

    [Test]
    [Arguments(32, 0)]
    [Arguments(33, 3)]
    [Arguments(34, 2)]
    [Arguments(35, 1)]
    public async Task AlignmentPaddingByteCount_UnpaddedSizes(int unpaddedSize, int expectedPad)
    {
        await Assert.That(BlfConstants.AlignmentPaddingByteCount(unpaddedSize)).IsEqualTo(expectedPad);
    }

    [Test]
    public async Task MaxBlockReadSize_Is256Mib()
    {
        int maxBlockReadSize = BlfConstants.MaxBlockReadSize;

        await Assert.That(maxBlockReadSize).IsEqualTo(256 * 1024 * 1024);
    }
}

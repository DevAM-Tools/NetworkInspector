// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>Range behavior of <see cref="BlfTimestamp.DateToUnixNanoseconds"/> for far-future header dates.</summary>
internal sealed class BlfDateTimestampTests
{
    private static BlfDate _Date(int year, int month, int day, int hour, int minute, int second, int millisecond) => new()
    {
        Year = (ushort)year,
        Month = (ushort)month,
        Day = (ushort)day,
        Hour = (ushort)hour,
        Minute = (ushort)minute,
        Second = (ushort)second,
        Millisecond = (ushort)millisecond,
    };

    [Test]
    [Arguments(2262, 4, 12)]
    [Arguments(2263, 1, 1)]
    [Arguments(5000, 6, 15)]
    [Arguments(9999, 12, 31)]
    public async Task DateBeyondNanosecondRangeSaturatesInsteadOfWrapping(int year, int month, int day)
    {
        BlfDate date = _Date(year, month, day, 23, 59, 59, 999);

        long nanos = BlfTimestamp.DateToUnixNanoseconds(date, TimeZoneInfo.Utc);

        await Assert.That(nanos).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task DateNearDateTimeMaxValueInZoneWestOfUtcDoesNotThrow()
    {
        // 9999-12-31 23:59:59.999 at UTC-5 is 10000-01-01 04:59:59.999 UTC, outside DateTime's range.
        TimeZoneInfo west = TimeZoneInfo.CreateCustomTimeZone("west", TimeSpan.FromHours(-5), "west", "west");
        BlfDate date = _Date(9999, 12, 31, 23, 59, 59, 999);

        long nanos = BlfTimestamp.DateToUnixNanoseconds(date, west);

        await Assert.That(nanos).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task DateNearDateTimeMaxValueInZoneEastOfUtcDoesNotThrow()
    {
        TimeZoneInfo east = TimeZoneInfo.CreateCustomTimeZone("east", TimeSpan.FromHours(5), "east", "east");
        BlfDate date = _Date(9999, 12, 31, 23, 59, 59, 999);

        long nanos = BlfTimestamp.DateToUnixNanoseconds(date, east);

        await Assert.That(nanos).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task DateInZoneKeepsTheOffsetForOrdinaryDates()
    {
        TimeZoneInfo east = TimeZoneInfo.CreateCustomTimeZone("east", TimeSpan.FromHours(2), "east", "east");
        BlfDate date = _Date(2024, 1, 1, 2, 0, 0, 0);

        long nanos = BlfTimestamp.DateToUnixNanoseconds(date, east);

        // 2024-01-01 02:00 at UTC+2 is 2024-01-01 00:00 UTC.
        await Assert.That(nanos).IsEqualTo(1_704_067_200_000_000_000L);
    }

    [Test]
    public async Task DateAtYear2261StillConvertsExactly()
    {
        BlfDate date = _Date(2261, 1, 1, 0, 0, 0, 1);

        long nanos = BlfTimestamp.DateToUnixNanoseconds(date, TimeZoneInfo.Utc);

        long expectedMilliseconds = new DateTimeOffset(2261, 1, 1, 0, 0, 0, 1, TimeSpan.Zero).ToUnixTimeMilliseconds();
        await Assert.That(nanos).IsEqualTo(expectedMilliseconds * 1_000_000L);
    }
}

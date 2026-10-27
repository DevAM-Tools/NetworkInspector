// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Formatting of field values and settings is allocation-lean and re-entrant: no per-thread scratch buffer, an exact-size
/// string for large byte values, and nothing special when a lazy text factory formats other values while it runs.
/// </summary>
internal sealed class FieldValueFormattingTests
{
    #region Helpers

    private static byte[] _Bytes(int count)
    {
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bytes[i] = (byte)i;
        }

        return bytes;
    }

    /// <summary>Formats a value that mixes every scalar type with a hot-path-sized stack buffer; returns the result length.</summary>
    private static int _FormatMixed(int index)
    {
        FieldValueData value = (index % 9) switch
        {
            0 => FieldValueData.NewIPv6(new IPv6Address(0x20010DB800010002UL, 0x0003000400050006UL)),
            1 => FieldValueData.NewMacAddress(new MacAddress(0x001122334455UL)),
            2 => FieldValueData.NewI64(long.MinValue + index),
            3 => FieldValueData.NewU64(ulong.MaxValue - (ulong)index),
            4 => FieldValueData.NewF64(index + 0.125),
            5 => FieldValueData.NewBool((index & 1) == 0),
            6 => FieldValueData.NewTimestamp(Timestamp.FromNanos(1_700_000_000_123_456_789L + index)),
            7 => FieldValueData.NewUuid(new Uuid(0x550E8400E29B41D4UL, 0xA716446655440000UL)),
            _ => FieldValueData.NewBytes(_Bytes(16)),
        };
        return value.ToString().Length;
    }

    #endregion

    [Test]
    public async Task LargeBytesValueFormatsToOneExactSizeString()
    {
        FieldValueData value = FieldValueData.NewBytes(_Bytes(10_000));

        string text = value.ToString();

        // 3 characters per byte minus the missing trailing separator.
        await Assert.That(text.Length).IsEqualTo((3 * 10_000) - 1);
        await Assert.That(text.StartsWith("00 01 02 03", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.EndsWith(" 0F", StringComparison.Ordinal)).IsTrue(); // 9999 % 256 = 15
    }

    [Test]
    public async Task HundredBytesFormatEqualsTryFormatText()
    {
        FieldValueData value = FieldValueData.NewBytes(_Bytes(100));
        char[] large = new char[400];

        bool ok = value.TryFormat(large, out int written, default, CultureInfo.InvariantCulture);
        string text = value.ToString();

        await Assert.That(ok).IsTrue();
        await Assert.That(text.Length).IsEqualTo(299);
        await Assert.That(text).IsEqualTo(new string(large, 0, written));
    }

    [Test]
    public async Task LazyFactoryThatFormatsOtherValuesAllocatesOnlyResults()
    {
        IPv6Address address = new(0x20010DB800010002UL, 0x0003000400050006UL);
        SettingValue setting = SettingValue.String("setting-label");
        LazyString warmUp = LazyString.Lazy(() => FieldValue.NewIPv6(address).ToString() + "|" + setting.Format());
        LazyString measured = LazyString.Lazy(() => FieldValue.NewIPv6(address).ToString() + "|" + setting.Format());
        _ = warmUp.AsString; // JIT and static initialisation outside the measured window

        long before = GC.GetAllocatedBytesForCurrentThread();
        string text = measured.AsString;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(text).IsEqualTo("2001:DB8:1:2:3:4:5:6|setting-label");
        // Only the IPv6 text, the concatenation, and the cache publication; the former scratch buffer cost 4 MiB per nesting level.
        await Assert.That(allocated).IsLessThan(1024L);
    }

    [Test]
    public async Task TenThousandMixedFormatsAllocateNoLargeBuffer()
    {
        // Warm-up outside the measured window.
        for (int i = 0; i < 100; i++)
        {
            _ = _FormatMixed(i);
        }

        long maxSingleCall = 0;
        long total = 0;
        for (int i = 0; i < 10_000; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            _ = _FormatMixed(i);
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            total += delta;
            maxSingleCall = Math.Max(maxSingleCall, delta);
        }

        // The result string of the longest scalar (39 chars) is about 100 bytes and the 16-byte Bytes value adds its array;
        // a million-char scratch array would show up as one call far above this bound and as a total above 4 MB.
        await Assert.That(maxSingleCall).IsLessThan(1024L);
        await Assert.That(total).IsLessThan(10_000L * 512);
    }
}

// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="ValueCacheStorageModeExtensions"/> (F-VC-02).
/// </summary>
internal sealed class ValueCacheStorageModeExtensionsTests
{
    [Test]
    public async Task TryGetRange_NativeReturnsFalse()
    {
        bool ok = ValueCacheStorageMode.Native.TryGetRange(out long min, out long max);
        await Assert.That(ok).IsFalse();
        await Assert.That(min).IsEqualTo(0L);
        await Assert.That(max).IsEqualTo(0L);
    }

    [Test]
    public async Task TryGetRange_CompactFloatReturnsFalse()
    {
        bool ok = ValueCacheStorageMode.CompactFloat.TryGetRange(out _, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryGetRange_SignedRanges()
    {
        ValueCacheStorageMode.CompactInt8.TryGetRange(out long min8, out long max8);
        await Assert.That(min8).IsEqualTo((long)sbyte.MinValue);
        await Assert.That(max8).IsEqualTo((long)sbyte.MaxValue);

        ValueCacheStorageMode.CompactInt16.TryGetRange(out long min16, out long max16);
        await Assert.That(min16).IsEqualTo((long)short.MinValue);
        await Assert.That(max16).IsEqualTo((long)short.MaxValue);

        ValueCacheStorageMode.CompactInt32.TryGetRange(out long min32, out long max32);
        await Assert.That(min32).IsEqualTo((long)int.MinValue);
        await Assert.That(max32).IsEqualTo((long)int.MaxValue);
    }

    [Test]
    public async Task TryGetRange_UnsignedRanges()
    {
        ValueCacheStorageMode.CompactUInt8.TryGetRange(out long min8, out long max8);
        await Assert.That(min8).IsEqualTo(0L);
        await Assert.That(max8).IsEqualTo(255L);

        ValueCacheStorageMode.CompactUInt16.TryGetRange(out long min16, out long max16);
        await Assert.That(min16).IsEqualTo(0L);
        await Assert.That(max16).IsEqualTo(65535L);

        ValueCacheStorageMode.CompactUInt32.TryGetRange(out long min32, out long max32);
        await Assert.That(min32).IsEqualTo(0L);
        await Assert.That(max32).IsEqualTo(4294967295L);
    }

    [Test]
    public async Task WouldClamp_DetectsOutOfRange()
    {
        await Assert.That(ValueCacheStorageMode.CompactInt8.WouldClamp(127)).IsFalse();
        await Assert.That(ValueCacheStorageMode.CompactInt8.WouldClamp(128)).IsTrue();
        await Assert.That(ValueCacheStorageMode.CompactInt8.WouldClamp(-128)).IsFalse();
        await Assert.That(ValueCacheStorageMode.CompactInt8.WouldClamp(-129)).IsTrue();
        await Assert.That(ValueCacheStorageMode.CompactUInt8.WouldClamp(-1)).IsTrue();
        await Assert.That(ValueCacheStorageMode.CompactUInt8.WouldClamp(255)).IsFalse();
        await Assert.That(ValueCacheStorageMode.CompactUInt8.WouldClamp(256)).IsTrue();
    }

    [Test]
    public async Task WouldClamp_NativeAndFloatNeverClamp()
    {
        await Assert.That(ValueCacheStorageMode.Native.WouldClamp(long.MaxValue)).IsFalse();
        await Assert.That(ValueCacheStorageMode.Native.WouldClamp(long.MinValue)).IsFalse();
        await Assert.That(ValueCacheStorageMode.CompactFloat.WouldClamp(long.MaxValue)).IsFalse();
    }
}
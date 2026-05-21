// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Helper utilities for <see cref="ValueCacheStorageMode"/>: numeric range queries and
/// compatibility/clamp checks. Kept as a separate static class so the enum file remains
/// purely declarative.
/// </summary>
public static class ValueCacheStorageModeExtensions
{
    #region Public API

    /// <summary>
    /// Returns the inclusive numeric range a compact storage mode can represent
    /// without clamping. Returns <c>true</c> for <c>CompactInt8/16/32</c> and
    /// <c>CompactUInt8/16/32</c>; returns <c>false</c> for <see cref="ValueCacheStorageMode.Native"/>
    /// (lossless, no fixed range) and <see cref="ValueCacheStorageMode.CompactFloat"/>
    /// (continuous range with single-precision rounding).
    /// </summary>
    /// <param name="mode">The storage mode to query.</param>
    /// <param name="min">When the call returns <c>true</c>, the smallest value the mode can store.</param>
    /// <param name="max">When the call returns <c>true</c>, the largest value the mode can store.</param>
    /// <returns><c>true</c> for fixed-width compact integer modes; otherwise <c>false</c>.</returns>
    public static bool TryGetRange(this ValueCacheStorageMode mode, out long min, out long max)
    {
        switch (mode)
        {
            case ValueCacheStorageMode.CompactInt8:
                min = sbyte.MinValue;
                max = sbyte.MaxValue;
                return true;
            case ValueCacheStorageMode.CompactInt16:
                min = short.MinValue;
                max = short.MaxValue;
                return true;
            case ValueCacheStorageMode.CompactInt32:
                min = int.MinValue;
                max = int.MaxValue;
                return true;
            case ValueCacheStorageMode.CompactUInt8:
                min = 0;
                max = byte.MaxValue;
                return true;
            case ValueCacheStorageMode.CompactUInt16:
                min = 0;
                max = ushort.MaxValue;
                return true;
            case ValueCacheStorageMode.CompactUInt32:
                min = 0;
                max = uint.MaxValue;
                return true;
            default:
                min = 0;
                max = 0;
                return false;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when storing <paramref name="value"/> in <paramref name="mode"/>
    /// would require clamping. For modes without a fixed integer range
    /// (<see cref="ValueCacheStorageMode.Native"/>, <see cref="ValueCacheStorageMode.CompactFloat"/>)
    /// always returns <c>false</c>.
    /// </summary>
    public static bool WouldClamp(this ValueCacheStorageMode mode, long value)
    {
        if (!TryGetRange(mode, out long min, out long max))
        {
            return false;
        }
        return value < min || value > max;
    }

    #endregion
}

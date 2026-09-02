// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Optional bounds for a <see cref="ValueCache"/>. Both properties <see langword="null"/> means unlimited.
/// When a write would exceed either bound, that packet's staged rows are not published and
/// <see cref="ValueCache.IsCapacityReached"/> becomes sticky true.
/// </summary>
/// <param name="MaxRowCount">Maximum published rows per series; must be greater than zero when set.</param>
/// <param name="MaxBytes">Maximum charged bytes across all series; must be greater than zero when set.</param>
public readonly record struct ValueCacheLimits(int? MaxRowCount, long? MaxBytes)
{
    /// <summary>No row or byte bound.</summary>
    public static ValueCacheLimits Unlimited { get; } = new(null, null);
}

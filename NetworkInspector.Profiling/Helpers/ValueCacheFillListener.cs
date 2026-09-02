// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>
/// Signals <see cref="Filled"/> when the <c>udp.srcport</c> series has at least
/// <see cref="_TargetCount"/> published rows. Used as a packet-count proxy for both
/// single-field and RecordAllFields caches on IPv6/UDP fixtures.
/// </summary>
internal sealed class ValueCacheFillListener : IValueCacheListener
{
    #region Fields

    private readonly int _TargetCount;

    #endregion

    #region Lifecycle

    /// <summary>Creates a listener that waits for <paramref name="targetCount"/> udp.srcport rows.</summary>
    internal ValueCacheFillListener(string uiName, int targetCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uiName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetCount);
        UiName = uiName;
        _TargetCount = targetCount;
    }

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string UiName { get; }

    /// <summary>Set when <c>udp.srcport</c> has reached the target row count.</summary>
    internal ManualResetEventSlim Filled { get; } = new(false);

    /// <inheritdoc/>
    public void OnNewRows(ISessionReader session, ValueCacheReaderView cache, int fromIndex, int toIndexExclusive)
    {
        _ = session;
        _ = fromIndex;
        _ = toIndexExclusive;
        if (!cache.TryGetSeries<ulong>("udp.srcport", out ValueCacheSeries<ulong>? series) || series is null)
        {
            return;
        }

        if (series.Count >= _TargetCount)
        {
            Filled.Set();
        }
    }

    #endregion
}

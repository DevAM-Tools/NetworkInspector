// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Configuration options for <see cref="PcapSource"/>.
/// </summary>
public sealed class PcapSourceOptions : IFileSourceOptions
{
    #region Properties

    /// <summary>
    /// Number of memory-mapped view handles in the pool.
    /// Default: CPU core count, clamped [1, 256].
    /// </summary>
    public int MaxHandles
    {
        get => _MaxHandles;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 256);
            _MaxHandles = value;
        }
    }
    private readonly int _MaxHandles = Math.Clamp(Environment.ProcessorCount, 1, 256);

    /// <summary>Optional override for the UI display name.</summary>
    public string? UiName
    {
        get; init;
    }

    /// <summary>
    /// Scan mode: Full scans the entire file upfront, Lazy scans on demand.
    /// Default: Lazy.
    /// </summary>
    public ScanMode ScanMode { get; init; } = ScanMode.Lazy;

    /// <summary>
    /// Maximum file size (bytes) for in-memory preloading.
    /// Files larger than this use memory-mapped I/O.
    /// Null = always use memory-mapped I/O.
    /// Default: 256 MiB on 64-bit processes, 64 MiB on 32-bit processes.
    /// </summary>
    public long? PreloadBudget
    {
        get => _PreloadBudget;
        init
        {
            if (value.HasValue)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value.Value);
            }
            _PreloadBudget = value;
        }
    }
    private readonly long? _PreloadBudget =
        Environment.Is64BitProcess ? 256L * 1024 * 1024 : 64L * 1024 * 1024;

    /// <summary>
    /// Error tolerance mode for frame reading.
    /// Default: <see cref="ErrorToleranceMode.Tolerant"/> (skip errors and continue).
    /// </summary>
    public ErrorToleranceMode ErrorTolerance { get; init; } = ErrorToleranceMode.Tolerant;

    #endregion
}

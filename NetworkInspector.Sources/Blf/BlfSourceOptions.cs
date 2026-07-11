// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf;

/// <summary>
/// Configuration options for <see cref="BlfSource"/>.
/// </summary>
public sealed class BlfSourceOptions : IFileSourceOptions
{
    #region Properties

    /// <summary>
    /// Scan mode: Full scans the entire file upfront, Lazy defers scanning.
    /// Default is <see cref="ScanMode.Lazy"/>.
    /// </summary>
    public ScanMode ScanMode { get; init; } = ScanMode.Lazy;

    private int _CacheBudget = Format.BlfConstants.DefaultCacheBudget;

    /// <summary>
    /// Maximum byte budget for the 2Q container cache.
    /// Must be non-negative. A value of zero disables caching.
    /// Default is 32 MiB.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Value is negative.</exception>
    public int CacheBudget
    {
        get => _CacheBudget;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _CacheBudget = value;
        }
    }

    private long? _PreloadBudget =
        Environment.Is64BitProcess ? 256L * 1024 * 1024 : 64L * 1024 * 1024;

    /// <summary>
    /// Maximum file size in bytes to fully load into memory.
    /// Files smaller than or equal to this value are read into a byte array at open time
    /// (zero-copy random access). Files larger than this value are memory-mapped, which
    /// avoids pinning large LOH allocations at the cost of additional I/O syscalls.
    /// Must be non-negative.
    /// Default is 256 MiB on 64-bit processes and 64 MiB on 32-bit processes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Value is negative.</exception>
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

    /// <summary>
    /// UI display name for this source.
    /// If null, the file name is used.
    /// </summary>
    public string? UiName
    {
        get; init;
    }

    /// <summary>
    /// Error tolerance mode for frame reading.
    /// Default: <see cref="ErrorToleranceMode.Tolerant"/> (skip errors and continue).
    /// </summary>
    public ErrorToleranceMode ErrorTolerance { get; init; } = ErrorToleranceMode.Tolerant;

    /// <summary>
    /// Timezone in which the BLF file's <c>start_date</c> SYSTEMTIME fields are interpreted.
    /// <para>
    /// The BLF specification does not record a timezone with the SYSTEMTIME structure.
    /// Vector's reference tools and Wireshark write/read it as local civil time, so the
    /// same file produces different absolute timestamps depending on how these fields are
    /// interpreted versus the Unix epoch used by <see cref="BlfSource"/>.
    /// </para>
    /// <para>
    /// Default: <see cref="TimeZoneInfo.Local"/> — matches Wireshark/tshark and mainstream
    /// Vector tooling end-to-end. Use <see cref="TimeZoneInfo.Utc"/> only if your capture
    /// pipeline explicitly treats SYSTEMTIME components as UTC (non-standard versus those tools).
    /// </para>
    /// </summary>
    public TimeZoneInfo TimestampTimeZone { get; init; } = TimeZoneInfo.Local;

    private int _MmapSlotCount = Environment.ProcessorCount;

    /// <summary>
    /// Number of memory-mapped view accessor slots used for concurrent random-access reads
    /// when the file exceeds <see cref="PreloadBudget"/>.
    /// Must be positive.
    /// Default: <see cref="Environment.ProcessorCount"/>. Ignored when the file is loaded in-memory.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Value is less than 1.</exception>
    public int MmapSlotCount
    {
        get => _MmapSlotCount;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _MmapSlotCount = value;
        }
    }

    private long _MaxUncompressedContainerSize;

    /// <summary>
    /// Maximum allowed uncompressed size in bytes for a single BLF log container.
    /// When a container's <c>uncompressedSize</c> header field exceeds this value,
    /// <see cref="Format.BlfDecompressionLimitExceededException"/> is thrown before any
    /// allocation is attempted.
    /// <para>
    /// A value of <c>0</c> disables the check (default). When the limit is active it
    /// must be positive.
    /// </para>
    /// <para>
    /// This guard complements the operating-system OOM protection: it lets callers
    /// impose a budget without waiting for an allocation to actually fail, and it
    /// provides a meaningful diagnostic (configured limit + requested size) instead
    /// of an opaque <see cref="OutOfMemoryException"/>.
    /// </para>
    /// <para>
    /// <b>Security recommendation:</b> When processing BLF files from untrusted sources,
    /// set this to a realistic upper bound for your capture equipment (e.g. 128 MiB).
    /// Without a limit, a malicious BLF file can declare
    /// <c>uncompressedSize = 4 294 967 295</c> (uint max), causing a ~4 GiB single
    /// allocation attempt. All three call sites catch <see cref="OutOfMemoryException"/>
    /// with clean rollback, so the process remains stable — but the allocation attempt
    /// itself may trigger GC pressure. A configured limit rejects the container before
    /// any allocation occurs.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Value is negative.</exception>
    public long MaxUncompressedContainerSize
    {
        get => _MaxUncompressedContainerSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _MaxUncompressedContainerSize = value;
        }
    }

    private int _MaxDecompressionConcurrency = Environment.ProcessorCount;

    /// <summary>
    /// Maximum number of container decompressions that may run simultaneously across all threads.
    /// This controls the transient peak memory usage: at most
    /// <c>MaxDecompressionConcurrency × MaxUncompressedContainerSize</c> bytes may be allocated
    /// simultaneously for decompression output buffers, independent of the number of calling threads.
    /// <para>
    /// When multiple threads request the same container concurrently, only one thread performs
    /// the actual decompression (the "winner"); all other threads wait on a
    /// <see cref="System.Threading.ManualResetEventSlim"/> and share the winner's result — they
    /// do not count against this limit.
    /// </para>
    /// <para>
    /// If <see cref="MaxUncompressedContainerSize"/> is zero (limit inactive), this property
    /// still throttles concurrency, but the per-decompression allocation size is unbounded.
    /// </para>
    /// Must be at least 1.
    /// Default: <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Value is less than 1.</exception>
    public int MaxDecompressionConcurrency
    {
        get => _MaxDecompressionConcurrency;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _MaxDecompressionConcurrency = value;
        }
    }

    #endregion
}

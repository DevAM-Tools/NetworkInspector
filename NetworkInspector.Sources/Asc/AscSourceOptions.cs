// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc;

/// <summary>
/// Configuration options for <see cref="AscStreamSource"/> and <see cref="AscSource"/>.
/// <see cref="AscStreamSource"/> reads only <see cref="ErrorTolerance"/>, <see cref="TimestampTimeZone"/> and
/// <see cref="FrameSlabSize"/>; the other options (preload budget, UI name) apply to <see cref="AscSource"/> alone.
/// Immutable after construction (init-only properties), so one instance can be shared by several sources.
/// </summary>
public sealed class AscSourceOptions : IFileSourceOptions
{
    #region Constants

    /// <summary>
    /// Default threshold for in-memory preloading: 256 MiB on 64-bit processes,
    /// 64 MiB on 32-bit processes (large preload risks address-space exhaustion on 32-bit).
    /// Not a security limit but a memory trade-off the caller owns (see <see cref="PreloadBudget"/>): a preloaded file
    /// costs its size once in one array, a larger file costs only its index and re-reads lines from disk.
    /// </summary>
    public static long DefaultPreloadBudget
    {
        get;
    } =
        Environment.Is64BitProcess ? 256L * 1024 * 1024 : 64L * 1024 * 1024;

    /// <summary>
    /// Buffer size used by the disk-based backend when reading ASC lines
    /// for files that exceed <see cref="PreloadBudget"/>: 4 MiB. Also the read buffer of <see cref="AscStreamSource"/>.
    /// One buffer per source, allocated once (large object heap); it only sets how many bytes each read call fetches, not
    /// how much of the file is held.
    /// </summary>
    public const int DiskReadBufferSize = 4 * 1024 * 1024;

    /// <summary>
    /// Maximum length of a single ASC line in bytes (including data payload).
    /// Value: 65 536 bytes — larger than the biggest possible CAN-FD or Ethernet line.
    /// <para>
    /// It sizes the carry-over buffer that joins a line split across two read chunks (disk scan of <see cref="AscSource"/>,
    /// and <see cref="AscStreamSource"/>). A line that straddles a chunk boundary and exceeds it is skipped, without an
    /// error, so a line without a line feed cannot make that buffer grow. A line that lies wholly inside one 4 MiB chunk
    /// is not carried and is not subject to this limit, and the in-memory backend does not apply it.
    /// </para>
    /// <para>
    /// Threat: memory exhaustion by an endless line. Constant, not configurable; fixed and therefore secure by default.
    /// Residual risk: the limit is not enforced for a line inside one chunk, so the bound that holds is the chunk size.
    /// </para>
    /// </summary>
    public const int MaxLineLength = 65_536;

    #endregion

    #region Properties

    /// <summary>
    /// ASC files are always fully scanned at open time to build the frame index.
    /// This property is part of <see cref="IFileSourceOptions"/> for interface uniformity
    /// and always returns <see cref="ScanMode.Full"/>.
    /// </summary>
    public ScanMode ScanMode { get; } = ScanMode.Full;

    private long? _PreloadBudget = DefaultPreloadBudget;

    /// <summary>
    /// Maximum file size in bytes that is fully loaded into memory as a single byte array
    /// for zero-seek random access. Files larger than this value
    /// use a disk-based backend: only line byte offsets are stored in the index,
    /// and each line is re-read from disk when a frame is requested.
    /// Must be non-negative. Default matches <see cref="DefaultPreloadBudget"/>. <see langword="null"/> means "never
    /// preload". A file larger than one .NET array can hold (<see cref="Array.MaxLength"/>) always uses the disk backend,
    /// whatever the budget. Configurable by the code that opens the source; open by default (the default is the
    /// host-bitness value above, a memory trade-off, not a security boundary).
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
    /// Timezone in which the ASC <c>date</c> header field is interpreted.
    /// <para>
    /// Vector ASC writes a free-form local-time date string with no timezone marker, so
    /// the same file produces different absolute timestamps depending on the host
    /// timezone unless the caller pins the interpretation explicitly.
    /// </para>
    /// <para>
    /// Default: <see cref="TimeZoneInfo.Utc"/> — produces deterministic, machine-independent
    /// timestamps. Set to <see cref="TimeZoneInfo.Local"/> for Vector-compatible behaviour,
    /// or to a fixed <see cref="TimeZoneInfo"/> if the capture's source timezone is known.
    /// </para>
    /// </summary>
    public TimeZoneInfo TimestampTimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>
    /// Slab size in bytes for the frame buffers of sequential reads (<c>NextFrame</c> of <see cref="AscSource"/> and
    /// <see cref="AscStreamSource"/>). 0 (default) allocates one array per frame. Values from 1 024 to 16 777 216 group
    /// consecutive frames into shared slabs: fewer GC objects, but a consumer that keeps one frame alive keeps its whole
    /// slab alive. Frames above a quarter of the slab get their own array. CAN XL and FlexRay frames, and
    /// <see cref="AscSource.FrameById"/>, always use one array per frame.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is neither 0 nor in 1 024 … 16 777 216.</exception>
    public int FrameSlabSize
    {
        get => _FrameSlabSize;
        init => _FrameSlabSize = FrameSlabAllocator.ValidateOption(value);
    }
    private readonly int _FrameSlabSize;

    #endregion
}

// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Core;

namespace NetworkInspector.Sources.Asc;

/// <summary>
/// Configuration options for <see cref="AscStreamSource"/> and <see cref="AscSource"/>.
/// </summary>
public sealed class AscSourceOptions : IFileSourceOptions
{
    #region Constants

    /// <summary>
    /// Default threshold for in-memory preloading: 256 MiB on 64-bit processes,
    /// 64 MiB on 32-bit processes (large preload risks address-space exhaustion on 32-bit).
    /// </summary>
    public static long DefaultPreloadBudget
    {
        get;
    } =
        Environment.Is64BitProcess ? 256L * 1024 * 1024 : 64L * 1024 * 1024;

    /// <summary>
    /// Buffer size used by the disk-based backend when reading ASC lines
    /// for files that exceed <see cref="PreloadBudget"/>: 4 MiB.
    /// </summary>
    public const int DiskReadBufferSize = 4 * 1024 * 1024;

    /// <summary>
    /// Maximum length of a single ASC line in bytes (including data payload).
    /// Lines longer than this are skipped during the disk-backend scan and frame re-read.
    /// Value: 65 536 bytes — larger than the biggest possible CAN-FD or Ethernet line.
    /// </summary>
    public const int MaxLineLength = 65_536;

    #endregion

    #region Properties

    /// <summary>
    /// ASC files are always fully scanned at open time to build the frame index.
    /// This property is part of <see cref="IFileSourceOptions"/> for interface uniformity
    /// and always returns <see cref="ScanMode.Full"/>.
    /// </summary>
    ScanMode IFileSourceOptions.ScanMode => ScanMode.Full;

    private long _PreloadBudget = DefaultPreloadBudget;

    /// <summary>
    /// Maximum file size in bytes that is fully loaded into memory as a
    /// <c>string[]</c> for zero-seek random access. Files larger than this value
    /// use a disk-based backend: only line byte offsets are stored in the index,
    /// and each line is re-read from disk when a frame is requested.
    /// Must be non-negative. Default matches <see cref="DefaultPreloadBudget"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Value is negative.</exception>
    public long PreloadBudget
    {
        get => _PreloadBudget;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _PreloadBudget = value;
        }
    }

    /// <inheritdoc/>
    long? IFileSourceOptions.PreloadBudget => PreloadBudget;

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

    #endregion
}

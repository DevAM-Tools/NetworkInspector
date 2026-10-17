// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Configuration options for <see cref="PcapngSource"/> and <see cref="PcapngStreamSource"/>.
/// Stream readers use <see cref="ErrorTolerance"/> and <see cref="FrameSlabSize"/> and ignore the file-only members
/// (<see cref="ScanMode"/>, <see cref="PreloadBudget"/>, <see cref="UiName"/>).
/// </summary>
public sealed class PcapngSourceOptions : IFileSourceOptions
{
    #region Properties

    /// <summary>Optional override for the UI display name.</summary>
    public string? UiName
    {
        get; init;
    }

    /// <summary>
    /// Scan mode: Full scans the entire file upfront, Lazy scans on demand.
    /// Both modes return the same frames and raise <c>FrameSkipped</c> at the same positions.
    /// Default: Lazy.
    /// </summary>
    public ScanMode ScanMode { get; init; } = ScanMode.Lazy;

    /// <summary>
    /// Maximum file size (bytes) for in-memory preloading.
    /// Files larger than this use memory-mapped I/O; so do files above <see cref="Array.MaxLength"/> bytes,
    /// whatever the budget says.
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

    /// <summary>
    /// Slab size in bytes for the frame copies of sequential reads (<c>NextFrame</c>). 0 (default) allocates one
    /// array per frame. Values from 1 024 to 16 777 216 group consecutive frames into shared slabs: fewer GC objects,
    /// but a consumer that keeps one frame alive keeps its whole slab alive. Frames above a quarter of the slab get
    /// their own array.
    /// <para>
    /// Applies where frames are copied: the memory-mapped file backend and <see cref="PcapngStreamSource"/>. Preloaded
    /// files are never copied (frames alias the loaded file). <see cref="PcapngSource.FrameById"/> always allocates one
    /// array per frame.
    /// </para>
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

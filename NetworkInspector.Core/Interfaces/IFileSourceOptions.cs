// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// Common options shared by file-based frame sources.
/// </summary>
public interface IFileSourceOptions
{
    /// <summary>
    /// Scan mode: Full scans the entire file upfront, Lazy scans on demand.
    /// A source that cannot scan lazily reports <see cref="ScanMode.Full"/> (the ASC options always do).
    /// </summary>
    ScanMode ScanMode
    {
        get;
    }

    /// <summary>
    /// Maximum file size in bytes for in-memory preloading.
    /// Files larger than this are not loaded into memory: PCAPNG and BLF memory-map them, ASC reads its lines from disk.
    /// <see langword="null"/> means never preload (always the memory-mapped or disk-backed path).
    /// The shipped implementations reject a negative value with <see cref="ArgumentOutOfRangeException"/> when the option
    /// is set, so a negative budget never reaches a source.
    /// </summary>
    long? PreloadBudget
    {
        get;
    }

    /// <summary>
    /// Error tolerance mode for frame reading.
    /// Default: <see cref="ErrorToleranceMode.Tolerant"/> (skip errors and continue).
    /// </summary>
    ErrorToleranceMode ErrorTolerance
    {
        get;
    }
}

// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// Common options shared by file-based frame sources.
/// </summary>
public interface IFileSourceOptions
{
    #region Properties

    /// <summary>
    /// Scan mode: Full scans the entire file upfront, Lazy scans on demand.
    /// </summary>
    ScanMode ScanMode
    {
        get;
    }

    /// <summary>
    /// Maximum file size in bytes for in-memory preloading.
    /// Files larger than this use memory-mapped I/O.
    /// Null or negative means always use memory-mapped I/O.
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

    #endregion
}
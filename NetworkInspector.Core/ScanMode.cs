// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core;

/// <summary>
/// Controls how a source scans the input file during opening.
/// </summary>
public enum ScanMode
{
    #region Enum Values

    /// <summary>
    /// Scans the entire file upfront before returning any frames.
    /// Slower to open, but <see cref="IFrameSource.EstimatedFrameCount"/> is
    /// available immediately and random access is ready after <c>Start()</c>.
    /// </summary>
    Full,

    /// <summary>
    /// Scans the file incrementally as frames are requested.
    /// Fast open, but <see cref="IFrameSource.EstimatedFrameCount"/> returns
    /// <c>null</c> until the file is fully consumed.
    /// </summary>
    Lazy,

    #endregion
}
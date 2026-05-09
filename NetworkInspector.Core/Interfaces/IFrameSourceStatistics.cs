// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// Provides read-only access to frame source statistics.
/// Thread-safe: all properties may be read from any thread at any time.
/// Values are monotonically increasing during the lifetime of the source.
/// </summary>
public interface IFrameSourceStatistics
{
    #region Properties

    /// <summary>
    /// Number of frames successfully read and returned via <see cref="IFrameSource.NextFrame"/>.
    /// </summary>
    long ReadFrameCount
    {
        get;
    }

    /// <summary>
    /// Number of frames that were skipped due to recoverable errors
    /// (e.g., corrupted block, decompression failure, unresolved interface).
    /// </summary>
    long SkippedFrameCount
    {
        get;
    }

    /// <summary>
    /// Number of non-fatal errors encountered during reading.
    /// Multiple errors may be counted for a single skipped frame.
    /// </summary>
    long ErrorCount
    {
        get;
    }

    /// <summary>
    /// Whether the source has encountered at least one error.
    /// Shortcut for <c>ErrorCount &gt; 0</c>.
    /// </summary>
    bool HasErrors
    {
        get;
    }

    #endregion
}

// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Controls how a frame source or exporter handles recoverable errors.
/// </summary>
public enum ErrorToleranceMode
{
    #region Enum Values

    /// <summary>
    /// Skip frames with recoverable errors and continue reading.
    /// Skipped frames are counted in <see cref="IFrameSourceStatistics.SkippedFrameCount"/>.
    /// This is the default to preserve backward compatibility.
    /// </summary>
    Tolerant,

    /// <summary>
    /// Stop sequential reading on the first recoverable error.
    /// <see cref="IFrameSource.NextFrame()"/> returns <c>null</c>.
    /// Random access to already-read frames (via <see cref="IRandomAccessFrameSource"/>)
    /// remains available.
    /// </summary>
    Strict,

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Describes a recoverable error encountered while reading a frame.
/// Passed to <see cref="IErrorTolerantFrameSource.FrameSkipped"/> subscribers.
/// </summary>
public sealed class FrameReadErrorEventArgs : EventArgs
{
    #region Properties

    /// <summary>
    /// Zero-based index of the frame that could not be read.
    /// Defaults to -1 (unknown). Set explicitly when the frame index is available.
    /// </summary>
    public int FrameIndex { get; init; } = -1;

    /// <summary>
    /// File offset where the error occurred. Defaults to -1 (unknown).
    /// </summary>
    public long FileOffset { get; init; } = -1;

    /// <summary>
    /// Category of the error.
    /// </summary>
    public FrameReadErrorKind Kind
    {
        get; init;
    }

    /// <summary>
    /// Human-readable description of the error.
    /// </summary>
    public required string Message
    {
        get; init;
    }

    #endregion
}

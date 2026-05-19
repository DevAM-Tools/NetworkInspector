// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// A frame source that supports configurable error tolerance behavior.
/// <para>
/// When <see cref="ErrorTolerance"/> is <see cref="ErrorToleranceMode.Tolerant"/>,
/// the source skips frames that cannot be read due to recoverable errors
/// and continues reading subsequent frames. Skipped frames are counted in
/// <see cref="IFrameSourceStatistics.SkippedFrameCount"/>.
/// </para>
/// <para>
/// When <see cref="ErrorTolerance"/> is <see cref="ErrorToleranceMode.Strict"/>,
/// the source stops sequential reading on the first recoverable error.
/// <see cref="IFrameSource.NextFrame()"/> returns <c>null</c> (source exhausted).
/// If the source also implements <see cref="IRandomAccessFrameSource"/>,
/// random access to already-read frames remains available after the abort.
/// </para>
/// </summary>
/// <remarks>
/// This interface extends <see cref="IFrameSourceStatistics"/> because
/// error tolerance is only meaningful when skipped frames are observable.
/// </remarks>
public interface IErrorTolerantFrameSource : IFrameSourceStatistics
{
    #region Properties

    /// <summary>
    /// Gets or sets the error tolerance mode.
    /// May be changed at any time, even while reading.
    /// Default should be <see cref="ErrorToleranceMode.Tolerant"/> to preserve
    /// backward compatibility with existing behavior.
    /// </summary>
    ErrorToleranceMode ErrorTolerance
    {
        get; set;
    }

    #endregion

    #region Events

    /// <summary>
    /// Event raised when a frame is skipped due to a recoverable error.
    /// Only raised when <see cref="ErrorTolerance"/> is
    /// <see cref="ErrorToleranceMode.Tolerant"/>.
    /// <para>
    /// Subscribers must be thread-safe and non-blocking.
    /// The event is raised synchronously on the calling thread.
    /// </para>
    /// </summary>
    event EventHandler<FrameReadErrorEventArgs>? FrameSkipped;

    #endregion
}
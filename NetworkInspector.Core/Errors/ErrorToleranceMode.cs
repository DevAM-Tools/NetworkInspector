// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Controls how a frame source or exporter handles recoverable errors.
/// A recoverable error is one the component can detect and step over (a corrupt block, an unsupported frame); fatal
/// errors such as an I/O failure while writing abort in both modes.
/// </summary>
public enum ErrorToleranceMode
{
    #region Enum Values

    /// <summary>
    /// Skip frames with recoverable errors and continue reading.
    /// Skipped frames are counted in <see cref="IFrameSourceStatistics.SkippedFrameCount"/>.
    /// This is the default to preserve backward compatibility. Exporters skip the offending item, count it in
    /// their statistics, and raise their item-skipped event.
    /// </summary>
    Tolerant,

    /// <summary>
    /// Stop sequential reading on the first recoverable error.
    /// <see cref="IFrameSource.NextFrame(CancellationToken)"/> returns <c>null</c>.
    /// Random access to already-read frames (via <see cref="IRandomAccessFrameSource"/>)
    /// remains available. Exporters abort the export at the first recoverable error (their per-item callback returns
    /// <see langword="false"/>).
    /// </summary>
    Strict,

    #endregion
}

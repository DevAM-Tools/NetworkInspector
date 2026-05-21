// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// An exporter that supports configurable error tolerance.
/// <para>
/// In Tolerant mode: recoverable errors (unsupported types, malformed data) skip the
/// frame/packet and continue. Skipped items are counted in
/// <see cref="IExporterStatistics.SkippedCount"/>.
/// Fatal errors (I/O failure, initialization) always abort regardless of mode.
/// </para>
/// <para>
/// In Strict mode: the first recoverable error aborts the export.
/// <c>OnFrame</c>/<c>OnPacket</c> returns <c>false</c> (unsubscribes).
/// </para>
/// </summary>
public interface IErrorTolerantExporter : IExporterStatistics
{
    #region Properties

    /// <summary>Gets or sets the error tolerance mode. Default: Tolerant.</summary>
    ErrorToleranceMode ErrorTolerance
    {
        get; set;
    }

    #endregion

    #region Events

    /// <summary>
    /// Event raised when a frame/packet is skipped due to a recoverable error.
    /// Only raised in Tolerant mode.
    /// </summary>
    event EventHandler<ExportErrorEventArgs>? ItemSkipped;

    #endregion
}

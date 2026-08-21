// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// Live output-size progress for exporters used by size-based split policies.
/// <para>
/// <see cref="EstimatedOutputBytes"/> is the approximate size of the current output
/// (file or dataset) <b>if all buffered data were flushed now</b>. Implementations must
/// update this from in-memory counters only — never by probing the filesystem.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. Read from the same thread that drives the export.
/// </para>
/// </summary>
public interface IExportByteProgress
{
    #region Properties

    /// <summary>
    /// Approximate number of output bytes already committed plus bytes pending in buffers
    /// for the current output segment. Monotonic non-decreasing while the segment is open.
    /// </summary>
    long EstimatedOutputBytes
    {
        get;
    }

    #endregion
}

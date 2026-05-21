// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Configuration for stream reassembly.
/// <para>
/// <b>Thread-safety:</b> Configuration is exposed through <c>get; init;</c> properties — all
/// fields are populated during initialization and are immutable thereafter. Instances are
/// therefore safe to share across any number of threads after construction.
/// </para>
/// </summary>
public sealed class StreamReassemblyConfig
{
    #region Properties

    /// <summary>Maximum PDU size in bytes.</summary>
    public int MaxPduSize { get; init; } = 65536;

    /// <summary>Maximum buffer size per direction in bytes.</summary>
    public int MaxBufferSize { get; init; } = 1048576; // 1 MiB

    /// <summary>PDU boundary detector.</summary>
    public IPduBoundaryDetector? BoundaryDetector
    {
        get; init;
    }

    /// <summary>Optional resynchronization heuristic.</summary>
    public IResyncHeuristic? ResyncHeuristic
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, segment data is copied into a separate buffer.
    /// Required for live-capture sources where the underlying packet buffer may be recycled.
    /// File-based sources can leave this <see langword="false"/> for zero-copy operation.
    /// </summary>
    public bool CopySegments
    {
        get; init;
    }

    #endregion
}

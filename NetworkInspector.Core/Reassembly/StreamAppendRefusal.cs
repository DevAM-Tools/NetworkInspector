// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>Why <see cref="SequenceStreamBuffer.TryAppend(uint, ReadOnlyMemory{byte}, int, out StreamAppendRefusal)"/> stored nothing.</summary>
public enum StreamAppendRefusal : byte
{
    /// <summary>The segment was stored (or was a retransmission); nothing was refused.</summary>
    None = 0,

    /// <summary>The bytes would exceed <see cref="StreamReassemblyConfig.MaxBufferSize"/> (limit <see cref="CoreLimits.MaxStreamBufferSize"/>).</summary>
    BufferSize = 1,

    /// <summary>
    /// The segment starts before the first byte of the stream and <see cref="StreamReassemblyConfig.MaxRebases"/> rebuilds were
    /// already made (limit <see cref="CoreLimits.MaxStreamRebases"/>).
    /// </summary>
    RebaseLimit = 2,
}

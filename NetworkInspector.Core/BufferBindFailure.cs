// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Why <see cref="MutField.TryBindBuffer"/> did not store a buffer. Both causes are expected failures of a busy or hostile
/// packet, so the protocol reports the one that applies by name instead of dispatching empty data.
/// </summary>
/// <remarks>
/// Related: <see cref="MutField.TryBindBuffer"/> (the only producer), <see cref="MutField.BoundBytes"/> (what a protocol reads to
/// apply its own total; the packet itself has no byte limit).
/// </remarks>
public enum BufferBindFailure : byte
{
    /// <summary>The buffer was stored; nothing failed.</summary>
    None = 0,

    /// <summary>
    /// The packet holds 255 additional buffers, the most the effect-layer key can address (it packs the buffer index in 8 bits).
    /// Nothing was stored.
    /// </summary>
    TableFull = 1,

    /// <summary>The packet is sealed: its buffer table is frozen. Nothing was stored.</summary>
    Sealed = 2,
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// Why <see cref="DerivedBytesLimit.TryBind"/> did not bind derived bytes. The protocol reports the cause that applies by name:
/// the setting for the total, the 255-buffer limit for the table.
/// </summary>
/// <remarks>Related: <see cref="DerivedBytesLimit.TryBind"/> (the only producer).</remarks>
internal enum DerivedBytesFailure : byte
{
    /// <summary>The bytes were bound; nothing failed.</summary>
    None = 0,

    /// <summary>The packet takes no further buffer: 255 are stored, or the packet is sealed. Nothing was bound.</summary>
    TableFull = 1,

    /// <summary>
    /// The bytes bound to the packet plus these bytes would pass the protocol's total. Nothing was bound, so a smaller buffer may
    /// still fit.
    /// </summary>
    TotalExceeded = 2,
}

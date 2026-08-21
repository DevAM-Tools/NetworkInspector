// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Describes how the packet ids returned by a pull relate to each other.
/// </summary>
/// <remarks>
/// Consumers that address packets by offset (virtualised grids, ring buffers) need to know whether
/// they may compute an id from a slot index. This flag makes that explicit instead of forcing the
/// caller to re-scan the returned ids.
/// </remarks>
public enum PacketIdLayout : byte
{
    /// <summary>
    /// Ids are consecutive integers: <c>destination[i].Id == startId + i</c>. Individual packets may
    /// still be <see langword="null"/> where the store holds no packet for that id.
    /// </summary>
    Contiguous = 0,

    /// <summary>
    /// Ids may skip values because non-matching or unavailable packets were filtered out of the
    /// scanned range. Read each <see cref="PacketRef.Id"/> instead of deriving it from the index.
    /// </summary>
    Gapped = 1,
}

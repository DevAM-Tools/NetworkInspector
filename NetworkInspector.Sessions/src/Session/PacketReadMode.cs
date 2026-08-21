// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Selects which packets a listener-bound pull returns.
/// </summary>
public enum PacketReadMode : byte
{
    /// <summary>
    /// Every packet id starting at the requested id, whether or not it matches the listener's
    /// filter. Ids are always consecutive; a slot may still carry a <see langword="null"/> packet
    /// when nothing is stored for that id.
    /// </summary>
    All = 0,

    /// <summary>
    /// Only packets that match the listener's filter. A listener without a filter, or one whose
    /// filter is <see cref="NetworkInspector.Filter.Filter.AlwaysMatch"/>, takes the same fast path
    /// as <see cref="All"/> and performs no per-packet work.
    /// </summary>
    Matching = 1,
}

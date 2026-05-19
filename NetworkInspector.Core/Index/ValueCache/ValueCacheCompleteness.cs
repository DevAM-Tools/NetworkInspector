// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Tracks whether a <see cref="ValueCacheSeries"/> is complete or if entries were dropped/clamped.
/// Consumers can check individual flags to determine the nature of data loss.
/// </summary>
[Flags]
public enum ValueCacheCompleteness : byte
{
    #region Enum Values

    /// <summary>All values were recorded without issues.</summary>
    None = 0,

    /// <summary>
    /// At least one value was clamped because it exceeded the compact storage range.
    /// Only relevant for CompactInt8/16/32 and CompactUInt8/16/32 modes.
    /// </summary>
    HasOverflow = 1 << 0,

    /// <summary>
    /// At least one value was skipped because its packet timestamp was
    /// not monotonically increasing (out-of-order packet).
    /// </summary>
    HasTimestampSkips = 1 << 1,

    /// <summary>
    /// At least one packet that matches the field was evicted from the
    /// PacketStore and could not be re-parsed (source not random-access).
    /// Only relevant for retroactive cache builds.
    /// </summary>
    HasEvictedPackets = 1 << 2,

    /// <summary>
    /// Multiple values for the same field existed in a single packet.
    /// First-value-wins was applied; subsequent values were dropped.
    /// </summary>
    HasDuplicateDrops = 1 << 3,

    #endregion
}
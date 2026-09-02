// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Gather-only snapshot of one published series row. Do not persist this type; read columns via
/// chunk spans for scans.
/// </summary>
/// <typeparam name="T">Minimal payload type for this series.</typeparam>
/// <param name="PacketId">Owning packet id.</param>
/// <param name="TimestampNanos">Packet timestamp in nanoseconds.</param>
/// <param name="Value">Typed payload.</param>
public readonly record struct ValueCacheRow<T>(int PacketId, long TimestampNanos, T Value)
    where T : unmanaged;

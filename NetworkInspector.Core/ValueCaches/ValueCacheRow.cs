// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Gather-only snapshot of one published series row. Do not persist this type. Scalar scan with
/// <see cref="ValueCacheSeriesHandle{T}"/> (index, range, or <c>foreach</c>). SIMD / bulk scan with
/// <see cref="ValueCacheSeriesHandle{T}.EnumerateChunks()"/>.
/// </summary>
/// <typeparam name="T">Payload type for this series.</typeparam>
/// <param name="PacketId">Owning packet id.</param>
/// <param name="TimestampNanos">Packet timestamp in nanoseconds.</param>
/// <param name="Value">Typed payload.</param>
public readonly record struct ValueCacheRow<T>(int PacketId, long TimestampNanos, T Value);

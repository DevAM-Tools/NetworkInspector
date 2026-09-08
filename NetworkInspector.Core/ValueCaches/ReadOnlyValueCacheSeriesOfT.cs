// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Zero-allocation read-only view over a <see cref="ValueCacheSeries{T}"/>.
/// <para>
/// When the compile-time type is this struct, accessors can inline to the owner.
/// Consume it through generic methods constrained to <see cref="IReadOnlyValueCacheSeries{T}"/>
/// so the JIT does not box.
/// </para>
/// <para>
/// Warning: do not cast this struct to <see cref="IReadOnlyValueCacheSeries{T}"/>, store it in that
/// interface type, or pass it to a non-generic parameter of that type. Those conversions box.
/// Prefer this struct (or a generic constraint) at call sites.
/// </para>
/// </summary>
/// <typeparam name="T">Payload type selected from the stack <see cref="FieldType"/>.</typeparam>
public readonly struct ReadOnlyValueCacheSeries<T> : IReadOnlyValueCacheSeries<T>
{
    #region Fields

    private readonly ValueCacheSeries<T> _Owner;

    #endregion

    #region Lifecycle

    /// <summary>Creates a view over <paramref name="owner"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.</exception>
    public ReadOnlyValueCacheSeries(ValueCacheSeries<T> owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _Owner = owner;
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public FieldId FieldId => _Owner.FieldId;

    /// <inheritdoc/>
    public int ChunkShift => _Owner.ChunkShift;

    /// <inheritdoc/>
    public FieldType FieldType => _Owner.FieldType;

    /// <inheritdoc/>
    public ValueCaptureMode CaptureMode => _Owner.CaptureMode;

    /// <inheritdoc/>
    public int Count => _Owner.Count;

    /// <inheritdoc/>
    public ValueCacheSeriesHandle<T> Handle => _Owner.Handle;

    /// <inheritdoc/>
    public bool PacketIdsMonotonic => _Owner.PacketIdsMonotonic;

    /// <inheritdoc/>
    public bool TimestampsMonotonic => _Owner.TimestampsMonotonic;

    /// <inheritdoc/>
    public ValueCacheRow<T> this[int index] => _Owner[index];

    #endregion

    #region Public API

    /// <inheritdoc/>
    public int GetPacketId(int index) => _Owner.GetPacketId(index);

    /// <inheritdoc/>
    public long GetTimestamp(int index) => _Owner.GetTimestamp(index);

    /// <inheritdoc/>
    public bool TryGetPacketIdChunk(int chunkIndex, int observedCount, out ReadOnlySpan<int> span) =>
        _Owner.TryGetPacketIdChunk(chunkIndex, observedCount, out span);

    /// <inheritdoc/>
    public bool TryGetTimestampChunk(int chunkIndex, int observedCount, out ReadOnlySpan<long> span) =>
        _Owner.TryGetTimestampChunk(chunkIndex, observedCount, out span);

    /// <inheritdoc/>
    public ValueCacheSeriesHandle GetHandle() => Handle.Keys;

    /// <inheritdoc/>
    public T GetValue(int index) => _Owner.GetValue(index);

    /// <inheritdoc/>
    public bool TryGetValueChunk(int chunkIndex, int observedCount, out ReadOnlySpan<T> span) =>
        _Owner.TryGetValueChunk(chunkIndex, observedCount, out span);

    #endregion
}

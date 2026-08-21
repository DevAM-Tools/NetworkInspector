// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Growable byte buffer backed by a rented array from <see cref="ArrayPool{T}.Shared"/>.
/// The buffer doubles on demand and returns its old array to the pool on grow,
/// which keeps steady-state allocations near zero once the high-water mark
/// has been reached.
/// <para>
/// <b>Lifecycle:</b> <see cref="Return"/> must be called when the buffer is no
/// longer needed (typically from the owner's <c>OnFinish</c> / <c>Dispose</c>).
/// <see cref="Return"/> returns the rented array to the pool and resets the buffer
/// to empty; it does <em>not</em> immediately rent a new array.
/// The next call to <see cref="Write"/>, <see cref="WriteByte"/>, or
/// <see cref="Reserve"/> will lazily rent a fresh array at that point.
/// <see cref="Return"/> is idempotent.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. Callers must serialize access.
/// </para>
/// </summary>
internal sealed class PooledBuffer
{
    /// <summary>
    /// Sentinel for the "returned" state. A zero-length array means the next
    /// write needs to rent a fresh array — never call <see cref="ArrayPool{T}.Return"/>
    /// on this sentinel.
    /// </summary>
    private static readonly byte[] _EmptySentinel = [];

    private byte[] _Array;

    /// <summary>Number of bytes written.</summary>
    internal int Length { get; private set; }

    /// <summary>Creates a buffer with the specified initial capacity.</summary>
    /// <param name="capacity">Initial capacity in bytes (0 is allowed and rents lazily).</param>
    internal PooledBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _Array = capacity == 0 ? _EmptySentinel : ArrayPool<byte>.Shared.Rent(capacity);
        Length = 0;
    }

    /// <summary>The written portion of the buffer.</summary>
    internal ReadOnlySpan<byte> WrittenSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Array.AsSpan(0, Length);
    }

    /// <summary>Current allocated capacity.</summary>
    internal int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Array.Length;
    }

    /// <summary>Appends data to the buffer, growing if necessary.</summary>
    /// <param name="data">The data to append.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Write(ReadOnlySpan<byte> data)
    {
        int required = Length + data.Length;
        if (required > _Array.Length)
        {
            _Grow(required);
        }
        data.CopyTo(_Array.AsSpan(Length));
        Length += data.Length;
    }

    /// <summary>Appends a single byte to the buffer.</summary>
    /// <param name="value">The byte to append.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteByte(byte value)
    {
        if (Length >= _Array.Length)
        {
            _Grow(Length + 1);
        }
        _Array[Length++] = value;
    }

    /// <summary>Reserves space in the buffer and returns a span to write into.</summary>
    /// <param name="count">Number of bytes to reserve.</param>
    /// <returns>A writable span of exactly <paramref name="count"/> bytes.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<byte> Reserve(int count)
    {
        int required = Length + count;
        if (required > _Array.Length)
        {
            _Grow(required);
        }
        Span<byte> span = _Array.AsSpan(Length, count);
        Length += count;
        return span;
    }

    /// <summary>Resets the write position to zero without releasing the array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Reset() => Length = 0;

    /// <summary>
    /// Returns the underlying array to the pool and resets the buffer to empty.
    /// Idempotent — may be called multiple times without error.
    /// The rented array is returned immediately; a subsequent
    /// <see cref="Write"/>, <see cref="WriteByte"/>, or <see cref="Reserve"/> call
    /// will lazily rent a fresh array from the pool at that point.
    /// </summary>
    internal void Return()
    {
        if (_Array.Length > 0)
        {
            // Return the rented buffer (no need to clear — caller-supplied bytes only)
            ArrayPool<byte>.Shared.Return(_Array);
        }
        _Array = _EmptySentinel;
        Length = 0;
    }

    /// <summary>
    /// Grows the internal buffer to accommodate at least <paramref name="required"/>
    /// bytes. Rents a new array from the pool, copies the existing content over,
    /// and returns the previous array to the pool.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _Grow(int required)
    {
        // At least double the current capacity, or the required size — whichever is larger.
        // ArrayPool may return a buffer larger than requested; that is fine and we use the
        // full reported length as our new capacity.
        int newCapacity = Math.Max(_Array.Length * 2, required);
        byte[] newArray = ArrayPool<byte>.Shared.Rent(newCapacity);
        // If the copy throws (e.g. due to a StackOverflowException or any other
        // unexpected failure), return the newly rented buffer to the pool so it is not
        // leaked, and leave the original buffer intact so the caller can still observe
        // the pre-growth state.
        try
        {
            if (Length > 0)
            {
                _Array.AsSpan(0, Length).CopyTo(newArray);
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(newArray);
            throw;
        }
        if (_Array.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_Array);
        }
        _Array = newArray;
    }
}

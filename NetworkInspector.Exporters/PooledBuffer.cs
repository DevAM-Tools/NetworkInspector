// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Growable byte buffer backed by a rented array from <see cref="ArrayPool{T}.Shared"/>.
/// The buffer doubles on demand and returns its old array to the pool on grow,
/// which keeps steady-state allocations near zero once the high-water mark
/// has been reached.
/// <para>
/// <b>Why a class around a raw array:</b> exporters append many small pieces per packet. A single reusable buffer
/// that survives across packets (see <see cref="Reset"/>) replaces a per-packet <c>MemoryStream</c> or string, so the
/// exporters emit no garbage per packet.
/// </para>
/// <para>
/// <b>Span lifetime:</b> spans from <see cref="WrittenSpan"/> and <see cref="Reserve"/> point into the current
/// array. Any later <see cref="Write"/>, <see cref="WriteByte"/>, <see cref="Reserve"/> or <see cref="Return"/> may
/// replace or release that array, so such a span must be used before the next call that can grow the buffer.
/// </para>
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

    /// <summary>
    /// The current backing array: either a rented pool array or <see cref="_EmptySentinel"/>.
    /// Its length (which can exceed the requested capacity because the pool rounds up) is the usable capacity.
    /// </summary>
    private byte[] _Array;

    /// <summary>Number of bytes written.</summary>
    internal int Length { get; private set; }

    /// <summary>Creates a buffer with the specified initial capacity.</summary>
    /// <param name="capacity">Initial capacity in bytes (0 is allowed and rents lazily).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
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

    /// <summary>
    /// Appends data to the buffer, growing if necessary. The data must not alias this buffer's own array,
    /// because growing replaces the array before the copy.
    /// </summary>
    /// <param name="data">The data to append.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Write(ReadOnlySpan<byte> data)
    {
        // Hot path: called for every token of every exported packet, so it is inlined and the rare grow is
        // split into a NoInlining method to keep this body small. The sum can wrap past int.MaxValue; the unsigned
        // comparison sends a wrapped (negative) sum into _Grow as well, which rejects it with a message that names the
        // limit, so an overflow never turns into a silently short buffer or an unrelated range exception.
        int required = unchecked(Length + data.Length);
        if ((uint)required > (uint)_Array.Length)
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
            _Grow(unchecked(Length + 1));
        }
        _Array[Length++] = value;
    }

    /// <summary>
    /// Reserves space in the buffer and returns a span to write into. The reserved bytes count as written
    /// immediately (<see cref="Length"/> advances by <paramref name="count"/>), and their content is unspecified
    /// until the caller fills them, because pooled arrays are not cleared.
    /// </summary>
    /// <param name="count">Number of bytes to reserve; must not be negative.</param>
    /// <returns>A writable span of exactly <paramref name="count"/> bytes.</returns>
    /// <exception cref="InvalidOperationException">The buffer would exceed <see cref="Array.MaxLength"/> bytes.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<byte> Reserve(int count)
    {
        // Same overflow handling as Write: a wrapped sum is rejected in _Grow instead of reaching AsSpan.
        int required = unchecked(Length + count);
        if ((uint)required > (uint)_Array.Length)
        {
            _Grow(required);
        }
        Span<byte> span = _Array.AsSpan(Length, count);
        Length += count;
        return span;
    }

    /// <summary>
    /// Discards the bytes after the first <paramref name="length"/> bytes, so the buffer holds exactly what it held when
    /// <see cref="Length"/> was <paramref name="length"/>. Used to undo a partly written item. The array is kept.
    /// </summary>
    /// <param name="length">New length; must be between 0 and the current <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative or larger than <see cref="Length"/>.</exception>
    internal void TruncateTo(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Length);
        Length = length;
    }

    /// <summary>
    /// Resets the write position to zero without releasing the array, so the next packet reuses the already
    /// grown capacity. Contrast with <see cref="Return"/>, which gives the array back to the pool.
    /// </summary>
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
            // Return the rented buffer without clearing it: clearing would cost a pass over the whole array, and the
            // bytes are exported capture data the process already holds. Residual risk: stale packet bytes stay in
            // the shared pool until the array is reused; the pool is process-local, so no other trust domain sees them.
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
        // Limit: Array.MaxLength bytes (the largest .NET array; a format and platform fact, so a constant). A negative
        // "required" is a sum that wrapped past int.MaxValue. Threat: a hostile or corrupt input drives an owner past the
        // limit; exceeding it is an expected failure and is reported with the limit named, never as a silent wrap. The
        // owners (exporters) catch it per item.
        if (required < 0 || required > Array.MaxLength)
        {
            throw new InvalidOperationException(
                "A pooled buffer cannot hold more than "
                + Array.MaxLength.ToString(CultureInfo.InvariantCulture)
                + " bytes (Array.MaxLength); the requested size is beyond it.");
        }

        // At least double the current capacity, or the required size — whichever is larger. Doubling makes the
        // total copy work amortized O(n) over a run of appends and bounds the number of rents to O(log n).
        // ArrayPool may return a buffer larger than requested; that is fine and we use the
        // full reported length as our new capacity. The doubling is computed in long and capped at the limit, so it
        // can neither wrap nor ask for an array that cannot exist.
        long doubled = Math.Min((long)_Array.Length * 2, Array.MaxLength);
        int newCapacity = checked((int)Math.Max(doubled, required));
        byte[] newArray = ArrayPool<byte>.Shared.Rent(newCapacity);

        // Copy first, release the old array second: if the copy throws, return the newly rented buffer to the pool
        // so it is not leaked, and leave the original buffer intact so the caller still observes the pre-growth
        // state (no half-grown buffer). The copy itself is defensive; spans of a live array do not normally throw.
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
        // Only now is the old array no longer needed; the sentinel (length 0) must never be returned to the pool.
        if (_Array.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_Array);
        }
        _Array = newArray;
    }
}

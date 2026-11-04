// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Parquet;

/// <summary>
/// A value column copied from a batch span into an array rented from <see cref="ArrayPool{T}.Shared"/>, for the value-type
/// <c>WriteAsync</c> overloads of Parquet.Net, which take <see cref="ReadOnlyMemory{T}"/> and cannot read a
/// <see cref="List{T}"/>.
/// <para>
/// <b>Why pooled:</b> the copy is unavoidable (the batch lists cannot be exposed as memory), but the array need not be new
/// garbage per flush: a new <c>T[]</c> per value column would be garbage of the batch's size that survives into
/// generation 1. Renting makes the steady state allocation-free. The write is awaited (blocked on) before
/// <see cref="Dispose"/> returns the array, and Parquet.Net encodes the values into its own page buffers during that call, so
/// nothing reads the array after it went back to the pool. The pool does not clear the array: the values are plain numbers,
/// not secrets.
/// </para>
/// <para>
/// <b>Misuse:</b> <see cref="Memory"/> is valid until <see cref="Dispose"/>; the length is the number of copied values, not the
/// rented array's length — passing the array itself instead of <see cref="Memory"/> would write the pool's spare slots as rows.
/// </para>
/// <para><b>Thread safety:</b> a value owned by the thread that writes the row group.</para>
/// </summary>
/// <typeparam name="T">The unmanaged value type of the column.</typeparam>
internal readonly struct PooledColumn<T> : IDisposable
    where T : struct
{
    #region Rented copy

    /// <summary>The rented array; longer than <see cref="_Length"/> because the pool rounds sizes up. Returned by <see cref="Dispose"/>.</summary>
    private readonly T[] _Array;

    /// <summary>The number of copied values at the start of <see cref="_Array"/>.</summary>
    private readonly int _Length;

    /// <summary>Wraps a rented array that already holds the copied values.</summary>
    /// <param name="array">The rented array, owned by the new value until <see cref="Dispose"/>.</param>
    /// <param name="length">The number of copied values at the start of <paramref name="array"/>.</param>
    private PooledColumn(T[] array, int length)
    {
        _Array = array;
        _Length = length;
    }

    /// <summary>The copied values (exactly the number that was copied).</summary>
    internal ReadOnlyMemory<T> Memory => _Array.AsMemory(0, _Length);

    /// <summary>Copies <paramref name="values"/> into a rented array.</summary>
    /// <param name="values">The column values of the batch.</param>
    /// <returns>The pooled copy; dispose it after the row group was written.</returns>
    internal static PooledColumn<T> CopyOf(ReadOnlySpan<T> values)
    {
        // At least one slot: Rent(0) is allowed but the array must not be the shared empty instance when it is returned.
        T[] array = ArrayPool<T>.Shared.Rent(Math.Max(1, values.Length));
        values.CopyTo(array);
        return new PooledColumn<T>(array, values.Length);
    }

    /// <summary>
    /// Rents a column of <paramref name="length"/> values for the caller to fill, for a column that is derived from the batch value by
    /// value (an address bag's integers) instead of copied from a span.
    /// </summary>
    /// <param name="length">The number of values; 0 or more.</param>
    /// <returns>
    /// The pooled column; its values are unspecified until the caller writes every entry of <see cref="WritableSpan"/> (the pool does
    /// not clear arrays). Dispose it after the row group was written.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    internal static PooledColumn<T> Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new PooledColumn<T>(ArrayPool<T>.Shared.Rent(Math.Max(1, length)), length);
    }

    /// <summary>The values to fill after <see cref="Rent"/>: exactly the requested number, not the rented array's length.</summary>
    internal Span<T> WritableSpan => _Array.AsSpan(0, _Length);

    /// <summary>Returns the array to the pool.</summary>
    public void Dispose() => ArrayPool<T>.Shared.Return(_Array);

    #endregion
}

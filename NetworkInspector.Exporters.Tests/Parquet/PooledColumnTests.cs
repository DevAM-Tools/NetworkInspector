// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Parquet;

/// <summary>
/// <see cref="PooledColumn{T}"/>: a column rented for the caller to fill has exactly the requested length (the pool's array is
/// longer), shows what the caller wrote, and refuses a negative length.
/// </summary>
internal sealed class PooledColumnTests
{
    #region Renting and filling a column

    /// <summary>Rents a column of the length, fills it with 1, 2, 3, ..., and returns what <see cref="PooledColumn{T}.Memory"/> shows.</summary>
    /// <param name="length">The number of values.</param>
    /// <returns>The values of the column.</returns>
    private static uint[] _RentAndFill(int length)
    {
        using PooledColumn<uint> column = PooledColumn<uint>.Rent(length);
        Span<uint> values = column.WritableSpan;
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = checked((uint)(index + 1));
        }

        return column.Memory.ToArray();
    }

    /// <summary>
    /// A rented column has exactly the requested length, even though the pool hands out a longer array, and it keeps the values
    /// the caller wrote.
    /// </summary>
    /// <param name="length">The number of values to rent.</param>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(1000)]
    public async Task ARentedColumnHasExactlyTheRequestedLengthAndKeepsWhatWasWritten(int length)
    {
        uint[] values = _RentAndFill(length);

        // The pool rounds the array up (a 1000-value column gets 1024), so a column that exposed the array would have extra rows.
        await Assert.That(values.Length).IsEqualTo(length);
        await Assert.That(values.Select(static (value, index) => value == checked((uint)(index + 1))).All(static match => match)).IsTrue();
    }

    #endregion

    #region Refusals

    /// <summary>Renting a column of negative length throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task ARentedColumnOfNegativeLengthIsRefused()
    {
        await Assert.That(static () => PooledColumn<uint>.Rent(-1)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// Column readers over the bytes of a finished Parquet file, for content tests that export onto a
/// <see cref="MemoryExportTarget"/> and read the entry back without a file or a database.
/// </summary>
/// <remarks>Thread safety: stateless; every call opens its own reader on its own copy of the bytes.</remarks>
internal static class ParquetReads
{
    /// <summary>Reads one column of every row group in file order.</summary>
    /// <typeparam name="T">The CLR type of the column values.</typeparam>
    /// <param name="file">The bytes of a finished Parquet file.</param>
    /// <param name="column">The column name.</param>
    /// <param name="read">Reads one row group of the column into the buffer, which has one slot per row.</param>
    /// <returns>The values of all row groups in file order.</returns>
    private static async Task<T[]> _ColumnAsync<T>(byte[] file, string column, Func<ParquetRowGroupReader, DataField, T[], ValueTask> read)
    {
        using MemoryStream stream = new(file);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        DataField field = reader.Schema.FindDataField(column);
        List<T> values = [];
        for (int group = 0; group < reader.RowGroupCount; group++)
        {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(group);
            T[] buffer = new T[rowGroup.RowCount];
            await read(rowGroup, field, buffer);
            values.AddRange(buffer);
        }

        return [.. values];
    }

    /// <summary>Reads a 32-bit integer column.</summary>
    /// <param name="file">The bytes of a finished Parquet file.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The values in file order.</returns>
    internal static Task<int[]> IntsAsync(byte[] file, string column) =>
        _ColumnAsync<int>(file, column, static (rowGroup, field, buffer) => rowGroup.ReadAsync<int>(field, buffer, null, CancellationToken.None));

    /// <summary>Reads a UINT8 column.</summary>
    /// <param name="file">The bytes of a finished Parquet file.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The values in file order.</returns>
    internal static Task<byte[]> BytesAsync(byte[] file, string column) =>
        _ColumnAsync<byte>(file, column, static (rowGroup, field, buffer) => rowGroup.ReadAsync<byte>(field, buffer, null, CancellationToken.None));

    /// <summary>Reads a binary column.</summary>
    /// <param name="file">The bytes of a finished Parquet file.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The values in file order.</returns>
    internal static Task<byte[][]> BinariesAsync(byte[] file, string column) =>
        _ColumnAsync<byte[]>(file, column, static (rowGroup, field, buffer) => rowGroup.ReadAsync(field, buffer, null, CancellationToken.None));

    /// <summary>Reads a text column; a NULL cell is <see langword="null"/>.</summary>
    /// <param name="file">The bytes of a finished Parquet file.</param>
    /// <param name="column">The column name.</param>
    /// <returns>The values in file order.</returns>
    internal static Task<string?[]> TextsAsync(byte[] file, string column) =>
        _ColumnAsync<string?>(file, column, static (rowGroup, field, buffer) => rowGroup.ReadAsync(field, buffer, null, CancellationToken.None));

    /// <summary>Whether a Parquet file has a data column with the given name.</summary>
    /// <param name="file">The bytes of a finished Parquet file.</param>
    /// <param name="column">The column name.</param>
    /// <returns><see langword="true"/> when the schema has the column.</returns>
    internal static async Task<bool> HasColumnAsync(byte[] file, string column)
    {
        using MemoryStream stream = new(file);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        return reader.Schema.GetDataFields().Any(field => string.Equals(field.Name, column, StringComparison.Ordinal));
    }
}

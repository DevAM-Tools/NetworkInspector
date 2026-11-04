// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// The small DuckDB queries that the DuckDB, Parquet (through an in-memory DuckDB), and buffer tests use to read an export back:
/// one value as a number or as text, a column as a list of texts, and a statement without a result. They state no export layout;
/// the test passes the SQL.
/// </summary>
/// <remarks>Thread safety: stateless; every method works on the connection it is given, which one test owns.</remarks>
internal static class DuckDbReadBack
{
    #region Reading an export back

    /// <summary>Runs a statement that returns no rows.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="sql">The statement.</param>
    internal static void Execute(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Returns the first column of the first row as a number.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="sql">A query that returns one number.</param>
    /// <returns>The number.</returns>
    internal static long Scalar(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Counts the rows of a table.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="table">The table name.</param>
    /// <returns>The number of rows.</returns>
    internal static long Count(DuckDBConnection connection, string table) => Scalar(connection, Invariant($"SELECT COUNT(*) FROM {table}"));

    /// <summary>Returns the first column of the first row as text.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="sql">A query that returns one value.</param>
    /// <returns>The value as text; the empty string when the value is NULL.</returns>
    internal static string Text(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        string? text = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return text
            ?? string.Empty;
    }

    /// <summary>Returns the first column of every row as text, in result order.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="sql">A query that returns one column.</param>
    /// <returns>The values as text.</returns>
    internal static List<string> Texts(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using DuckDBDataReader reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            string? text = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
            rows.Add(text
                ?? string.Empty);
        }

        return rows;
    }

    #endregion
}

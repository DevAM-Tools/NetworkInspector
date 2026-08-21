// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Shared CLI argument helpers used by <see cref="ConvertCommand"/> and <see cref="ExportCommand"/>.
/// </summary>
internal static class CliArgumentParsing
{
    #region Public API

    /// <summary>
    /// Runs a command body and maps <see cref="ArgumentException"/> to
    /// <see cref="ExitCode.ArgumentError"/> with a stderr message.
    /// </summary>
    internal static int RunWithArgumentGuard(Func<int> body)
    {
        try
        {
            return body();
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return (int)ExitCode.ArgumentError;
        }
    }

    /// <summary>Checks whether a string is a help flag.</summary>
    internal static bool IsHelpFlag(string arg) =>
        arg is "--help" or "-h" or "-?" or "/?" or "--HELP" or "-H";

    /// <summary>Gets the next argument value, throwing if missing or null.</summary>
    internal static string GetNextArg(string[] args, ref int index, string name)
    {
        index++;
        if (index >= args.Length)
        {
            throw new ArgumentException($"Option '{name}' requires a value.");
        }

        string? value = args[index];
        if (value is null)
        {
            throw new ArgumentException($"Option '{name}' received a null argument (internal error).");
        }

        return value;
    }

    /// <summary>Parses a non-negative long value, throwing a user-friendly message on failure.</summary>
    internal static long ParseNonNegativeLong(string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) || result < 0)
        {
            throw new ArgumentException($"Invalid numeric value: '{value}'.");
        }

        return result;
    }

    /// <summary>
    /// Parses a non-negative frame/packet count, rejecting negatives and values above
    /// <see cref="ArrayIndexIdRange.MaxCount"/> (or <paramref name="maxInclusive"/> when provided).
    /// </summary>
    /// <param name="value">Raw CLI argument text.</param>
    /// <param name="optionName">Option name for overflow messages (e.g. <c>--max-frames</c>).</param>
    /// <param name="maxInclusive">
    /// Optional upper bound inclusive. When <see langword="null"/>, uses <see cref="ArrayIndexIdRange.MaxCount"/>.
    /// </param>
    internal static int ParseNonNegativeInt(
        string value,
        string? optionName = null,
        int? maxInclusive = null)
    {
        int max = maxInclusive ?? ArrayIndexIdRange.MaxCount;

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            throw new ArgumentException($"Invalid numeric value: '{value}'.");
        }

        if (result < 0)
        {
            throw new ArgumentException($"Invalid numeric value: '{value}'.");
        }

        if (result > max)
        {
            string prefix = optionName is not null ? $"{optionName} too large" : "Value too large";
            throw new ArgumentException(
                $"{prefix}: '{value}'. Max is {max.ToString(CultureInfo.InvariantCulture)}.");
        }

        return result;
    }

    /// <summary>
    /// Converts a MiB quantity to bytes for BLF cache budgets, rejecting values that would
    /// overflow <see cref="int"/> when multiplied by 1024².
    /// </summary>
    /// <param name="miB">Size in mebibytes (must already be non-negative).</param>
    /// <param name="optionName">CLI option name for error messages.</param>
    /// <returns>Byte budget suitable for <see cref="int"/> cache APIs.</returns>
    internal static int MiBToCacheBudgetBytes(long miB, string optionName)
    {
        const long bytesPerMiB = 1024L * 1024L;
        long maxMiB = int.MaxValue / bytesPerMiB;
        if (miB > maxMiB)
        {
            throw new ArgumentException(
                $"{optionName} too large: '{miB.ToString(CultureInfo.InvariantCulture)}'. " +
                $"Max is {maxMiB.ToString(CultureInfo.InvariantCulture)} MiB.");
        }

        return (int)(miB * bytesPerMiB);
    }

    /// <summary>
    /// Converts a MiB quantity to bytes for split-size limits, rejecting values that would
    /// overflow <see cref="long"/> when multiplied by 1024².
    /// </summary>
    /// <param name="miB">Size in mebibytes (must already be non-negative).</param>
    /// <param name="optionName">CLI option name for error messages.</param>
    /// <returns>Byte limit, or 0 when <paramref name="miB"/> is 0.</returns>
    internal static long MiBToSplitSizeBytes(long miB, string optionName)
    {
        if (miB == 0)
        {
            return 0;
        }

        const long bytesPerMiB = 1024L * 1024L;
        long maxMiB = long.MaxValue / bytesPerMiB;
        if (miB > maxMiB)
        {
            throw new ArgumentException(
                $"{optionName} too large: '{miB.ToString(CultureInfo.InvariantCulture)}'. " +
                $"Max is {maxMiB.ToString(CultureInfo.InvariantCulture)} MiB.");
        }

        return miB * bytesPerMiB;
    }

    #endregion
}

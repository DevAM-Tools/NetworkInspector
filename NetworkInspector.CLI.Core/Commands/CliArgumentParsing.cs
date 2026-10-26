// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Shared CLI argument helpers used by every command (<see cref="ConvertCommand"/>, <see cref="ExportCommand"/>,
/// <see cref="PluginsCommand"/>, <see cref="SettingsCommand"/>, <see cref="ProfileCommand"/>): the guard that turns a bad argument
/// into exit code 1, the option-value readers, and the number parsers. All numbers are parsed with the invariant culture, so a
/// command line means the same on every machine. Stateless; thread-safe.
/// </summary>
internal static class CliArgumentParsing
{
    #region Public API

    /// <summary>
    /// Runs a command body and maps <see cref="ArgumentException"/> to <see cref="ExitCode.ArgumentError"/> with a message on
    /// <paramref name="error"/>. The parsers below signal a bad argument by throwing <see cref="ArgumentException"/>, which keeps
    /// the parsing loops free of error plumbing; this guard is the one place that turns it into an error line and exit code 1.
    /// Any other exception propagates to the top-level handler of <see cref="CliEntry"/> (exit code 3). Accepted residual risk:
    /// an <see cref="ArgumentException"/> thrown by a bug deeper in the body is reported as a usage error as well.
    /// </summary>
    /// <param name="body">The command body.</param>
    /// <param name="error">The error writer of the command.</param>
    /// <returns>The exit code of the body, or <see cref="ExitCode.ArgumentError"/>.</returns>
    internal static int RunWithArgumentGuard(Func<int> body, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            return body();
        }
        catch (ArgumentException ex)
        {
            error.WriteLine($"Error: {ex.Message}");
            return (int)ExitCode.ArgumentError;
        }
    }

    /// <summary>
    /// Runs an asynchronous command body and maps <see cref="ArgumentException"/> and <see cref="CliUsageException"/> to
    /// <see cref="ExitCode.ArgumentError"/> with a message on <paramref name="error"/>. The extra exception type covers mistakes
    /// of the user that surface late in a run (see <see cref="CliUsageException"/>). The residual risk of
    /// <see cref="RunWithArgumentGuard"/> applies here too.
    /// </summary>
    /// <param name="body">The command body.</param>
    /// <param name="error">The error writer of the command.</param>
    /// <returns>The exit code of the body, or <see cref="ExitCode.ArgumentError"/>.</returns>
    internal static async Task<int> RunWithArgumentGuardAsync(Func<Task<int>> body, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or CliUsageException)
        {
            error.WriteLine($"Error: {ex.Message}");
            return (int)ExitCode.ArgumentError;
        }
    }

    /// <summary>Consumes a settings option (<c>--profile</c>, <c>--settings-path</c>, …) at the current argument, if it is one.</summary>
    /// <param name="args">All arguments of the command.</param>
    /// <param name="index">Position of the current argument; advanced past the consumed value.</param>
    /// <param name="settings">Collects the settings options.</param>
    /// <returns><see langword="true"/> when the argument was a settings option.</returns>
    /// <exception cref="ArgumentException">The option has no value or the value is not a number.</exception>
    internal static bool ParseSettingsOption(string[] args, ref int index, CliSettingsOptionsBuilder settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.TryConsume(args, ref index);
    }

    /// <summary>
    /// Checks whether a string is a help flag. Only the spellings listed are help flags (lower case, and the upper-case forms of
    /// <c>--help</c> and <c>-h</c>); other casings such as <c>--Help</c> are not.
    /// </summary>
    /// <param name="arg">The argument as typed.</param>
    /// <returns><see langword="true"/> for a help flag.</returns>
    internal static bool IsHelpFlag(string arg) =>
        arg is "--help" or "-h" or "-?" or "/?" or "--HELP" or "-H";

    /// <summary>
    /// Gets the value that follows an option, throwing if it is missing. The value is taken as it is, even when it looks like
    /// another option (<c>-o --foo</c> writes a file named <c>--foo</c>): an option that needs a value always consumes the next
    /// argument.
    /// </summary>
    /// <param name="args">All arguments of the command.</param>
    /// <param name="index">Position of the option; advanced to the position of its value.</param>
    /// <param name="name">The option name for the error message.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentException">The option is the last argument, or its value is <see langword="null"/>.</exception>
    internal static string GetNextArg(string[] args, ref int index, string name)
    {
        index++;
        if (index >= args.Length)
        {
            throw new ArgumentException($"Option '{name}' requires a value.");
        }

        // The operating system never hands over a null argument; only a caller that built the array by hand can, which is a bug
        // of that caller and not of the user.
        string? value = args[index];
        if (value is null)
        {
            throw new ArgumentException($"Option '{name}' received a null argument (internal error).");
        }

        return value;
    }

    /// <summary>
    /// Parses a signed 64-bit integer (invariant culture, no thousands separators), throwing a user-friendly message on failure.
    /// Used for the settings size limit, where zero or a negative value is meaningful (it disables the check).
    /// </summary>
    /// <param name="value">The argument text.</param>
    /// <returns>The number.</returns>
    /// <exception cref="ArgumentException">The text is not an integer in the range of <see cref="long"/>.</exception>
    internal static long ParseInt64(string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result))
        {
            throw new ArgumentException($"Invalid numeric value: '{value}'.");
        }

        return result;
    }

    /// <summary>
    /// Parses a signed 32-bit integer (invariant culture), throwing a user-friendly message on failure. Used for the settings
    /// depth limit, where zero or a negative value is meaningful (it disables the check).
    /// </summary>
    /// <param name="value">The argument text.</param>
    /// <returns>The number.</returns>
    /// <exception cref="ArgumentException">The text is not an integer in the range of <see cref="int"/>.</exception>
    internal static int ParseInt32(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            throw new ArgumentException($"Invalid numeric value: '{value}'.");
        }

        return result;
    }

    /// <summary>
    /// Parses a non-negative long value, throwing a user-friendly message on failure. The caller converts MiB quantities to bytes
    /// with <see cref="MiBToCacheBudgetBytes"/> or <see cref="MiBToSplitSizeBytes"/>, which check the overflow of the product.
    /// </summary>
    /// <param name="value">The argument text.</param>
    /// <returns>The number, zero or greater.</returns>
    /// <exception cref="ArgumentException">The text is not an integer, is negative, or exceeds <see cref="long"/>.</exception>
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
    /// <returns>The number, between zero and the bound.</returns>
    /// <exception cref="ArgumentException">The text is not an integer, is negative, or is above the bound.</exception>
    internal static int ParseNonNegativeInt(
        string value,
        string? optionName = null,
        int? maxInclusive = null)
    {
        // Limit record: value = ArrayIndexIdRange.MaxCount (read from the runtime) · threat: none beyond a clear message; the
        // frame and packet ids are array indexes, so a count above this could never be reached and would only fail late with a
        // capacity error · constant: a fact of the id type, not a host or risk setting · posture: not applicable (no tuning knob).
        // An exceeded bound is an expected user error and names the option and the maximum.
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
    /// <exception cref="ArgumentException">
    /// <paramref name="miB"/> is above 2047 MiB, the largest whole MiB count that fits an <see cref="int"/> of bytes.
    /// </exception>
    internal static int MiBToCacheBudgetBytes(long miB, string optionName)
    {
        // The cache budget is an int number of bytes, so the largest accepted quantity is int.MaxValue / 1 MiB = 2047 MiB (integer
        // division). Checking in MiB before multiplying keeps the product from overflowing; an out-of-range value is refused with
        // the maximum named, never clamped silently.
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
    /// <exception cref="ArgumentException"><paramref name="miB"/> is so large that the byte limit would overflow <see cref="long"/>.</exception>
    internal static long MiBToSplitSizeBytes(long miB, string optionName)
    {
        // Zero means "no size split" (see SplitOutputManager) and needs no conversion.
        if (miB == 0)
        {
            return 0;
        }

        // Same overflow guard as the cache budget, with long bytes; the maximum is far above any real disk, so it only stops a typo.
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

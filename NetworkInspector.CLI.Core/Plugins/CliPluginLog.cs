// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI;

/// <summary>
/// The log of one plugin in the command line: every call writes one line <c>&lt;level&gt; &lt;plugin&gt;: &lt;text&gt;</c> to the error
/// writer and appends it to the log file; control characters in the text become spaces (see <see cref="CliLogFile.SingleLine"/>).
/// An exception adds its type and message to the line, never its stack trace. Thread
/// safety: plugins may log from any thread, so every write locks the error writer itself (the session locks the same
/// object), because a text writer is not thread-safe; the log file has its own lock. A log call never throws for I/O reasons:
/// a failing console or log file must not fail the plugin that writes to it.
/// </summary>
internal sealed class CliPluginLog : IPluginLog
{
    #region Fields

    private readonly string _Plugin;

    private readonly TextWriter _Error;

    private readonly CliLogFile? _File;

    #endregion

    #region Lifecycle

    /// <summary>Creates the log of a plugin.</summary>
    /// <param name="plugin">Plugin name, the source of every line.</param>
    /// <param name="error">The error writer of the command.</param>
    /// <param name="file">The log file, or <see langword="null"/> when none could be opened.</param>
    internal CliPluginLog(string plugin, TextWriter error, CliLogFile? file)
    {
        _Plugin = plugin;
        _Error = error;
        _File = file;
    }

    #endregion

    #region IPluginLog

    /// <inheritdoc/>
    public void Info(string text) => _Write("Info", text);

    /// <inheritdoc/>
    public void Warning(string text) => _Write("Warning", text);

    /// <inheritdoc/>
    public void Error(string text, Exception? exception = null)
    {
        // The start and stop lifecycle already names the exception in its text; a second copy would only repeat it.
        string description = exception is null
            ? string.Empty
            : exception.GetType().Name + ": " + exception.Message;
        _Write("Error", description.Length == 0 || text.Contains(description, StringComparison.Ordinal)
            ? text
            : text + " (" + description + ")");
    }

    #endregion

    #region Private Helpers

    /// <summary>Writes one line to the error writer and the file.</summary>
    private void _Write(string level, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // The same cleaned text goes to both sinks: a line break or a terminal sequence in a plugin's text must not forge an entry.
        string clean = CliLogFile.SingleLine(text);
        try
        {
            lock (_Error)
            {
                _Error.WriteLine(level + " " + _Plugin + ": " + clean);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // A closed or broken console must not fail the plugin that logs (it may log from a thread nobody watches): the line
            // still goes to the file below.
        }

        _File?.Append(level, _Plugin, clean);
    }

    #endregion
}

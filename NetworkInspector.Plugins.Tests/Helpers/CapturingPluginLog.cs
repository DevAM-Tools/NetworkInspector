// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>One captured log call.</summary>
/// <param name="Level"><c>Info</c>, <c>Warning</c>, or <c>Error</c>.</param>
/// <param name="Text">The text.</param>
/// <param name="Exception">The exception that came with the call, if any.</param>
internal sealed record LogEntry(string Level, string Text, Exception? Exception);

/// <summary>A thread-safe <see cref="IPluginLog"/> that keeps every call, for assertions.</summary>
internal sealed class CapturingPluginLog : IPluginLog
{
    #region Fields

    private readonly Lock _Lock = new();

    private readonly List<LogEntry> _Entries = [];

    #endregion

    #region Public API

    /// <summary>A snapshot of the captured calls in call order.</summary>
    internal IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_Lock)
            {
                return [.. _Entries];
            }
        }
    }

    /// <inheritdoc/>
    public void Info(string text) => _Add("Info", text, null);

    /// <inheritdoc/>
    public void Warning(string text) => _Add("Warning", text, null);

    /// <inheritdoc/>
    public void Error(string text, Exception? exception = null) => _Add("Error", text, exception);

    #endregion

    #region Private helpers

    private void _Add(string level, string text, Exception? exception)
    {
        lock (_Lock)
        {
            _Entries.Add(new LogEntry(level, text, exception));
        }
    }

    #endregion
}

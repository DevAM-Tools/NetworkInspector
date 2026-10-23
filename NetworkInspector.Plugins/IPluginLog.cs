// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// Log of one plugin. Implementations are thread-safe. Texts must not contain secrets or personal data.
/// </summary>
public interface IPluginLog
{
    /// <summary>Writes an informational entry.</summary>
    /// <param name="text">The entry text; one line.</param>
    void Info(string text);

    /// <summary>Writes a warning entry; the plugin keeps working.</summary>
    /// <param name="text">The entry text; one line.</param>
    void Warning(string text);

    /// <summary>Writes an error entry.</summary>
    /// <param name="text">The entry text; one line.</param>
    /// <param name="exception">The exception behind the error; its type and message are appended, never its stack trace.</param>
    void Error(string text, Exception? exception = null);
}

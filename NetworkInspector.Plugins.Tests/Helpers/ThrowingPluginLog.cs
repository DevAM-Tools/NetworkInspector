// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>An <see cref="IPluginLog"/> that fails on every call, for proving that a broken log cannot break the code that reports to it. Stateless.</summary>
internal sealed class ThrowingPluginLog : IPluginLog
{
    /// <inheritdoc/>
    public void Info(string text) => throw new InvalidOperationException("log boom");

    /// <inheritdoc/>
    public void Warning(string text) => throw new InvalidOperationException("log boom");

    /// <inheritdoc/>
    public void Error(string text, Exception? exception = null) => throw new InvalidOperationException("log boom");
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>Shared failing factory for the source registrations of the failing test plugins. Stateless.</summary>
internal static class TestFailures
{
    /// <summary>A source factory that never creates a source.</summary>
    /// <param name="request">Ignored.</param>
    /// <param name="source">Always <see langword="null"/>.</param>
    /// <param name="error">Always an error.</param>
    /// <returns>Always <see langword="false"/>.</returns>
    internal static bool CannotCreateSource(SourceRequest request, [NotNullWhen(true)] out IFrameSource? source,
        [NotNullWhen(false)] out PluginError? error)
    {
        source = null;
        error = new PluginError(PluginErrorCode.Failed, "The test source creates nothing.");
        return false;
    }
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Shared disposal helpers for frame sources opened by CLI commands.
/// </summary>
internal static class CliSourceLifetime
{
    #region Public API

    /// <summary>
    /// Disposes all sources, writing a warning to stderr for each failure.
    /// If every disposal fails the aggregate is re-thrown so callers are not
    /// silently left with unreleased resources.
    /// </summary>
    internal static void DisposeSources(List<IFrameSource> sources)
    {
        List<Exception>? errors = null;
        foreach (IFrameSource source in sources)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Warning: failed to dispose source '{source.GetType().Name}': {ex.Message}");
                errors ??= [];
                errors.Add(ex);
            }
        }

        if (errors is not null && errors.Count == sources.Count && sources.Count > 0)
        {
            throw new AggregateException("All source disposals failed.", errors);
        }
    }

    #endregion
}

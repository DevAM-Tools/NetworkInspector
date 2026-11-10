// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Shared disposal helpers for frame sources opened by CLI commands. Sources are plugin code, so a source that fails to dispose
/// must not keep the others from being released. Stateless; thread-safe for distinct lists.
/// </summary>
internal static class CliSourceLifetime
{
    #region Public API

    /// <summary>
    /// Disposes all sources, writing a warning to <paramref name="error"/> for each failure.
    /// If every disposal fails the aggregate is re-thrown so callers are not
    /// silently left with unreleased resources. A partial failure stays a warning.
    /// </summary>
    /// <param name="sources">The sources the command opened (also the ones whose start failed).</param>
    /// <param name="error">Receives one warning per failed disposal.</param>
    /// <exception cref="AggregateException">Every disposal failed (and there was at least one source).</exception>
    internal static void DisposeSources(List<IFrameSource> sources, TextWriter error)
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
                // Plugin code: whatever a source throws on disposal is reported and the loop goes on with the next source.
                error.WriteLine(
                    Invariant($"Warning: failed to dispose source '{source.GetType().Name}': {ex.Message}"));
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

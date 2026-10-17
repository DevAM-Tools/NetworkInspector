// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>Reports one finish-time error through an exporter's error channel (counters and <c>ItemSkipped</c>).</summary>
/// <param name="kind">Error classification.</param>
/// <param name="message">Human-readable message.</param>
internal delegate void ExporterErrorReport(ExportErrorKind kind, string message);

/// <summary>One release step of <see cref="ExporterFinish.Run"/>: what is released (for the message) and how.</summary>
/// <param name="What">Short noun phrase for the message, e.g. <c>"payload buffer return"</c>.</param>
/// <param name="Release">The release action; null skips the step (resource never created).</param>
internal readonly record struct ExporterReleaseStep(string What, Action? Release);

/// <summary>
/// The finish sequence every exporter shares, so a fix to tolerant-mode cleanup applies to all of them at once:
/// <list type="number">
/// <item>Finalize: the format trailer and the final flush. A failure is reported
/// (<c>"{format} finalization failed: …"</c>) and not rethrown — the output may already be unusable.</item>
/// <item>Release, in the given order: pooled buffers, builders, and last the output (which closes the stream unless the
/// caller owns it). Every step runs even when an earlier one failed; each failure is reported
/// (<c>"{format} {what} failed: …"</c>, <see cref="ExportErrorKind.IoError"/>).</item>
/// <item>When any release step failed, one <see cref="AggregateException"/> (<c>"{format} exporter cleanup failed."</c>)
/// carries all release failures, thrown after every step has run.</item>
/// </list>
/// </summary>
/// <remarks>Stateless; thread-safe. Exporters call it from <c>OnFinish</c> after setting their own finished latch.</remarks>
internal static class ExporterFinish
{
    #region Public API

    /// <summary>Runs the finish sequence described on the type.</summary>
    /// <param name="format">Format name for the messages (e.g. <c>"BLF"</c>).</param>
    /// <param name="finalize">Trailer and final flush; null when the exporter has nothing to finalize.</param>
    /// <param name="report">Exporter error channel.</param>
    /// <param name="releases">Release steps in order; the output disposal comes last.</param>
    /// <exception cref="AggregateException">At least one release step failed.</exception>
    internal static void Run(
        string format, Action? finalize, ExporterErrorReport report, params ReadOnlySpan<ExporterReleaseStep> releases)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(report);

        // Finalize: a failure here must not prevent the releases below.
        try
        {
            finalize?.Invoke();
        }
        catch (Exception ex)
        {
            report(
                ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                $"{format} finalization failed: {ex.Message}");
        }

        // Release: every step runs; failures are collected and thrown together afterwards.
        List<Exception>? failures = null;
        foreach (ExporterReleaseStep step in releases)
        {
            if (step.Release is null)
            {
                continue;
            }

            try
            {
                step.Release();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
                report(ExportErrorKind.IoError, $"{format} {step.What} failed: {ex.Message}");
            }
        }

        if (failures is not null)
        {
            throw new AggregateException($"{format} exporter cleanup failed.", failures);
        }
    }

    #endregion
}

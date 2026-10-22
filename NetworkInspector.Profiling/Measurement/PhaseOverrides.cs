// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Measurement;

/// <summary>
/// Optional replacements for a scenario's warm-up and timed-phase lengths (<c>--warmup</c>, <c>--duration</c>).
/// Shorter phases make an A/B series faster; both variants of a series always get the same values.
/// </summary>
/// <param name="WarmupSeconds">Warm-up seconds, or null for the scenario's own <see cref="IProfilingScenario.WarmupDuration"/>.</param>
/// <param name="DurationSeconds">Timed seconds, or null for the scenario's own <see cref="IProfilingScenario.Duration"/>.</param>
internal readonly record struct PhaseOverrides(double? WarmupSeconds, double? DurationSeconds)
{
    #region Public API

    /// <summary>The warm-up length to use for <paramref name="scenario"/>.</summary>
    internal TimeSpan Warmup(IProfilingScenario scenario) =>
        WarmupSeconds is double seconds
            ? TimeSpan.FromSeconds(seconds)
            : scenario.WarmupDuration;

    /// <summary>The timed-phase length to use for <paramref name="scenario"/>.</summary>
    internal TimeSpan Duration(IProfilingScenario scenario) =>
        DurationSeconds is double seconds
            ? TimeSpan.FromSeconds(seconds)
            : scenario.Duration;

    #endregion
}

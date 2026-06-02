// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Represents a single profiling scenario that can be set up, executed
/// repeatedly for a fixed duration, and torn down. Add new scenarios by
/// implementing this interface and registering them in <see cref="Program"/>.
///
/// <para>
/// The runner in <see cref="Program"/> calls <see cref="Run"/> in a tight loop
/// until the configured <see cref="Duration"/> has elapsed. A warm-up phase of
/// <see cref="WarmupDuration"/> triggers the JIT before the timed phase begins.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Lifecycle contract:</b> the runner calls members in this strict order:
/// <c>Setup()</c> once, then <c>Run()</c> repeatedly during warm-up, then
/// <c>Run()</c> repeatedly during the timed phase, then <c>Cleanup()</c> once.
/// Default property values are evaluated once at the point of registration; they
/// must be stable for the lifetime of the object.</para>
/// <para><b>Throughput coupling:</b> when <see cref="WorkUnitsPerIteration"/> is
/// greater than zero, <see cref="WorkUnitName"/> must be a non-empty string.
/// Implementations that override <see cref="WorkUnitsPerIteration"/> must also
/// override <see cref="WorkUnitName"/> to return a meaningful label.</para>
/// </remarks>
internal interface IProfilingScenario
{
    /// <summary>Short identifier used for command-line filtering (e.g., "packet-parsing").</summary>
    string Name
    {
        get;
    }

    /// <summary>Human-readable description printed before the scenario runs.</summary>
    string Description
    {
        get;
    }

    /// <summary>
    /// Duration of the warm-up phase. <see cref="Run"/> is called repeatedly
    /// for this long so the JIT has compiled all hot paths before profiling begins.
    /// Default: 2 seconds.
    /// </summary>
    /// <remarks>Override to shorten warm-up for fast-starting scenarios or lengthen it
    /// for scenarios with multi-tier JIT compilation requirements. Value must be positive
    /// and is read once before <see cref="Setup"/> is called.</remarks>
    TimeSpan WarmupDuration => TimeSpan.FromSeconds(2);

    /// <summary>
    /// Duration of the timed profiling phase. <see cref="Run"/> is called repeatedly
    /// until this time has elapsed. Default: 7 seconds.
    /// </summary>
    /// <remarks>Override to extend for scenarios that require longer steady-state
    /// observation windows. Value must be positive and is read once before
    /// <see cref="Setup"/> is called.</remarks>
    TimeSpan Duration => TimeSpan.FromSeconds(7);

    /// <summary>
    /// Called once before any warm-up or timed iterations.
    /// Allocate and initialise all long-lived resources here so they are not
    /// charged to the profiling hot path.
    /// </summary>
    void Setup();

    /// <summary>
    /// Number of work units (packets, frames, evaluations, …) processed per single
    /// <see cref="Run"/> call. The runner multiplies this by the number of
    /// completed iterations to compute throughput metrics (e.g. kpps).
    /// Default: 0 (no throughput metric displayed).
    /// </summary>
    /// <remarks>
    /// When overriding to a value greater than zero, <see cref="WorkUnitName"/> must
    /// also be overridden to return a non-empty, meaningful label. The value is
    /// expected to be constant for the lifetime of the scenario object.
    /// </remarks>
    long WorkUnitsPerIteration => 0;

    /// <summary>
    /// Human-readable name for the work unit counted by
    /// <see cref="WorkUnitsPerIteration"/> (e.g. "packets", "frames", "evaluations").
    /// Only used when <see cref="WorkUnitsPerIteration"/> is greater than zero.
    /// </summary>
    /// <remarks>
    /// Must not be empty when <see cref="WorkUnitsPerIteration"/> is greater than zero.
    /// The default value <c>"items"</c> is a safe placeholder; override in tandem with
    /// <see cref="WorkUnitsPerIteration"/> to provide a domain-specific label.
    /// </remarks>
    string WorkUnitName => "items";

    /// <summary>
    /// The hot path: called repeatedly during both warm-up and timed phases.
    /// Keep this method as close to pure work as possible so the profiler captures
    /// meaningful call trees.
    /// </summary>
    void Run();

    /// <summary>
    /// Called once after all iterations. Release resources allocated in <see cref="Setup"/>.
    /// </summary>
    void Cleanup();
}

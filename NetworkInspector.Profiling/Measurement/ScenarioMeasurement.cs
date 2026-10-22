// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Measurement;

/// <summary>
/// Result of one timed scenario run as written to and read from an A/B CSV file.
/// </summary>
/// <param name="Scenario">Scenario name (no commas).</param>
/// <param name="WorkUnitName">Work unit label, or <c>iterations</c> when the scenario has no work units.</param>
/// <param name="Iterations">Completed <see cref="IProfilingScenario.Run"/> calls in the timed phase.</param>
/// <param name="WorkUnits">Work units processed in the timed phase.</param>
/// <param name="NetSeconds">Net <see cref="IProfilingScenario.Run"/> time in seconds.</param>
/// <param name="Throughput">Work units per net second (higher is better).</param>
/// <param name="AllocBytesPerUnit">Bytes allocated inside <see cref="IProfilingScenario.Run"/> per work unit.</param>
/// <param name="Gen0">Gen-0 collections during the timed phase.</param>
/// <param name="Gen1">Gen-1 collections during the timed phase.</param>
/// <param name="Gen2">Gen-2 collections during the timed phase.</param>
/// <param name="PausePercent">GC pause time percentage reported by the runtime after the timed phase.</param>
/// <param name="RetainedBytesPerUnit">
/// Retained managed bytes per work unit reported by the scenario (<see cref="IProfilingScenario.RetainedBytesPerUnit"/>),
/// or <see cref="double.NaN"/> when the scenario does not measure retained memory.
/// </param>
internal readonly record struct ScenarioMeasurement(
    string Scenario,
    string WorkUnitName,
    long Iterations,
    long WorkUnits,
    double NetSeconds,
    double Throughput,
    double AllocBytesPerUnit,
    int Gen0,
    int Gen1,
    int Gen2,
    double PausePercent,
    double RetainedBytesPerUnit);

/// <summary>One CSV row: the variant (<c>A</c> baseline, <c>B</c> candidate), the run number, and the measurement.</summary>
/// <param name="Variant"><c>A</c> or <c>B</c>.</param>
/// <param name="Run">1-based run number within the A/B sequence.</param>
/// <param name="Measurement">The measured values.</param>
internal readonly record struct MeasurementRow(char Variant, int Run, ScenarioMeasurement Measurement);

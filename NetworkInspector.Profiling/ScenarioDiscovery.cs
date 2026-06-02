// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling;

/// <summary>
/// Discovers all <see cref="IProfilingScenario"/> implementations in the current assembly
/// that expose a parameterless constructor, instantiates them, and returns them ordered
/// by <see cref="IProfilingScenario.Name"/>.
///
/// <para>
/// This lets new scenarios be added without editing <c>Program.cs</c>. Scenarios that
/// require constructor arguments (e.g. a <c>bool materialize</c> flag) are not
/// discoverable and must be registered manually.
/// </para>
/// </summary>
internal static class ScenarioDiscovery
{
    /// <summary>
    /// Returns all auto-discoverable <see cref="IProfilingScenario"/> instances, sorted by
    /// <see cref="IProfilingScenario.Name"/> using ordinal comparison.
    /// </summary>
    internal static IProfilingScenario[] Discover()
    {
        return Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t =>
                t.IsClass
                && !t.IsAbstract
                && typeof(IProfilingScenario).IsAssignableFrom(t)
                && t.GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null) is not null)
            .Select(t => (IProfilingScenario)Activator.CreateInstance(
                type: t,
                bindingAttr: BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                args: null,
                culture: null)!)
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToArray();
    }
}

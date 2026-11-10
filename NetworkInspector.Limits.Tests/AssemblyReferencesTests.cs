// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Tests;

/// <summary>The assembly boundary of the limits library: what it references and to whom it shows its internals.</summary>
internal sealed class AssemblyReferencesTests
{
    #region Referenced assemblies

    [Test]
    public async Task TheLimitsAssemblyReferencesOnlyTheRuntime()
    {
        string[] names = [.. typeof(LimitValue).Assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty)];

        string[] foreign = [.. names.Where(name => !_IsRuntimeAssembly(name))];

        await Assert.That(names.Any(name => name.StartsWith("NetworkInspector", StringComparison.Ordinal))).IsFalse();
        await Assert.That(foreign).IsEmpty();
    }

    /// <summary>Whether an assembly name belongs to the .NET runtime libraries.</summary>
    /// <param name="name">The simple assembly name.</param>
    /// <returns><see langword="true"/> for <c>System</c>, <c>System.*</c>, <c>netstandard</c>, and <c>mscorlib</c>.</returns>
    private static bool _IsRuntimeAssembly(string name) =>
        name == "System"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || name == "netstandard"
        || name == "mscorlib";

    #endregion

    #region Internals

    [Test]
    public async Task TheLimitsAssemblyShowsItsInternalsToItsOwnTestProjectOnly()
    {
        string[] friends = [.. typeof(LimitValue).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName)];

        await Assert.That(friends).IsEquivalentTo(["NetworkInspector.Limits.Tests"]);
    }

    #endregion
}

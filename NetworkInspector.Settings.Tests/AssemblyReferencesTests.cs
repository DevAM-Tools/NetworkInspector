// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>The assembly boundary of the settings library: what it references and to whom it shows its internals.</summary>
internal sealed class AssemblyReferencesTests
{
    #region Referenced assemblies

    [Test]
    public async Task TheSettingsAssemblyReferencesOnlyTheRuntimeTheInvariantStringHelperAndTheLimitsLibrary()
    {
        string[] names = [.. typeof(SettingsManager).Assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty)];

        string[] foreign = [.. names.Where(name => !_IsRuntimeAssembly(name) && name is not ("InvariantStringHelper" or "NetworkInspector.Limits"))];

        await Assert.That(foreign).IsEmpty();
        await Assert.That(names).Contains("NetworkInspector.Limits");
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
    public async Task TheSettingsAssemblyShowsItsInternalsToItsOwnTestProjectOnly()
    {
        string[] friends = [.. typeof(SettingsManager).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName)];

        await Assert.That(friends).IsEquivalentTo(["NetworkInspector.Settings.Tests"]);
    }

    #endregion

    #region Names the generated code relies on

    [Test]
    public async Task TheTypesThatGeneratedProtocolCodeNamesLiveInTheNamespaceTheGeneratorWrites()
    {
        // Sync: the generator of protocol code names these two types by their full names (the constants of its source); a move of
        // either type needs the generator and its test changed in the same step.
        await Assert.That(typeof(SettingsRegistrar).FullName).IsEqualTo("NetworkInspector.Settings.SettingsRegistrar");
        await Assert.That(typeof(EnumSettingMetadata).FullName).IsEqualTo("NetworkInspector.Settings.EnumSettingMetadata");
    }

    #endregion
}

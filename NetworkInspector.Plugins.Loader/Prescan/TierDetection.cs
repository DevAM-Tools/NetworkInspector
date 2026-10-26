// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The one table of plugin entry interfaces, used by the pre-scan (metadata) and for built-in instances (reflection). An
/// entry type lists the interface of its tier in its own interface table; the C# compiler writes the whole interface closure
/// of a class's own declaration there, so an <c>IAppPlugin</c> class also lists <c>IHostPlugin</c> and <c>IPlugin</c> and the
/// most derived match decides the tier. A class that only inherits the interface from a base class does not list it and is not
/// an entry type (a documented pitfall). Stateless; thread-safe.
/// </summary>
internal static class TierDetection
{
    #region Types

    /// <summary>One entry interface of a tier: where it is declared and which tier it stands for.</summary>
    /// <param name="Namespace">Namespace of the interface.</param>
    /// <param name="Name">Simple name of the interface.</param>
    /// <param name="AssemblyName">Simple name of the assembly that declares the interface.</param>
    /// <param name="Tier">The tier that the interface stands for.</param>
    private readonly record struct EntryInterface(string Namespace, string Name, string AssemblyName, PluginTier Tier);

    #endregion

    #region Fields

    // The contract interface of each tier, with the assembly that declares it (the host and app contracts are named here as data
    // because the loader cannot reference those assemblies). Listed in ascending tier order for readability; the loops below
    // keep the highest matching tier whatever the order. An array and plain loops: three entries, no lookup structure needed.
    private static readonly EntryInterface[] _Interfaces =
    [
        new("NetworkInspector.Plugins", "IPlugin", "NetworkInspector.Plugins", PluginTier.Core),
        new("NetworkInspector.Host", "IHostPlugin", "NetworkInspector.Host.Abstractions", PluginTier.Host),
        new("NetworkInspector.App", "IAppPlugin", "NetworkInspector.App.Plugins", PluginTier.App),
    ];

    #endregion

    #region Internal API

    /// <summary>
    /// Finds the tier of a type definition from the interfaces in its own interface table. An interface counts only when it
    /// is a type reference into the matching contract assembly, compared by namespace and name without allocating. A type
    /// definition or type specification as interface never matches, so a plugin cannot define its own <c>IPlugin</c>.
    /// </summary>
    /// <param name="reader">Metadata of the assembly.</param>
    /// <param name="type">The type definition to inspect.</param>
    /// <returns>The most derived matching tier, or <see langword="null"/> when no entry interface is listed.</returns>
    internal static PluginTier? FromInterfaces(MetadataReader reader, TypeDefinition type)
    {
        // This runs for every non-abstract class of every inspected assembly, so it compares metadata string handles with
        // MetadataReader.StringComparer, which does not allocate a string per name; only a reference into one of the contract
        // assemblies can match, which also means a plugin cannot declare its own IPlugin to get picked up.
        PluginTier? best = null;
        foreach (InterfaceImplementationHandle handle in type.GetInterfaceImplementations())
        {
            // Only a type reference counts (an interface from another assembly). A type definition or a type specification (a
            // generic instantiation) is never a contract interface.
            EntityHandle interfaceHandle = reader.GetInterfaceImplementation(handle).Interface;
            if (interfaceHandle.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            // The reference must be resolved by an assembly reference; a nested type (resolved by another type) or a module
            // reference is not an entry interface.
            TypeReference reference = reader.GetTypeReference((TypeReferenceHandle)interfaceHandle);
            if (reference.ResolutionScope.Kind != HandleKind.AssemblyReference)
            {
                continue;
            }

            // Name, namespace, and declaring assembly must all match; the most derived tier among the listed interfaces wins,
            // because the compiler lists the whole interface closure of the class.
            AssemblyReference scope = reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope);
            foreach (EntryInterface candidate in _Interfaces)
            {
                if (reader.StringComparer.Equals(reference.Name, candidate.Name)
                    && reader.StringComparer.Equals(reference.Namespace, candidate.Namespace)
                    && reader.StringComparer.Equals(scope.Name, candidate.AssemblyName)
                    && (best is null || candidate.Tier > best))
                {
                    best = candidate.Tier;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The same table over a loaded type, used for built-in plugin instances. Unlike the metadata form, reflection also
    /// reports interfaces that the type inherits from a base class.
    /// </summary>
    /// <param name="type">The plugin type.</param>
    /// <returns>The most derived matching tier, or <see langword="null"/> when the type implements no entry interface.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    internal static PluginTier? FromType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // Loaded interfaces are compared by their strings (allocation is fine here: it runs once per built-in plugin).
        PluginTier? best = null;
        foreach (Type implemented in type.GetInterfaces())
        {
            foreach (EntryInterface candidate in _Interfaces)
            {
                if (implemented.Namespace == candidate.Namespace
                    && implemented.Name == candidate.Name
                    && implemented.Assembly.GetName().Name == candidate.AssemblyName
                    && (best is null || candidate.Tier > best))
                {
                    best = candidate.Tier;
                }
            }
        }

        return best;
    }

    #endregion
}

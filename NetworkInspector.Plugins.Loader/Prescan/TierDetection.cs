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

    private readonly record struct EntryInterface(string Namespace, string Name, string AssemblyName, PluginTier Tier);

    #endregion

    #region Fields

    // Ascending tier order; the loop keeps the highest match.
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
        PluginTier? best = null;
        foreach (InterfaceImplementationHandle handle in type.GetInterfaceImplementations())
        {
            EntityHandle interfaceHandle = reader.GetInterfaceImplementation(handle).Interface;
            if (interfaceHandle.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            TypeReference reference = reader.GetTypeReference((TypeReferenceHandle)interfaceHandle);
            if (reference.ResolutionScope.Kind != HandleKind.AssemblyReference)
            {
                continue;
            }

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

    /// <summary>The same table over a loaded type, used for built-in plugin instances.</summary>
    /// <param name="type">The plugin type.</param>
    /// <returns>The most derived matching tier, or <see langword="null"/> when the type implements no entry interface.</returns>
    internal static PluginTier? FromType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
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

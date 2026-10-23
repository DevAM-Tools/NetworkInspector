// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// Creates plugin objects from entry type names and describes failures in one line. Every failure of third-party code — a
/// missing dependency, a throwing static or instance constructor — becomes a reason text, never an exception for the
/// caller. Stateless; thread-safe.
/// </summary>
internal static class PluginActivator
{
    #region Internal API

    /// <summary>Creates the plugin object of one entry type.</summary>
    /// <param name="assembly">The loaded plugin assembly.</param>
    /// <param name="entryTypeName">Full name of the entry type, as found by the pre-scan.</param>
    /// <param name="plugin">The new plugin object; <see langword="null"/> on failure.</param>
    /// <param name="reason">Why no plugin was created; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="plugin"/> is set.</returns>
    internal static bool TryCreate(Assembly assembly, string entryTypeName, [NotNullWhen(true)] out IPlugin? plugin,
        [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrEmpty(entryTypeName);

        plugin = null;
        ConstructorInfo? constructor;
        try
        {
            if (!_TryFindConstructor(assembly, entryTypeName, out constructor, out reason))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Resolving the type touches its base types and interfaces, which may live in a dependency that is missing (a load
            // failure), and reflection over foreign code can fail in other ways too: both are failures of this plugin only.
            reason = "entry type could not be loaded: " + Describe(ex);
            return false;
        }

        try
        {
            plugin = (IPlugin)constructor.Invoke(null);
            reason = null;
            return true;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // The reflection wrapper says nothing; the inner exception is the plugin's own failure.
            Exception inner = ex.InnerException;
            reason = inner is TypeInitializationException
                ? "static constructor threw: " + Describe(inner.InnerException ?? inner)
                : "constructor threw: " + Describe(inner);
        }
        catch (TypeInitializationException ex)
        {
            reason = "static constructor threw: " + Describe(ex.InnerException ?? ex);
        }
        catch (Exception ex) when (_IsLoadFailure(ex))
        {
            // A dependency of the constructor's body is missing or unreadable (reported by the JIT, not wrapped).
            reason = "constructor could not run: " + Describe(ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Anything else the reflection call reports (a wrapper without inner exception, an access problem): it is still a
            // failure of this plugin and must not end the load of the others.
            reason = "constructor could not run: " + Describe(ex);
        }

        plugin = null;
        return false;
    }

    /// <summary>One-line description of an exception: type name and message.</summary>
    /// <param name="exception">The exception to describe.</param>
    /// <returns><c>TypeName: message</c>.</returns>
    internal static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Invariant($"{exception.GetType().Name}: {exception.Message}");
    }

    #endregion

    #region Private helpers

    // Looks the entry type up and checks it against the loaded contract; returns the public parameterless constructor.
    private static bool _TryFindConstructor(Assembly assembly, string entryTypeName, [NotNullWhen(true)] out ConstructorInfo? constructor,
        [NotNullWhen(false)] out string? reason)
    {
        constructor = null;
        Type? type = assembly.GetType(entryTypeName, throwOnError: false);
        if (type is null)
        {
            reason = Invariant($"Entry type '{entryTypeName}' was not found in the plugin assembly.");
            return false;
        }

        // A plugin that ships its own copy of the contract fails this check: its IPlugin is another type.
        if (!typeof(IPlugin).IsAssignableFrom(type))
        {
            reason = Invariant($"Entry type '{entryTypeName}' does not implement the loaded IPlugin; the plugin ships its own NetworkInspector.Plugins.");
            return false;
        }

        constructor = type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        if (constructor is null)
        {
            reason = Invariant($"Entry type '{entryTypeName}' needs a public parameterless constructor.");
            return false;
        }

        reason = null;
        return true;
    }

    // Failures that mean "this code cannot be loaded here" rather than "this code ran and failed".
    private static bool _IsLoadFailure(Exception exception) =>
        exception is FileLoadException or FileNotFoundException or BadImageFormatException or TypeLoadException or MemberAccessException;

    #endregion
}

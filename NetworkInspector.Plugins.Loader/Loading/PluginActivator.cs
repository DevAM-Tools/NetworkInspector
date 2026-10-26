// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// Creates plugin objects from entry type names and describes failures in one line. Every failure of third-party code — a
/// missing dependency, a throwing static or instance constructor — becomes a reason text, never an exception for the
/// caller. The constructor runs on the calling thread without a time bound (only <see cref="PluginLifecycle"/> bounds plugin
/// code by time), so a constructor that never returns blocks the load. Stateless; thread-safe.
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
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="entryTypeName"/> is <see langword="null"/> or empty.</exception>
    internal static bool TryCreate(Assembly assembly, string entryTypeName, [NotNullWhen(true)] out IPlugin? plugin,
        [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrEmpty(entryTypeName);

        // First find the entry type and its constructor. This only inspects types (no plugin code runs), but it may load the
        // type's base types and interfaces, which can fail for foreign code.
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

        // Then run the constructor (plugin code, including the static constructor on first use). Reflection is used because
        // the type is only known by name from the metadata scan. Each catch below names the stage that failed, so the plugin
        // author can tell a throwing constructor from a missing dependency.
        try
        {
            plugin = (IPlugin)constructor.Invoke(null);
            reason = null;
            return true;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // The reflection wrapper says nothing; the inner exception is the plugin's own failure. A failing static
            // constructor can arrive wrapped (here) or bare (next catch), so both shapes give the same reason.
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

        // Type name (the short name, without namespace) and message only, never the stack trace: the reason is one line, and the
        // plugin log contract appends no stack trace either. The message comes from foreign code and is treated as plain text.
        // The getter is foreign code too and can throw: this method runs inside the catch blocks that contain plugin failures, so a
        // throw here would carry the plugin's failure out of the containment boundary and end the load, start, or shutdown of every
        // other plugin. The type name is always available, so the description falls back to it.
        string message;
        try
        {
            message = exception.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            message = "(the message could not be read)";
        }

        return Invariant($"{exception.GetType().Name}: {message}");
    }

    #endregion

    #region Private helpers

    /// <summary>Looks the entry type up and checks it against the loaded contract; returns the public parameterless constructor.</summary>
    /// <param name="assembly">The loaded plugin assembly.</param>
    /// <param name="entryTypeName">Full name of the entry type.</param>
    /// <param name="constructor">The constructor; <see langword="null"/> on failure.</param>
    /// <param name="reason">Why no constructor was found; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="constructor"/> is set.</returns>
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

        // A plugin that ships its own copy of the contract fails this check: its IPlugin is another type. The load context
        // normally prevents that by taking the contract from the application, so this is the guard behind that rule; the
        // metadata scan matched the interface by name only, and this check is what proves type identity.
        if (!typeof(IPlugin).IsAssignableFrom(type))
        {
            reason = Invariant($"Entry type '{entryTypeName}' does not implement the loaded IPlugin; the plugin ships its own NetworkInspector.Plugins.");
            return false;
        }

        // Only a public instance constructor without parameters, as the entry point contract of IPlugin states; a plugin with
        // another constructor shape gets the reason below instead of a reflection exception.
        constructor = type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        if (constructor is null)
        {
            reason = Invariant($"Entry type '{entryTypeName}' needs a public parameterless constructor.");
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>Failures that mean "this code cannot be loaded here" rather than "this code ran and failed".</summary>
    /// <param name="exception">The exception thrown by the reflection call.</param>
    /// <returns><see langword="true"/> for the load-failure types.</returns>
    private static bool _IsLoadFailure(Exception exception) =>
        exception is FileLoadException or FileNotFoundException or BadImageFormatException or TypeLoadException or MemberAccessException;

    #endregion
}

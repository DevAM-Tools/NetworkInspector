// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// Shared construction step of the exporter registrations: runs the Core exporter builder and maps the expected failures
/// to <see cref="PluginError"/>, so that no registration throws for a bad option value or an unwritable target. The
/// application decides existence and overwrite of the target before the call (<c>ExportTargets</c> of the loader), so
/// this step never deletes or replaces anything. Stateless; thread-safe.
/// </summary>
internal static class ExporterBuild
{
    #region Constants

    /// <summary>Option name used in errors about the output target.</summary>
    internal const string TargetOption = "target";

    #endregion

    #region Internal API

    /// <summary>
    /// Builds an exporter. The Core exporter itself is the result (it already is the <c>IFrameListener</c> or
    /// <c>IPacketListener</c> of the contract), so there is no wrapper type. Exceptions that are not listed here are bugs
    /// and propagate.
    /// </summary>
    /// <typeparam name="TListener">The listener type of the contract.</typeparam>
    /// <param name="build">Runs the builder chain of the Core exporter and returns the built exporter.</param>
    /// <param name="target">Output path, for the messages.</param>
    /// <param name="listener">The built exporter; <see langword="null"/> on failure.</param>
    /// <param name="error">Why no exporter was built; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="listener"/> is set.</returns>
    internal static bool TryBuild<TListener>(Func<TListener> build, string target, [NotNullWhen(true)] out TListener? listener,
        [NotNullWhen(false)] out PluginError? error)
        where TListener : class
    {
        ArgumentNullException.ThrowIfNull(build);

        // Catch order matters: DirectoryNotFoundException and FileNotFoundException derive from IOException, so their clause
        // must precede the IOException clause or they would be reported as unreadable instead of not found. Only these
        // expected failure types are caught; anything else is a bug in the builder or the registration and propagates.
        try
        {
            // The Core exporters open their output with the first item, so a missing folder would surface late and in the
            // middle of an export. The check turns it into the same early error for every format.
            _EnsureParentFolderExists(target);
            listener = build();
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
        {
            error = new PluginError(PluginErrorCode.FileNotFound, Invariant($"Cannot write '{target}': {exception.Message}"),
                TargetOption);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            error = new PluginError(PluginErrorCode.FileUnreadable, Invariant($"Cannot write '{target}': {exception.Message}"),
                TargetOption);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            // The Core builders reject a value they cannot write (an out-of-range option, an invalid path) with these types.
            // The registration cannot tell which option was at fault here, so no option name is attached; registrations
            // that can name the option check it themselves before calling this step.
            error = new PluginError(PluginErrorCode.InvalidOption, exception.Message);
        }

        listener = null;
        return false;
    }

    /// <summary>
    /// Builds an exporter on a host target. Nothing is opened here (the exporters open their entry with the first item), so only
    /// the builder's refusals of an option value are expected; they map to <see cref="PluginErrorCode.InvalidOption"/>. Exceptions
    /// that are not listed here are bugs and propagate.
    /// </summary>
    /// <typeparam name="TListener">The listener type of the contract.</typeparam>
    /// <param name="build">Runs the builder chain of the Core exporter, ending in <c>ToTarget</c>, and returns the built exporter.</param>
    /// <param name="listener">The built exporter; <see langword="null"/> on failure.</param>
    /// <param name="error">Why no exporter was built; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="listener"/> is set.</returns>
    internal static bool TryBuildOnTarget<TListener>(Func<TListener> build, [NotNullWhen(true)] out TListener? listener,
        [NotNullWhen(false)] out PluginError? error)
        where TListener : class
    {
        ArgumentNullException.ThrowIfNull(build);
        try
        {
            listener = build();
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            // The Core builders reject a value they cannot write (an out-of-range option) with these types. The registration cannot
            // tell which option was at fault here; registrations that can name it check it themselves before this step.
            listener = null;
            error = new PluginError(PluginErrorCode.InvalidOption, exception.Message);
            return false;
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>Throws <see cref="DirectoryNotFoundException"/> when the folder that should hold the target does not exist.</summary>
    private static void _EnsureParentFolderExists(string target)
    {
        // Without the trailing separator a directory target ("out/") would be taken for its own parent.
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)));
        if (parent is not null && !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(Invariant($"Folder '{parent}' does not exist."));
        }
    }

    #endregion
}

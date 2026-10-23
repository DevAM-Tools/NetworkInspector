// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// A built-in test plugin whose <c>Configure</c> is a delegate, so that a test states the contributions it needs in place.
/// Thread safety: the delegate runs on the loading thread.
/// </summary>
internal sealed class ScriptedPlugin : IPlugin
{
    #region Fields

    private readonly Action<IPluginBuilder> _Configure;

    #endregion

    #region Lifecycle

    /// <summary>Creates the plugin.</summary>
    /// <param name="name">Plugin name.</param>
    /// <param name="configure">What <c>Configure</c> does.</param>
    internal ScriptedPlugin(string name, Action<IPluginBuilder> configure)
    {
        Info = new PluginInfo(name, "1.0.0", "MIT", "Scripted test plugin.", []);
        _Configure = configure;
    }

    #endregion

    #region IPlugin

    /// <inheritdoc/>
    public PluginInfo Info { get; }

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder) => _Configure(builder);

    #endregion

    #region Public API

    /// <summary>A source factory that never creates a source; for registrations whose creation is not under test.</summary>
    /// <param name="request">Ignored.</param>
    /// <param name="source">Always <see langword="null"/>.</param>
    /// <param name="error">Always an error.</param>
    /// <returns>Always <see langword="false"/>.</returns>
    internal static bool NoSource(SourceRequest request, [NotNullWhen(true)] out IFrameSource? source, [NotNullWhen(false)] out PluginError? error)
    {
        source = null;
        error = new PluginError(PluginErrorCode.Failed, "No source.");
        return false;
    }

    /// <summary>An exporter factory that never creates a listener; for registrations whose creation is not under test.</summary>
    /// <param name="request">Ignored.</param>
    /// <param name="listener">Always <see langword="null"/>.</param>
    /// <param name="error">Always an error.</param>
    /// <returns>Always <see langword="false"/>.</returns>
    internal static bool NoPacketExporter(ExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        listener = null;
        error = new PluginError(PluginErrorCode.Failed, "No exporter.");
        return false;
    }

    /// <summary>A frame exporter factory that never creates a listener.</summary>
    /// <param name="request">Ignored.</param>
    /// <param name="listener">Always <see langword="null"/>.</param>
    /// <param name="error">Always an error.</param>
    /// <returns>Always <see langword="false"/>.</returns>
    internal static bool NoFrameExporter(ExportRequest request, [NotNullWhen(true)] out IFrameListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        listener = null;
        error = new PluginError(PluginErrorCode.Failed, "No exporter.");
        return false;
    }

    /// <summary>Shorthand for a source registration without options.</summary>
    /// <param name="kind">Source kind.</param>
    /// <returns>The registration.</returns>
    internal static SourceRegistration Source(string kind) => new(kind, kind, "Test source " + kind, [], false, static _ => { }, NoSource);

    /// <summary>Shorthand for a packet exporter registration without options.</summary>
    /// <param name="format">Format name.</param>
    /// <returns>The registration.</returns>
    internal static ExporterRegistration<IPacketListener> PacketExporter(string format) =>
        new(format, format, "Test format " + format, "." + format.ToLowerInvariant(), static _ => { }, NoPacketExporter);

    /// <summary>Shorthand for a frame exporter registration without options.</summary>
    /// <param name="format">Format name.</param>
    /// <returns>The registration.</returns>
    internal static ExporterRegistration<IFrameListener> FrameExporter(string format) =>
        new(format, format, "Test format " + format, "." + format.ToLowerInvariant(), static _ => { }, NoFrameExporter);

    #endregion
}

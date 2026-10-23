// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// Collects the contributions of one plugin while it is configured. Using the builder after <see cref="IPlugin.Configure"/>
/// returned (and the higher tier methods ran) throws <see cref="InvalidOperationException"/>. Every violation of a
/// registration rule throws inside the configure call, so that the plugin is marked as failed and the application continues
/// without it. Thread safety: single-threaded; called on the loading thread only.
/// </summary>
public interface IPluginBuilder
{
    /// <summary>Name of the plugin being configured.</summary>
    string PluginName { get; }

    /// <summary>
    /// Adds a registration that runs for every stack the application builds (session start, restart, reset) with the Core
    /// builder: protocols, parsers, heuristic parsers, post-parsers, reassembly, and protocol settings. The callback may run
    /// on any thread and more than once, so it must be stateless or thread-safe.
    /// </summary>
    /// <param name="register">The registration callback.</param>
    void AddStackContribution(Action<IStackBuilder> register);

    /// <summary>
    /// Registers plugin settings with the Core registrar in <see cref="SettingScope.Application"/> or
    /// <see cref="SettingScope.Profile"/>. The action runs once per settings manager of that scope and must create new
    /// <see cref="Setting"/> instances each time, because a setting binds to the first manager that registers it.
    /// </summary>
    /// <param name="scope">Application or profile scope.</param>
    /// <param name="register">Registers groups and settings.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scope"/> is <see cref="SettingScope.Session"/>
    /// (session settings belong to the host) or not a defined value.</exception>
    void AddSettings(SettingScope scope, Action<SettingsRegistrar> register);

    /// <summary>Adds a source kind; the kind is unique among all plugins (ignoring case).</summary>
    /// <param name="source">The source registration.</param>
    void AddSource(SourceRegistration source);

    /// <summary>Adds an exporter of frames (the Core <see cref="IFrameListener"/>); formats are unique across frame and packet exporters.</summary>
    /// <param name="exporter">The exporter registration.</param>
    void AddFrameExporter(ExporterRegistration<IFrameListener> exporter);

    /// <summary>Adds an exporter of parsed packets (the Core <see cref="IPacketListener"/>); formats are unique across frame and packet exporters.</summary>
    /// <param name="exporter">The exporter registration.</param>
    void AddPacketExporter(ExporterRegistration<IPacketListener> exporter);
}

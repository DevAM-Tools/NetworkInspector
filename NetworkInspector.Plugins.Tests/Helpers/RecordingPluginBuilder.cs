// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Test <see cref="IPluginBuilder"/> that stores every call, so that tests can inspect what a plugin contributed without a
/// loader. Not thread-safe: tests configure a plugin on one thread.
/// </summary>
internal sealed class RecordingPluginBuilder : IPluginBuilder
{
    #region Fields

    private int _NameReads;

    #endregion

    #region IPluginBuilder

    /// <summary>Name returned by <see cref="PluginName"/>.</summary>
    public string Name { get; init; } = "Test.Recording";

    /// <inheritdoc/>
    public string PluginName
    {
        get
        {
            _NameReads++;
            return Name;
        }
    }

    /// <summary>How often <see cref="PluginName"/> was read.</summary>
    public int NameReads => _NameReads;

    /// <summary>Stack contributions in call order.</summary>
    public List<Action<IStackBuilder>> StackContributions { get; } = [];

    /// <summary>Settings registrations (scope and action) in call order.</summary>
    public List<(SettingScope Scope, Action<SettingsRegistrar> Register)> Settings { get; } = [];

    /// <summary>Source registrations in call order.</summary>
    public List<SourceRegistration> Sources { get; } = [];

    /// <summary>Frame exporter registrations in call order.</summary>
    public List<ExporterRegistration<IFrameListener>> FrameExporters { get; } = [];

    /// <summary>Packet exporter registrations in call order.</summary>
    public List<ExporterRegistration<IPacketListener>> PacketExporters { get; } = [];

    /// <inheritdoc/>
    public void AddStackContribution(Action<IStackBuilder> register) => StackContributions.Add(register);

    /// <inheritdoc/>
    public void AddSettings(SettingScope scope, Action<SettingsRegistrar> register) => Settings.Add((scope, register));

    /// <inheritdoc/>
    public void AddSource(SourceRegistration source) => Sources.Add(source);

    /// <inheritdoc/>
    public void AddFrameExporter(ExporterRegistration<IFrameListener> exporter) => FrameExporters.Add(exporter);

    /// <inheritdoc/>
    public void AddPacketExporter(ExporterRegistration<IPacketListener> exporter) => PacketExporters.Add(exporter);

    #endregion
}

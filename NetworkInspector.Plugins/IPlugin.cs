// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// Entry point of a core-tier plugin. The entry type is a public, non-abstract, non-generic class with a public
/// parameterless constructor that lists this interface (or <c>IHostPlugin</c>, <c>IAppPlugin</c> of the higher tiers) in
/// its own interface list; the pre-scan finds it by that interface without loading the assembly.
/// <para>
/// Order: <see cref="Configure"/> for every plugin, then the higher tier methods, then the application is composed, then
/// <see cref="StartAsync"/> in load order; at exit <see cref="StopAsync"/> in reverse load order while sessions still
/// exist. A throw from any of these members marks only that plugin as failed; the application continues.
/// </para>
/// <para>
/// Thread safety: the application calls the members sequentially; a plugin that keeps state for later callbacks (stack
/// contributions run for every stack, on any thread; context events) makes that state thread-safe itself.
/// </para>
/// </summary>
public interface IPlugin
{
    /// <summary>Name, version, license, description, and dependencies of the plugin.</summary>
    PluginInfo Info { get; }

    /// <summary>
    /// Declares every core-tier contribution of the plugin. The builder is valid only while the plugin is configured;
    /// using it afterwards throws <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <param name="builder">Collects the contributions of this plugin.</param>
    void Configure(IPluginBuilder builder);

    /// <summary>
    /// Starts the plugin once the application is composed. Bounded by the application setting
    /// <c>plugins.startTimeoutSeconds</c>; a throw or a timeout marks the plugin as failed and refuses its contributions.
    /// Default: nothing to do.
    /// </summary>
    /// <param name="context">What the application offers a started plugin; valid until <see cref="StopAsync"/> returns.</param>
    /// <param name="cancellationToken">Cancelled at the start timeout or when the application shuts down.</param>
    ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// Stops the plugin while sessions still exist (remove own listeners and value caches here). Bounded by
    /// <c>plugins.stopTimeoutSeconds</c>; the token is cancelled at the timeout. Default: nothing to do.
    /// </summary>
    /// <param name="cancellationToken">Cancelled at the stop timeout.</param>
    ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

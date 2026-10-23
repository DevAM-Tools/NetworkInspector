// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests.TestPlugins;

/// <summary>A plugin whose <c>StartAsync</c> throws; it also registers a source so that the refusal can be observed.</summary>
public sealed class StartThrowsPlugin : IPlugin
{
    /// <inheritdoc/>
    public PluginInfo Info { get; } = new("Test.StartThrows", "1.0.0", "MIT", "Throws in StartAsync.", []);

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder) =>
        builder.AddSource(new SourceRegistration("TestStartThrows", "Test", "Refused after the failed start.", [], false, static _ => { },
            TestFailures.CannotCreateSource));

    /// <inheritdoc/>
    public ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("start boom");
}

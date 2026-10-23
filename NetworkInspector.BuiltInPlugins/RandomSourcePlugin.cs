// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>Registers the synthetic <c>Random</c> frame source used for tests, demos, and benchmarks. Stateless; thread-safe.</summary>
public sealed class RandomSourcePlugin : IPlugin
{
    /// <inheritdoc/>
    public PluginInfo Info { get; } = BuiltInPluginInfo.Create("NetworkInspector.RandomSource",
        "Generates reproducible synthetic frames (Ethernet, IP, UDP, TCP streams, CAN, CAN FD).");

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddSource(RandomSourceRegistration.Create());
    }
}

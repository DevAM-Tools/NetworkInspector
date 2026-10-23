// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// Registers the capture file sources: Vector BLF, PCAPNG, legacy PCAP, and Vector ASC. Stateless; thread-safe.
/// </summary>
public sealed class FileSourcesPlugin : IPlugin
{
    /// <inheritdoc/>
    public PluginInfo Info { get; } = BuiltInPluginInfo.Create("NetworkInspector.FileSources",
        "Reads Vector BLF, PCAPNG, legacy PCAP, and Vector ASC capture files.");

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddSource(BlfSourceRegistration.Create());
        builder.AddSource(PcapngSourceRegistration.Create());
        builder.AddSource(PcapSourceRegistration.Create());
        builder.AddSource(AscSourceRegistration.Create());
    }
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>PCAP</c> source kind for legacy libpcap files. It exists as a kind of its own so that dialogs and the file
/// extension <c>.pcap</c> find it; the Core <see cref="PcapngSource"/> detects the legacy format by its magic number, so
/// option list and factory are those of <see cref="PcapngSourceRegistration"/>. Stateless; the factory may run concurrently.
/// </summary>
internal static class PcapSourceRegistration
{
    #region Constants

    /// <summary>Kind name of the registration.</summary>
    internal const string Kind = "PCAP";

    #endregion

    #region Internal API

    /// <summary>Creates the registration the file sources plugin adds with <see cref="IPluginBuilder.AddSource"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static SourceRegistration Create() => new(
        Kind: Kind,
        UiName: "PCAP (legacy)",
        Description: "Reads legacy libpcap capture files.",
        FileExtensions: [".pcap"],
        AllowsMultipleFiles: true,
        RegisterOptions: PcapngSourceRegistration.RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>Factory of the PCAP kind; the messages name the kind the caller asked for.</summary>
    private static bool _TryCreate(SourceRequest request, [NotNullWhen(true)] out IFrameSource? source,
        [NotNullWhen(false)] out PluginError? error) =>
        PcapngSourceRegistration.TryCreate(request, Kind, out source, out error);

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>PCAPNG</c> source kind. The registration returns the Core <see cref="PcapngSource"/> itself and maps its option
/// settings onto <see cref="PcapngSourceOptions"/>. The legacy <c>PCAP</c> kind (<see cref="PcapSourceRegistration"/>)
/// reuses the option list and the factory, because the same reader detects both file formats. Stateless; the factory may
/// run concurrently.
/// </summary>
internal static class PcapngSourceRegistration
{
    #region Constants

    /// <summary>Kind name of the registration.</summary>
    internal const string Kind = "PCAPNG";

    #endregion

    #region Internal API

    /// <summary>Creates the registration the file sources plugin adds with <see cref="IPluginBuilder.AddSource"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static SourceRegistration Create() => new(
        Kind: Kind,
        UiName: "PCAPNG",
        Description: "Reads PCAP Next Generation capture files.",
        FileExtensions: [".pcapng"],
        AllowsMultipleFiles: true,
        RegisterOptions: RegisterOptions,
        Create: _TryCreate);

    /// <summary>Registers the options shared by the PCAPNG and PCAP kinds with the defaults of <see cref="PcapngSourceOptions"/>.</summary>
    /// <param name="options">The registrar over the fresh option manager of one request.</param>
    internal static void RegisterOptions(SettingsRegistrar options)
    {
        FileSourceOptions.RegisterUiName(options);
        FileSourceOptions.RegisterScanMode(options);
        FileSourceOptions.RegisterErrorTolerance(options);
        FileSourceOptions.RegisterPreloadBudget(options, new PcapngSourceOptions().PreloadBudget ?? 0);
        FileSourceOptions.RegisterFrameSlab(options);
    }

    /// <summary>Opens the file of the request as a Core PCAPNG source (which also reads legacy PCAP files).</summary>
    /// <param name="request">The request.</param>
    /// <param name="kindName">Kind name for the error messages (<c>PCAPNG</c> or <c>PCAP</c>).</param>
    /// <param name="source">The opened source; <see langword="null"/> on failure.</param>
    /// <param name="error">Why no source was created; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="source"/> is set.</returns>
    internal static bool TryCreate(SourceRequest request, string kindName, [NotNullWhen(true)] out IFrameSource? source,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validate the file and the options that Core settings cannot check on their own.
        source = null;
        ReadOnlySettingsManagerView values = request.Options;
        if (!FileSourceOptions.TryGetSinglePath(request.Paths, out string? path, out error)
            || !FileSourceOptions.TryGetFrameSlab(values, out int slab, out error))
        {
            return false;
        }

        PcapngSourceOptions options = new()
        {
            UiName = FileSourceOptions.GetUiName(values),
            ScanMode = FileSourceOptions.GetScanMode(values),
            ErrorTolerance = FileSourceOptions.GetErrorTolerance(values),
            PreloadBudget = OptionValues.GetI64(values, FileSourceOptions.PreloadBudgetOption),
            FrameSlabSize = slab,
        };

        // The Core source itself is the result; there is no wrapper type.
        return FileSourceOptions.TryOpen(() => PcapngSource.Open(path, options), path, kindName, out source, out error);
    }

    #endregion

    #region Private Helpers

    /// <summary>Factory of the PCAPNG kind.</summary>
    private static bool _TryCreate(SourceRequest request, [NotNullWhen(true)] out IFrameSource? source,
        [NotNullWhen(false)] out PluginError? error) =>
        TryCreate(request, Kind, out source, out error);

    #endregion
}

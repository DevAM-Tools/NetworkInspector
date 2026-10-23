// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace Sample.Protocol;

/// <summary>
/// The <c>SampleGen</c> source kind: generated frames, no files. Stateless; <see cref="TryCreate"/> may run concurrently.
/// The registration shows the three parts of a source: metadata, option settings (Core settings with Core names, types,
/// defaults, and bounds), and the factory that returns a Core <see cref="IFrameSource"/>.
/// </summary>
public static class SampleSource
{
    #region Constants

    /// <summary>Kind name of the registration.</summary>
    public const string Kind = "SampleGen";

    #endregion

    #region Public API

    /// <summary>The registration the plugin adds with <see cref="IPluginBuilder.AddSource"/>.</summary>
    public static SourceRegistration Registration { get; } = new(
        Kind: Kind,
        UiName: "Sample generator",
        Description: "Generates Ethernet/IPv4/UDP or TCP frames carrying the NIS1 sample payload.",
        FileExtensions: [],
        AllowsMultipleFiles: false,
        RegisterOptions: RegisterOptions,
        Create: TryCreate);

    /// <summary>
    /// Registers the option settings: Core names, types, defaults, and bounds. Core validates every value, so
    /// <see cref="TryCreate"/> only reads values that are already inside their bounds.
    /// </summary>
    /// <param name="options">The registrar over the fresh option manager of one request.</param>
    public static void RegisterOptions(SettingsRegistrar options)
    {
        options.RegisterI64Setting("count", "Frames", "", 100, min: 0, max: int.MaxValue,
            description: "Number of frames to generate.");
        options.RegisterEnumSetting("transport", "Transport", "", 0, EnumSettingMetadata.FromPairs([("udp", 0), ("tcp", 1)]),
            description: "udp: the sample protocol by its port 40000; tcp: through the TCP heuristic.");
        options.RegisterI64Setting("port", "Destination port", "", SampleWire.DefaultPort, min: 1, max: 65535,
            description: "UDP or TCP destination port.");
        options.RegisterU64Setting("startCounter", "First counter", "", 0, description: "Counter of the first frame.");
    }

    /// <summary>Creates the source; bounds and enum names were checked by the option settings.</summary>
    /// <param name="request">The request; the generator reads no files, so <c>Paths</c> must be empty.</param>
    /// <param name="source">The created, not yet started source; <see langword="null"/> on failure.</param>
    /// <param name="error">Why no source was created; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="source"/> is set.</returns>
    public static bool TryCreate(SourceRequest request, [NotNullWhen(true)] out IFrameSource? source,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A file path is the one input this kind cannot use; refuse it with the option name "paths".
        source = null;
        if (request.Paths.Count != 0)
        {
            error = new PluginError(PluginErrorCode.InvalidOption, "The sample generator reads no files.", "paths");
            return false;
        }

        // Option values are inside their Core bounds, so the narrowing casts below cannot lose information.
        ReadOnlySettingsManagerView options = request.Options;
        long count = options.GetI64Setting("count") ?? 100;
        bool tcp = options.GetEnumSetting("transport")?.Name == "tcp";
        long port = options.GetI64Setting("port") ?? SampleWire.DefaultPort;
        ulong startCounter = options.GetU64Setting("startCounter") ?? 0;

        source = new SampleFrameSource((int)count, tcp, (ushort)port, startCounter);
        error = null;
        return true;
    }

    #endregion
}

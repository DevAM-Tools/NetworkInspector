// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>Random</c> source kind: reproducible synthetic frames without a file. The registration returns the Core
/// <see cref="RandomFrameSource"/> itself and maps its option settings onto <see cref="RandomSourceOptions"/>; the
/// defaults match the command line (1000 frames, seed 42, UDP over IPv4). Stateless; the factory may run concurrently.
/// </summary>
internal static class RandomSourceRegistration
{
    #region Constants

    /// <summary>Kind name of the registration.</summary>
    internal const string Kind = "Random";

    /// <summary>Option name: number of frames, 0 for an endless source.</summary>
    private const string _CountOption = "count";

    /// <summary>Option name: seed of the generator.</summary>
    private const string _SeedOption = "seed";

    /// <summary>Option name: kind of frames.</summary>
    private const string _ModeOption = "mode";

    /// <summary>Option name: frames per second of the generated timestamps.</summary>
    private const string _RateOption = "framesPerSecond";

    /// <summary>Option name: smallest frame size.</summary>
    private const string _MinSizeOption = "minFrameSize";

    /// <summary>Option name: largest frame size.</summary>
    private const string _MaxSizeOption = "maxFrameSize";

    /// <summary>Nanoseconds per second, to turn the rate into the timestamp interval.</summary>
    private const double _NanosecondsPerSecond = 1e9;

    #endregion

    #region Fields

    /// <summary>
    /// Mode names in the order of their numbers: the number is the stored value of the enumeration setting, the name is the
    /// text people type. <c>udp4</c> (index 4) is the default.
    /// </summary>
    private static readonly (string Name, ulong Value, RandomFrameMode Mode)[] _Modes =
    [
        ("fullrandom", 0, RandomFrameMode.FullRandom),
        ("ethernet", 1, RandomFrameMode.Ethernet),
        ("ipv4", 2, RandomFrameMode.IPv4),
        ("ipv6", 3, RandomFrameMode.IPv6),
        ("udp4", 4, RandomFrameMode.UdpIPv4),
        ("udp6", 5, RandomFrameMode.UdpIPv6),
        ("can", 6, RandomFrameMode.Can),
        ("canfd", 7, RandomFrameMode.CanFd),
        ("tcp4", 8, RandomFrameMode.TcpIPv4),
        ("tcp6", 9, RandomFrameMode.TcpIPv6),
        ("tcpstream4", 10, RandomFrameMode.TcpStreamIPv4),
        ("tcpstream6", 11, RandomFrameMode.TcpStreamIPv6),
    ];

    /// <summary>Mode lookup by registered name; built once because the table never changes.</summary>
    private static readonly FrozenDictionary<string, RandomFrameMode> _ModeByName =
        _Modes.ToFrozenDictionary(static entry => entry.Name, static entry => entry.Mode, StringComparer.Ordinal);

    #endregion

    #region Internal API

    /// <summary>Creates the registration the random source plugin adds with <see cref="IPluginBuilder.AddSource"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static SourceRegistration Create() => new(
        Kind: Kind,
        UiName: "Random synthetic",
        Description: "Generates reproducible synthetic frames: Ethernet, IP, UDP, TCP streams, CAN, and CAN FD.",
        FileExtensions: [],
        AllowsMultipleFiles: false,
        RegisterOptions: _RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>Registers the options of the random source.</summary>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
        options.RegisterI64Setting(_CountOption, "Frames", "", 1000, min: 0, max: ArrayIndexIdRange.MaxCount,
            description: "Number of frames to generate; 0 generates frames until the source is stopped.");
        options.RegisterU64Setting(_SeedOption, "Seed", "", 42,
            description: "Seed of the generator; the same seed gives the same frames.");
        options.RegisterEnumSetting(_ModeOption, "Frame mode", "", 4, EnumSettingMetadata.FromPairs(_ModePairs()),
            description: "Kind of frames: fullrandom, ethernet, ipv4, ipv6, udp4, udp6, can, canfd, tcp4, tcp6, tcpstream4, or tcpstream6.");
        options.RegisterF64Setting(_RateOption, "Frames per second", "", 1000, min: 0.001, max: 1_000_000_000,
            description: "Rate of the generated timestamps in frames per second.");
        options.RegisterI64Setting(_MinSizeOption, "Smallest frame", "", 64, min: 1, max: int.MaxValue,
            description: "Smallest frame size in bytes.");
        options.RegisterI64Setting(_MaxSizeOption, "Largest frame", "", 1518, min: 1, max: int.MaxValue,
            description: "Largest frame size in bytes.");
        FileSourceOptions.RegisterUiName(options);
    }

    /// <summary>Lists the (name, number) pairs of the mode setting.</summary>
    private static IEnumerable<(string Name, ulong Value)> _ModePairs()
    {
        foreach ((string name, ulong value, RandomFrameMode _) in _Modes)
        {
            yield return (name, value);
        }
    }

    /// <summary>Creates the Core random source.</summary>
    private static bool _TryCreate(SourceRequest request, [NotNullWhen(true)] out IFrameSource? source,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The generator reads no file; a path is a caller mistake.
        source = null;
        if (request.Paths.Count != 0)
        {
            error = new PluginError(PluginErrorCode.InvalidOption, "The Random source reads no files.",
                FileSourceOptions.PathsOption);
            return false;
        }

        // Cross-option rule that Core settings cannot express: the size range must not be empty.
        ReadOnlySettingsManagerView values = request.Options;
        long minSize = OptionValues.GetI64(values, _MinSizeOption);
        long maxSize = OptionValues.GetI64(values, _MaxSizeOption);
        if (minSize > maxSize)
        {
            error = new PluginError(PluginErrorCode.InvalidOption,
                Invariant($"The smallest frame size {minSize} exceeds the largest frame size {maxSize}."), _MinSizeOption);
            return false;
        }

        // The bounds of the settings make every cast safe: counts and sizes are at most int.MaxValue, the rate is between
        // 0.001 and 1e9 frames per second, so the interval is between 1 ns and 1e12 ns.
        double rate = OptionValues.GetF64(values, _RateOption);
        RandomSourceOptions options = new()
        {
            FrameCount = (int)OptionValues.GetI64(values, _CountOption),
            Seed = OptionValues.GetU64(values, _SeedOption),
            Mode = _ModeByName[OptionValues.GetEnumName(values, _ModeOption)],
            MinFrameSize = (int)minSize,
            MaxFrameSize = (int)maxSize,
            TimestampInterval = (long)Math.Round(_NanosecondsPerSecond / rate),
        };

        // The options class validates the combination (for example a size range too small for the mode); that is the
        // caller's input, so it becomes an option error and not an exception.
        try
        {
            source = new RandomFrameSource(options, FileSourceOptions.GetUiName(values));
            error = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            error = new PluginError(PluginErrorCode.InvalidOption, exception.Message);
            return false;
        }
    }

    #endregion
}

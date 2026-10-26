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

    /// <summary>
    /// Largest frame size of the IPv4 modes (IPv4, UDP over IPv4, TCP over IPv4): Ethernet header 14 plus the 16-bit IPv4 total
    /// length 65 535 = 65 549 bytes. Limit record: value 65 549 · threat: none from outside, a larger frame would wrap the 16-bit
    /// length fields into a frame that contradicts its own header · constant (a fact of the format, the same bound as the Core
    /// options class) · posture: not a posture question, the generated data would be wrong.
    /// </summary>
    private const int _MaxFrameSizeIpv4 = 14 + ushort.MaxValue;

    /// <summary>
    /// Largest frame size of the IPv6 modes (IPv6, UDP over IPv6, TCP over IPv6): Ethernet header 14 plus IPv6 header 40 plus the
    /// 16-bit payload length 65 535 = 65 589 bytes. Limit record: as for <see cref="_MaxFrameSizeIpv4"/>.
    /// </summary>
    private const int _MaxFrameSizeIpv6 = 14 + 40 + ushort.MaxValue;

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
        // The defaults deliberately differ from RandomSourceOptions, which is the library default: here the seed is fixed (42)
        // and the mode is UDP over IPv4, so two runs of the command line produce the same frames (the class default draws a
        // clock-derived seed and uses FullRandom). Callers that want other frames set the options.
        // Limits: count up to the dense frame id range every source shares. Frame sizes and the rate are bounded by the format of
        // the chosen mode and by the count, so those bounds depend on other options and are checked in _TryCreate (the setting
        // bounds only cover what holds for every mode: sizes up to int.MaxValue, rates 0.001 to 1e9 frames per second, which
        // keeps the interval between 1 ns and 1e12 ns).
        options.RegisterI64Setting(_CountOption, "Frames", "", 1000, min: 0, max: ArrayIndexIdRange.MaxCount,
            description: "Number of frames to generate; 0 generates frames until the source is stopped.");
        options.RegisterU64Setting(_SeedOption, "Seed", "", 42,
            description: "Seed of the generator; the same seed gives the same frames.");
        options.RegisterEnumSetting(_ModeOption, "Frame mode", "", 4, EnumSettingMetadata.FromPairs(_ModePairs()),
            description: "Kind of frames: fullrandom, ethernet, ipv4, ipv6, udp4, udp6, can, canfd, tcp4, tcp6, tcpstream4, or tcpstream6.");
        options.RegisterF64Setting(_RateOption, "Frames per second", "", 1000, min: 0.001, max: 1_000_000_000,
            description: "Rate of the generated timestamps in frames per second, 0.001 to 1000000000. The timestamps must fit in 64 " +
                "bits, which sets a higher lower bound for a large or unlimited count: an unlimited source needs at least 0.233.");
        options.RegisterI64Setting(_MinSizeOption, "Smallest frame", "", 64, min: 1, max: int.MaxValue,
            description: "Smallest frame size in bytes; modes with headers raise it to the size of their headers.");
        options.RegisterI64Setting(_MaxSizeOption, "Largest frame", "", 1518, min: 1, max: int.MaxValue,
            description: "Largest frame size in bytes. At most 65549 for ipv4, udp4, and tcp4, 65589 for ipv6, udp6, and tcp6 (the " +
                "16-bit length fields of the headers must not wrap), and 2147483591 for fullrandom and ethernet. Not checked for can, " +
                "canfd, tcpstream4, and tcpstream6, which ignore the frame sizes.");
        FileSourceOptions.RegisterUiName(options);
    }

    /// <summary>Lists the (name, number) pairs of the mode setting, in the order of <c>_Modes</c>.</summary>
    private static IEnumerable<(string Name, ulong Value)> _ModePairs()
    {
        foreach ((string name, ulong value, RandomFrameMode _) in _Modes)
        {
            yield return (name, value);
        }
    }

    /// <summary>
    /// Checks the largest frame size against the format of the mode. The IP and UDP length fields are 16 bits wide, so the IPv4
    /// modes allow 65 549 bytes and the IPv6 modes 65 589; <c>fullrandom</c> and <c>ethernet</c> have no length field and allow
    /// the largest array (one array of that size is allocated per frame). The CAN modes and the TCP stream modes ignore the
    /// frame sizes and are not checked.
    /// </summary>
    private static bool _TryCheckMaxFrameSize(long maxSize, RandomFrameMode mode, string modeName,
        [NotNullWhen(false)] out PluginError? error)
    {
        error = null;
        int limit = mode switch
        {
            RandomFrameMode.IPv4 or RandomFrameMode.UdpIPv4 or RandomFrameMode.TcpIPv4 => _MaxFrameSizeIpv4,
            RandomFrameMode.IPv6 or RandomFrameMode.UdpIPv6 or RandomFrameMode.TcpIPv6 => _MaxFrameSizeIpv6,
            RandomFrameMode.FullRandom or RandomFrameMode.Ethernet => Array.MaxLength,
            _ => int.MaxValue,
        };
        bool checkedMode = mode is not (RandomFrameMode.Can or RandomFrameMode.CanFd or RandomFrameMode.TcpStreamIPv4
            or RandomFrameMode.TcpStreamIPv6);
        if (checkedMode && maxSize > limit)
        {
            error = new PluginError(PluginErrorCode.InvalidOption,
                Invariant($"The largest frame size {maxSize} exceeds the maximum {limit} for mode {modeName}."), _MaxSizeOption);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks that the timestamps of the last frame fit in 64 bits: frame index times interval must not exceed
    /// <see cref="long.MaxValue"/>. The largest index is the count minus one, or the largest frame id for an unlimited source
    /// (about 4.29e9 ns, a rate of at least 0.233 frames per second). The source computes <c>BaseTimestamp + index * interval</c>
    /// in 64 bits, so the product has to be bounded here and in the Core options class.
    /// </summary>
    private static bool _TryCheckInterval(long interval, double rate, int count, [NotNullWhen(false)] out PluginError? error)
    {
        long largestIndex = count > 0
            ? count - 1L
            : ArrayIndexIdRange.MaxValue;
        long maxInterval = long.MaxValue / Math.Max(1L, largestIndex);
        if (interval <= maxInterval)
        {
            error = null;
            return true;
        }

        // The minimum is rounded up to three decimals, so that the value named in the message is itself accepted.
        double minimumRate = Math.Ceiling(_NanosecondsPerSecond / maxInterval * 1000) / 1000;
        string scope = count > 0
            ? Invariant($"{count} frames")
            : "an unlimited source";
        error = new PluginError(PluginErrorCode.InvalidOption,
            Invariant($"The rate {rate} frames per second is too low for {scope}: ") +
            Invariant($"the timestamps must fit in 64 bits, so the rate must be at least {minimumRate}."),
            _RateOption);
        return false;
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
        string modeName = OptionValues.GetEnumName(values, _ModeOption);
        RandomFrameMode mode = _ModeByName[modeName];
        int count = (int)OptionValues.GetI64(values, _CountOption);
        double rate = OptionValues.GetF64(values, _RateOption);
        long interval = (long)Math.Round(_NanosecondsPerSecond / rate);

        // The ranges that depend on other options. The Core options class enforces the same bounds at construction, but it names
        // its own properties; checking here names the option of this source and its range, so the user sees what to change.
        if (!_TryCheckMaxFrameSize(maxSize, mode, modeName, out error)
            || !_TryCheckInterval(interval, rate, count, out error))
        {
            return false;
        }

        RandomSourceOptions options = new()
        {
            FrameCount = count,
            Seed = OptionValues.GetU64(values, _SeedOption),
            Mode = mode,
            MinFrameSize = (int)minSize,
            MaxFrameSize = (int)maxSize,
            TimestampInterval = interval,
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

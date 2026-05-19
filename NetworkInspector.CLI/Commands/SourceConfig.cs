// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Parses source specifications into concrete <see cref="IFrameSource"/> instances.
/// Supports bare file paths (auto-detected by extension) and typed specs (<c>type:key=value</c>).
/// </summary>
internal abstract class SourceConfig
{
    /// <summary>
    /// Creates the <see cref="IFrameSource"/> described by this config.
    /// </summary>
    internal abstract IFrameSource CreateSource();

    /// <summary>
    /// Parses a source specification string.
    /// </summary>
    /// <remarks>
    /// Supported formats:
    /// <list type="bullet">
    ///   <item>Bare file path: <c>capture.pcapng</c> or <c>C:\Data\file.blf</c></item>
    ///   <item>Typed spec: <c>pcap:path=file.pcap</c>, <c>blf:path=file.blf</c></item>
    /// </list>
    /// Auto-detection rules for bare paths:
    /// <list type="bullet">
    ///   <item><c>.pcap</c> / <c>.pcapng</c> → <see cref="PcapSourceConfig"/></item>
    ///   <item><c>.blf</c> → <see cref="BlfSourceConfig"/></item>
    /// </list>
    /// </remarks>
    internal static SourceConfig Parse(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);

        // Check for typed spec (type:key=value)
        // Must not start with a drive letter pattern (e.g., "C:\...")
        int colonIndex = spec.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex > 0 && !IsDriveLetter(spec, colonIndex))
        {
            return ParseTypedSpec(spec, colonIndex);
        }

        // Bare file path — auto-detect by extension
        return ParseBarePath(spec);
    }

    /// <summary>
    /// Returns <c>true</c> when the colon at <paramref name="colonIndex"/> is a
    /// Windows drive letter separator (e.g., <c>C:\</c>).
    /// </summary>
    private static bool IsDriveLetter(string spec, int colonIndex)
    {
        // Drive letter: single alpha char before colon, followed by '\' or '/'
        return colonIndex == 1
            && char.IsAsciiLetter(spec[0])
            && spec.Length > 2
            && (spec[2] == '\\' || spec[2] == '/');
    }

    /// <summary>Parses a bare file path, auto-detecting the source type by extension.</summary>
    private static SourceConfig ParseBarePath(string path)
    {
        if (path.EndsWith(".pcap", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".pcapng", StringComparison.OrdinalIgnoreCase))
        {
            return new PcapSourceConfig(path);
        }

        if (path.EndsWith(".blf", StringComparison.OrdinalIgnoreCase))
        {
            return new BlfSourceConfig(path);
        }

        throw new ArgumentException(
            $"Cannot auto-detect source type for '{path}'. " +
            "Use an explicit type prefix (e.g., 'pcap:path=file') or a known extension (.pcap, .pcapng, .blf).");
    }

    /// <summary>Parses a typed specification like <c>pcap:path=file.pcap</c>.</summary>
    private static SourceConfig ParseTypedSpec(string spec, int colonIndex)
    {
        string type = spec[..colonIndex];
        string paramString = spec[(colonIndex + 1)..];
        Dictionary<string, string> parameters = ParseParameters(paramString);

        return type.ToUpperInvariant() switch
        {
            "PCAP" or "PCAPNG" => CreatePcapConfig(parameters),
            "BLF" => CreateBlfConfig(parameters),
            "RANDOM" => CreateRandomConfig(parameters),
            _ => throw new ArgumentException($"Unknown source type: '{type}'."),
        };
    }

    /// <summary>Parses comma-separated key=value parameters.</summary>
    private static Dictionary<string, string> ParseParameters(string paramString)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(paramString))
        {
            return result;
        }

        foreach (string pair in paramString.Split(','))
        {
            int eqIndex = pair.IndexOf('=', StringComparison.Ordinal);
            if (eqIndex > 0)
            {
                string key = pair[..eqIndex].Trim();
                string value = pair[(eqIndex + 1)..].Trim();
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>Creates a <see cref="PcapSourceConfig"/> from typed parameters.</summary>
    private static PcapSourceConfig CreatePcapConfig(Dictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("path", out string? path) || string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("PCAP source requires 'path' parameter (e.g., pcap:path=file.pcap).");
        }

        return new PcapSourceConfig(path);
    }

    /// <summary>Creates a <see cref="BlfSourceConfig"/> from typed parameters.</summary>
    private static BlfSourceConfig CreateBlfConfig(Dictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("path", out string? path) || string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("BLF source requires 'path' parameter (e.g., blf:path=file.blf).");
        }

        return new BlfSourceConfig(path);
    }

    /// <summary>Creates a <see cref="RandomSourceConfig"/> from typed parameters.</summary>
    /// <remarks>
    /// Supported parameters:
    /// <list type="bullet">
    ///   <item><c>count=N</c> — total frames to generate (0 = unlimited, default 1000)</item>
    ///   <item><c>seed=S</c> — PRNG seed for reproducibility (default 42)</item>
    ///   <item>
    ///     <c>mode=fullrandom|ethernet|ipv4|ipv6|udp4|udp6|can|canfd</c> — frame type (default udp4).
    ///     <c>random</c> is accepted as an alias for <c>fullrandom</c> for backward compatibility.
    ///   </item>
    /// </list>
    /// </remarks>
    private static RandomSourceConfig CreateRandomConfig(Dictionary<string, string> parameters)
    {
        int count = 1000;
        ulong seed = 42;
        RandomFrameMode mode = RandomFrameMode.UdpIPv4;

        if (parameters.TryGetValue("count", out string? countStr))
        {
            if (!int.TryParse(countStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 0)
            {
                throw new ArgumentException($"Invalid random source count: '{countStr}'.");
            }
        }

        if (parameters.TryGetValue("seed", out string? seedStr))
        {
            if (!ulong.TryParse(seedStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
            {
                throw new ArgumentException($"Invalid random source seed: '{seedStr}'.");
            }
        }

        if (parameters.TryGetValue("mode", out string? modeStr))
        {
            mode = modeStr.ToUpperInvariant() switch
            {
                "FULLRANDOM" or "FULL" or "RANDOM" => RandomFrameMode.FullRandom,  // RANDOM is a backward-compat alias
                "ETHERNET" or "ETH" => RandomFrameMode.Ethernet,
                "IPV4" or "IP4" => RandomFrameMode.IPv4,
                "IPV6" or "IP6" => RandomFrameMode.IPv6,
                "UDP4" or "UDPIPV4" => RandomFrameMode.UdpIPv4,
                "UDP6" or "UDPIPV6" => RandomFrameMode.UdpIPv6,
                "CAN" or "CAN20" => RandomFrameMode.Can,
                "CANFD" or "FD" => RandomFrameMode.CanFd,
                _ => throw new ArgumentException(
                    $"Unknown random frame mode: '{modeStr}'. Supported: fullrandom, ethernet, ipv4, ipv6, udp4, udp6, can, canfd."),
            };
        }

        return new RandomSourceConfig(count, seed, mode);
    }
}

/// <summary>Source config for PCAP/PCAPNG files.</summary>
internal sealed class PcapSourceConfig(string path) : SourceConfig
{
    /// <summary>Path to the PCAP/PCAPNG file.</summary>
    internal string Path { get; } = path;

    /// <inheritdoc/>
    internal override IFrameSource CreateSource() =>
        PcapSource.Open(Path);
}

/// <summary>Source config for BLF files.</summary>
internal sealed class BlfSourceConfig : SourceConfig
{
    /// <summary>Path to the BLF file.</summary>
    internal string Path
    {
        get;
    }

    /// <summary>
    /// Maximum byte budget for the BLF container cache (0 = default).
    /// </summary>
    internal int CacheBudget
    {
        get; init;
    }  // bytes

    /// <summary>Creates a BLF source config.</summary>
    internal BlfSourceConfig(string path)
    {
        Path = path;
    }

    /// <inheritdoc/>
    internal override IFrameSource CreateSource()
    {
        BlfSourceOptions? options = CacheBudget > 0
            ? new BlfSourceOptions { CacheBudget = CacheBudget }
            : null;
        return BlfSource.Open(Path, options);
    }
}

/// <summary>Source config for synthetic random frame generation.</summary>
internal sealed class RandomSourceConfig(
    int count,
    ulong seed,
    RandomFrameMode mode) : SourceConfig
{
    /// <summary>Total number of frames to generate (0 = unlimited).</summary>
    internal int Count { get; } = count;  // frames

    /// <summary>PRNG seed for reproducibility.</summary>
    internal ulong Seed { get; } = seed;

    /// <summary>The type of synthetic frames to generate.</summary>
    internal RandomFrameMode Mode { get; } = mode;

    /// <inheritdoc/>
    internal override IFrameSource CreateSource() =>
        new RandomFrameSource(Count, Seed, Mode);
}

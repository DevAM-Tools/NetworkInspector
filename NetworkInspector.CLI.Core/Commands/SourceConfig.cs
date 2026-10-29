// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// A parsed source spec: the registration that creates the source, the file (for file kinds), and the raw option texts. A spec
/// is <c>file.ext</c> (the kind comes from the extension) or <c>kind:key=value,…</c> (the kind comes from its name), and kinds
/// come from the plugin registry, so a plugin's source works exactly like a built-in one. The option texts go through the Core
/// text codec when the source is created, so a source option accepts the same forms as <c>ni settings</c> and every other
/// application that creates sources through the plugin registry. Immutable.
/// </summary>
internal sealed class SourceConfig
{
    #region Constants

    /// <summary>
    /// Characters that could cause harm if a spec were ever forwarded to a shell; the trust-boundary check refuses them.
    /// Security decision: the spec comes from the command line (the user's own input) and is never passed to a shell, so this
    /// is defence in depth, not the primary protection (refusing is the chosen mitigation; nothing is escaped or rewritten).
    /// Accepted cost: a file name that contains one of these characters (for example
    /// <c>capture (1).pcap</c>) cannot be used as a source. Accepted residual risk: other shell-significant characters (space,
    /// <c>$</c>, quotes) are not refused, which is why the spec must never reach a shell.
    /// </summary>
    private static readonly char[] _Forbidden = ['|', '&', '>', '<', ';', '`', '(', ')', '{', '}'];

    /// <summary>
    /// Spellings of the Random source's <c>mode</c> that earlier versions accepted, mapped to the names of the option. Applied
    /// when the kind is Random so existing scripts keep working.
    /// </summary>
    private static readonly FrozenDictionary<string, string> _LegacyRandomModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["random"] = "fullrandom",
        ["full"] = "fullrandom",
        ["eth"] = "ethernet",
        ["ip4"] = "ipv4",
        ["ip6"] = "ipv6",
        ["udpipv4"] = "udp4",
        ["udpipv6"] = "udp6",
        ["can20"] = "can",
        ["fd"] = "canfd",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Name of the Random kind, whose <c>mode</c> texts have legacy spellings.</summary>
    private const string _RandomKind = "Random";

    #endregion

    #region Lifecycle

    private SourceConfig(SourceRegistration registration, string? path, IReadOnlyList<KeyValuePair<string, string>> optionTexts)
    {
        Registration = registration;
        Path = path;
        OptionTexts = optionTexts;
    }

    #endregion

    #region Properties

    /// <summary>The registration that creates the source.</summary>
    internal SourceRegistration Registration { get; }

    /// <summary>The file of a file kind; <see langword="null"/> for kinds without files.</summary>
    internal string? Path { get; }

    /// <summary>The option texts in the order given.</summary>
    internal IReadOnlyList<KeyValuePair<string, string>> OptionTexts { get; }

    #endregion

    #region Public API

    /// <summary>Parses <c>file.ext</c> (kind by extension) or <c>kind:key=value,…</c> (kind by name).</summary>
    /// <param name="spec">The spec as typed.</param>
    /// <param name="registry">The registry that supplies the kinds.</param>
    /// <returns>The parsed spec.</returns>
    /// <exception cref="ArgumentException">Shell metacharacters, an unknown kind or extension, or a missing path for a file kind.</exception>
    internal static SourceConfig Parse(string spec, PluginRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        ArgumentNullException.ThrowIfNull(registry);
        _ValidateSourceSpec(spec);

        // A colon that is not a drive letter separates the kind from its options; everything else is a bare path. A Windows
        // path such as C:\data\x.pcap therefore stays a path.
        int colonIndex = spec.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex > 0 && !_IsDriveLetter(spec, colonIndex))
        {
            return _ParseTyped(spec[..colonIndex], spec[(colonIndex + 1)..], registry);
        }

        return _ParseBare(spec, registry);
    }

    /// <summary>Returns a copy with one more option text unless the spec already sets that option.</summary>
    /// <param name="key">Option name.</param>
    /// <param name="value">Option text.</param>
    /// <returns>The copy, or this instance when the option is already set.</returns>
    internal SourceConfig WithDefaultOption(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        foreach (KeyValuePair<string, string> pair in OptionTexts)
        {
            if (string.Equals(pair.Key, key, StringComparison.Ordinal))
            {
                return this;
            }
        }

        return new SourceConfig(Registration, Path, [.. OptionTexts, new KeyValuePair<string, string>(key, value)]);
    }

    /// <summary>
    /// Creates the source: the options of the registration at their defaults, the option texts applied with the Core codec,
    /// then the registration's factory with the file (if any). The option manager is disposed afterwards; the source copied
    /// what it needs.
    /// </summary>
    /// <param name="sessionSettings">The settings of the profile (the session settings of the request).</param>
    /// <param name="source">The created, not yet started source; <see langword="null"/> on failure.</param>
    /// <param name="error">Why no source was created; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="source"/> is set.</returns>
    internal bool TryCreateSource(ReadOnlySettingsManagerView sessionSettings, [NotNullWhen(true)] out IFrameSource? source,
        [NotNullWhen(false)] out PluginError? error)
    {
        // The option settings live only for this call (the using): the factory reads its option values while it creates the
        // source, so the read-only view handed to it is not valid afterwards. A text the codec rejects returns an error value.
        using SettingsManager options = RegistrationOptions.CreateDefaults(Registration.RegisterOptions);
        if (!RegistrationOptions.TryApplyTexts(options, OptionTexts, out error))
        {
            source = null;
            return false;
        }

        string[] paths = Path is null
            ? []
            : [Path];
        return Registration.Create(new SourceRequest(paths, options.ReadOnly, sessionSettings), out source, out error);
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Refuses shell metacharacters. Defence in depth at the command-line trust boundary: a spec is never forwarded to a
    /// shell, but a path that could be is better refused where it enters.
    /// </summary>
    private static void _ValidateSourceSpec(string spec)
    {
        foreach (char ch in _Forbidden)
        {
            if (spec.Contains(ch, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Source spec contains forbidden character '{ch}': '{spec}'. " +
                    "Shell metacharacters are not permitted in source specifications.");
            }
        }
    }

    /// <summary>Whether the colon at <paramref name="colonIndex"/> is the separator of a Windows drive letter (<c>C:\</c>).</summary>
    private static bool _IsDriveLetter(string spec, int colonIndex) =>
        colonIndex == 1
        && char.IsAsciiLetter(spec[0])
        && spec.Length > 2
        && (spec[2] == '\\' || spec[2] == '/');

    /// <summary>Parses a bare path and finds the kind by the extension.</summary>
    private static SourceConfig _ParseBare(string path, PluginRegistry registry)
    {
        // Spelled out because the property Path of this class would otherwise hide System.IO.Path. The match is by the file
        // extensions that the source registrations declare (case-insensitive), in registration order.
        string extension = System.IO.Path.GetExtension(path);
        foreach (SourceRegistration registration in registry.Sources)
        {
            if (registration.FileExtensions.Any(candidate => string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase)))
            {
                return new SourceConfig(registration, path, []);
            }
        }

        string known = string.Join(", ", registry.Sources.SelectMany(static r => r.FileExtensions).Distinct(StringComparer.OrdinalIgnoreCase));
        throw new ArgumentException(
            $"Cannot auto-detect source type for '{path}'. " +
            $"Use an explicit type prefix (e.g., 'pcap:path=file') or a known extension ({known}).");
    }

    /// <summary>Parses <c>kind:key=value,…</c>.</summary>
    private static SourceConfig _ParseTyped(string type, string parameters, PluginRegistry registry)
    {
        if (!registry.TryGetSource(type, out SourceRegistration? registration, out string? problem))
        {
            throw new ArgumentException(problem);
        }

        // Old spellings of the Random modes are rewritten to the current option values; every other pair goes to the Core
        // codec as typed (it judges names, types, and ranges when the source is created).
        List<KeyValuePair<string, string>> pairs = _ParsePairs(parameters);
        bool isRandom = string.Equals(registration.Kind, _RandomKind, StringComparison.OrdinalIgnoreCase);
        if (isRandom)
        {
            pairs = [.. pairs.Select(static pair => string.Equals(pair.Key, "mode", StringComparison.Ordinal)
                && _LegacyRandomModes.TryGetValue(pair.Value, out string? current)
                    ? new KeyValuePair<string, string>(pair.Key, current)
                    : pair)];
        }

        // A file kind takes its file from the "path" pair, which is not an option of the source.
        if (registration.FileExtensions.Count == 0)
        {
            return new SourceConfig(registration, null, pairs);
        }

        int pathIndex = pairs.FindIndex(static pair => string.Equals(pair.Key, "path", StringComparison.OrdinalIgnoreCase));
        if (pathIndex < 0 || string.IsNullOrWhiteSpace(pairs[pathIndex].Value))
        {
            string example = registration.Kind.ToLowerInvariant() + ":path=file" + registration.FileExtensions[0];
            throw new ArgumentException($"{registration.Kind} source requires 'path' parameter (e.g., {example}).");
        }

        string path = pairs[pathIndex].Value;
        pairs.RemoveAt(pathIndex);
        return new SourceConfig(registration, path, pairs);
    }

    /// <summary>
    /// Splits <c>key=value,key=value</c>; the first <c>=</c> of a pair separates key and value. Unlike the format specs, source
    /// options have no bare flags, so a pair without a <c>=</c> or without a key is refused instead of being dropped: a dropped
    /// pair would run the source with a default the user did not ask for. A blank pair (a doubled or trailing comma) carries no
    /// information and is skipped. Keys and values are trimmed and taken literally, so a value cannot contain a comma.
    /// </summary>
    /// <param name="parameters">The text after the first colon of the spec.</param>
    /// <returns>The pairs in the order given.</returns>
    /// <exception cref="ArgumentException">A pair has no <c>=</c> or an empty key.</exception>
    private static List<KeyValuePair<string, string>> _ParsePairs(string parameters)
    {
        List<KeyValuePair<string, string>> result = [];
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return result;
        }

        foreach (string pair in parameters.Split(','))
        {
            if (string.IsNullOrWhiteSpace(pair))
            {
                continue;
            }

            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0 || pair[..equals].Trim().Length == 0)
            {
                throw new ArgumentException(
                    $"Source option '{pair.Trim()}' is not of the form key=value. Source options are written kind:key=value,key=value.");
            }

            result.Add(new KeyValuePair<string, string>(pair[..equals].Trim(), pair[(equals + 1)..].Trim()));
        }

        return result;
    }

    #endregion
}

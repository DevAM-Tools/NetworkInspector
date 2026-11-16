// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// A parsed output format spec (for <c>ni convert</c>) resolved to a registration of frame exporters (PCAPNG, BLF, ASC, or a
/// plugin's). The option texts go through the Core text codec when the listener is created. Immutable.
/// </summary>
internal sealed class FrameFormatConfig
{
    #region Constants

    /// <summary>The format used when the output extension names none (the default of earlier versions).</summary>
    private const string _FallbackFormat = "PCAPNG";

    /// <summary>
    /// Spellings of the BLF <c>compression</c> option that earlier versions accepted, mapped to the names of the option. Applied
    /// to the BLF format only, so existing scripts that write <c>blf:compression=off</c> keep working while the option itself
    /// takes the Core names. Keys are compared ignoring case.
    /// </summary>
    private static readonly FrozenDictionary<string, string> _LegacyCompression = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["off"] = "none",
        ["no"] = "none",
        ["normal"] = "default",
        ["high"] = "best",
        ["max"] = "best",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    #endregion

    #region Lifecycle

    private FrameFormatConfig(ExporterRegistration<IFrameListener> registration, IReadOnlyList<KeyValuePair<string, string>> optionTexts)
    {
        Registration = registration;
        OptionTexts = optionTexts;
    }

    #endregion

    #region Properties

    /// <summary>The frame exporter registration.</summary>
    internal ExporterRegistration<IFrameListener> Registration { get; }

    /// <summary>The option texts in the order given.</summary>
    internal IReadOnlyList<KeyValuePair<string, string>> OptionTexts { get; }

    #endregion

    #region Public API

    /// <summary>Parses <c>format[:key=value,…]</c> against the registered frame formats (<c>pcap</c> is an alias of PCAPNG).</summary>
    /// <param name="spec">The spec as typed.</param>
    /// <param name="registry">The registry that supplies the formats.</param>
    /// <returns>The parsed spec.</returns>
    /// <exception cref="ArgumentException">An unknown format, a packet format (<c>Format 'CSV' writes packets; use 'ni export'.</c>),
    /// or a blank spec.</exception>
    internal static FrameFormatConfig Parse(string spec, PluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        FormatSpec.Split(spec, out string format, out List<KeyValuePair<string, string>> pairs);

        // "pcap" was always written as PCAPNG, so the old spelling stays an alias.
        if (string.Equals(format, "pcap", StringComparison.OrdinalIgnoreCase))
        {
            format = _FallbackFormat;
        }

        if (!registry.TryGetFrameExporter(format, out ExporterRegistration<IFrameListener>? registration, out string? problem))
        {
            // A packet format named here is a different command's job; say which.
            if (registry.TryGetPacketExporter(format, out ExporterRegistration<IPacketListener>? packets, out _))
            {
                throw new ArgumentException(Invariant($"Format '{packets.Format}' writes packets; use 'ni export'."));
            }

            throw new ArgumentException(problem);
        }

        // Older spellings of the BLF compression levels keep working: a value found in the legacy table is replaced by the
        // current name, every other pair is kept as typed.
        if (string.Equals(registration.Format, "BLF", StringComparison.OrdinalIgnoreCase))
        {
            pairs = [.. pairs.Select(static pair => string.Equals(pair.Key, "compression", StringComparison.OrdinalIgnoreCase)
                && _LegacyCompression.TryGetValue(pair.Value, out string? current)
                    ? new KeyValuePair<string, string>(pair.Key, current)
                    : pair)];
        }

        return new FrameFormatConfig(registration, pairs);
    }

    /// <summary>Finds the format by the extension of the output path; an empty or unknown extension means PCAPNG.</summary>
    /// <param name="extension">The extension with its dot, as <see cref="Path.GetExtension(string)"/> returns it.</param>
    /// <param name="registry">The registry that supplies the formats.</param>
    /// <returns>The format with default options.</returns>
    /// <exception cref="InvalidOperationException">The registry has no PCAPNG format (the built-in plugin is missing).</exception>
    internal static FrameFormatConfig FromExtension(string extension, PluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(registry);

        // The legacy extension .pcap was always written as PCAPNG, so it selects the PCAPNG format (whose registered extension is
        // .pcapng).
        string wanted = string.Equals(extension, ".pcap", StringComparison.OrdinalIgnoreCase)
            ? ".pcapng"
            : extension;
        foreach (ExporterRegistration<IFrameListener> registration in registry.FrameExporters)
        {
            if (registration.FileExtension.Length > 0
                && string.Equals(registration.FileExtension, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return new FrameFormatConfig(registration, []);
            }
        }

        // Unknown extension (for example ".out" or a directory stem without extension): the default format. This differs from
        // the packet formats, which have no default and refuse an unknown extension.
        if (!registry.TryGetFrameExporter(_FallbackFormat, out ExporterRegistration<IFrameListener>? fallback, out string? problem))
        {
            throw new InvalidOperationException(problem);
        }

        return new FrameFormatConfig(fallback, []);
    }

    /// <summary>Creates the listener after <c>ExportTargets.TryPrepare</c>; the option texts go through the Core codec.</summary>
    /// <param name="fullPath">The prepared output path.</param>
    /// <param name="stack">The stack of the session (frame formats do not use it).</param>
    /// <param name="storedLimit">Returns the stored value of a limit key, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the export.</param>
    /// <param name="listener">The listener; <see langword="null"/> on failure.</param>
    /// <param name="error">Why no listener was created; <see langword="null"/> on success.</param>
    /// <param name="onIgnoredStoredValue">Receives each stored limit that was refused and replaced by the definition default.</param>
    /// <returns><see langword="true"/> when <paramref name="listener"/> is set.</returns>
    internal bool TryCreate(string fullPath, IStack stack, Func<string, LimitValue?> storedLimit, Action<PluginError> onIgnoredStoredValue,
        [NotNullWhen(true)] out IFrameListener? listener, [NotNullWhen(false)] out PluginError? error, CancellationToken cancellationToken) =>
        FormatSpec.TryCreate(Registration, OptionTexts, fullPath, false, stack, storedLimit, onIgnoredStoredValue, out listener, out error,
            cancellationToken);

    #endregion
}

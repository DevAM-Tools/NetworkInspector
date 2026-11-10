// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// A parsed export format spec (for <c>ni export</c>) resolved to a registration of packet exporters. Formats come from the
/// plugin registry, so a plugin's format works exactly like a built-in one; the option texts go through the Core text codec when
/// the listener is created. Immutable.
/// </summary>
internal sealed class PacketFormatConfig
{
    #region Lifecycle

    private PacketFormatConfig(ExporterRegistration<IPacketListener> registration, IReadOnlyList<KeyValuePair<string, string>> optionTexts)
    {
        Registration = registration;
        OptionTexts = optionTexts;
    }

    #endregion

    #region Properties

    /// <summary>The packet exporter registration.</summary>
    internal ExporterRegistration<IPacketListener> Registration { get; }

    /// <summary>The option texts in the order given.</summary>
    internal IReadOnlyList<KeyValuePair<string, string>> OptionTexts { get; }

    /// <summary>Whether the output is a dataset directory (a registration without a file extension, such as Parquet).</summary>
    internal bool IsDirectoryOutput => Registration.FileExtension.Length == 0;

    #endregion

    #region Public API

    /// <summary>Parses <c>format[:key=value,flag,…]</c> against the registered packet formats.</summary>
    /// <param name="spec">The spec as typed.</param>
    /// <param name="registry">The registry that supplies the formats.</param>
    /// <returns>The parsed spec.</returns>
    /// <exception cref="ArgumentException">An unknown format, a frame format (<c>Format 'BLF' writes frames; use 'ni convert'.</c>),
    /// or a blank spec.</exception>
    internal static PacketFormatConfig Parse(string spec, PluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        FormatSpec.Split(spec, out string format, out List<KeyValuePair<string, string>> pairs);

        if (!registry.TryGetPacketExporter(format, out ExporterRegistration<IPacketListener>? registration, out string? problem))
        {
            // A frame format named here is a different command's job; say which.
            if (registry.TryGetFrameExporter(format, out ExporterRegistration<IFrameListener>? frames, out _))
            {
                throw new ArgumentException(Invariant($"Format '{frames.Format}' writes frames; use 'ni convert'."));
            }

            throw new ArgumentException(problem);
        }

        // The old flag "nocompress" of the PBF format is the option compressed=false (kept so existing scripts keep working).
        if (string.Equals(registration.Format, "PBF", StringComparison.OrdinalIgnoreCase))
        {
            pairs = [.. pairs.Select(static pair => string.Equals(pair.Key, "nocompress", StringComparison.OrdinalIgnoreCase)
                ? new KeyValuePair<string, string>("compressed", "false")
                : pair)];
        }

        return new PacketFormatConfig(registration, pairs);
    }

    /// <summary>
    /// Finds the format by the extension of the output path. There is no default format: an unknown extension (and a directory
    /// format, which has no extension) is an error that lists the formats and asks for <c>-f</c> (frame formats differ: they fall back to PCAPNG).
    /// </summary>
    /// <param name="extension">The extension with its dot, as <see cref="Path.GetExtension(string)"/> returns it.</param>
    /// <param name="registry">The registry that supplies the formats.</param>
    /// <returns>The format with default options.</returns>
    /// <exception cref="ArgumentException">No packet format uses the extension; the message lists the formats.</exception>
    internal static PacketFormatConfig FromExtension(string extension, PluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(registry);
        foreach (ExporterRegistration<IPacketListener> registration in registry.PacketExporters)
        {
            if (registration.FileExtension.Length > 0
                && string.Equals(registration.FileExtension, extension, StringComparison.OrdinalIgnoreCase))
            {
                return new PacketFormatConfig(registration, []);
            }
        }

        string known = string.Join(", ", registry.PacketExporters.Select(static registration => registration.FileExtension.Length == 0
            ? Invariant($"{registration.Format} (directory; use -f {registration.Format.ToLowerInvariant()})")
            : Invariant($"{registration.Format} ({registration.FileExtension})")));
        throw new ArgumentException(
            Invariant($"Cannot auto-detect export format from extension '{extension}'. Packet formats: {known}. Use -f to specify the format explicitly."));
    }

    /// <summary>Creates the listener after <c>ExportTargets.TryPrepare</c>; the option texts go through the Core codec.</summary>
    /// <param name="fullPath">The prepared output path.</param>
    /// <param name="stack">The stack that parsed the packets.</param>
    /// <param name="storedLimit">Returns the stored value of a limit key, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the export.</param>
    /// <param name="listener">The listener; <see langword="null"/> on failure.</param>
    /// <param name="error">Why no listener was created; <see langword="null"/> on success.</param>
    /// <param name="onIgnoredStoredValue">Receives each stored limit that was refused and replaced by the definition default.</param>
    /// <returns><see langword="true"/> when <paramref name="listener"/> is set.</returns>
    internal bool TryCreate(string fullPath, IStack stack, Func<string, LimitValue?> storedLimit, Action<PluginError> onIgnoredStoredValue,
        [NotNullWhen(true)] out IPacketListener? listener, [NotNullWhen(false)] out PluginError? error, CancellationToken cancellationToken) =>
        FormatSpec.TryCreate(Registration, OptionTexts, fullPath, stack, storedLimit, onIgnoredStoredValue, out listener, out error,
            cancellationToken);

    #endregion
}

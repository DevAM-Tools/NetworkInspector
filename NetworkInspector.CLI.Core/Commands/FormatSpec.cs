// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Shared parts of the export format specs <c>format[:key=value,flag,…]</c> of <see cref="PacketFormatConfig"/> and
/// <see cref="FrameFormatConfig"/>: splitting a spec and creating the listener from a registration with the option texts.
/// Stateless; thread-safe.
/// </summary>
internal static class FormatSpec
{
    #region Public API

    /// <summary>
    /// Splits a spec into the format name and its option pairs. A bare flag without <c>=</c> (for example <c>compressed</c>)
    /// is the same as <c>flag=true</c>. Keys and values are trimmed and taken literally: there is no escaping, so a value cannot
    /// contain a comma. The names are not checked here; the format registration and the Core text codec judge them later.
    /// </summary>
    /// <param name="spec">The spec as typed.</param>
    /// <param name="format">The format name in front of the first colon (the whole spec when it has no colon).</param>
    /// <param name="pairs">The option pairs in the order given.</param>
    /// <exception cref="ArgumentException">The spec is blank.</exception>
    internal static void Split(string spec, out string format, out List<KeyValuePair<string, string>> pairs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);

        // Only the first colon splits the spec; a colon at position 0 (or none) leaves the whole text as the format name, which
        // then fails as an unknown format with the full text in the message.
        int colon = spec.IndexOf(':', StringComparison.Ordinal);
        format = colon > 0
            ? spec[..colon]
            : spec;
        pairs = [];
        string parameters = colon > 0
            ? spec[(colon + 1)..]
            : string.Empty;
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return;
        }

        foreach (string part in parameters.Split(','))
        {
            // key=value; the first '=' separates, so a value may contain further '=' characters.
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                pairs.Add(new KeyValuePair<string, string>(part[..equals].Trim(), part[(equals + 1)..].Trim()));
                continue;
            }

            // A bare word is a boolean flag; empty parts (a trailing comma) are ignored.
            string flag = part.Trim();
            if (flag.Length > 0)
            {
                pairs.Add(new KeyValuePair<string, string>(flag, "true"));
            }
        }
    }

    /// <summary>
    /// Creates the listener of a registration: the options at their defaults (a limit option at the stored limit), the option
    /// texts applied with the Core codec, then the registration's factory. The option manager is disposed afterwards; the
    /// exporter copied what it needs.
    /// </summary>
    /// <typeparam name="TListener">The Core listener type.</typeparam>
    /// <param name="registration">The export format.</param>
    /// <param name="optionTexts">The option texts of the spec.</param>
    /// <param name="fullPath">The prepared output path (see <c>ExportTargets.TryPrepare</c>).</param>
    /// <param name="isDirectory">Whether the format writes a dataset into a folder at <paramref name="fullPath"/>.</param>
    /// <param name="stack">The stack that parsed the packets.</param>
    /// <param name="storedLimit">
    /// Returns the stored value of a limit key, or <see langword="null"/>; a limit option takes its default from it.
    /// </param>
    /// <param name="cancellationToken">Cancels the export.</param>
    /// <param name="listener">The created listener; <see langword="null"/> on failure.</param>
    /// <param name="error">Why no listener was created; <see langword="null"/> on success.</param>
    /// <param name="onIgnoredStoredValue">Receives each stored limit that was refused and replaced by the definition default.</param>
    /// <returns><see langword="true"/> when <paramref name="listener"/> is set.</returns>
    internal static bool TryCreate<TListener>(ExporterRegistration<TListener> registration,
        IReadOnlyList<KeyValuePair<string, string>> optionTexts, string fullPath, bool isDirectory, IStack stack,
        Func<string, LimitValue?> storedLimit, Action<PluginError> onIgnoredStoredValue, [NotNullWhen(true)] out TListener? listener,
        [NotNullWhen(false)] out PluginError? error, CancellationToken cancellationToken)
        where TListener : class
    {
        // The option settings live only for this call (the using): the factories read their option values while they create the
        // listener, so the read-only view handed to them is not valid afterwards. A text the codec rejects is an expected failure
        // and returns an error value, never an exception. A limit option starts at the stored limit; the inline option texts
        // applied next override it.
        using SettingsManager options = RegistrationOptions.CreateDefaults(
            registration.RegisterOptions, storedLimit, out IReadOnlyList<PluginError> ignored);
        foreach (PluginError ignoredValue in ignored)
        {
            onIgnoredStoredValue(ignoredValue);
        }

        if (!RegistrationOptions.TryApplyTexts(options, optionTexts, out error))
        {
            listener = null;
            return false;
        }

        // The item count is 0 (unknown): the command streams the sources and cannot tell the total in advance; the field is
        // informational for progress only, never a stop condition.
        // A format whose library opens the file itself gets the prepared path; every other format gets a target on that path, a
        // folder for a dataset format, one file otherwise. ExportTargets prepared the path before this call either way.
        if (registration.Creation.TryGetForFile(out FileExporterCreate<TListener>? forFile))
        {
            return forFile(new FileExportRequest(fullPath, options.ReadOnly, stack, 0, cancellationToken), out listener, out error);
        }

        if (!registration.Creation.TryGetForStreams(out StreamExporterCreate<TListener>? forStreams))
        {
            throw new InvalidOperationException("An exporter creation holds exactly one way to create."); // Invariant of ExporterCreation.
        }

        IExportTarget target = isDirectory
            ? new DirectoryExportTarget(fullPath)
            : new FileExportTarget(fullPath);
        return forStreams(new StreamExportRequest(target, options.ReadOnly, stack, 0, cancellationToken), out listener, out error);
    }

    #endregion
}

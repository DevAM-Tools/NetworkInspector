// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp.Config;

/// <summary>
/// The first trust boundary of the SOME/IP configuration: it turns untrusted JSON (a file named by a setting, a caller-owned
/// stream) into the DTO graph and refuses the file as a whole when its version or its size is not acceptable. Everything
/// after that (references, cycles, duplicates, value ranges) is the compiler's job and reports one warning per defect.
/// <para>
/// Every failure is a <see cref="SettingsLoadWarning"/> that names the node or the position and the rule; nothing throws for
/// file content and nothing is dropped silently. The loader does not run a JSON Schema engine: the structural rules of the
/// schema are implemented once, in the strict DTO deserialization (unknown properties and unknown discriminators are
/// refused) and in the compiler. A schema engine would be a new dependency for a check that an editor already performs on
/// the shipped schema.
/// </para>
/// <para><b>Thread-safety:</b> stateless; safe for concurrent callers that use their own streams and warning lists.</para>
/// </summary>
internal static class SomeIpConfigLoader
{
    #region Constants

    /// <summary>
    /// The major version of the file format this build reads. A format fact: the major number changes only on a breaking
    /// change of the format, so a file with another major version is refused instead of half-loaded.
    /// </summary>
    internal const int SupportedMajorVersion = 1;

    /// <summary>The settings group that identifies the SOME/IP parser on load warnings.</summary>
    internal const string GroupName = "someip";

    /// <summary>Setting: path of the SOME/IP service description file (may be empty; empty is not an error).</summary>
    internal const string ConfigFileSetting = "someip.config_file";

    #endregion

    #region Internal API

    /// <summary>
    /// Loads the file named by <see cref="ConfigFileSetting"/>. The settings manager resolves the path against the settings
    /// folder, confines it to that folder, and bounds the file size and the JSON depth (so this method has no size limit of
    /// its own). An empty setting is not an error: it returns false without a warning.
    /// </summary>
    /// <param name="settings">The settings of the stack being built.</param>
    /// <param name="config">The deserialized document on success.</param>
    /// <param name="warnings">Receives one warning when the file is configured but cannot be loaded.</param>
    /// <returns>True when a document was loaded.</returns>
    internal static bool TryLoadFromSettings(
        ReadOnlySettingsManagerView settings,
        [NotNullWhen(true)] out SomeIpConfig? config,
        ICollection<SettingsLoadWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        if (!settings.TryLoadReferencedJsonConfig(
                ConfigFileSetting,
                SomeIpConfigJsonContext.Default.SomeIpConfig,
                out config,
                out SettingsLoadWarning? warning))
        {
            if (warning is not null)
            {
                warnings.Add(warning.Value);
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Deserializes a document from a caller-owned stream; the stream is not closed. The host does not bound a caller-owned
    /// stream (documented on <see cref="JsonConfigStream"/>): a caller that passes untrusted bytes bounds them first.
    /// </summary>
    /// <param name="stream">Readable stream at the start of the JSON.</param>
    /// <param name="config">The deserialized document on success.</param>
    /// <param name="warnings">Receives one warning naming the position and the rule when the document is refused.</param>
    /// <returns>True when a document was loaded.</returns>
    internal static bool TryLoadFromStream(Stream stream, [NotNullWhen(true)] out SomeIpConfig? config, ICollection<SettingsLoadWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(warnings);

        try
        {
            if (!JsonConfigStream.TryLoad(
                    stream,
                    SomeIpConfigJsonContext.Default.SomeIpConfig,
                    GroupName,
                    ConfigFileSetting,
                    out config,
                    out SettingsLoadWarning? warning))
            {
                warnings.Add(warning!.Value); // TryLoad sets the warning whenever it returns false
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FormatException)
        {
            // Defense in depth for file content: the serializer can report an unknown type discriminator as
            // NotSupportedException, and a token of the wrong type from a reader accessor as InvalidOperationException or
            // FormatException. The converters check their tokens, but a refused document must never become an exception
            // at registration, whichever layer notices it, so it is the same class of defect as any other refused structure.
            config = null;
            warnings.Add(_Warning(SettingsLoadWarningKind.DeserializationError, ex.Message));
            return false;
        }
    }

    /// <summary>
    /// The shared tail of every entry point: the version and the size of the document. Returns false and adds one warning when
    /// the document must be refused as a whole.
    /// </summary>
    /// <param name="config">The deserialized document.</param>
    /// <param name="maxServices">The effective <c>someip.config_max_services</c>.</param>
    /// <param name="maxTypes">The effective <c>someip.config_max_types</c>.</param>
    /// <param name="warnings">Receives the warning.</param>
    /// <returns>True when the document may be compiled.</returns>
    internal static bool TryAccept(SomeIpConfig config, int maxServices, int maxTypes, ICollection<SettingsLoadWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(warnings);

        // schema_version is "major.minor"; only the major decides. A missing or malformed value is the same class of defect
        // as an unsupported major: the file is refused as a whole, never half-loaded.
        if (string.IsNullOrEmpty(config.SchemaVersion))
        {
            warnings.Add(_Warning(
                SettingsLoadWarningKind.DeserializationError,
                string.Create(CultureInfo.InvariantCulture, $"schema_version is missing; the supported major version is {SupportedMajorVersion}.")));
            return false;
        }

        if (!_TryParseMajor(config.SchemaVersion, out int major))
        {
            warnings.Add(_Warning(
                SettingsLoadWarningKind.DeserializationError,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"schema_version '{config.SchemaVersion}' is malformed (expected 'major.minor' with digits only); the supported major version is {SupportedMajorVersion}.")));
            return false;
        }

        if (major != SupportedMajorVersion)
        {
            warnings.Add(_Warning(
                SettingsLoadWarningKind.DeserializationError,
                string.Create(CultureInfo.InvariantCulture, $"schema_version major {major} is not supported; the supported major version is {SupportedMajorVersion}.")));
            return false;
        }

        // Limit someip.config_max_services: registration time and memory grow with the service count, and the file is
        // untrusted (generators, imported profiles). Secure by default. A hit refuses the whole file with the key and the
        // value named. Rejected alternative: compile the first N services (silent truncation hides the defect).
        if (config.Services.Length > maxServices)
        {
            warnings.Add(SomeIpLimits.ConfigMaxServices.HitWarning((ulong)config.Services.Length, (ulong)maxServices));
            return false;
        }

        // Limit someip.config_max_types: same threat and posture, counted over all services. A long counter, because the
        // sum of many services' type counts is not bounded by the array length of one service.
        long typeCount = 0;
        foreach (SomeIpServiceConfig? service in config.Services)
        {
            typeCount += service?.DataTypes.Length ?? 0;
        }

        if (typeCount > maxTypes)
        {
            warnings.Add(SomeIpLimits.ConfigMaxTypes.HitWarning((ulong)typeCount, (ulong)maxTypes));
            return false;
        }

        return true;
    }

    #endregion

    #region Private helpers

    private static SettingsLoadWarning _Warning(SettingsLoadWarningKind kind, string message) =>
        new(kind, GroupName, ConfigFileSetting, message);

    /// <summary>
    /// Parses the major number of "major.minor". Both parts must be non-empty runs of ASCII digits; anything else (a sign, a
    /// letter, a third part, white space) is malformed. The major must fit an int.
    /// </summary>
    private static bool _TryParseMajor(string version, out int major)
    {
        major = 0;
        int dot = version.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == version.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> majorText = version.AsSpan(0, dot);
        ReadOnlySpan<char> minorText = version.AsSpan(dot + 1);
        return _IsDigits(majorText)
            && _IsDigits(minorText)
            && int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out major);
    }

    private static bool _IsDigits(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return text.Length > 0;
    }

    #endregion
}

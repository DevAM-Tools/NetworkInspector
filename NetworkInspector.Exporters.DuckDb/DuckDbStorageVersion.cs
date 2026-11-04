// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.DuckDb;

/// <summary>
/// The DuckDB storage format a <see cref="DuckDbExporter"/> writes, which decides the oldest DuckDB release that can open the
/// file. A reader with DuckDB at least as new as the chosen version opens the file; an older DuckDB refuses it with a clear
/// message. Newer versions allow DuckDB to choose newer column encodings; the effect on the file size depends on the data (see the
/// README § Storage Version for one measured sample, where the versions differed by about one percent). The newest
/// version is the default (<see cref="DuckDbExporter.DefaultStorageVersion"/>) and an older one is chosen only for an older reader.
/// <para>
/// The set is closed: it lists the versions the bundled DuckDB 1.5 can write and accepts for its
/// <c>storage_compatibility_version</c> setting, a format fact of the library and not a tuning knob. A DuckDB.NET upgrade that
/// brings a new storage version adds a member here, in the plugin option pairs, and in the README table. Member names spell the
/// dot as <c>Dot</c> (<c>V1Dot2</c> is DuckDB 1.2) because the repository's naming rule CA1707 forbids an underscore in a public member.
/// </para>
/// <para>
/// DuckDB reports a file by the oldest version that has the same file format: <c>V1Dot0</c> and <c>V1Dot1</c> write the same format
/// (reported as <c>v1.0.0+</c>), every later version has a format of its own.
/// </para>
/// <para>
/// Enum, no thread-safety concerns. Sync: the enum setting pairs in <c>DuckDbExporterRegistration</c> (plugin
/// <c>NetworkInspector.BuiltInPlugins</c>) and the README § Storage Version table.
/// </para>
/// </summary>
/// <seealso cref="DuckDbExporter.Builder.WithStorageVersion"/>
public enum DuckDbStorageVersion
{
    /// <summary>
    /// Setting text <c>v1.0.0</c>: the file opens in DuckDB 1.0 and later, the widest reach and the least compact encodings.
    /// </summary>
    V1Dot0,

    /// <summary>Setting text <c>v1.1.0</c>: the file opens in DuckDB 1.1 and later.</summary>
    V1Dot1,

    /// <summary>Setting text <c>v1.2.0</c>: the file opens in DuckDB 1.2 and later.</summary>
    V1Dot2,

    /// <summary>Setting text <c>v1.3.0</c>: the file opens in DuckDB 1.3 and later.</summary>
    V1Dot3,

    /// <summary>Setting text <c>v1.4.0</c>: the file opens in DuckDB 1.4 and later.</summary>
    V1Dot4,

    /// <summary>
    /// Setting text <c>v1.5.0</c>: the file opens in DuckDB 1.5 and later; the newest format the bundled engine writes and the
    /// default.
    /// </summary>
    V1Dot5,
}

/// <summary>
/// The mapping of <see cref="DuckDbStorageVersion"/> to the text of the DuckDB setting <c>storage_compatibility_version</c>.
/// Stateless; thread-safe.
/// </summary>
/// <remarks>
/// Sync: the enum setting pairs in <c>DuckDbExporterRegistration</c> and the README § Storage Version table list the same six
/// texts; <see cref="AllowedText"/> repeats them for the refusal messages, and a test compares the three against the enum.
/// </remarks>
internal static class DuckDbStorageVersions
{
    #region Mapping

    /// <summary>
    /// The setting text of <see cref="DuckDbExporter.DefaultStorageVersion"/>. A constant so the builder can initialize its field
    /// without calling the Try mapping; a test pins it to the mapping of the default.
    /// </summary>
    internal const string DefaultSettingValue = "v1.5.0";

    /// <summary>The allowed setting texts as one list, for the refusal messages that name the allowed values.</summary>
    internal const string AllowedText = "v1.0.0, v1.1.0, v1.2.0, v1.3.0, v1.4.0, v1.5.0";

    /// <summary>
    /// Maps a storage version to the value of the DuckDB setting <c>storage_compatibility_version</c>.
    /// </summary>
    /// <param name="version">The version; any value, also an undefined one.</param>
    /// <param name="settingValue">The setting text (<c>v1.0.0</c> to <c>v1.5.0</c>); empty when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="false"/> for an undefined value; the caller decides how to report it (an undefined value is a bug of the caller).</returns>
    /// <remarks>Cost: a switch over six constants; allocation-free (the texts are literals).</remarks>
    internal static bool TryGetSettingValue(this DuckDbStorageVersion version, out string settingValue)
    {
        settingValue = version switch
        {
            DuckDbStorageVersion.V1Dot0 => "v1.0.0",
            DuckDbStorageVersion.V1Dot1 => "v1.1.0",
            DuckDbStorageVersion.V1Dot2 => "v1.2.0",
            DuckDbStorageVersion.V1Dot3 => "v1.3.0",
            DuckDbStorageVersion.V1Dot4 => "v1.4.0",
            DuckDbStorageVersion.V1Dot5 => "v1.5.0",
            _ => string.Empty,
        };

        return settingValue.Length > 0;
    }

    #endregion
}

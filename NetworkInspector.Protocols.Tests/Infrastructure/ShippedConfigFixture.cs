// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure;

/// <summary>
/// Reads the schemas and examples that the package ships and the negative samples of the test project, by path, from the
/// folders the test project copies next to the test assembly (<c>Schemas/</c> and <c>Fixtures/</c>). A test therefore always
/// checks the files a user receives, not a copy.
/// <para>
/// <b>Structural rules the SOME/IP configuration loader mirrors</b> (from <c>Schemas/someip-config.schema.json</c>; the loader
/// does not run a schema engine, so these are implemented once, from this list):
/// </para>
/// <list type="table">
/// <listheader><term>Rule</term><description>Value</description></listheader>
/// <item><term>Type categories</term><description>base_type, array, structure, tlv_structure, union, bitfield, alias</description></item>
/// <item><term>Primitives</term><description>uint8, uint16, uint32, uint64, int8, int16, int32, int64, float, double, bool, string, uuid</description></item>
/// <item><term>Member kinds</term><description>method, event, field, event_group</description></item>
/// <item><term>schema_version</term><description>"major.minor", digits only</description></item>
/// <item><term>TLV key / client key</term><description>decimal string; TLV id 0..4095, client id 0..65535</description></item>
/// <item><term>E2E profile</term><description>"04", "44", "08"</description></item>
/// <item><term>String encoding</term><description>utf-8, ascii, utf-16be, utf-16le</description></item>
/// <item><term>Length sizes</term><description>1, 2, 4 (structure also 0)</description></item>
/// <item><term>Unknown properties</term><description>refused everywhere</description></item>
/// </list>
/// <para><b>Thread-safety:</b> stateless; every temp directory is unique per call.</para>
/// </summary>
internal static class ShippedConfigFixture
{
    #region Constants

    /// <summary>Folder (below the profile folder) that holds the referenced configuration files, as the shipped profile examples name them.</summary>
    private const string _ProfileConfigFolder = "configs";

    #endregion

    #region Paths

    /// <summary>The copy of the package's <c>Schemas/</c> folder next to the test assembly.</summary>
    internal static string SchemasRoot { get; } = Path.Combine(AppContext.BaseDirectory, "Schemas");

    /// <summary>The copy of the test project's <c>Fixtures/</c> folder next to the test assembly.</summary>
    internal static string FixturesRoot { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>Full path of a shipped example file, for example <c>("someip", "minimal_service.json")</c>.</summary>
    /// <param name="protocol">The example folder: <c>someip</c>, <c>pdu-transport</c>, <c>signal-message</c> or <c>profile</c>.</param>
    /// <param name="name">The file name.</param>
    internal static string ExamplePath(string protocol, string name) => Path.Combine(SchemasRoot, "examples", protocol, name);

    /// <summary>Every shipped example file, for tests that load them all.</summary>
    internal static IReadOnlyList<string> AllExamplePaths() =>
        [.. Directory.EnumerateFiles(Path.Combine(SchemasRoot, "examples"), "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    #endregion

    #region Reading

    /// <summary>Opens a shipped example as a stream; the caller disposes it.</summary>
    /// <param name="protocol">The example folder.</param>
    /// <param name="name">The file name.</param>
    internal static FileStream ReadExample(string protocol, string name) => File.OpenRead(ExamplePath(protocol, name));

    /// <summary>Opens a negative sample of the test project as a stream; the caller disposes it.</summary>
    /// <param name="protocol">The fixture folder below <c>Fixtures/</c>, for example <c>someip</c>.</param>
    /// <param name="name">The file name inside <c>negative/</c>.</param>
    internal static FileStream ReadNegative(string protocol, string name) =>
        File.OpenRead(Path.Combine(FixturesRoot, protocol, "negative", name));

    /// <summary>Reads a shipped schema file as text.</summary>
    /// <param name="fileName">For example <c>someip-config.schema.json</c>.</param>
    internal static string ReadSchemaText(string fileName) => File.ReadAllText(Path.Combine(SchemasRoot, fileName));

    #endregion

    #region Temp files

    /// <summary>
    /// Writes <paramref name="json"/> to a file in a new unique temp directory and returns the file path. The caller deletes the
    /// directory (<see cref="Path.GetDirectoryName(string?)"/> of the path) in a <c>finally</c> block.
    /// </summary>
    /// <param name="json">The file content.</param>
    internal static string WriteTemp(string json)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ni_config_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>
    /// Builds a profile folder from the shipped <c>profile/</c> examples as a real profile looks: the settings group files at the
    /// top, the referenced configuration files in <c>configs/</c> (the settings manager reads every <c>*.json</c> at the top as a
    /// group file, so configuration files live one level down). The caller deletes the returned directory in a <c>finally</c> block.
    /// </summary>
    /// <param name="groups">The group files to include, for example <c>pdu_transport.json</c>; all of them when empty.</param>
    /// <returns>The profile folder, to be used as the storage path of a settings manager.</returns>
    internal static string ProfileDirectory(params string[] groups)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ni_profile_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        string configs = Path.Combine(directory, _ProfileConfigFolder);
        _ = Directory.CreateDirectory(configs);

        string profileSource = Path.Combine(SchemasRoot, "examples", "profile");
        IEnumerable<string> files = groups.Length == 0
            ? Directory.EnumerateFiles(profileSource, "*.json").Select(static path => Path.GetFileName(path))
            : groups;
        foreach (string file in files)
        {
            File.Copy(Path.Combine(profileSource, file), Path.Combine(directory, file));
        }

        // The configuration files the profile examples name; the relative paths then resolve as in a real profile.
        File.Copy(ExamplePath("pdu-transport", "pdu_names.json"), Path.Combine(configs, "pdu_names.json"));
        File.Copy(ExamplePath("signal-message", "can_and_pdu_bindings.json"), Path.Combine(configs, "can_and_pdu_bindings.json"));
        File.Copy(ExamplePath("someip", "full_service.json"), Path.Combine(configs, "full_service.json"));
        return directory;
    }

    #endregion
}

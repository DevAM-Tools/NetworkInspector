// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>Text</c> packet exporter: the human-readable protocol tree. The registration returns the Core
/// <see cref="TextExporter"/> itself. Stateless; the factory may run concurrently.
/// </summary>
internal static class TextExporterRegistration
{
    #region Constants

    /// <summary>Format name of the registration.</summary>
    internal const string Format = "Text";

    /// <summary>Option name: detail level of the field tree.</summary>
    private const string _LevelOption = "level";

    /// <summary>Option name: maximum characters of string and byte values.</summary>
    private const string _TruncateOption = "truncate";

    #endregion

    #region Internal API

    /// <summary>Creates the registration the exporters plugin adds with <see cref="IPluginBuilder.AddPacketExporter"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static ExporterRegistration<IPacketListener> Create() => new(
        Format: Format,
        UiName: "Text",
        Description: "Writes each packet as a human-readable protocol tree.",
        FileExtension: ".txt",
        RegisterOptions: _RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>Registers the options with the defaults of the command line (standard detail, 256 characters).</summary>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
        options.RegisterEnumSetting(_LevelOption, "Detail level", "", 1,
            EnumSettingMetadata.FromPairs([("summary", 0), ("standard", 1), ("full", 2)]),
            description: "summary: protocol containers only; standard: all fields except raw bytes; full: all fields including raw bytes.");
        options.RegisterI64Setting(_TruncateOption, "Value length limit", "", 256, min: 0, max: int.MaxValue,
            description: "Maximum characters of a string or byte value; 0 writes values in full.");
    }

    /// <summary>Creates the Core text exporter.</summary>
    private static bool _TryCreate(ExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The bound of the setting makes the cast safe.
        ReadOnlySettingsManagerView values = request.Options;
        TextDetailLevel level = OptionValues.GetEnumName(values, _LevelOption) switch
        {
            "summary" => TextDetailLevel.Summary,
            "full" => TextDetailLevel.Full,
            _ => TextDetailLevel.Standard,
        };

        int truncate = (int)OptionValues.GetI64(values, _TruncateOption);
        return ExporterBuild.TryBuild<IPacketListener>(
            () => TextExporter.CreateBuilder()
                .ToFile(request.TargetPath)
                .WithDetailLevel(level)
                .WithMaxTextLength(truncate)
                .WithCancellationToken(request.CancellationToken)
                .Build(),
            request.TargetPath, out listener, out error);
    }

    #endregion
}

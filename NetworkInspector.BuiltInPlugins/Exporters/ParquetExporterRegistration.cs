// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>Parquet</c> packet exporter. The output is a dataset directory, never a single file, so the registration has no
/// file extension. The registration returns the Core <see cref="ParquetExporter"/> itself. Stateless; the factory may run
/// concurrently.
/// </summary>
internal static class ParquetExporterRegistration
{
    #region Constants

    /// <summary>Format name of the registration.</summary>
    internal const string Format = "Parquet";

    /// <summary>Option name: declares that the packets arrive in non-decreasing timestamp order (metadata only; nothing is sorted).</summary>
    private const string _SortedOption = "timestampSorted";

    #endregion

    #region Internal API

    /// <summary>Creates the registration the exporters plugin adds with <see cref="IPluginBuilder.AddPacketExporter"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static ExporterRegistration<IPacketListener> Create() => new(
        Format: Format,
        UiName: "Parquet",
        Description: "Writes parsed packets as an Apache Parquet dataset in a directory.",
        FileExtension: "",
        RegisterOptions: _RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>Registers the options with the defaults of the Core exporter builder.</summary>
    private static void _RegisterOptions(SettingsRegistrar options) =>
        options.RegisterBoolSetting(_SortedOption, "Packets arrive sorted", "", false,
            description: "Declares that the packets arrive in non-decreasing timestamp order. Metadata only: the exporter sorts nothing " +
                "and writes the packets in the order they arrive.");

    /// <summary>Creates the Core Parquet exporter for a directory target.</summary>
    private static bool _TryCreate(ExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The flag is passed through as the Core builder defines it: a declaration that the packets arrive in timestamp
        // order (metadata for the shared columnar batch). It does not sort the packets.
        bool sorted = OptionValues.GetBool(request.Options, _SortedOption);
        return ExporterBuild.TryBuild<IPacketListener>(
            () => ParquetExporter.CreateBuilder()
                .ToDirectory(request.TargetPath)
                .WithTimestampSorted(sorted)
                .WithCancellationToken(request.CancellationToken)
                .Build(),
            request.TargetPath, out listener, out error);
    }

    #endregion
}

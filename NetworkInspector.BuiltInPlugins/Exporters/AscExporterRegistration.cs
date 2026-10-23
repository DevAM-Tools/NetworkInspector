// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>ASC</c> frame exporter. The registration returns the Core <see cref="AscExporter"/> itself. Stateless; the
/// factory may run concurrently.
/// </summary>
internal static class AscExporterRegistration
{
    #region Constants

    /// <summary>Format name of the registration.</summary>
    internal const string Format = "ASC";

    /// <summary>Option name: fraction digits of the line timestamps.</summary>
    private const string _DecimalsOption = "timestampDecimals";

    #endregion

    #region Internal API

    /// <summary>Creates the registration the exporters plugin adds with <see cref="IPluginBuilder.AddFrameExporter"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static ExporterRegistration<IFrameListener> Create() => new(
        Format: Format,
        UiName: "Vector ASC",
        Description: "Writes CAN and CAN FD frames as a Vector CANalyzer ASCII log.",
        FileExtension: ".asc",
        RegisterOptions: _RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>Registers the options with the range of the Core exporter builder.</summary>
    private static void _RegisterOptions(SettingsRegistrar options) =>
        options.RegisterI64Setting(_DecimalsOption, "Timestamp decimals", "", AscExporter.MinTimestampDecimals,
            min: AscExporter.MinTimestampDecimals, max: AscExporter.MaxTimestampDecimals,
            description: "Fraction digits of the line timestamps: 6 (microseconds, what CANoe writes) to 9 (nanoseconds).");

    /// <summary>Creates the Core ASC exporter.</summary>
    private static bool _TryCreate(ExportRequest request, [NotNullWhen(true)] out IFrameListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The bounds of the setting equal the builder range, so the cast is safe.
        int decimals = (int)OptionValues.GetI64(request.Options, _DecimalsOption);
        return ExporterBuild.TryBuild<IFrameListener>(
            () => AscExporter.CreateBuilder()
                .ToFile(request.TargetPath)
                .WithTimestampDecimals(decimals)
                .WithCancellationToken(request.CancellationToken)
                .Build(),
            request.TargetPath, out listener, out error);
    }

    #endregion
}

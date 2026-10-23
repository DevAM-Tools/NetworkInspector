// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>ASC</c> source kind: Vector CANalyzer ASCII logs. The registration returns the Core <see cref="AscSource"/>
/// itself and maps its option settings onto <see cref="AscSourceOptions"/>. ASC files are always scanned completely, so
/// the kind offers no scan mode. Stateless; the factory may run concurrently.
/// </summary>
internal static class AscSourceRegistration
{
    #region Constants

    /// <summary>Kind name of the registration.</summary>
    internal const string Kind = "ASC";

    #endregion

    #region Internal API

    /// <summary>Creates the registration the file sources plugin adds with <see cref="IPluginBuilder.AddSource"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static SourceRegistration Create() => new(
        Kind: Kind,
        UiName: "Vector ASC",
        Description: "Reads Vector CANalyzer ASCII log files (CAN and CAN FD).",
        FileExtensions: [".asc"],
        AllowsMultipleFiles: true,
        RegisterOptions: _RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>Registers the ASC options with the defaults of <see cref="AscSourceOptions"/>.</summary>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
        FileSourceOptions.RegisterUiName(options);
        FileSourceOptions.RegisterErrorTolerance(options);
        FileSourceOptions.RegisterPreloadBudget(options, AscSourceOptions.DefaultPreloadBudget);
        FileSourceOptions.RegisterTimeZone(options);
        FileSourceOptions.RegisterFrameSlab(options);
    }

    /// <summary>Opens the file of the request as a Core ASC source.</summary>
    private static bool _TryCreate(SourceRequest request, [NotNullWhen(true)] out IFrameSource? source,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validate the file and the options that Core settings cannot check on their own.
        source = null;
        ReadOnlySettingsManagerView values = request.Options;
        if (!FileSourceOptions.TryGetSinglePath(request.Paths, out string? path, out error)
            || !FileSourceOptions.TryGetTimeZone(values, out TimeZoneInfo? zone, out error)
            || !FileSourceOptions.TryGetFrameSlab(values, out int slab, out error))
        {
            return false;
        }

        AscSourceOptions options = new()
        {
            UiName = FileSourceOptions.GetUiName(values),
            ErrorTolerance = FileSourceOptions.GetErrorTolerance(values),
            PreloadBudget = OptionValues.GetI64(values, FileSourceOptions.PreloadBudgetOption),
            TimestampTimeZone = zone,
            FrameSlabSize = slab,
        };

        // The Core source itself is the result; there is no wrapper type.
        return FileSourceOptions.TryOpen(() => AscSource.Open(path, options), path, Kind, out source, out error);
    }

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>BLF</c> source kind: Vector Binary Logging Format files. The registration returns the Core
/// <see cref="BlfSource"/> itself and maps its option settings onto <see cref="BlfSourceOptions"/>. Stateless;
/// <see cref="Create"/> and the factory may run concurrently.
/// </summary>
internal static class BlfSourceRegistration
{
    #region Constants

    /// <summary>Kind name of the registration.</summary>
    internal const string Kind = "BLF";

    /// <summary>Option name: byte budget of the decompressed container cache.</summary>
    private const string _CacheBudgetOption = "cacheBudgetBytes";

    /// <summary>Option name: largest uncompressed container.</summary>
    private const string _MaxContainerOption = "maxContainerBytes";

    /// <summary>Option name: parallel decompressions.</summary>
    private const string _ConcurrencyOption = "maxDecompressionConcurrency";

    #endregion

    #region Internal API

    /// <summary>Creates the registration the file sources plugin adds with <see cref="IPluginBuilder.AddSource"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static SourceRegistration Create() => new(
        Kind: Kind,
        UiName: "Vector BLF",
        Description: "Reads Vector Binary Logging Format files (CAN, CAN FD, LIN, FlexRay, Ethernet).",
        FileExtensions: [".blf"],
        AllowsMultipleFiles: true,
        RegisterOptions: _RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>Registers the BLF options with the defaults of <see cref="BlfSourceOptions"/>.</summary>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
        FileSourceOptions.RegisterUiName(options);
        FileSourceOptions.RegisterScanMode(options);
        FileSourceOptions.RegisterErrorTolerance(options);
        // Defaults are read from a fresh options object, so the setting default and the Core class default cannot drift
        // apart. The preload budget is null (never preload) only when a caller of the Core class asks for it; the default
        // object always has a value, and 0 (always map) is the equivalent setting.
        FileSourceOptions.RegisterPreloadBudget(options, new BlfSourceOptions().PreloadBudget ?? 0);

        // The cache budget is an int in the options class, so the bound keeps every accepted value castable.
        options.RegisterI64Setting(_CacheBudgetOption, "Container cache", "", new BlfSourceOptions().CacheBudget, min: 0,
            max: int.MaxValue, description: "Byte budget of the decompressed container cache; 0 disables it.");
        FileSourceOptions.RegisterTimeZone(options);

        // Limit: largest uncompressed container, 0 (check off) or 1 byte and up, default 128 MiB from the Core options class.
        // Threat: memory exhaustion through an untrusted uncompressedSize field, rejected before any allocation. Secure by
        // default; disabling it is opt-in with 0. Configurable here by whoever opens the source (CLI, MCP, UI); the setting
        // adds no ceiling of its own; the Core options class owns the rule.
        options.RegisterI64Setting(_MaxContainerOption, "Container size limit", "", BlfSourceOptions.DefaultMaxUncompressedContainerSize,
            min: 0, description: "Largest uncompressed container in bytes; 0 disables the check.");

        // Limit: parallel decompressions, 1 to int.MaxValue. Threat: transient memory of up to this number times the container
        // limit when many reader threads miss the container cache at once. The default is host-derived (core count): it is read
        // from the Core options class, which derives it from the processor count.
        options.RegisterI64Setting(_ConcurrencyOption, "Decompression threads", "", new BlfSourceOptions().MaxDecompressionConcurrency,
            min: 1, max: int.MaxValue, description: "Number of container decompressions that may run in parallel.");
        FileSourceOptions.RegisterFrameSlab(options);
    }

    /// <summary>Opens the file of the request as a Core BLF source.</summary>
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

        // Every number is inside its setting bounds, so the narrowing casts cannot lose information. The session settings
        // are not used by this kind.
        BlfSourceOptions options = new()
        {
            UiName = FileSourceOptions.GetUiName(values),
            ScanMode = FileSourceOptions.GetScanMode(values),
            ErrorTolerance = FileSourceOptions.GetErrorTolerance(values),
            PreloadBudget = OptionValues.GetI64(values, FileSourceOptions.PreloadBudgetOption),
            CacheBudget = (int)OptionValues.GetI64(values, _CacheBudgetOption),
            TimestampTimeZone = zone,
            MaxUncompressedContainerSize = OptionValues.GetI64(values, _MaxContainerOption),
            MaxDecompressionConcurrency = (int)OptionValues.GetI64(values, _ConcurrencyOption),
            FrameSlabSize = slab,
        };

        // The Core source itself is the result; there is no wrapper type.
        return FileSourceOptions.TryOpen(() => BlfSource.Open(path, options), path, Kind, out source, out error);
    }

    #endregion
}

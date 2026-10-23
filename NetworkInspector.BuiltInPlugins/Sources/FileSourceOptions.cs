// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// Option registration and reading shared by the file source registrations (BLF, PCAPNG, PCAP, ASC) and the Random source.
/// The names, types, defaults, and bounds are Core settings, so the CLI, MCP, and the UI parse the same texts and show the
/// same messages. Every option has group <c>""</c>, a UI name, and a one-sentence description. Stateless; thread-safe.
/// </summary>
internal static class FileSourceOptions
{
    #region Constants

    /// <summary>Option name: display name of the source.</summary>
    internal const string UiNameOption = "uiName";

    /// <summary>Option name: lazy or full scan.</summary>
    internal const string ScanModeOption = "scanMode";

    /// <summary>Option name: tolerant or strict handling of damaged records.</summary>
    internal const string ErrorToleranceOption = "errorTolerance";

    /// <summary>Option name: largest file that is read into memory instead of memory-mapped.</summary>
    internal const string PreloadBudgetOption = "preloadBudgetBytes";

    /// <summary>Option name: size of the frame buffer slabs.</summary>
    internal const string FrameSlabOption = "frameSlabBytes";

    /// <summary>Option name: time zone of file timestamps that carry no zone.</summary>
    internal const string TimeZoneOption = "timestampTimeZone";

    /// <summary>Option name used in errors about the file list.</summary>
    internal const string PathsOption = "paths";

    /// <summary>Smallest non-zero slab size of the Core file sources (see <c>FrameSlabSize</c> of their option classes).</summary>
    private const long _MinFrameSlabBytes = 1024;

    /// <summary>Largest slab size of the Core file sources: 16 MiB.</summary>
    private const long _MaxFrameSlabBytes = 16L * 1024 * 1024;

    #endregion

    #region Registration

    /// <summary>Registers <c>uiName</c>: an empty text means that the source shows the file name.</summary>
    /// <param name="options">The registrar over the fresh option manager of one request.</param>
    internal static void RegisterUiName(SettingsRegistrar options) =>
        options.RegisterStringSetting(UiNameOption, "Name", "", "",
            description: "Name shown for the source; empty uses the file name.");

    /// <summary>Registers <c>scanMode</c> (<c>lazy</c> default, or <c>full</c>).</summary>
    /// <param name="options">The registrar over the fresh option manager of one request.</param>
    internal static void RegisterScanMode(SettingsRegistrar options) =>
        options.RegisterEnumSetting(ScanModeOption, "Scan mode", "", 0, EnumSettingMetadata.FromPairs([("lazy", 0), ("full", 1)]),
            description: "lazy indexes the file while frames are read; full indexes the whole file when it is opened.");

    /// <summary>Registers <c>errorTolerance</c> (<c>tolerant</c> default, or <c>strict</c>).</summary>
    /// <param name="options">The registrar over the fresh option manager of one request.</param>
    internal static void RegisterErrorTolerance(SettingsRegistrar options) =>
        options.RegisterEnumSetting(ErrorToleranceOption, "Error tolerance", "", 0,
            EnumSettingMetadata.FromPairs([("tolerant", 0), ("strict", 1)]),
            description: "tolerant skips damaged records and reports them; strict stops at the first damaged record.");

    /// <summary>Registers <c>preloadBudgetBytes</c> with the default of the Core source of the registering kind.</summary>
    /// <param name="options">The registrar over the fresh option manager of one request.</param>
    /// <param name="defaultBytes">Default of the kind's option class (256 MiB, 64 MiB in a 32-bit process).</param>
    internal static void RegisterPreloadBudget(SettingsRegistrar options, long defaultBytes) =>
        options.RegisterI64Setting(PreloadBudgetOption, "Preload budget", "", defaultBytes, min: 0,
            description: "Largest file in bytes that is read into memory; larger files are memory-mapped. 0 always maps.");

    /// <summary>Registers <c>frameSlabBytes</c> (0 = one array per frame).</summary>
    /// <param name="options">The registrar over the fresh option manager of one request.</param>
    internal static void RegisterFrameSlab(SettingsRegistrar options) =>
        options.RegisterI64Setting(FrameSlabOption, "Frame slab size", "", 0, min: 0, max: _MaxFrameSlabBytes,
            description: "Size in bytes of the shared buffers for sequentially read frames; 0 allocates one array per frame, "
                + "otherwise 1024 to 16777216.");

    /// <summary>Registers <c>timestampTimeZone</c> (<c>UTC</c> default).</summary>
    /// <param name="options">The registrar over the fresh option manager of one request.</param>
    internal static void RegisterTimeZone(SettingsRegistrar options) =>
        options.RegisterStringSetting(TimeZoneOption, "Timestamp time zone", "", "UTC",
            description: "Time zone of file timestamps that carry none: UTC, Local, or a system time zone id.");

    #endregion

    #region Reading

    /// <summary>
    /// Finds the single file of a request. Dialogs, MCP, and the CLI create one source per file, so a request with zero or
    /// several paths is a caller mistake, reported with the option name <c>paths</c>.
    /// </summary>
    /// <param name="paths">The paths of the request.</param>
    /// <param name="fullPath">The normalized full path of the existing file; <see langword="null"/> on failure.</param>
    /// <param name="error">Why no path was found; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="fullPath"/> is set.</returns>
    internal static bool TryGetSinglePath(IReadOnlyList<string> paths, [NotNullWhen(true)] out string? fullPath,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // Exactly one path is required; the text tells the caller how to open several files.
        fullPath = null;
        if (paths.Count == 0)
        {
            error = new PluginError(PluginErrorCode.InvalidOption, "A file path is required.", PathsOption);
            return false;
        }

        if (paths.Count > 1)
        {
            error = new PluginError(PluginErrorCode.InvalidOption,
                "Exactly one file per source; open several sources for several files.", PathsOption);
            return false;
        }

        // Normalize to a full path; an unusable path text is an option mistake, not a missing file.
        string path = paths[0];
        if (string.IsNullOrWhiteSpace(path))
        {
            error = new PluginError(PluginErrorCode.InvalidOption, "The file path is empty.", PathsOption);
            return false;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = new PluginError(PluginErrorCode.InvalidOption, Invariant($"'{path}' is not a valid file path: {exception.Message}"),
                PathsOption);
            return false;
        }

        // The file must exist now; a file that vanishes later surfaces as an error of the open call.
        if (!File.Exists(normalized))
        {
            error = new PluginError(PluginErrorCode.FileNotFound, Invariant($"File '{normalized}' does not exist."), PathsOption);
            return false;
        }

        fullPath = normalized;
        error = null;
        return true;
    }

    /// <summary>Resolves <c>timestampTimeZone</c>: <c>UTC</c>, <c>Local</c>, or a system time zone id.</summary>
    /// <param name="options">The applied option settings of the request.</param>
    /// <param name="zone">The time zone; <see langword="null"/> on failure.</param>
    /// <param name="error">Why the zone is unknown; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="zone"/> is set.</returns>
    internal static bool TryGetTimeZone(ReadOnlySettingsManagerView options, [NotNullWhen(true)] out TimeZoneInfo? zone,
        [NotNullWhen(false)] out PluginError? error) =>
        TryResolveTimeZone(OptionValues.GetString(options, TimeZoneOption), TimeZoneOption, out zone, out error);

    /// <summary>Resolves a time zone text for the option <paramref name="optionName"/>.</summary>
    /// <param name="text"><c>UTC</c>, <c>Local</c> (ignoring case), or a system time zone id.</param>
    /// <param name="optionName">Option named in the error.</param>
    /// <param name="zone">The time zone; <see langword="null"/> on failure.</param>
    /// <param name="error">Why the zone is unknown; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="zone"/> is set.</returns>
    internal static bool TryResolveTimeZone(string text, string optionName, [NotNullWhen(true)] out TimeZoneInfo? zone,
        [NotNullWhen(false)] out PluginError? error)
    {
        if (text.Equals("UTC", StringComparison.OrdinalIgnoreCase))
        {
            zone = TimeZoneInfo.Utc;
            error = null;
            return true;
        }

        if (text.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            zone = TimeZoneInfo.Local;
            error = null;
            return true;
        }

        if (TimeZoneInfo.TryFindSystemTimeZoneById(text, out zone))
        {
            error = null;
            return true;
        }

        error = new PluginError(PluginErrorCode.InvalidOption,
            Invariant($"Unknown time zone '{text}'. Use UTC, Local, or a system time zone id."), optionName);
        return false;
    }

    /// <summary>
    /// Reads <c>frameSlabBytes</c>. The setting accepts 0 to 16 MiB, the range of the options classes; the classes also
    /// refuse 1 to 1023, which a setting bound cannot express, so the value is checked here with the same rule.
    /// </summary>
    /// <param name="options">The applied option settings of the request.</param>
    /// <param name="bytes">The slab size; 0 when the option is off or invalid.</param>
    /// <param name="error">Why the value is unusable; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="bytes"/> is valid.</returns>
    internal static bool TryGetFrameSlab(ReadOnlySettingsManagerView options, out int bytes, [NotNullWhen(false)] out PluginError? error)
    {
        long value = OptionValues.GetI64(options, FrameSlabOption);
        if (value != 0 && value < _MinFrameSlabBytes)
        {
            bytes = 0;
            error = new PluginError(PluginErrorCode.InvalidOption,
                Invariant($"Frame slab size must be 0 (off) or between {_MinFrameSlabBytes} and {_MaxFrameSlabBytes} bytes."),
                FrameSlabOption);
            return false;
        }

        // The setting bound is 16 MiB, so the narrowing cannot lose information.
        bytes = (int)value;
        error = null;
        return true;
    }

    /// <summary>Reads <c>scanMode</c>.</summary>
    /// <param name="options">The applied option settings of the request.</param>
    /// <returns>The scan mode.</returns>
    internal static ScanMode GetScanMode(ReadOnlySettingsManagerView options) =>
        OptionValues.GetEnumName(options, ScanModeOption) == "full"
            ? ScanMode.Full
            : ScanMode.Lazy;

    /// <summary>Reads <c>errorTolerance</c>.</summary>
    /// <param name="options">The applied option settings of the request.</param>
    /// <returns>The error tolerance mode.</returns>
    internal static ErrorToleranceMode GetErrorTolerance(ReadOnlySettingsManagerView options) =>
        OptionValues.GetEnumName(options, ErrorToleranceOption) == "strict"
            ? ErrorToleranceMode.Strict
            : ErrorToleranceMode.Tolerant;

    /// <summary>Reads <c>uiName</c>.</summary>
    /// <param name="options">The applied option settings of the request.</param>
    /// <returns>The display name, or <see langword="null"/> when the option is empty (the source then shows the file name).</returns>
    internal static string? GetUiName(ReadOnlySettingsManagerView options)
    {
        string name = OptionValues.GetString(options, UiNameOption);
        return name.Length == 0
            ? null
            : name;
    }

    #endregion

    #region Opening

    /// <summary>
    /// Opens a file source and maps the expected failures to <see cref="PluginError"/>. Exceptions that are not listed
    /// here are bugs and propagate.
    /// </summary>
    /// <param name="open">Opens the Core source.</param>
    /// <param name="path">Full path of the file, for the messages.</param>
    /// <param name="kindName">Name of the file kind, for the messages (for example <c>BLF</c>).</param>
    /// <param name="source">The opened source; <see langword="null"/> on failure.</param>
    /// <param name="error">Why the file could not be opened; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="source"/> is set.</returns>
    internal static bool TryOpen(Func<IFrameSource> open, string path, string kindName, [NotNullWhen(true)] out IFrameSource? source,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(open);

        // The order matters: FileNotFound and DirectoryNotFound derive from IOException, and the format exceptions are
        // reported as an unsupported file, not as an unreadable one.
        try
        {
            source = open();
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            error = new PluginError(PluginErrorCode.FileNotFound, Invariant($"File '{path}' does not exist."), PathsOption);
        }
        catch (Exception exception) when (exception is PcapException or BlfException or InvalidDataException)
        {
            error = new PluginError(PluginErrorCode.UnsupportedFormat,
                Invariant($"'{path}' is not a valid {kindName} file: {exception.Message}"), PathsOption);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            error = new PluginError(PluginErrorCode.FileUnreadable, Invariant($"File '{path}' cannot be read: {exception.Message}"),
                PathsOption);
        }
        catch (ArgumentException exception)
        {
            error = new PluginError(PluginErrorCode.InvalidOption, exception.Message);
        }

        source = null;
        return false;
    }

    #endregion
}

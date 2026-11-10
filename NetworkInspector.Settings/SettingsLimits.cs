// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// The trust-boundary limits of the settings library: the size and the nesting of the files it parses, the number of files it
/// reads, the time it waits for the folder lock of another process, and the length of a value text. Immutable static data;
/// thread-safe.
/// </summary>
/// <remarks>
/// Every limit is a <see cref="LimitDefinition"/>. A front end chooses a <see cref="LimitValue"/> for each and hands them to
/// <see cref="SettingsManager"/> as <see cref="SettingsLimitValues"/>; that value applies to the first file that is read. A
/// manager on which the application calls <see cref="SettingsManager.RegisterOwnLimits"/> also offers each limit as a setting
/// of the group <see cref="Group"/> (file <c>settings.json</c>): the manager reads that file first, so a stored value bounds every
/// other file and can be changed and stored like any other setting. Every default is one fixed number inside its range: the
/// library reads neither the memory nor the processors of the machine. <c>0</c> never means off; <see cref="LimitValue.Off"/> does.
/// <para>Architecture: docs/architecture.md § Settings.</para>
/// <para>Sync: NetworkInspector.Settings/README.md § What your application decides and docs/guides/settings.md § Choose the limits of
/// the loader; <see cref="SettingsLimitValues"/> holds one value per
/// definition here.</para>
/// </remarks>
/// <seealso cref="SettingsLimitValues"/>
/// <seealso cref="SettingsManager"/>
/// <seealso cref="SettingValueText"/>
public static class SettingsLimits
{
    #region Group of the own limits

    /// <summary>
    /// The group of every limit of the library, which is also the stem of the file that holds their stored values. Sync: the key of
    /// every definition below starts with this group, and <see cref="SettingsManager.Load"/> reads <c>settings.json</c> first.
    /// </summary>
    public const string Group = "settings";

    #endregion

    #region Loader limits

    /// <summary>
    /// The largest settings file, manifest, or referenced configuration file that is read.
    /// </summary>
    /// <remarks>
    /// Threat: a hostile or runaway file is parsed into a complete in-memory document and exhausts memory. Configurable: by every
    /// front end, through <see cref="SettingsLimitValues.MaxFileSize"/>. Posture: secure by default, because the files are edited
    /// by users and other programs. Value: 64 MiB, 64 times an ordinary settings file; range 1 KiB to 1 TiB; <c>off</c> is
    /// allowed (the user decides). A hit skips the file and is reported, never truncated.
    /// </remarks>
    public static LimitDefinition MaxFileSize { get; } = new(
        "settings.maxFileSize",
        LimitUnits.Bytes,
        defaultValue: 64L << 20,
        minimum: 1L << 10,
        maximum: 1L << 40,
        new LimitTexts(
            Title: "Largest settings file",
            Description: "The largest size of one settings file, or of one configuration file that a setting refers to, that is read.",
            Reason: "A settings file is parsed into a complete in-memory document, so a hostile or runaway file could use all memory.",
            Benefit: "A damaged or hostile file is skipped with a clear message and the application still starts.",
            SmallerValues: "Files above the value are skipped: 1 MiB skips a 2 MiB file.",
            LargerValues: "More memory may be used per file: 256 MiB lets a 200 MiB file load but needs several times that memory while it is parsed.",
            OffEffect: "Every file is read whatever its size and a hostile file can use all memory."));

    /// <summary>The deepest JSON nesting that is read in a settings file or a referenced configuration file.</summary>
    /// <remarks>
    /// Threat: deeply nested JSON costs the parser time and stack, and recursive walks over the parsed tree could run out of stack.
    /// Configurable and posture as <see cref="MaxFileSize"/>. Value: 64, the default of System.Text.Json and far above any settings
    /// file; range 4 to 1024; <c>off</c> is allowed. A hit skips the file and is reported.
    /// </remarks>
    public static LimitDefinition MaxJsonDepth { get; } = new(
        "settings.maxJsonDepth",
        LimitUnits.Count,
        defaultValue: 64,
        minimum: 4,
        maximum: 1_024,
        new LimitTexts(
            Title: "Deepest settings nesting",
            Description: "The deepest nesting of arrays and objects in a settings file or in a configuration file that a setting refers to.",
            Reason: "Deeply nested JSON costs the parser time and stack, and a recursive walk over the parsed tree could run out of stack.",
            Benefit: "A hostile file is skipped with a clear message instead of ending the process.",
            SmallerValues: "Deeper files are skipped: 8 skips a file that nests 9 levels.",
            LargerValues: "Deeper files load: 512 accepts nesting of 512 levels but a recursive walk then needs more stack.",
            OffEffect: "Any nesting is accepted and a hostile file can end the process by exhausting the stack."));

    /// <summary>The longest time a save waits for the lock of the settings folder that another process holds.</summary>
    /// <remarks>
    /// Threat: another process that holds the lock for a long time (a hung or very slow writer) would block this caller without
    /// end. Configurable: by every front end, through <see cref="SettingsLimitValues.FolderLockTimeout"/>. Posture: secure by
    /// default. Value: 2 s, 20 times the usual hold of a save of a few small files; range 100 ms to 1 h; <c>off</c> waits without
    /// end. A hit fails closed: nothing is written and the exception names the limit. The operating system releases the lock when
    /// the holding process ends, so a crashed holder never blocks for good.
    /// </remarks>
    public static LimitDefinition FolderLockTimeout { get; } = new(
        "settings.folderLockTimeout",
        LimitUnits.Duration,
        defaultValue: 2_000,
        minimum: 100,
        maximum: 3_600_000,
        new LimitTexts(
            Title: "Settings folder lock wait",
            Description: "The longest time a save waits for the lock of the settings folder while another process holds it.",
            Reason: "A process that hangs while it holds the lock would otherwise block every other save without end.",
            Benefit: "A save fails with a clear message and changes nothing instead of hanging.",
            SmallerValues: "A save gives up earlier: 200 ms fails whenever another process saves at the same time for longer.",
            LargerValues: "A save waits longer: 1 min lets a slow disk finish but a hung process blocks the save for a minute.",
            OffEffect: "A save waits for the lock without end and a hung process blocks every other save."));

    /// <summary>The most group files that one <see cref="SettingsManager.Load"/> reads.</summary>
    /// <remarks>
    /// Threat: many files, each below <see cref="MaxFileSize"/>, exhaust memory together, because unknown keys also stay in memory
    /// as orphans. Configurable and posture as <see cref="MaxFileSize"/>. Value: 1,024, more than 10 times the number of groups of
    /// an ordinary folder; range 1 to 1,000,000; <c>off</c> is allowed. A hit stops the load of further files and is reported.
    /// </remarks>
    public static LimitDefinition MaxGroupFiles { get; } = new(
        "settings.maxGroupFiles",
        LimitUnits.Count,
        defaultValue: 1_024,
        minimum: 1,
        maximum: 1_000_000,
        new LimitTexts(
            Title: "Most settings files",
            Description: "The most settings files that are read from one settings folder.",
            Reason: "Many files, each below the size limit, could together use all memory.",
            Benefit: "A runaway folder is reported and the application still starts.",
            SmallerValues: "Further files are not read: 10 leaves the eleventh file unread.",
            LargerValues: "More files are read: 5000 allows a folder of 5000 groups and needs memory for all of them.",
            OffEffect: "Every file of the folder is read and a runaway folder can use all memory."));

    #endregion

    #region Text limit

    /// <summary>
    /// The longest value text that <see cref="SettingValueText.TryParse(IReadOnlySetting, string, out SettingValue, out SettingProblem?)"/>
    /// accepts.
    /// </summary>
    /// <remarks>
    /// Threat: a text of hundreds of megabytes makes an array or bytes setting allocate memory in proportion (one string per
    /// comma element, half the text length for hex bytes). Configurable: by every front end, through
    /// <see cref="SettingsLimitValues.MaxTextLength"/> and the overload of <c>TryParse</c> that takes it. Posture: secure by
    /// default, because the text comes from the command line, remote clients, and text boxes. Value: 1,048,576 characters, 16
    /// times the longest ordinary value (a list of paths); range 1 to 2^30; <c>off</c> is allowed. A hit refuses the text with
    /// <see cref="SettingProblemKind.TextTooLong"/>.
    /// </remarks>
    public static LimitDefinition MaxTextLength { get; } = new(
        "settings.maxTextLength",
        LimitUnits.Count,
        defaultValue: 1_048_576,
        minimum: 1,
        maximum: 1L << 30,
        new LimitTexts(
            Title: "Longest value text",
            Description: "The most characters of the text of one setting value that is parsed, for example from a command line or a text box.",
            Reason: "A very long text makes an array or bytes setting allocate memory in proportion to the text.",
            Benefit: "An oversized text is refused with a clear message before anything is allocated for it.",
            SmallerValues: "Longer texts are refused: 1000 refuses a list of paths with 1001 characters.",
            LargerValues: "Longer texts are parsed: 100000000 accepts a text of 100 million characters but needs memory in proportion to it.",
            OffEffect: "A text of any length is parsed and a hostile text can use all memory."));

    #endregion

    #region Catalog

    /// <summary>Every limit of the library, for <see cref="LimitCatalog.Check"/> and for a front end that lists them.</summary>
    public static IReadOnlyList<LimitDefinition> All { get; } = [MaxFileSize, MaxJsonDepth, FolderLockTimeout, MaxGroupFiles, MaxTextLength];

    #endregion

    #region Hit sentence

    /// <summary>
    /// The change hint of a hit that the library reports itself. The library does not know the front end, so the sentence only
    /// says who decides; a front end that wants a more specific text catches the hit (a warning kind or a problem kind) and
    /// words it itself.
    /// </summary>
    /// <param name="definition">The limit that was hit.</param>
    /// <returns>A sentence that ends with a period, as <see cref="LimitMessages.HitMessage"/> requires.</returns>
    internal static string ChangeHint(LimitDefinition definition) =>
        Invariant($"Change the setting {definition.Key}, or the value the application passes when it creates the settings manager.");

    #endregion
}

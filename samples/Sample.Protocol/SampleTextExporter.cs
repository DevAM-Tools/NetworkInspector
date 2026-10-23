// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace Sample.Protocol;

/// <summary>
/// The <c>SampleText</c> export format: one tab-separated line per sample packet with its id, counter, gear, and speed. The
/// registration shows the three parts of an exporter: metadata, option settings, and the factory that returns a Core
/// <see cref="IPacketListener"/>. Stateless; <see cref="TryCreate"/> may run concurrently.
/// </summary>
public static class SampleTextExporter
{
    #region Constants

    /// <summary>Format name of the registration.</summary>
    public const string Format = "SampleText";

    private const string _IncludeHeaderOption = "includeHeader";

    #endregion

    #region Public API

    /// <summary>The registration the plugin adds with <see cref="IPluginBuilder.AddPacketExporter"/>.</summary>
    public static ExporterRegistration<IPacketListener> Registration { get; } = new(
        Format: Format,
        UiName: "Sample text",
        Description: "One line per sample packet: id, counter, gear, speed.",
        FileExtension: ".sampletxt",
        RegisterOptions: RegisterOptions,
        Create: TryCreate);

    /// <summary>Registers the option settings: <c>includeHeader</c> (Bool, default true).</summary>
    /// <param name="options">The registrar over the fresh option manager of one request.</param>
    public static void RegisterOptions(SettingsRegistrar options)
    {
        options.RegisterBoolSetting(_IncludeHeaderOption, "Header line", "", true, "Write the column header line.");
    }

    /// <summary>
    /// Creates the listener. The application prepared the target (overwrite is its decision), so an existing file is still
    /// refused here as <see cref="PluginErrorCode.TargetExists"/>: the listener opens the file with
    /// <see cref="FileMode.CreateNew"/> and never replaces anything on its own.
    /// </summary>
    /// <param name="request">The request: target path, applied options, and the stack that parsed the packets.</param>
    /// <param name="listener">The created listener; <see langword="null"/> on failure.</param>
    /// <param name="error">Why no listener was created; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="listener"/> is set.</returns>
    public static bool TryCreate(ExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        listener = null;

        // 1. Target: a path is required, and the parent folder must exist.
        if (string.IsNullOrWhiteSpace(request.TargetPath))
        {
            error = new PluginError(PluginErrorCode.InvalidOption, "An output path is required.");
            return false;
        }

        // GetFullPath throws for paths the file system cannot express; that is bad input, not a bug, so it becomes an error value.
        string fullPath;
        string? folder;
        try
        {
            fullPath = Path.GetFullPath(request.TargetPath);
            folder = Path.GetDirectoryName(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = new PluginError(PluginErrorCode.InvalidOption, Invariant($"'{request.TargetPath}' is not a valid output path: {ex.Message}"));
            return false;
        }

        // A path without a parent folder (a drive or file system root) cannot name an output file.
        if (folder is null)
        {
            error = new PluginError(PluginErrorCode.InvalidOption, Invariant($"'{fullPath}' is not an output file path."));
            return false;
        }

        if (!Directory.Exists(folder))
        {
            error = new PluginError(PluginErrorCode.FileNotFound, Invariant($"Folder '{folder}' does not exist."));
            return false;
        }

        // 2. Fields: the stack must know the sample protocol, otherwise there is nothing to write.
        if (!_TryGetField(request.Stack, "sample.counter", out FieldId counter, out error)
            || !_TryGetField(request.Stack, "sample.gear", out FieldId gear, out error)
            || !_TryGetField(request.Stack, "sample.speed", out FieldId speed, out error))
        {
            return false;
        }

        // 3. File: CreateNew refuses an existing file; the exists check only tells the two IOException causes apart.
        bool includeHeader = request.Options.GetBoolSetting(_IncludeHeaderOption) ?? true;
        FileStream stream;
        try
        {
            stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (IOException) when (File.Exists(fullPath))
        {
            error = new PluginError(PluginErrorCode.TargetExists, Invariant($"Output '{fullPath}' exists."));
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = new PluginError(PluginErrorCode.FileUnreadable, Invariant($"Cannot create '{fullPath}': {ex.Message}"));
            return false;
        }

        listener = new SampleTextListener(stream, fullPath, includeHeader, counter, gear, speed);
        error = null;
        return true;
    }

    #endregion

    #region Private helpers

    // Resolves one field id of the sample protocol or explains what is missing.
    private static bool _TryGetField(IStack stack, string name, out FieldId fieldId, [NotNullWhen(false)] out PluginError? error)
    {
        if (stack.GetFieldId(name) is FieldId found)
        {
            fieldId = found;
            error = null;
            return true;
        }

        fieldId = default;
        error = new PluginError(PluginErrorCode.Failed, Invariant($"The stack has no field '{name}'; load the sample protocol."));
        return false;
    }

    #endregion
}

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
        Creation: ExporterCreation.ForStreams<IPacketListener>(TryCreate));

    /// <summary>Registers the option settings: <c>includeHeader</c> (Bool, default true).</summary>
    /// <param name="options">The registrar over the fresh option manager of one request.</param>
    public static void RegisterOptions(SettingsRegistrar options)
    {
        options.RegisterBoolSetting(_IncludeHeaderOption, "Header line", "", true, "Write the column header line.");
    }

    /// <summary>
    /// Creates the listener. The format never sees a path: the host prepared the target (where the output lands and whether an
    /// earlier output is replaced are its decisions), and the format opens its one entry,
    /// <see cref="ExportEntryName.Main"/>. A refused entry is an error value, never an exception.
    /// </summary>
    /// <param name="request">The request: target, applied options, and the stack that parsed the packets.</param>
    /// <param name="listener">The created listener; <see langword="null"/> on failure.</param>
    /// <param name="error">Why no listener was created; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="listener"/> is set.</returns>
    public static bool TryCreate(StreamExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        listener = null;

        // 1. Fields: the stack must know the sample protocol, otherwise there is nothing to write. They are checked before the
        // target is touched, so a failure here opens no entry.
        if (!_TryGetField(request.Stack, "sample.counter", out FieldId counter, out error)
            || !_TryGetField(request.Stack, "sample.gear", out FieldId gear, out error)
            || !_TryGetField(request.Stack, "sample.speed", out FieldId speed, out error))
        {
            return false;
        }

        // 2. Entry: the host's target hands out the stream; a refusal carries the host's reason.
        bool includeHeader = request.Options.GetBoolSetting(_IncludeHeaderOption) ?? true;
        if (!request.Target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out string failure))
        {
            error = new PluginError(PluginErrorCode.FileUnreadable, failure);
            return false;
        }

        listener = new SampleTextListener(stream, includeHeader, counter, gear, speed);
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

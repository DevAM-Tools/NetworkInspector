// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces.Tests;

/// <summary>
/// Reads trace files back and parses them with the standard protocols (and, when asked, the sample plugin's contribution), so
/// that the recipe tests compare what the kit wrote with what the application decodes. Stateless; thread-safe.
/// </summary>
internal static class TraceReading
{
    #region Types

    /// <summary>The <see cref="IPluginBuilder"/> that only remembers the stack contributions of a plugin.</summary>
    private sealed class ContributionRecorder : IPluginBuilder
    {
        internal List<Action<IStackBuilder>> StackContributions { get; } = [];

        public string PluginName => "Test.Recorder";

        public void AddStackContribution(Action<IStackBuilder> register) => StackContributions.Add(register);

        public void AddSettings(SettingScope scope, Action<SettingsRegistrar> register)
        {
        }

        public void AddSource(SourceRegistration source)
        {
        }

        public void AddFrameExporter(ExporterRegistration<IFrameListener> exporter)
        {
        }

        public void AddPacketExporter(ExporterRegistration<IPacketListener> exporter)
        {
        }
    }

    /// <summary>A frame read back from a trace with the parsed packet.</summary>
    internal sealed record ParsedFrame(Frame Frame, Packet Packet);

    #endregion

    #region Public API

    /// <summary>Reads every frame of a PCAPNG trace.</summary>
    /// <param name="path">The trace file.</param>
    /// <param name="stack">The stack whose interface registry creates the frames.</param>
    /// <returns>The frames in file order.</returns>
    internal static List<Frame> ReadFrames(string path, Stack stack)
    {
        using PcapngSource source = PcapngSource.Open(path);
        source.Start(stack.FrameInterfaceRegistry.RegisterSource(source), stack.FrameInterfaceRegistry);
        List<Frame> frames = [];
        while (source.NextFrame() is { } frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>Builds a stack with the standard protocols, a preloaded setting, and optionally the sample contribution.</summary>
    /// <param name="withSample">Whether the sample plugin contributes to the stack.</param>
    /// <param name="profileSettings">Settings of the trace (name to text); string settings only.</param>
    /// <param name="settingsBase">
    /// The settings storage folder; a configuration file named by a setting must lie inside it, so the tests pass the cache folder.
    /// </param>
    /// <returns>The stack; the caller disposes it.</returns>
    internal static Stack BuildStack(bool withSample, IReadOnlyDictionary<string, string>? profileSettings = null,
        string? settingsBase = null)
    {
        SettingsManager settings = settingsBase is null
            ? new SettingsManager()
            : new SettingsManager(settingsBase, CoreSettingsLayout.Instance);
        if (profileSettings is not null)
        {
            foreach (KeyValuePair<string, string> setting in profileSettings)
            {
                settings.PreloadValue(setting.Key, SettingValue.String(setting.Value));
            }
        }

        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        IReadOnlyList<SettingsLoadWarning> warnings = builder.RegisterStandardProtocols();
        if (warnings.Count > 0)
        {
            throw new InvalidOperationException("The standard protocols registered with warnings: " + string.Join(" | ", warnings.Select(static warning => warning.Message)));
        }

        if (withSample)
        {
            ContributionRecorder recorder = new();
            new SampleProtocolPlugin().Configure(recorder);
            foreach (Action<IStackBuilder> contribution in recorder.StackContributions)
            {
                contribution(builder);
            }
        }

        return builder.Build();
    }

    /// <summary>Reads and parses a whole trace; every packet gets a complete field tree.</summary>
    /// <param name="path">The trace file.</param>
    /// <param name="stack">The stack that parses.</param>
    /// <returns>Frames with their packets, in file order.</returns>
    internal static List<ParsedFrame> ReadAndParse(string path, Stack stack)
    {
        List<ParsedFrame> result = [];
        ParseOptions options = new();
        foreach (Frame frame in ReadFrames(path, stack))
        {
            if (!Packet.TryParse(result.Count, stack, frame, in options, out Packet? packet, out ParseFailure failure)
                || packet is null)
            {
                throw new InvalidOperationException(failure.ToString());
            }

            result.Add(new ParsedFrame(frame, packet));
        }

        return result;
    }

    /// <summary>Reads an unsigned field, or <see langword="null"/> when the packet has no such field.</summary>
    /// <param name="stack">The stack that knows the field.</param>
    /// <param name="packet">The packet.</param>
    /// <param name="fieldName">The registered field name.</param>
    /// <returns>The value or <see langword="null"/>.</returns>
    internal static ulong? U64(Stack stack, Packet packet, string fieldName)
    {
        FieldId? id = stack.GetFieldId(fieldName);
        if (id is null || !packet.TryGetFieldValue(id.Value, out FieldValue value, materialize: true))
        {
            return null;
        }

        return value.Data.TryGetAsU64(out ulong number)
            ? number
            : null;
    }

    /// <summary>Reads a numeric field of any type (unsigned, signed, or floating point), or <see langword="null"/> when it is absent.</summary>
    /// <param name="stack">The stack that knows the field.</param>
    /// <param name="packet">The packet.</param>
    /// <param name="fieldName">The registered field name.</param>
    /// <returns>The value as a double, or <see langword="null"/>.</returns>
    internal static double? Number(Stack stack, Packet packet, string fieldName)
    {
        FieldId? id = stack.GetFieldId(fieldName);
        if (id is null || !packet.TryGetFieldValue(id.Value, out FieldValue value, materialize: true))
        {
            return null;
        }

        if (value.Data.TryGetAsU64(out ulong unsigned))
        {
            return unsigned;
        }

        if (value.Data.TryGetAsI64(out long signed))
        {
            return signed;
        }

        return value.Data.TryGetAsF64(out double number)
            ? number
            : null;
    }

    /// <summary>Reads a field as text, or <see langword="null"/> when the packet has no such field.</summary>
    /// <param name="stack">The stack that knows the field.</param>
    /// <param name="packet">The packet.</param>
    /// <param name="fieldName">The registered field name.</param>
    /// <returns>The text or <see langword="null"/>.</returns>
    internal static string? Text(Stack stack, Packet packet, string fieldName)
    {
        FieldId? id = stack.GetFieldId(fieldName);
        if (id is null || !packet.TryGetFieldValue(id.Value, out FieldValue value, materialize: true))
        {
            return null;
        }

        return value.ToString(null, CultureInfo.InvariantCulture);
    }

    #endregion
}

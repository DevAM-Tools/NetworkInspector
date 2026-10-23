// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// The <c>PCAPNG</c> frame exporter. The registration returns the Core <see cref="PcapngExporter"/> itself. Stateless; the
/// factory may run concurrently.
/// </summary>
internal static class PcapngExporterRegistration
{
    #region Constants

    /// <summary>Format name of the registration.</summary>
    internal const string Format = "PCAPNG";

    /// <summary>Option name: maximum stored bytes per packet.</summary>
    private const string _SnapLengthOption = "snapLength";

    /// <summary>Option name: timestamp resolution.</summary>
    private const string _ResolutionOption = "timestampResolution";

    /// <summary>Option name: comment of the section header.</summary>
    private const string _CommentOption = "comment";

    /// <summary>Exponent of nanosecond resolution (10^-9), the exporter default.</summary>
    private const byte _NanosecondExponent = 9;

    /// <summary>Exponent of microsecond resolution (10^-6).</summary>
    private const byte _MicrosecondExponent = 6;

    #endregion

    #region Internal API

    /// <summary>Creates the registration the exporters plugin adds with <see cref="IPluginBuilder.AddFrameExporter"/>.</summary>
    /// <returns>The registration record.</returns>
    internal static ExporterRegistration<IFrameListener> Create() => new(
        Format: Format,
        UiName: "PCAPNG",
        Description: "Writes frames as a PCAP Next Generation capture file.",
        FileExtension: ".pcapng",
        RegisterOptions: _RegisterOptions,
        Create: _TryCreate);

    #endregion

    #region Private Helpers

    /// <summary>Registers the options with the defaults of the Core exporter builder.</summary>
    private static void _RegisterOptions(SettingsRegistrar options)
    {
        // The builder accepts snap lengths up to int.MaxValue (its slicing path casts to int), so the setting stops there.
        options.RegisterU64Setting(_SnapLengthOption, "Snap length", "", 0, min: 0, max: int.MaxValue,
            description: "Maximum bytes stored per packet; 0 uses the exporter default (no truncation).");
        options.RegisterEnumSetting(_ResolutionOption, "Timestamp resolution", "", 0,
            EnumSettingMetadata.FromPairs([("nanoseconds", 0), ("microseconds", 1)]),
            description: "Resolution of the written timestamps: nanoseconds or microseconds.");
        options.RegisterStringSetting(_CommentOption, "Comment", "", "",
            description: "Comment stored in the section header; empty stores none.");
    }

    /// <summary>Creates the Core PCAPNG exporter.</summary>
    private static bool _TryCreate(ExportRequest request, [NotNullWhen(true)] out IFrameListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Read the options; the bounds of the settings make the snap length cast safe.
        ReadOnlySettingsManagerView values = request.Options;
        uint snapLength = (uint)OptionValues.GetU64(values, _SnapLengthOption);
        byte resolution = OptionValues.GetEnumName(values, _ResolutionOption) == "microseconds"
            ? _MicrosecondExponent
            : _NanosecondExponent;
        string comment = OptionValues.GetString(values, _CommentOption);

        return ExporterBuild.TryBuild<IFrameListener>(
            () =>
            {
                PcapngExporter.Builder builder = PcapngExporter.CreateBuilder()
                    .ToFile(request.TargetPath)
                    .WithTimestampResolution(resolution)
                    .WithCancellationToken(request.CancellationToken);

                // Zero keeps the exporter default; the builder refuses a zero snap length.
                if (snapLength != 0)
                {
                    builder.WithSnapLength(snapLength);
                }

                if (comment.Length != 0)
                {
                    builder.WithComment(comment);
                }

                return builder.Build();
            },
            request.TargetPath, out listener, out error);
    }

    #endregion
}

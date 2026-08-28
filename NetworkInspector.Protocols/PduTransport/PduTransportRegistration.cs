// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.PduTransport;

/// <summary>
/// Loads PDU Transport JSON and registers <see cref="PduTransportProtocol"/>,
/// returning every registration warning to the caller.
/// </summary>
/// <remarks>
/// <para>
/// Call from a custom stack, or via <see cref="ProtocolRegistration.RegisterStandardProtocols"/>
/// which forwards the collected warnings. The <c>pdu_transport.config_file</c> setting
/// (profile/settings, may be empty) is always applied. Stream/object overloads add extra
/// PDU names on top of that file; they do not replace it.
/// </para>
/// <para>
/// UDP parser selection (not a socket listen) uses <see cref="UdpDispatchPortsSetting"/>,
/// a <c>U64Array</c>, not the names JSON. Empty means UDP never calls this parser.
/// Host preload before <c>RegisterStandardProtocols</c>:
/// <c>settings.PreloadValue(PduTransportRegistration.UdpDispatchPortsSetting, SettingValue.U64Array([47290UL, 47291UL]));</c>
/// A scalar <c>ulong</c> preload is ignored (type mismatch; default empty).
/// </para>
/// <para><b>Thread safety:</b> not thread-safe; call once during single-threaded stack build.</para>
/// </remarks>
public static class PduTransportRegistration
{
    #region Constants

    /// <summary>Setting: path to the PDU Transport JSON configuration file.</summary>
    public const string ConfigFileSetting = "pdu_transport.config_file";

    /// <summary>Setting: UDP ports that select PDU Transport on <c>udp.port</c>.</summary>
    public const string UdpDispatchPortsSetting = "pdu_transport.udp_dispatch_ports";

    private const string _GroupName = "pdu_transport";

    #endregion

    #region Public API

    /// <summary>
    /// Deserializes PDU Transport JSON from <paramref name="stream"/> (does not close the stream).
    /// </summary>
    /// <param name="stream">Readable stream positioned at the JSON payload.</param>
    /// <param name="config">Deserialized config on success; otherwise <see langword="null"/>.</param>
    /// <param name="warning">Set when deserialization fails; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when the JSON was deserialized successfully.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <see langword="null"/>.</exception>
    public static bool TryLoadConfig(
        Stream stream,
        [NotNullWhen(true)] out PduTransportConfig? config,
        out SettingsLoadWarning? warning)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return JsonConfigStream.TryLoad(
            stream,
            PduTransportConfigContext.Default.PduTransportConfig,
            _GroupName,
            ConfigFileSetting,
            out config,
            out warning);
    }

    /// <summary>
    /// Registers PDU Transport using the <see cref="ConfigFileSetting"/> path (if set).
    /// Field-size clamps, UDP dispatch-port filter warnings, and config-load failures
    /// are appended to <paramref name="warnings"/>.
    /// </summary>
    /// <param name="builder">Stack builder during the registration phase.</param>
    /// <param name="warnings">Receives registration warnings. Caller guarantees non-null.</param>
    /// <returns>The registered protocol instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/> or <paramref name="warnings"/> is <see langword="null"/>.
    /// </exception>
    public static PduTransportProtocol Register(IStackBuilder builder, ICollection<SettingsLoadWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(warnings);
        PduTransportProtocol protocol = new();
        _RegisterProtocol(builder, protocol, warnings);
        return protocol;
    }

    /// <summary>
    /// Registers PDU Transport from JSON on <paramref name="stream"/> as <b>additional</b>
    /// names, merged on top of <see cref="ConfigFileSetting"/> (empty file is valid).
    /// Load failure still registers the protocol and records a warning; the file setting
    /// is still applied.
    /// </summary>
    /// <param name="builder">Stack builder during the registration phase.</param>
    /// <param name="stream">Readable stream positioned at the JSON payload. Not closed.</param>
    /// <param name="warnings">Receives load and registration warnings. Caller guarantees non-null.</param>
    /// <returns>The registered protocol instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/>, <paramref name="stream"/>, or
    /// <paramref name="warnings"/> is <see langword="null"/>.
    /// </exception>
    public static PduTransportProtocol Register(
        IStackBuilder builder,
        Stream stream,
        ICollection<SettingsLoadWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(warnings);
        PduTransportProtocol protocol = new();
        _ = protocol.TryLoadConfigFromStream(stream, out _);
        _RegisterProtocol(builder, protocol, warnings);
        return protocol;
    }

    /// <summary>
    /// Registers PDU Transport from an already-deserialized <paramref name="config"/> as
    /// <b>additional</b> names, merged on top of <see cref="ConfigFileSetting"/>.
    /// </summary>
    /// <param name="builder">Stack builder during the registration phase.</param>
    /// <param name="config">Deserialized PDU Transport configuration.</param>
    /// <param name="warnings">Receives registration warnings (e.g. field-size clamps).</param>
    /// <returns>The registered protocol instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/>, <paramref name="config"/>, or
    /// <paramref name="warnings"/> is <see langword="null"/>.
    /// </exception>
    public static PduTransportProtocol Register(
        IStackBuilder builder,
        PduTransportConfig config,
        ICollection<SettingsLoadWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(warnings);
        PduTransportProtocol protocol = new();
        protocol.ApplyConfig(config);
        _RegisterProtocol(builder, protocol, warnings);
        return protocol;
    }

    #endregion

    #region Private helpers

    /// <summary>Registers the protocol, runs field registration, then copies warnings to the caller.</summary>
    private static void _RegisterProtocol(
        IStackBuilder builder,
        PduTransportProtocol protocol,
        ICollection<SettingsLoadWarning> warnings)
    {
        ProtocolId id = builder.RegisterProtocol(protocol);
        protocol.RegisterFields(builder, id);
        protocol.AppendRegistrationWarnings(warnings);
    }

    #endregion
}

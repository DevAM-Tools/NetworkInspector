// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Collections.Immutable;
using System.Text;
using NetworkInspector.FrameBuilder;

namespace NetworkInspector.Protocols.Tests.Infrastructure.TsharkUat;

/// <summary>
/// Emits the minimal Wireshark CSV UAT triple (PDU-Transport identifiers + extended port config,
/// Signal-PDU identifiers + signal list + binding) so <c>tshark -C &lt;profile&gt;</c>
/// dissects the same frames that Network Inspector parses from JSON + UDP dispatch.
/// </summary>
/// <remarks>
/// Supports only non-multiplexed Signal-PDU layouts whose static signals are contiguous in
/// positional order (matching the Wireshark sequential deserializer). Multiplexed layouts throw
/// explicitly so tests do not silently diverge.
/// </remarks>
internal static class TsharkPduTransportSignalUatProfile
{
    /// <summary>Wireshark sentinel for &quot;any&quot; UDP/TCP port in extended config (<c>packet-pdu-transport.c</c>).</summary>
    internal const int WiresharkUdpPortAny65536 = 65536;

    /// <summary>
    /// Builds <c>WIRESHARK_CONFIG_PARENT/profiles/&lt;runId&gt;/</c> and returns that profile folder.
    /// </summary>
    internal static string CreateProfileDirectoryUnderEphemeralPersonalDir(string personalRoot, string runId)
    {
        string profilesDir = Path.Combine(personalRoot, "profiles");
        string dir = Path.Combine(profilesDir, runId);
        _ = Directory.CreateDirectory(dir);

        WiresharkProfilePreferences.MergeIntoProfilePreferences(dir);
        return dir;
    }

    /// <summary>Writes PDU-Transport identifier + UDP extended-config UAT tables only.</summary>
    internal static void EmitPduTransportUdpDescriptors(
        string profileDirectory,
        ushort udpDestinationPort,
        PduTransportConfigFb pduTransport)
    {
        int idBits = pduTransport.IdFieldSize * 8;
        int lengthBits = pduTransport.LengthFieldSize * 8;
        WritePduTransportIdentifiers(profileDirectory, pduTransport);
        WritePduTransportExtendedConfig(profileDirectory, udpDestinationPort, idBits, lengthBits);
    }

    /// <summary>
    /// Writes PDU-Transport and Signal-PDU CSV tables consumed by Wireshark preference UATs.
    /// </summary>
    /// <param name="profileDirectory">Path to one profile (<c>.../profiles/&lt;runId&gt;/</c>).</param>
    /// <param name="udpDestinationPort">UDP destination port used for <c>PDU_Transport_extended_config</c>.</param>
    /// <param name="pduTransport">FrameBuilder single source of truth for field sizes and ID→name rows.</param>
    /// <param name="pduTransportWireId">PDU identifier that appears in the on-the-wire header for the tested frame.</param>
    /// <param name="signalLayout">Structured layout mirrored into <c>Signal_PDU_*</c> tables.</param>
    internal static void EmitPduTransportOverUdpWithSignalPdu(
        string profileDirectory,
        ushort udpDestinationPort,
        PduTransportConfigFb pduTransport,
        uint pduTransportWireId,
        SignalPduLayout signalLayout)
    {
        ArgumentNullException.ThrowIfNull(signalLayout);
        if (signalLayout.Mux is not null ||
            !(signalLayout.MuxGroups.IsDefault || signalLayout.MuxGroups.Length == 0))
        {
            throw new NotSupportedException(
                "Sequential Wireshark Signal-PDU UAT parity does not multiplex; use raw Signal-Pdu UDP tests.");
        }

        EmitPduTransportUdpDescriptors(profileDirectory, udpDestinationPort, pduTransport);
        WriteSignalPduIdentifiers(profileDirectory, signalLayout);
        WriteSignalPduSignalList(profileDirectory, signalLayout);
        WriteSignalPduBindingPduTransport(profileDirectory, pduTransportWireId, signalLayout.PduId);
    }

    private static void WritePduTransportIdentifiers(string profileDirectory, PduTransportConfigFb cfg)
    {
        StringBuilder sb = new();
        foreach (PduEntry entry in cfg.Pdus)
        {
            _ = sb.Append(WiresharkCsvUat.UatQuoted(WiresharkCsvUat.Hex32Upper(entry.PduId)))
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(entry.Name))
                .AppendLine();
        }

        string path = Path.Combine(profileDirectory, WiresharkPduTransportFilenames.Identifiers + WiresharkCsvUat.Filesuffix);
        File.WriteAllText(path, sb.ToString(), WiresharkUtf8NoBom());
    }

    private static void WritePduTransportExtendedConfig(
        string profileDirectory,
        ushort udpDestinationPort,
        int idFieldBits,
        int lengthFieldBits)
    {
        string path = Path.Combine(profileDirectory, WiresharkPduTransportFilenames.ExtendedConfig + WiresharkCsvUat.Filesuffix);

        /*
         Row order per Wireshark <c>pdu_transport_ext_cfg_uat_fields</c>:
         tcp, source_port, destination_port, size_of_id_field (bits), size_of_length_field (bits), default_id (hex).
        */
        string line = string.Join(
            ',',
            WiresharkCsvUat.UatQuoted(WiresharkCsvUat.Bool(false)),
            WiresharkCsvUat.UatQuoted(WiresharkUdpPortAny65536),
            WiresharkCsvUat.UatQuoted(udpDestinationPort),
            WiresharkCsvUat.UatQuoted(idFieldBits),
            WiresharkCsvUat.UatQuoted(lengthFieldBits),
            WiresharkCsvUat.UatQuoted(WiresharkCsvUat.Hex32Upper(0)));

        File.WriteAllText(path, line + Environment.NewLine, WiresharkUtf8NoBom());
    }

    private static void WriteSignalPduIdentifiers(string profileDirectory, SignalPduLayout layout)
    {
        string path = Path.Combine(profileDirectory, WiresharkSignalPduFilenames.Identifiers + WiresharkCsvUat.Filesuffix);
        string line =
            $"{WiresharkCsvUat.UatQuoted(WiresharkCsvUat.Hex32Upper(layout.PduId))},{WiresharkCsvUat.UatQuoted(layout.Name)}";
        File.WriteAllText(path, line + Environment.NewLine, WiresharkUtf8NoBom());
    }

    private static void WriteSignalPduSignalList(string profileDirectory, SignalPduLayout layout)
    {
        ImmutableArray<SignalSpec> sigs = layout.Signals;
        int n = sigs.Length;
        StringBuilder sb = new();

        for (int pos = 0; pos < n; pos++)
        {
            SignalSpec s = sigs[pos];
            string filter = ProtoFilterSanitizer.FilterToken(s.Name);
            string dataType = s.Type switch
            {
                SignalType.Signed => "int",
                SignalType.Unsigned => "uint",
                SignalType.F32 or SignalType.F64 => "float",
                _ => throw new InvalidOperationException($"Unsupported signal type {s.Type} for Wireshark parity."),
            };

            if ((s.Type is SignalType.F32 or SignalType.F64) && (s.Factor != 1.0 || s.Offset != 0.0))
            {
                throw new InvalidOperationException("Wireshark Signal-PDU rejects scaled float rows; use uint/int or unscaled floats.");
            }

            int bitLen = s.BitLength;
            // 17-column UAT row layout (must match packet-signal-pdu.c spdu_signal_list_uat_fields):
            //  1: id (Signal PDU ID hex)
            //  2: num_of_params (number of signals dec)
            //  3: pos (0-based position dec)
            //  4: name (signal name string)
            //  5: filter_string (lower-case filter token)
            //  6: data_type (uint | int | float)
            //  7: big_endian (true | false)
            //  8: bitlength_base_type (original type width dec)
            //  9: bitlength_encoded_type (wire width dec)
            // 10: scaler (double)
            // 11: offset (double)
            // 12: multiplexer (true | false)
            // 13: multiplex_value_only (signed dec, -1 = all)
            // 14: hidden (true | false)
            // 15: aggregate_sum (true | false)
            // 16: aggregate_avg (true | false)
            // 17: aggregate_int (true | false)
            sb.Append(WiresharkCsvUat.UatQuoted(WiresharkCsvUat.Hex32Upper(layout.PduId)))     //  1: id
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(n))                                           //  2: num_of_params
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(pos))                                         //  3: pos
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(s.Name))                                      //  4: name
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(filter))                                      //  5: filter_string
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(dataType))                                    //  6: data_type
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(WiresharkCsvUat.Bool(s.Endian == SignalEndian.Big))) //  7: big_endian
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(bitLen))                                      //  8: bitlength_base_type
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(bitLen))                                      //  9: bitlength_encoded_type
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(WiresharkCsvUat.CsvDouble(s.Factor)))         // 10: scaler
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(WiresharkCsvUat.CsvDouble(s.Offset)))         // 11: offset
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(WiresharkCsvUat.Bool(false)))                 // 12: multiplexer
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(-1))                                          // 13: multiplex_value_only
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(WiresharkCsvUat.Bool(false)))                 // 14: hidden
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(WiresharkCsvUat.Bool(false)))                 // 15: aggregate_sum
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(WiresharkCsvUat.Bool(false)))                 // 16: aggregate_avg
                .Append(',')
                .Append(WiresharkCsvUat.UatQuoted(WiresharkCsvUat.Bool(false)))                 // 17: aggregate_int
                .AppendLine();
        }

        string path = Path.Combine(profileDirectory, WiresharkSignalPduFilenames.SignalList + WiresharkCsvUat.Filesuffix);
        File.WriteAllText(path, sb.ToString(), WiresharkUtf8NoBom());
    }

    private static void WriteSignalPduBindingPduTransport(
        string profileDirectory,
        uint pduTransportWireId,
        uint signalPduMessageId)
    {
        string path = Path.Combine(profileDirectory, WiresharkSignalPduFilenames.BindingPduTransport + WiresharkCsvUat.Filesuffix);
        string line = string.Join(
            ',',
            WiresharkCsvUat.UatQuoted(WiresharkCsvUat.Hex32Upper(pduTransportWireId)),
            WiresharkCsvUat.UatQuoted(WiresharkCsvUat.Hex32Upper(signalPduMessageId)));
        File.WriteAllText(path, line + Environment.NewLine, WiresharkUtf8NoBom());
    }

    private static UTF8Encoding WiresharkUtf8NoBom() =>
        new(encoderShouldEmitUTF8Identifier: false);

    private static class ProtoFilterSanitizer
    {
        internal static string FilterToken(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "sig";
            }

            StringBuilder sb = new(name.Length);
            bool first = true;
            foreach (char c in name)
            {
                if (char.IsLetter(c))
                {
                    _ = sb.Append(char.ToLowerInvariant(c));
                    first = false;
                }
                else if (char.IsDigit(c))
                {
                    if (first)
                    {
                        _ = sb.Append('s');
                    }

                    _ = sb.Append(c);
                    first = false;
                }
                else if (c is '_' && !first)
                {
                    _ = sb.Append('_');
                }
                else
                {
                    _ = sb.Append('_');
                    first = false;
                }
            }

            return sb.Length == 0 ? "sig" : sb.ToString();
        }
    }
}

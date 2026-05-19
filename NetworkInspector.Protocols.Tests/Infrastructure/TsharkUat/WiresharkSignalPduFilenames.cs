// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests.Infrastructure.TsharkUat;

/// <summary>
/// Filenames Wireshark binds to Signal-PDU preference UAT tables (basename without suffix).
/// They match Wireshark constants in <c>packet-signal-pdu.c</c>; profile filenames use the
/// basename only (no extension — see <see cref="WiresharkCsvUat.Filesuffix"/>).
/// </summary>
internal static class WiresharkSignalPduFilenames
{
    internal const string Identifiers = "Signal_PDU_identifiers";

    internal const string SignalValues = "Signal_PDU_signal_values";

    internal const string SignalList = "Signal_PDU_signal_list";

    internal const string BindingPduTransport = "Signal_PDU_Binding_PDU_Transport";
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// Keys for PCAP and PCAPNG metadata on <see cref="FrameInterfaceInfo.Properties"/>.
/// PCAPNG readers write these keys. The PCAPNG exporter reads them back and writes the matching
/// IDB / SHB options, so a PCAPNG → PCAPNG copy keeps the interface metadata.
/// </summary>
/// <remarks>
/// This file is link-compiled into the exporter assembly. <see cref="InterfaceOptions"/> is the single
/// table both sides use, so reader and writer cannot drift apart.
/// </remarks>
internal static class PcapInterfacePropertyKeys
{
    #region Interface-level metadata

    /// <summary>Interface speed in bits per second (value type: <see cref="ulong"/>).</summary>
    internal const string Speed = "if.speed";

    /// <summary>FCS (Frame Check Sequence) length in bytes (value type: <see cref="byte"/>).</summary>
    internal const string FcsLength = "if.fcs_length";

    /// <summary>Snapshot length — maximum captured octets per packet (value type: <see cref="uint"/>).</summary>
    internal const string SnapLength = "if.snap_length";

    /// <summary>
    /// Capture filter expression active during capture (value type: <see cref="string"/>).
    /// The PCAPNG option stores a filter-kind byte first; the property holds the text after it.
    /// </summary>
    internal const string Filter = "if.filter";

    /// <summary>Operating system of the machine where the interface resides (value type: <see cref="string"/>).</summary>
    internal const string Os = "if.os";

    /// <summary>Hardware description of the interface (value type: <see cref="string"/>).</summary>
    internal const string Hardware = "if.hardware";

    /// <summary>Transmit speed in bits per second (value type: <see cref="ulong"/>).</summary>
    internal const string TxSpeed = "if.txspeed";

    /// <summary>Receive speed in bits per second (value type: <see cref="ulong"/>).</summary>
    internal const string RxSpeed = "if.rxspeed";

    /// <summary>Raw numeric link-type code for diagnostics (value type: <see cref="ushort"/>).</summary>
    internal const string RawLinkType = "if.raw_link_type";

    #endregion

    #region Capture-level metadata

    /// <summary>Hardware description of the capture device (value type: <see cref="string"/>).</summary>
    internal const string CaptureHardware = "capture.hardware";

    /// <summary>Operating system of the capture machine (value type: <see cref="string"/>).</summary>
    internal const string CaptureOs = "capture.os";

    /// <summary>Application that created the capture file (value type: <see cref="string"/>).</summary>
    internal const string CaptureApplication = "capture.application";

    #endregion

    #region Option table

    /// <summary>
    /// IDB options that round-trip through interface properties: option code, property key, and value kind.
    /// <c>if_name</c> (2) and <c>if_description</c> (3) travel as the interface name and description,
    /// <c>if_tsresol</c> (9) and <c>if_tsoffset</c> (14) are consumed into the timestamps, and
    /// <c>if_fcslen</c> (13) has its own key (<see cref="FcsLength"/>), so none of them is listed here.
    /// </summary>
    internal static ReadOnlySpan<PcapInterfaceOption> InterfaceOptions => _InterfaceOptions;

    // One shared array: the table is read once per interface registration (reader) and once per IDB (writer).
    private static readonly PcapInterfaceOption[] _InterfaceOptions =
    [
        new(PcapConstants.OptIfSpeed, Speed, PcapOptionValueKind.UInt64),
        new(PcapConstants.OptIfFilter, Filter, PcapOptionValueKind.Filter),
        new(PcapConstants.OptIfOs, Os, PcapOptionValueKind.Utf8),
        new(PcapConstants.OptIfHardware, Hardware, PcapOptionValueKind.Utf8),
        new(PcapConstants.OptIfTxSpeed, TxSpeed, PcapOptionValueKind.UInt64),
        new(PcapConstants.OptIfRxSpeed, RxSpeed, PcapOptionValueKind.UInt64),
    ];

    #endregion
}

/// <summary>Encoding of a PCAPNG option value that maps to an interface property.</summary>
internal enum PcapOptionValueKind : byte
{
    /// <summary>UTF-8 text without terminator; the property holds a <see cref="string"/>.</summary>
    Utf8,

    /// <summary>64-bit unsigned integer in section byte order; the property holds a <see cref="ulong"/>.</summary>
    UInt64,

    /// <summary>One filter-kind byte followed by UTF-8 text; the property holds the text.</summary>
    Filter,
}

/// <summary>One IDB option ↔ interface property mapping.</summary>
/// <param name="Code">PCAPNG option code.</param>
/// <param name="Key">Interface property key.</param>
/// <param name="Kind">Value encoding.</param>
internal readonly record struct PcapInterfaceOption(ushort Code, string Key, PcapOptionValueKind Kind);

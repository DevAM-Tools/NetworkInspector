// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// Keys for PCAP and PCAPNG metadata on <see cref="FrameInterfaceInfo.Properties"/>.
/// PCAPNG readers write these keys. PCAPNG export reads <see cref="FcsLength"/>
/// and writes it as IDB <c>if_fcslen</c>.
/// </summary>
internal static class PcapInterfacePropertyKeys
{
    #region Interface-level metadata

    /// <summary>Interface speed in bits per second (value type: <see cref="ulong"/>).</summary>
    internal const string Speed = "if.speed";

    /// <summary>FCS (Frame Check Sequence) length in bytes (value type: <see cref="byte"/>).</summary>
    internal const string FcsLength = "if.fcs_length";

    /// <summary>Snapshot length — maximum captured octets per packet (value type: <see cref="uint"/>).</summary>
    internal const string SnapLength = "if.snap_length";

    /// <summary>Capture filter expression active during capture (value type: <see cref="string"/>).</summary>
    internal const string Filter = "if.filter";

    /// <summary>Operating system of the machine where the interface resides (value type: <see cref="string"/>).</summary>
    internal const string Os = "if.os";

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
}

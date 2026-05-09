// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Infos;

/// <summary>
/// Well-known property keys for <see cref="FrameInterfaceInfo.Properties"/>.
/// Sources attach these during <see cref="FrameInterfaceRegistry.Register"/> to provide
/// additional metadata about each capture interface.
/// </summary>
public static class FrameInterfacePropertyKeys
{
    #region Interface-level metadata (from PCAPNG IDB options, or equivalent)

    /// <summary>Interface speed in bits per second (value type: <see cref="ulong"/>).</summary>
    public const string Speed = "if.speed";

    /// <summary>FCS (Frame Check Sequence) length in bytes (value type: <see cref="byte"/>).</summary>
    public const string FcsLength = "if.fcs_length";

    /// <summary>Snapshot length — maximum captured octets per packet (value type: <see cref="uint"/>).</summary>
    public const string SnapLength = "if.snap_length";

    /// <summary>Capture filter expression active during capture (value type: <see cref="string"/>).</summary>
    public const string Filter = "if.filter";

    /// <summary>Operating system of the machine where the interface resides (value type: <see cref="string"/>).</summary>
    public const string Os = "if.os";

    /// <summary>Raw numeric link-type code for diagnostics (value type: <see cref="ushort"/>).</summary>
    public const string RawLinkType = "if.raw_link_type";

    #endregion

    #region Capture-level metadata (from PCAPNG SHB options, or equivalent)

    /// <summary>Hardware description of the capture device (value type: <see cref="string"/>).</summary>
    public const string CaptureHardware = "capture.hardware";

    /// <summary>Operating system of the capture machine (value type: <see cref="string"/>).</summary>
    public const string CaptureOs = "capture.os";

    /// <summary>Application that created the capture file (value type: <see cref="string"/>).</summary>
    public const string CaptureApplication = "capture.application";

    #endregion

    #region BLF-specific metadata

    /// <summary>BLF channel number (value type: <see cref="long"/>).</summary>
    public const string BlfChannel = "blf.channel";

    /// <summary>BLF object type identifier (value type: <see cref="uint"/>).</summary>
    public const string BlfObjectType = "blf.object_type";

    /// <summary>BLF bus type constant (value type: <see cref="byte"/>).</summary>
    public const string BlfBusType = "blf.bus_type";
    #endregion
}

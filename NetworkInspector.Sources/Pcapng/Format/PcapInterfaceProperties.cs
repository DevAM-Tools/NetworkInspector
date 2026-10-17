// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// Interface property bags and packet-skip texts shared by <see cref="PcapngSource"/> and
/// <see cref="PcapngStreamSource"/>, so the file reader and the stream reader register identical
/// interfaces and report identical skips.
/// </summary>
/// <remarks>Stateless; all members are thread-safe.</remarks>
internal static class PcapInterfaceProperties
{
    #region Property bags

    /// <summary>
    /// Builds the registry properties of a PCAPNG interface: raw link type, snap length, the table-mapped IDB
    /// options (<see cref="PcapInterfacePropertyKeys.InterfaceOptions"/>), FCS length, and the SHB options of the
    /// owning section.
    /// </summary>
    internal static Dictionary<string, object> ForPcapng(InterfaceInfo info, SectionInfo section)
    {
        // RawLinkType and SnapLength are always available.
        Dictionary<string, object> properties = new()
        {
            [PcapInterfacePropertyKeys.RawLinkType] = info.RawLinkType,
            [PcapInterfacePropertyKeys.SnapLength] = info.SnapLength,
        };

        // Interface-level metadata (IDB options), in file order.
        if (info.OptionProperties is { } optionProperties)
        {
            foreach (KeyValuePair<string, object> option in optionProperties)
            {
                properties[option.Key] = option.Value;
            }
        }

        if (info.FcsLength.HasValue)
        {
            properties[PcapInterfacePropertyKeys.FcsLength] = info.FcsLength.Value;
        }

        // Section-level metadata (SHB options), shared by all interfaces of the section.
        if (section.Hardware is not null)
        {
            properties[PcapInterfacePropertyKeys.CaptureHardware] = section.Hardware;
        }

        if (section.Os is not null)
        {
            properties[PcapInterfacePropertyKeys.CaptureOs] = section.Os;
        }

        if (section.UserApplication is not null)
        {
            properties[PcapInterfacePropertyKeys.CaptureApplication] = section.UserApplication;
        }

        return properties;
    }

    /// <summary>Builds the registry properties of the single legacy PCAP interface (raw link type, snap length).</summary>
    internal static Dictionary<string, object> ForLegacy(LegacyPcapInfo info) => new()
    {
        [PcapInterfacePropertyKeys.RawLinkType] = info.RawLinkType,
        [PcapInterfacePropertyKeys.SnapLength] = info.SnapLength,
    };

    #endregion

    #region Skip texts

    /// <summary>Error kind and message for a packet block that did not produce a frame.</summary>
    internal static (FrameReadErrorKind Kind, string Message) DescribeSkip(ScanPacketResult result) => result switch
    {
        ScanPacketResult.SkipNoInterface => (FrameReadErrorKind.UnresolvedInterface, "Packet block references an interface that was not declared."),
        ScanPacketResult.SkipUnsupportedTimestampResolution => (FrameReadErrorKind.Other, "unsupported if_tsresol"),
        _ => (FrameReadErrorKind.CorruptedBlock, "Malformed packet block."),
    };

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// Metadata extracted from a legacy PCAP global header.
/// Used to configure parsing of the subsequent packet records.
/// </summary>
internal sealed class LegacyPcapInfo
{
    #region Properties

    /// <summary>Whether the file uses swapped byte order.</summary>
    internal bool ByteSwapped
    {
        get;
    }

    /// <summary>Whether timestamps use nanosecond resolution (vs. microsecond).</summary>
    internal bool NanosecondTimestamps
    {
        get;
    }

    /// <summary>Raw link type code from the global header.</summary>
    internal ushort RawLinkType
    {
        get;
    }

    /// <summary>Resolved link type, or null if the raw value is unknown.</summary>
    internal LinkType? LinkType
    {
        get;
    }

    /// <summary>Snapshot length — maximum captured octets per packet.</summary>
    internal uint SnapLength
    {
        get;
    }

    #endregion

    #region Constructors

    /// <summary>Creates a LegacyPcapInfo from the detection result and raw global header fields.</summary>
    internal LegacyPcapInfo(bool byteSwapped, bool nanosecondTimestamps, ushort rawLinkType, uint snapLength)
    {
        ByteSwapped = byteSwapped;
        NanosecondTimestamps = nanosecondTimestamps;
        RawLinkType = rawLinkType;
        LinkType linkCandidate = (LinkType)rawLinkType;
        LinkType = Enum.IsDefined(linkCandidate) ? linkCandidate : null;
        SnapLength = snapLength == 0 ? PcapConstants.DefaultSnapLength : snapLength;
    }

    #endregion

    #region Internal API

    /// <summary>
    /// Converts legacy PCAP timestamp fields to nanoseconds since epoch.
    /// </summary>
    /// <param name="seconds">Seconds since Unix epoch.</param>
    /// <param name="fractional">Microseconds or nanoseconds fraction.</param>
    /// <returns>Timestamp in nanoseconds since Unix epoch.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long TimestampToNanos(uint seconds, uint fractional)
    {
        long nanos = (long)seconds * 1_000_000_000L;
        if (NanosecondTimestamps)
        {
            nanos += fractional;
        }
        else
        {
            // Microseconds → nanoseconds
            nanos += (long)fractional * 1_000L;
        }
        return nanos;
    }

    #endregion
}

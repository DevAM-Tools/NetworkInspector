// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// Metadata about a single capture interface within a PCAPNG section.
/// Built from an Interface Description Block (IDB) and its options.
/// </summary>
internal sealed class InterfaceInfo
{
    #region Properties

    /// <summary>Resolved link type, or null if the raw value is unknown.</summary>
    internal LinkType? LinkType
    {
        get; private set;
    }

    /// <summary>Raw link type code from the IDB.</summary>
    internal ushort RawLinkType
    {
        get;
    }

    /// <summary>Snapshot length — maximum captured octets per packet.</summary>
    internal uint SnapLength
    {
        get; private set;
    }

    /// <summary>
    /// Timestamp resolution: number of timestamp units per second.
    /// Default is 1,000,000 (microsecond resolution).
    /// </summary>
    internal ulong TimestampResolution { get; private set; } = PcapConstants.TsResolMicroseconds;

    /// <summary>Offset applied to all packet timestamps (in timestamp units).</summary>
    internal long TimestampOffset
    {
        get; private set;
    }

    /// <summary>Interface name from if_name option.</summary>
    internal string? Name
    {
        get; private set;
    }

    /// <summary>Interface description from if_description option.</summary>
    internal string? Description
    {
        get; private set;
    }

    /// <summary>Interface speed in bits per second from if_speed option.</summary>
    internal ulong? Speed
    {
        get; private set;
    }

    /// <summary>Capture filter expression from if_filter option.</summary>
    internal string? Filter
    {
        get; private set;
    }

    /// <summary>Operating system from if_os option.</summary>
    internal string? Os
    {
        get; private set;
    }

    /// <summary>FCS length in bytes from if_fcslen option.</summary>
    internal byte? FcsLength
    {
        get; private set;
    }

    #endregion

    #region Constructors

    /// <summary>Creates a new InterfaceInfo with the given raw link type and snap length.</summary>
    internal InterfaceInfo(ushort rawLinkType, uint snapLength)
    {
        RawLinkType = rawLinkType;
        LinkType linkCandidate = (LinkType)rawLinkType;
        LinkType = Enum.IsDefined(linkCandidate) ? linkCandidate : null;
        // Per PCAPNG spec §4.2, SnapLength == 0 means "no packet capture length limit".
        // Store uint.MaxValue internally so all Math.Min operations pass the actual packet
        // length through unchanged, rather than capping every packet to zero.
        SnapLength = snapLength == 0 ? uint.MaxValue : snapLength;
    }

    #endregion

    #region Internal API

    /// <summary>
    /// Sets the timestamp resolution from a raw if_tsresol option byte.
    /// Bit 7 = 0 → resolution is 10^value (e.g. 6 → microseconds).
    /// Bit 7 = 1 → resolution is 2^(value &amp; 0x7F) (binary resolution).
    /// </summary>
    internal void SetTimestampResolution(byte rawResolution)
    {
        if ((rawResolution & 0x80) != 0)
        {
            // Binary power: 2^(value & 0x7F)
            int exponent = rawResolution & 0x7F;
            TimestampResolution = 1UL << exponent;
        }
        else
        {
            // Decimal power: 10^value.
            // 10^19 ≈ 1e19 < 2^64 ≈ 1.84e19; 10^20 > 2^64, so exponent > 19 overflows ulong.
            // Cap at 19 and fall back to nanosecond resolution (10^9) for higher values to
            // prevent silent ulong wrap-around that would produce nonsensical timestamps.
            const int MaxDecimalExponent = 19;
            if (rawResolution > MaxDecimalExponent)
            {
                // Exponent is too large for ulong; fall back to nanosecond resolution (10^9).
                TimestampResolution = PcapConstants.TsResolNanoseconds;
                return;
            }

            ulong resolution = 1;
            for (int i = 0; i < rawResolution; i++)
            {
                resolution *= 10;
            }
            TimestampResolution = resolution;
        }
    }

    /// <summary>Sets the timestamp offset.</summary>
    internal void SetTimestampOffset(long offset) => TimestampOffset = offset;

    /// <summary>Sets the interface name.</summary>
    internal void SetName(string name) => Name = name;

    /// <summary>Sets the interface description.</summary>
    internal void SetDescription(string description) => Description = description;

    /// <summary>Sets the interface speed in bits per second.</summary>
    internal void SetSpeed(ulong speed) => Speed = speed;

    /// <summary>Sets the capture filter expression.</summary>
    internal void SetFilter(string filter) => Filter = filter;

    /// <summary>Sets the operating system.</summary>
    internal void SetOs(string os) => Os = os;

    /// <summary>Sets the FCS length.</summary>
    internal void SetFcsLength(byte fcsLength) => FcsLength = fcsLength;

    /// <summary>
    /// Converts a raw 64-bit PCAPNG timestamp to nanoseconds since epoch.
    /// Applies the interface's timestamp resolution and offset.
    /// </summary>
    /// <param name="rawTimestamp">Raw 64-bit timestamp from the packet block.</param>
    /// <returns>Timestamp in nanoseconds since Unix epoch.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long TimestampToNanos(ulong rawTimestamp)
    {
        // Apply offset first (in timestamp units)
        long adjusted = (long)rawTimestamp + TimestampOffset;

        // Convert to nanoseconds based on resolution
        // Formula: nanos = adjusted * (1_000_000_000 / resolution)
        // To avoid overflow for high-resolution sources, we use the equivalent:
        // nanos = adjusted / resolution * 1_000_000_000 + (adjusted % resolution) * 1_000_000_000 / resolution
        if (TimestampResolution == PcapConstants.TsResolNanoseconds)
        {
            // Already nanoseconds — no conversion needed
            return adjusted;
        }
        if (TimestampResolution == PcapConstants.TsResolMicroseconds)
        {
            // Microseconds → nanoseconds: multiply by 1000.
            // Use checked to detect captures spanning more than ~292 years from epoch.
            // On overflow, saturate to long.MaxValue rather than propagating an exception
            // (callers are not prepared for TimestampToNanos to throw).
            try
            {
                return checked(adjusted * 1_000);
            }
            catch (OverflowException)
            {
                return adjusted < 0 ? long.MinValue : long.MaxValue;
            }
        }

        // General case — split to avoid overflow
        long wholeSeconds = adjusted / (long)TimestampResolution;
        long remainder = adjusted % (long)TimestampResolution;

        // Guard against overflow in the final nanosecond accumulation.
        // wholeSeconds * 1_000_000_000L can overflow for extreme timestamps.
        try
        {
            return checked(wholeSeconds * 1_000_000_000L + remainder * 1_000_000_000L / (long)TimestampResolution);
        }
        catch (OverflowException)
        {
            return adjusted < 0 ? long.MinValue : long.MaxValue;
        }
    }

    #endregion
}

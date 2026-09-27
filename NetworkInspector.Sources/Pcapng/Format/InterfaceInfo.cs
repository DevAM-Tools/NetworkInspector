// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// Metadata about a single capture interface within a PCAPNG section.
/// Built from an Interface Description Block (IDB) and its options.
/// </summary>
internal sealed class InterfaceInfo
{
    #region Properties

    /// <summary>
    /// Resolved link type, or null when <see cref="RawLinkType"/> is not a defined <see cref="Core.LinkType"/> member.
    /// Unknown DLTs do not produce frames; the packet is skipped.
    /// </summary>
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

    /// <summary>Seconds added to each packet timestamp (PCAPNG if_tsoffset). Default 0.</summary>
    internal long TimestampOffsetSeconds
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

    /// <summary>
    /// False when <c>if_tsresol</c> is a decimal exponent above 19.
    /// Packets on this interface are skipped; the clock is not guessed.
    /// </summary>
    internal bool HasSupportedTimestampResolution
    {
        get; private set;
    } = true;

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
    /// Binary exponents above 63 do not fit in <see cref="ulong"/> and are rejected.
    /// A decimal exponent above 19 does not fit in <see cref="ulong"/> and is rejected.
    /// </summary>
    /// <returns>False when the exponent does not fit. The interface is then marked unsupported.</returns>
    internal bool TrySetTimestampResolution(byte rawResolution)
    {
        if ((rawResolution & 0x80) != 0)
        {
            // Binary power: 2^(value & 0x7F). A ulong shift of 64 or more wraps to 1,
            // so exponents 64..127 are rejected instead of stored as 2^63.
            int exponent = rawResolution & 0x7F;
            if (exponent > 63)
            {
                HasSupportedTimestampResolution = false;
                return false;
            }

            TimestampResolution = 1UL << exponent;
            HasSupportedTimestampResolution = true;
            return true;
        }

        // Decimal power: 10^value.
        // 10^19 fits in ulong. 10^20 does not. Do not substitute another clock.
        const int MaxDecimalExponent = 19;
        if (rawResolution > MaxDecimalExponent)
        {
            HasSupportedTimestampResolution = false;
            return false;
        }

        ulong resolution = 1;
        for (int i = 0; i < rawResolution; i++)
        {
            resolution *= 10;
        }

        TimestampResolution = resolution;
        HasSupportedTimestampResolution = true;
        return true;
    }

    /// <summary>Sets the timestamp offset in seconds (PCAPNG if_tsoffset).</summary>
    internal void SetTimestampOffset(long offsetSeconds) => TimestampOffsetSeconds = offsetSeconds;

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
    /// Converts interface ticks first, then adds <see cref="TimestampOffsetSeconds"/> as whole seconds.
    /// </summary>
    /// <param name="rawTimestamp">Raw 64-bit timestamp from the packet block.</param>
    /// <returns>Timestamp in nanoseconds since Unix epoch.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long TimestampToNanos(ulong rawTimestamp)
    {
        long nanos = _TicksToNanos(rawTimestamp);
        if (TimestampOffsetSeconds == 0)
        {
            return nanos;
        }

        const long nsPerSecond = 1_000_000_000L;
        if (TimestampOffsetSeconds > long.MaxValue / nsPerSecond)
        {
            return long.MaxValue;
        }

        if (TimestampOffsetSeconds < long.MinValue / nsPerSecond)
        {
            return long.MinValue;
        }

        return _SaturatingAdd(nanos, TimestampOffsetSeconds * nsPerSecond);
    }

    /// <summary>
    /// Converts raw interface ticks to nanoseconds without applying if_tsoffset.
    /// The tick count stays unsigned until the nanosecond value is known to fit in <see cref="long"/>.
    /// A tick count above <see cref="long.MaxValue"/> saturates; it does not become negative.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long _TicksToNanos(ulong rawTimestamp)
    {
        ulong resolution = TimestampResolution;
        if (resolution == 0)
        {
            return 0;
        }

        const ulong nsPerSecond = 1_000_000_000UL;

        if (resolution == PcapConstants.TsResolNanoseconds)
        {
            if (rawTimestamp > (ulong)long.MaxValue)
            {
                return long.MaxValue;
            }

            return (long)rawTimestamp;
        }

        if (resolution == PcapConstants.TsResolMicroseconds)
        {
            // Microseconds → nanoseconds: multiply by 1000.
            if (rawTimestamp > (ulong)(long.MaxValue / 1000))
            {
                return long.MaxValue;
            }

            return (long)rawTimestamp * 1000L;
        }

        // nanos = wholeSeconds * 1e9 + (remainder * 1e9 / resolution).
        // Remainder uses a widened product when resolution * 1e9 would wrap a ulong.
        ulong wholeSeconds = rawTimestamp / resolution;
        ulong remainder = rawTimestamp % resolution;
        if (wholeSeconds > (ulong)(long.MaxValue / (long)nsPerSecond))
        {
            return long.MaxValue;
        }

        long fromSeconds = (long)wholeSeconds * (long)nsPerSecond;
        ulong remNs = _RemainderToNanos(remainder, resolution, nsPerSecond);
        if ((ulong)fromSeconds > (ulong)long.MaxValue - remNs)
        {
            return long.MaxValue;
        }

        return fromSeconds + (long)remNs;
    }

    /// <summary>
    /// Converts the fractional tick count to nanoseconds.
    /// Uses <see cref="UInt128"/> when <c>remainder * nsPerSecond</c> does not fit in <see cref="ulong"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong _RemainderToNanos(ulong remainder, ulong resolution, ulong nsPerSecond)
    {
        if (remainder == 0)
        {
            return 0;
        }

        if (resolution <= ulong.MaxValue / nsPerSecond || remainder <= ulong.MaxValue / nsPerSecond)
        {
            return remainder * nsPerSecond / resolution;
        }

        return (ulong)((UInt128)remainder * nsPerSecond / resolution);
    }

    /// <summary>Adds two longs, saturating at <see cref="long.MinValue"/> / <see cref="long.MaxValue"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long _SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
        {
            return long.MaxValue;
        }

        if (right < 0 && left < long.MinValue - right)
        {
            return long.MinValue;
        }

        return left + right;
    }

    #endregion
}

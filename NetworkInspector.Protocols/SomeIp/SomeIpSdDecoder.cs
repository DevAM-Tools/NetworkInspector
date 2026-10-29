// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// The anomalies of a service discovery payload. Each flag is one named diagnostic (<c>someip_sd.error.&lt;kind&gt;</c>) and one
/// index group, so a filter on a diagnostic can be answered from the index without expanding the message.
/// </summary>
[Flags]
internal enum SomeIpSdErrors : ushort
{
    /// <summary>No anomaly.</summary>
    None = 0,

    /// <summary>The payload is shorter than the fixed part, or an array declares more bytes than the payload holds.</summary>
    Truncated = 1 << 0,

    /// <summary>The entries array is not a whole number of 16-byte entries.</summary>
    EntriesMalformed = 1 << 1,

    /// <summary>The entries array has length zero.</summary>
    EntriesEmpty = 1 << 2,

    /// <summary>An entry type other than find, offer, subscribe, subscribe acknowledgement.</summary>
    EntryUnknown = 1 << 3,

    /// <summary>An offer with a non-zero time to live that references no option, so it announces no endpoint.</summary>
    OfferWithoutEndpoint = 1 << 4,

    /// <summary>A subscribe that directly follows the stop-subscribe of the same service, instance and eventgroup.</summary>
    SubscribeAfterStop = 1 << 5,

    /// <summary>The options array or one option ends beyond the payload.</summary>
    OptionsTruncated = 1 << 6,

    /// <summary>Fewer than three bytes remain after the last whole option, too few for the next option's length and type.</summary>
    OptionsBytesLeft = 1 << 7,

    /// <summary>An option type that the specification does not define.</summary>
    OptionUnknown = 1 << 8,

    /// <summary>A defined option whose size differs from the size its type requires.</summary>
    OptionWrongLength = 1 << 9,

    /// <summary>A multicast or service discovery endpoint option whose layer-4 protocol is TCP.</summary>
    L4Unsupported = 1 << 10,

    /// <summary>A configuration item whose length byte reaches beyond the option.</summary>
    ConfigMalformed = 1 << 11,

    /// <summary>More options than an entry can reference (271); the surplus is shown as raw bytes.</summary>
    TooManyOptions = 1 << 12,
}

/// <summary>How an option's bytes are interpreted.</summary>
internal enum SomeIpSdOptionKind : byte
{
    /// <summary>An option type that is not defined; the bytes after the type are raw.</summary>
    Unknown,

    /// <summary>A defined option whose size does not match its type; the bytes after the type are raw.</summary>
    WrongLength,

    /// <summary>A configuration option: a list of length-prefixed items.</summary>
    Configuration,

    /// <summary>A load balancing option: priority and weight.</summary>
    LoadBalancing,

    /// <summary>An IPv4 endpoint, multicast or service discovery endpoint option.</summary>
    Ipv4Endpoint,

    /// <summary>An IPv6 endpoint, multicast or service discovery endpoint option.</summary>
    Ipv6Endpoint,
}

/// <summary>
/// What the eager scan of a service discovery payload found: exactly the facts the index needs and the summary of the packet
/// list, with the guarantee that the lazy subtree contains precisely what these flags announce.
/// </summary>
/// <param name="HasContainer">The fixed part exists, so the <c>someip_sd</c> container is emitted.</param>
/// <param name="HasEntries">The entries array holds at least one byte, so the entries container is emitted.</param>
/// <param name="HasOptions">The options array holds at least one byte, so the options container is emitted.</param>
/// <param name="UnicastFlag">The unicast flag of the message is set.</param>
/// <param name="EntryKinds">The union of <see cref="SomeIpTables.SdKind"/> bits of the entries.</param>
/// <param name="Errors">Every anomaly that the lazy subtree will show.</param>
internal readonly record struct SomeIpSdScan(
    bool HasContainer,
    bool HasEntries,
    bool HasOptions,
    bool UnicastFlag,
    byte EntryKinds,
    SomeIpSdErrors Errors);

/// <summary>
/// The events of a walk over a service discovery payload. The walk is written once (<see cref="SomeIpSdDecoder.Walk"/>) and both
/// the eager scan and the lazy population consume its events, so the index that the scan feeds and the tree that the population
/// builds cannot disagree: a second implementation of the length rules would drift sooner or later.
/// </summary>
internal interface ISomeIpSdVisitor
{
    /// <summary>The fixed part exists: flags byte, three reserved bytes, the declared length of the entries array.</summary>
    void Container(byte flags, uint reserved, uint entriesLength);

    /// <summary>An anomaly of the payload or of an array as a whole.</summary>
    void ArrayError(SomeIpSdErrors error);

    /// <summary>The entries array has bytes: <paramref name="wholeEntries"/> complete entries and <paramref name="rawBytes"/> bytes behind them.</summary>
    void EntriesBegin(int wholeEntries, int rawBytes);

    /// <summary>One complete entry of 16 bytes with the anomalies that belong to it.</summary>
    void Entry(int index, int offset, ReadOnlySpan<byte> entry, SomeIpSdErrors errors);

    /// <summary>The bytes behind the last whole entry.</summary>
    void EntriesRaw(int offset, ReadOnlySpan<byte> raw);

    /// <summary>The declared length of the options array (present when four bytes follow the entries).</summary>
    void OptionsLength(uint length);

    /// <summary>The options array has <paramref name="optionBytes"/> bytes.</summary>
    void OptionsBegin(int optionBytes);

    /// <summary>One whole option with its interpretation and the anomalies that belong to it.</summary>
    void Option(int index, int offset, ReadOnlySpan<byte> option, SomeIpSdOptionKind kind, SomeIpSdErrors errors);

    /// <summary>Bytes of the options array that are not a decodable option (cut off, too few, beyond the option limit).</summary>
    void OptionsRaw(int offset, ReadOnlySpan<byte> raw);
}

/// <summary>
/// The decoder of SOME/IP service discovery payloads: the walk over the bytes and the eager scan that is built on it.
/// <para>
/// Payload layout (all big-endian): one flags byte, three reserved bytes, the length of the entries array (4 bytes), the entries
/// (16 bytes each), the length of the options array (4 bytes), the options. An option is a 2-byte length (counting the bytes
/// behind the type), a 1-byte type and its data, so its size is length plus three.
/// </para>
/// <para>
/// <b>Security:</b> every length is untrusted. They are compared as unsigned values against the bytes that exist, and an array
/// that declares more than exists is walked as far as the bytes go and reported; no allocation depends on a declared size, and
/// the number of decoded options is bounded by the format (an entry addresses an option by an 8-bit index and a 4-bit count, so
/// at most 271 options are reachable). The walk touches each byte at most once.
/// </para>
/// <para><b>Thread-safety:</b> stateless; the walk allocates nothing, so any number of threads may walk any number of payloads.</para>
/// </summary>
internal static class SomeIpSdDecoder
{
    #region Constants

    /// <summary>Size of the fixed part: flags (1), reserved (3), entries length (4).</summary>
    internal const int FixedPartSize = 8;

    /// <summary>Size of one entry.</summary>
    internal const int EntrySize = 16;

    /// <summary>The unicast flag in the flags byte (bit 6).</summary>
    internal const byte UnicastFlagMask = 0x40;

    /// <summary>The reboot flag in the flags byte (bit 7).</summary>
    internal const byte RebootFlagMask = 0x80;

    /// <summary>The explicit initial data request flag in the flags byte (bit 5).</summary>
    internal const byte InitialEventsFlagMask = 0x20;

    /// <summary>Entry type: find a service.</summary>
    internal const byte TypeFind = 0x00;

    /// <summary>Entry type: offer a service.</summary>
    internal const byte TypeOffer = 0x01;

    /// <summary>Entry type: subscribe to an eventgroup.</summary>
    internal const byte TypeSubscribe = 0x06;

    /// <summary>Entry type: acknowledge a subscription.</summary>
    internal const byte TypeSubscribeAck = 0x07;

    /// <summary>Option type: configuration.</summary>
    internal const byte OptionConfiguration = 0x01;

    /// <summary>Option type: load balancing.</summary>
    internal const byte OptionLoadBalancing = 0x02;

    /// <summary>Size of an IPv4 endpoint option: length (2), type (1), reserved (1), address (4), reserved (1), protocol (1), port (2).</summary>
    internal const int Ipv4OptionSize = 12;

    /// <summary>Size of an IPv6 endpoint option: as the IPv4 option with a 16-byte address.</summary>
    internal const int Ipv6OptionSize = 24;

    /// <summary>Size of a load balancing option: length (2), type (1), reserved (1), priority (2), weight (2).</summary>
    internal const int LoadBalancingOptionSize = 8;

    #endregion

    #region Eager scan

    /// <summary>
    /// Walks the payload and reports what the lazy subtree will contain. Allocates nothing.
    /// </summary>
    /// <param name="sd">The payload of the SOME/IP message behind its header.</param>
    /// <param name="learnedPorts">
    /// Where to record the UDP ports that the entries announce, or <see langword="null"/> to learn nothing. Only the ordered first
    /// parse passes a set, and only when the user switched learning on: a replay of the packet must not change what later packets
    /// see.
    /// </param>
    /// <returns>The facts for the index and the packet summary.</returns>
    internal static SomeIpSdScan Scan(ReadOnlySpan<byte> sd, SomeIpLearnedPorts? learnedPorts = null)
    {
        // 272 bits: one per option an entry can reference (indices up to 255 plus runs of up to 15 stay below 271). Stack memory,
        // so learning adds no allocation to the scan.
        Span<ulong> referenced = stackalloc ulong[5];
        ScanVisitor visitor = new(learnedPorts, referenced);
        Walk(sd, ref visitor);
        return new SomeIpSdScan(visitor.HasContainer, visitor.HasEntries, visitor.HasOptions, visitor.Unicast, visitor.Kinds, visitor.Errors);
    }

    /// <summary>
    /// The visitor that collects the scan. A <c>ref struct</c> because it keeps a span on the stack of the caller, so the walk
    /// allocates nothing.
    /// <para>
    /// Learning needs the options an entry references, but the entries come before the options in the payload. The entries
    /// therefore mark the option indices they reference (a bitmap), and the options are looked up in it when they come by. Only
    /// entries that offer or subscribe (time to live above zero) and name a real service (not the "other service" id) teach a
    /// port: a withdrawn service announces nothing, and the other-service entry names no service of its own.
    /// </para>
    /// </summary>
    private ref struct ScanVisitor : ISomeIpSdVisitor
    {
        private readonly SomeIpLearnedPorts? _Learn;
        private readonly Span<ulong> _Referenced;

        internal bool HasContainer;
        internal bool HasEntries;
        internal bool HasOptions;
        internal bool Unicast;
        internal byte Kinds;
        internal SomeIpSdErrors Errors;

        internal ScanVisitor(SomeIpLearnedPorts? learn, Span<ulong> referenced)
        {
            _Learn = learn;
            _Referenced = referenced;
            HasContainer = false;
            HasEntries = false;
            HasOptions = false;
            Unicast = false;
            Kinds = 0;
            Errors = SomeIpSdErrors.None;
        }

        public void Container(byte flags, uint reserved, uint entriesLength)
        {
            HasContainer = true;
            Unicast = (flags & UnicastFlagMask) != 0;
        }

        public void ArrayError(SomeIpSdErrors error) => Errors |= error;

        public void EntriesBegin(int wholeEntries, int rawBytes) => HasEntries = true;

        public void Entry(int index, int offset, ReadOnlySpan<byte> entry, SomeIpSdErrors errors)
        {
            Errors |= errors;
            uint ttl = TimeToLive(entry);
            Kinds |= KindOf(entry[0], ttl == 0);

            if (_Learn is null || ttl == 0 || BinaryPrimitives.ReadUInt16BigEndian(entry[4..]) == SomeIpTables.SdOtherServiceId)
            {
                return;
            }

            // The two runs of referenced options: first index and count in bytes 1 and 3 (high nibble), second in bytes 2 and 3
            // (low nibble).
            _Mark(entry[1], entry[3] >> 4);
            _Mark(entry[2], entry[3] & 0x0F);
        }

        public void EntriesRaw(int offset, ReadOnlySpan<byte> raw)
        {
        }

        public void OptionsLength(uint length)
        {
        }

        public void OptionsBegin(int optionBytes) => HasOptions = true;

        public void Option(int index, int offset, ReadOnlySpan<byte> option, SomeIpSdOptionKind kind, SomeIpSdErrors errors)
        {
            Errors |= errors;
            if (_Learn is null || !_IsReferenced(index))
            {
                return;
            }

            // Only datagram endpoints teach a port: the learned ports decode UDP traffic. The protocol byte follows the
            // reserved byte behind the address; the port is the last two bytes of the option.
            if (kind == SomeIpSdOptionKind.Ipv4Endpoint && option[9] == SomeIpTables.L4Udp)
            {
                _Learn.Learn(BinaryPrimitives.ReadUInt16BigEndian(option[10..]));
            }
            else if (kind == SomeIpSdOptionKind.Ipv6Endpoint && option[21] == SomeIpTables.L4Udp)
            {
                _Learn.Learn(BinaryPrimitives.ReadUInt16BigEndian(option[22..]));
            }
        }

        public void OptionsRaw(int offset, ReadOnlySpan<byte> raw)
        {
        }

        private readonly void _Mark(int first, int count)
        {
            for (int i = first; i < first + count; i++)
            {
                _Referenced[i >> 6] |= 1UL << (i & 63);
            }
        }

        private readonly bool _IsReferenced(int index) => (_Referenced[index >> 6] & (1UL << (index & 63))) != 0;
    }

    #endregion

    #region Entry helpers

    /// <summary>The 24-bit time to live of an entry (bytes 9 to 11).</summary>
    /// <param name="entry">The 16 bytes of the entry.</param>
    internal static uint TimeToLive(ReadOnlySpan<byte> entry) => ((uint)entry[9] << 16) | ((uint)entry[10] << 8) | entry[11];

    /// <summary>The kind bit of an entry, or zero for an undefined entry type.</summary>
    /// <param name="type">The entry type byte.</param>
    /// <param name="timeToLiveIsZero">Whether the time to live is zero.</param>
    internal static byte KindOf(byte type, bool timeToLiveIsZero)
    {
        switch (type)
        {
            case TypeFind:
                return SomeIpTables.SdKind.Find;
            case TypeOffer:
                return timeToLiveIsZero
                    ? SomeIpTables.SdKind.StopOffer
                    : SomeIpTables.SdKind.Offer;
            case TypeSubscribe:
                return timeToLiveIsZero
                    ? SomeIpTables.SdKind.StopSubscribe
                    : SomeIpTables.SdKind.Subscribe;
            case TypeSubscribeAck:
                return timeToLiveIsZero
                    ? SomeIpTables.SdKind.SubscribeNack
                    : SomeIpTables.SdKind.SubscribeAck;
            default:
                return 0;
        }
    }

    /// <summary>Whether the entry type is one of the four defined types.</summary>
    /// <param name="type">The entry type byte.</param>
    internal static bool IsEventgroupEntry(byte type) => type is TypeSubscribe or TypeSubscribeAck;

    #endregion

    #region Configuration items

    /// <summary>
    /// Reads the next item of a configuration option. An item is a length byte and that many bytes; a length of zero ends the list.
    /// This is the one implementation of the item rule, used by the walk (to flag a malformed list) and by the population (to append
    /// the items), so both agree where the list ends.
    /// </summary>
    /// <param name="items">The bytes of the option behind its reserved byte.</param>
    /// <param name="position">The read position; advanced past the item, or to the end when the list ends.</param>
    /// <param name="item">The item bytes (without the length byte).</param>
    /// <param name="malformed">Set when an item's length reaches beyond the option.</param>
    /// <returns>True when an item was read.</returns>
    internal static bool NextConfigItem(ReadOnlySpan<byte> items, ref int position, out ReadOnlySpan<byte> item, out bool malformed)
    {
        malformed = false;
        item = default;
        if (position >= items.Length)
        {
            return false;
        }

        int length = items[position];
        if (length == 0)
        {
            position = items.Length;
            return false;
        }

        if (position + 1 + length > items.Length)
        {
            malformed = true;
            position = items.Length;
            return false;
        }

        item = items.Slice(position + 1, length);
        position += 1 + length;
        return true;
    }

    /// <summary>Whether the item list of a configuration option contains an item whose length reaches beyond the option.</summary>
    /// <param name="items">The bytes of the option behind its reserved byte.</param>
    internal static bool IsConfigMalformed(ReadOnlySpan<byte> items)
    {
        int position = 0;
        while (true)
        {
            if (!NextConfigItem(items, ref position, out _, out bool malformed))
            {
                return malformed;
            }
        }
    }
    #endregion

    #region The walk

    /// <summary>
    /// Walks the payload and reports every structural element to the visitor, in payload order. The rules, in the order they apply:
    /// <list type="number">
    /// <item><description>Fewer than 8 bytes: only a <see cref="SomeIpSdErrors.Truncated"/> report; there is no container.</description></item>
    /// <item><description>Entries: a declared length of zero is reported; a declared length beyond the payload is reported and the
    /// walk goes as far as the bytes go; a length that is not a multiple of 16 is reported and the surplus bytes are raw.</description></item>
    /// <item><description>Options: only when the entries array is complete and four more bytes exist; the declared length beyond the
    /// payload is reported and the walk goes as far as the bytes go.</description></item>
    /// <item><description>Per option: size length + 3; a size beyond the array ends the walk with a report; the 272nd option and the
    /// bytes behind it are raw with a report; fewer than three bytes at the end are raw with a report.</description></item>
    /// </list>
    /// </summary>
    /// <typeparam name="TVisitor">The visitor type; a struct, so the call is specialized and does not box.</typeparam>
    /// <param name="sd">The payload.</param>
    /// <param name="visitor">The visitor; receives the events.</param>
    internal static void Walk<TVisitor>(ReadOnlySpan<byte> sd, ref TVisitor visitor)
        where TVisitor : ISomeIpSdVisitor, allows ref struct
    {
        if (sd.Length < FixedPartSize)
        {
            visitor.ArrayError(SomeIpSdErrors.Truncated);
            return;
        }

        // Fixed part: the flags byte, three reserved bytes read as one 24-bit number, and the declared entries length.
        uint entriesLength = BinaryPrimitives.ReadUInt32BigEndian(sd[4..]);
        uint reserved = ((uint)sd[1] << 16) | ((uint)sd[2] << 8) | sd[3];
        visitor.Container(sd[0], reserved, entriesLength);

        int remaining = sd.Length - FixedPartSize;
        _WalkEntries(sd, entriesLength, remaining, ref visitor);

        // The options follow the entries only when the entries array is complete: a cut-off entries array leaves no position at
        // which the options length could be read.
        if (entriesLength > (uint)remaining)
        {
            return;
        }

        int optionsOffset = FixedPartSize + (int)entriesLength;
        if (sd.Length - optionsOffset < 4)
        {
            return;
        }

        _WalkOptions(sd, optionsOffset, ref visitor);
    }

    private static void _WalkEntries<TVisitor>(ReadOnlySpan<byte> sd, uint entriesLength, int remaining, ref TVisitor visitor)
        where TVisitor : ISomeIpSdVisitor, allows ref struct
    {
        if (entriesLength == 0)
        {
            visitor.ArrayError(SomeIpSdErrors.EntriesEmpty);
            return;
        }

        // Compared unsigned against what exists; the walk then goes as far as the bytes go.
        int available = (int)Math.Min(entriesLength, (uint)remaining);
        if (entriesLength > (uint)remaining)
        {
            visitor.ArrayError(SomeIpSdErrors.Truncated);
        }

        if (entriesLength % EntrySize != 0)
        {
            visitor.ArrayError(SomeIpSdErrors.EntriesMalformed);
        }

        if (available == 0)
        {
            return;
        }

        int whole = available / EntrySize;
        int rawBytes = available % EntrySize;
        visitor.EntriesBegin(whole, rawBytes);

        // The previous entry matters for one anomaly only: a subscribe that directly follows the stop-subscribe of the same
        // tuple. "Directly" is literal, so any other entry in between clears the memory.
        bool previousWasStopSubscribe = false;
        ushort previousService = 0;
        ushort previousInstance = 0;
        ushort previousEventgroup = 0;

        for (int i = 0; i < whole; i++)
        {
            ReadOnlySpan<byte> entry = sd.Slice(FixedPartSize + (i * EntrySize), EntrySize);
            byte type = entry[0];
            bool ttlZero = TimeToLive(entry) == 0;
            SomeIpSdErrors errors = SomeIpSdErrors.None;

            if (type is not (TypeFind or TypeOffer or TypeSubscribe or TypeSubscribeAck))
            {
                errors |= SomeIpSdErrors.EntryUnknown;
            }

            // The option counts are the two nibbles of byte 3; an offer that announces no endpoint is useless to a client.
            if (type == TypeOffer && !ttlZero && (entry[3] >> 4) == 0 && (entry[3] & 0x0F) == 0)
            {
                errors |= SomeIpSdErrors.OfferWithoutEndpoint;
            }

            ushort service = BinaryPrimitives.ReadUInt16BigEndian(entry[4..]);
            ushort instance = BinaryPrimitives.ReadUInt16BigEndian(entry[6..]);
            ushort eventgroup = BinaryPrimitives.ReadUInt16BigEndian(entry[14..]);
            if (type == TypeSubscribe && !ttlZero && previousWasStopSubscribe
                && previousService == service && previousInstance == instance && previousEventgroup == eventgroup)
            {
                errors |= SomeIpSdErrors.SubscribeAfterStop;
            }

            previousWasStopSubscribe = type == TypeSubscribe && ttlZero;
            previousService = service;
            previousInstance = instance;
            previousEventgroup = eventgroup;

            visitor.Entry(i, FixedPartSize + (i * EntrySize), entry, errors);
        }

        if (rawBytes > 0)
        {
            visitor.EntriesRaw(FixedPartSize + (whole * EntrySize), sd.Slice(FixedPartSize + (whole * EntrySize), rawBytes));
        }
    }

    private static void _WalkOptions<TVisitor>(ReadOnlySpan<byte> sd, int optionsOffset, ref TVisitor visitor)
        where TVisitor : ISomeIpSdVisitor, allows ref struct
    {
        uint optionsLength = BinaryPrimitives.ReadUInt32BigEndian(sd[optionsOffset..]);
        visitor.OptionsLength(optionsLength);

        int optionsRemaining = sd.Length - optionsOffset - 4;
        int optionBytes = (int)Math.Min(optionsLength, (uint)optionsRemaining);
        if (optionsLength > (uint)optionsRemaining)
        {
            visitor.ArrayError(SomeIpSdErrors.OptionsTruncated);
        }

        if (optionBytes == 0)
        {
            return;
        }

        int optionsStart = optionsOffset + 4;
        ReadOnlySpan<byte> options = sd.Slice(optionsStart, optionBytes);
        visitor.OptionsBegin(optionBytes);

        int position = 0;
        int index = 0;
        while (position < options.Length)
        {
            int rest = options.Length - position;

            // Fewer than three bytes cannot hold the length and the type of another option.
            if (rest < 3)
            {
                visitor.ArrayError(SomeIpSdErrors.OptionsBytesLeft);
                visitor.OptionsRaw(optionsStart + position, options[position..]);
                return;
            }

            // The size is the length field plus the three bytes the length does not count (itself and the type). Formed in int:
            // 65,535 + 3 cannot overflow.
            int size = BinaryPrimitives.ReadUInt16BigEndian(options[position..]) + 3;
            if (size > rest)
            {
                visitor.ArrayError(SomeIpSdErrors.OptionsTruncated);
                visitor.OptionsRaw(optionsStart + position, options[position..]);
                return;
            }

            // An entry addresses an option by an 8-bit index and a 4-bit count, so the last option it can reach is number 271.
            // Further options exist on the wire but nothing can reference them; they are shown raw, not decoded and not dropped.
            if (index == SomeIpTables.SdMaxOptions)
            {
                visitor.ArrayError(SomeIpSdErrors.TooManyOptions);
                visitor.OptionsRaw(optionsStart + position, options[position..]);
                return;
            }

            ReadOnlySpan<byte> option = options.Slice(position, size);
            SomeIpSdOptionKind kind = _Classify(option, out SomeIpSdErrors errors);
            visitor.Option(index, optionsStart + position, option, kind, errors);
            position += size;
            index++;
        }
    }

    /// <summary>
    /// Decides how the bytes of one whole option are read and which anomalies they show. The sizes are the sizes the format
    /// prescribes; a defined type of another size is not guessed at but shown raw.
    /// </summary>
    private static SomeIpSdOptionKind _Classify(ReadOnlySpan<byte> option, out SomeIpSdErrors errors)
    {
        errors = SomeIpSdErrors.None;
        byte type = option[2];
        switch (type)
        {
            case 0x04 or 0x14 or 0x24:
                if (option.Length != Ipv4OptionSize)
                {
                    errors = SomeIpSdErrors.OptionWrongLength;
                    return SomeIpSdOptionKind.WrongLength;
                }

                // Multicast and service discovery endpoints are datagram endpoints: a stream protocol cannot be multicast, and the
                // discovery endpoint is where datagrams are sent. Only the plain endpoint (0x04) may name TCP.
                if (type != 0x04 && option[9] == SomeIpTables.L4Tcp)
                {
                    errors = SomeIpSdErrors.L4Unsupported;
                }

                return SomeIpSdOptionKind.Ipv4Endpoint;

            case 0x06 or 0x16 or 0x26:
                if (option.Length != Ipv6OptionSize)
                {
                    errors = SomeIpSdErrors.OptionWrongLength;
                    return SomeIpSdOptionKind.WrongLength;
                }

                if (type != 0x06 && option[21] == SomeIpTables.L4Tcp)
                {
                    errors = SomeIpSdErrors.L4Unsupported;
                }

                return SomeIpSdOptionKind.Ipv6Endpoint;

            case OptionLoadBalancing:
                if (option.Length != LoadBalancingOptionSize)
                {
                    errors = SomeIpSdErrors.OptionWrongLength;
                    return SomeIpSdOptionKind.WrongLength;
                }

                return SomeIpSdOptionKind.LoadBalancing;

            case OptionConfiguration:
                // The items start behind the reserved byte; a size of three has neither a reserved byte nor items.
                if (option.Length > 4 && IsConfigMalformed(option[4..]))
                {
                    errors = SomeIpSdErrors.ConfigMalformed;
                }

                return SomeIpSdOptionKind.Configuration;

            default:
                errors = SomeIpSdErrors.OptionUnknown;
                return SomeIpSdOptionKind.Unknown;
        }
    }


    #endregion
}

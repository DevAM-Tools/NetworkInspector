// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// Precomputed display texts and protocol constants of SOME/IP and SOME/IP-SD.
/// <para>
/// Every table has exactly 256 entries, one per byte value, and every slot is filled (a value without a defined name gets its plain
/// hex form). A lookup is therefore one array index that cannot go out of range for any <see cref="byte"/> argument; it needs no
/// hashing, no number formatting and no allocation while a packet is parsed. The price is a one-time cost of a few kilobytes of
/// strings in the static initializer, paid at the first use and shared by every stack of the process. Rejected alternative: a
/// <c>switch</c> expression per lookup, which would format the unknown values on every call.
/// </para>
/// <para><b>Thread-safety:</b> the tables are written only by the static initializer and never modified afterwards, so they are
/// safe to read from any thread.</para>
/// </summary>
internal static class SomeIpTables
{
    #region Constants

    /// <summary>Message id of SOME/IP service discovery: service id 0xFFFF, method id 0x8100.</summary>
    internal const uint SdMessageId = 0xFFFF_8100;

    /// <summary>
    /// The service id that a service discovery entry uses to mean "any service" in a find request (0xFFFF). Shown as "(ANY)" by
    /// the entry texts.
    /// </summary>
    internal const ushort SdAnyServiceId = 0xFFFF;

    /// <summary>The service id used by service discovery entries for a service that is not the offered one (0xFFFE).</summary>
    internal const ushort SdOtherServiceId = 0xFFFE;

    /// <summary>
    /// The most options a service discovery message can reference. An entry addresses an option by an 8-bit index and counts its
    /// run in 4 bits, so the highest reachable option is index 255 plus a run of 15 minus one, i.e. 271 options. The 272nd option
    /// cannot be referenced; it is reported, never dropped silently.
    /// </summary>
    internal const int SdMaxOptions = 271;

    /// <summary>
    /// Segments of a segmented (TP) message start at a multiple of 16 bytes, because the offset field counts 16-byte units. Every
    /// segment except the last therefore carries a multiple of 16 payload bytes.
    /// </summary>
    internal const int TpSegmentAlignment = 16;

    /// <summary>IANA IP protocol number of TCP, as carried by service discovery endpoint options.</summary>
    internal const byte L4Tcp = 6;

    /// <summary>IANA IP protocol number of UDP, as carried by service discovery endpoint options.</summary>
    internal const byte L4Udp = 17;

    #endregion

    #region Message type

    // Order matters: the full table is derived from the name table, so the name table is initialized first.
    private static readonly string[] _MessageTypeNameTable = _BuildMessageTypeNameTable();

    private static readonly string[] _MessageTypeTable = _BuildMessageTypeTable();

    private static readonly string[] _BaseTypeTable = _BuildBaseTypeTable();

    /// <summary>The name of one of the five defined base types (request, request without return, notification, response, error), or null.</summary>
    private static string? _BaseName(byte baseType) => baseType switch
    {
        0x00 => "REQUEST",
        0x01 => "REQUEST_NO_RETURN",
        0x02 => "NOTIFICATION",
        0x80 => "RESPONSE",
        0x81 => "ERROR",
        _ => null,
    };

    /// <summary>
    /// Display text of a message type byte: the name with the 0xNN value in parentheses, <c>TP_</c> in front for a segmented
    /// variant and <c>_ACK</c> behind for an acknowledgement variant ("TP_ERROR (0xA1)", "REQUEST_ACK (0x40)"). A value that is
    /// not one of the five base types with optional flags is shown as its plain hex form.
    /// </summary>
    /// <param name="messageType">The message type byte.</param>
    /// <returns>The precomputed text.</returns>
    internal static string MessageType(byte messageType) => _MessageTypeTable[messageType];

    /// <summary>
    /// Display text of a base type (the message type with the TP and ACK flags cleared): "REQUEST (0x00)". A byte that is not a
    /// base type is shown as its plain hex form.
    /// </summary>
    /// <param name="baseType">The base type byte.</param>
    /// <returns>The precomputed text.</returns>
    internal static string BaseType(byte baseType) => _BaseTypeTable[baseType];

    /// <summary>
    /// The name of a message type without the value in parentheses ("TP_ERROR"), used where the value is shown elsewhere: the
    /// summary of the container and the packet list. An unnamed value is shown as its plain hex form.
    /// </summary>
    /// <param name="messageType">The message type byte.</param>
    /// <returns>The precomputed text.</returns>
    internal static string MessageTypeName(byte messageType) => _MessageTypeNameTable[messageType];

    private static string[] _BuildMessageTypeTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            string name = _MessageTypeNameTable[i];
            table[i] = name.StartsWith("0x", StringComparison.Ordinal)
                ? name
                : string.Create(CultureInfo.InvariantCulture, $"{name} ({_Hex((byte)i)})");
        }

        return table;
    }

    private static string[] _BuildMessageTypeNameTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            byte value = (byte)i;
            string? name = _BaseName((byte)(value & SomeIpHeader.BaseTypeMask));
            if (name is null)
            {
                table[i] = _Hex(value);
                continue;
            }

            // The TP variant puts the prefix in front and the ACK variant the suffix behind the base name; both flags together
            // give "TP_REQUEST_ACK".
            string tp = (value & SomeIpHeader.TpFlag) != 0
                ? "TP_"
                : "";
            string ack = (value & SomeIpHeader.AckFlag) != 0
                ? "_ACK"
                : "";
            table[i] = string.Create(CultureInfo.InvariantCulture, $"{tp}{name}{ack}");
        }

        return table;
    }

    private static string[] _BuildBaseTypeTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            string? name = _BaseName((byte)i);
            table[i] = name is null
                ? _Hex((byte)i)
                : string.Create(CultureInfo.InvariantCulture, $"{name} ({_Hex((byte)i)})");
        }

        return table;
    }

    #endregion

    #region Return code

    private static readonly string[] _ReturnCodeTable = _BuildReturnCodeTable();

    /// <summary>
    /// Display text of a return code: the names 0x00 to 0x0F that the specification defines ("E_OK (0x00)"); every other value is
    /// shown as its plain hex form.
    /// </summary>
    /// <param name="returnCode">The return code byte.</param>
    /// <returns>The precomputed text.</returns>
    internal static string ReturnCode(byte returnCode) => _ReturnCodeTable[returnCode];

    private static string[] _BuildReturnCodeTable()
    {
        string[] names =
        [
            "E_OK", "E_NOT_OK", "E_UNKNOWN_SERVICE", "E_UNKNOWN_METHOD", "E_NOT_READY", "E_NOT_REACHABLE", "E_TIMEOUT",
            "E_WRONG_PROTOCOL_VERSION", "E_WRONG_INTERFACE_VERSION", "E_MALFORMED_MESSAGE", "E_WRONG_MESSAGE_TYPE", "E_E2E_REPEATED",
            "E_E2E_WRONG_SEQUENCE", "E_E2E", "E_E2E_NOT_AVAILABLE", "E_E2E_NO_NEW_DATA",
        ];

        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = i < names.Length
                ? string.Create(CultureInfo.InvariantCulture, $"{names[i]} ({_Hex((byte)i)})")
                : _Hex((byte)i);
        }

        return table;
    }

    #endregion

    #region Service discovery

    private static readonly string[] _SdEntryTypeNameTable = _BuildSdEntryTypeNameTable(stop: false);

    private static readonly string[] _SdStopEntryTypeNameTable = _BuildSdEntryTypeNameTable(stop: true);

    private static readonly string[] _SdEntryTypeTable = _BuildSdEntryTypeTable(stop: false);

    private static readonly string[] _SdStopEntryTypeTable = _BuildSdEntryTypeTable(stop: true);

    private static readonly string[] _SdSummaryTable = _BuildSdSummaryTable();

    /// <summary>
    /// The kind bits of a service discovery message, in the order the summary lists them. A message carries the union of its
    /// entries' kinds; one byte holds all seven.
    /// </summary>
    internal static class SdKind
    {
        /// <summary>A find entry (type 0x00).</summary>
        internal const byte Find = 1 << 0;

        /// <summary>An offer with a non-zero time to live.</summary>
        internal const byte Offer = 1 << 1;

        /// <summary>An offer with time to live zero: the service is withdrawn.</summary>
        internal const byte StopOffer = 1 << 2;

        /// <summary>A subscribe with a non-zero time to live.</summary>
        internal const byte Subscribe = 1 << 3;

        /// <summary>A subscribe with time to live zero: the subscription ends.</summary>
        internal const byte StopSubscribe = 1 << 4;

        /// <summary>An acknowledgement of a subscription (type 0x07, non-zero time to live).</summary>
        internal const byte SubscribeAck = 1 << 5;

        /// <summary>A rejection of a subscription (type 0x07 with time to live zero).</summary>
        internal const byte SubscribeNack = 1 << 6;
    }

    /// <summary>
    /// Display text of a service discovery entry type with its value in parentheses ("Offer Service (0x01)"). Time to live zero
    /// turns an offer into a stop, a subscribe into a stop, and an acknowledgement into a rejection. Any value that is not one of
    /// the four defined entry types is shown as its plain hex form.
    /// </summary>
    /// <param name="entryType">The entry type byte.</param>
    /// <param name="timeToLiveIsZero">Whether the entry's time to live is zero.</param>
    /// <returns>The precomputed text.</returns>
    internal static string SdEntryType(byte entryType, bool timeToLiveIsZero) =>
        timeToLiveIsZero
            ? _SdStopEntryTypeTable[entryType]
            : _SdEntryTypeTable[entryType];

    /// <summary>The name of a service discovery entry type without the value ("Stop Offer Service"), for summaries.</summary>
    /// <param name="entryType">The entry type byte.</param>
    /// <param name="timeToLiveIsZero">Whether the entry's time to live is zero.</param>
    /// <returns>The precomputed text.</returns>
    internal static string SdEntryTypeName(byte entryType, bool timeToLiveIsZero) =>
        timeToLiveIsZero
            ? _SdStopEntryTypeNameTable[entryType]
            : _SdEntryTypeNameTable[entryType];

    /// <summary>
    /// The packet-info text of a service discovery message: "SOME/IP-SD [Find][Offer] (multicast)". The kinds are the union of the
    /// entries' kinds in a fixed order, so the same set always reads the same. Precomputed for all 128 kind sets and both
    /// directions (256 short strings), because a message is summarized on every parse and the text must not be formatted then.
    /// </summary>
    /// <param name="kinds">The union of <see cref="SdKind"/> bits.</param>
    /// <param name="unicast">Whether the unicast flag of the message is set.</param>
    /// <returns>The precomputed text.</returns>
    internal static string SdSummary(byte kinds, bool unicast)
    {
        // The table holds the multicast text at the even index and the unicast text at the odd one.
        int index = (kinds & 0x7F) << 1;
        if (unicast)
        {
            index |= 1;
        }

        return _SdSummaryTable[index];
    }

    private static string[] _BuildSdSummaryTable()
    {
        string[] names = ["[Find]", "[Offer]", "[StopOffer]", "[Subscribe]", "[StopSubscribe]", "[SubscribeAck]", "[SubscribeNack]"];
        string[] table = new string[256];
        for (int mask = 0; mask < 128; mask++)
        {
            StringBuilder text = new("SOME/IP-SD");
            for (int bit = 0; bit < names.Length; bit++)
            {
                if ((mask & (1 << bit)) != 0)
                {
                    _ = text.Append(' ').Append(names[bit]);
                }
            }

            table[mask << 1] = text.Append(" (multicast)").ToString();
            table[(mask << 1) | 1] = text.Replace("(multicast)", "(unicast)").ToString();
        }

        return table;
    }

    private static string[] _BuildSdEntryTypeNameTable(bool stop)
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = (i, stop) switch
            {
                (0x00, _) => "Find Service",
                (0x01, false) => "Offer Service",
                (0x01, true) => "Stop Offer Service",
                (0x06, false) => "Subscribe Eventgroup",
                (0x06, true) => "Stop Subscribe Eventgroup",
                (0x07, false) => "Subscribe Eventgroup Ack",
                (0x07, true) => "Subscribe Eventgroup Nack",
                _ => _Hex((byte)i),
            };
        }

        return table;
    }

    private static string[] _BuildSdEntryTypeTable(bool stop)
    {
        string[] names = _SdEntryTypeNameTable;
        if (stop)
        {
            names = _SdStopEntryTypeNameTable;
        }

        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = names[i].StartsWith("0x", StringComparison.Ordinal)
                ? names[i]
                : string.Create(CultureInfo.InvariantCulture, $"{names[i]} ({_Hex((byte)i)})");
        }

        return table;
    }

    private static readonly string[] _SdOptionTypeTable = _BuildSdOptionTypeTable();

    private static readonly string[] _L4ProtocolTable = _BuildL4ProtocolTable();

    /// <summary>
    /// Display text of a service discovery option type byte. The upper nibble marks the variant: 0x1 multicast and 0x2 the
    /// service discovery endpoint of the IPv4 (0x04) and IPv6 (0x06) endpoint options.
    /// </summary>
    /// <param name="optionType">The option type byte.</param>
    /// <returns>The precomputed text.</returns>
    internal static string SdOptionType(byte optionType) => _SdOptionTypeTable[optionType];

    private static readonly string[] _SdOptionTypeNameTable = _BuildSdOptionTypeNameTable();

    /// <summary>The name of a service discovery option type without the value ("IPv4 Endpoint"); an undefined type reads "Option (0xNN)".</summary>
    /// <param name="optionType">The option type byte.</param>
    /// <returns>The precomputed text.</returns>
    internal static string SdOptionTypeName(byte optionType) => _SdOptionTypeNameTable[optionType];

    private static string[] _BuildSdOptionTypeNameTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            string text = _SdOptionTypeTable[i];
            int open = text.IndexOf(" (0x", StringComparison.Ordinal);
            table[i] = open < 0
                ? string.Create(CultureInfo.InvariantCulture, $"Option ({text})")
                : text[..open];
        }

        return table;
    }

    /// <summary>
    /// Display text of the layer-4 protocol of an endpoint option. The byte is the IANA IP protocol number; only TCP (6) and UDP
    /// (17) are named, every other number is shown in decimal.
    /// </summary>
    /// <param name="protocol">The IP protocol number.</param>
    /// <returns>The precomputed text.</returns>
    internal static string L4Protocol(byte protocol) => _L4ProtocolTable[protocol];

    private static string[] _BuildSdOptionTypeTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            string? name = i switch
            {
                0x01 => "Configuration",
                0x02 => "Load Balancing",
                0x04 => "IPv4 Endpoint",
                0x06 => "IPv6 Endpoint",
                0x14 => "IPv4 Multicast",
                0x16 => "IPv6 Multicast",
                0x24 => "IPv4 SD Endpoint",
                0x26 => "IPv6 SD Endpoint",
                _ => null,
            };
            table[i] = name is null
                ? _Hex((byte)i)
                : string.Create(CultureInfo.InvariantCulture, $"{name} ({_Hex((byte)i)})");
        }

        return table;
    }

    private static string[] _BuildL4ProtocolTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = i switch
            {
                L4Tcp => "TCP (6)",
                L4Udp => "UDP (17)",
                _ => i.ToString(CultureInfo.InvariantCulture),
            };
        }

        return table;
    }

    #endregion

    #region Helpers

    /// <summary>The <c>0xNN</c> form of a byte, with upper-case hex digits like every other hex text of the host.</summary>
    private static string _Hex(byte value) => string.Create(CultureInfo.InvariantCulture, $"0x{value:X2}");

    #endregion
}

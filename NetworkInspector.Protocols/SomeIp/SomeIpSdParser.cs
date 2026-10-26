// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// SOME/IP Service Discovery (SD) parser.
/// Parses SD payload when message ID = 0xFFFF8100.
/// <para>SD wire format:</para>
/// <code>
/// | Flags(1) | Reserved(3) | Entries Length(4) | Entries... | Options Length(4) | Options... |
/// </code>
/// Each entry is 16 bytes. Options are variable-length (length(2) + type(1) + data).
/// <para>Wireshark ref: packet-someip-sd.c</para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread safety:</b> stateless static class; safe to call concurrently (each call only reads its arguments and
/// appends to the field passed in).
/// </para>
/// <para>
/// <b>Trust boundary:</b> every length and count comes straight from the wire. The parser appends fields while it
/// walks the payload, so a malformed array length is reported as a parse error after the already decoded fields have
/// been appended. No separate limit on the number of entries or options exists: both are bounded by the payload length
/// (16 bytes per entry, at least 3 bytes per option), i.e. by the frame size.
/// </para>
/// <para>
/// All multi-byte values are big-endian. All numbers are read from spans with <see cref="BinaryPrimitives"/>
/// instead of copying bytes; the only string created from payload bytes is the configuration option text.
/// </para>
/// </remarks>
internal static class SomeIpSdParser
{
    /// <summary>SD minimum header size: flags(1) + reserved(3) + entries_length(4).</summary>
    private const int _SdMinHeaderSize = 8;

    /// <summary>SD entry size in bytes (fixed 16 bytes per entry).</summary>
    private const int _SdEntrySize = 16;

    /// <summary>SD message ID (Service 0xFFFF, Method 0x8100).</summary>
    internal const uint SdMessageId = 0xFFFF_8100;

    /// <summary>
    /// Decides which index groups <see cref="Parse"/> will emit fields for, without building any field. The eager parse of
    /// <see cref="SomeIpProtocol"/> records the groups from this result, so the presence index equals the fields that
    /// the tree contains: the same length rules as <see cref="Parse"/> are applied, in the same order, on the same bytes.
    /// </summary>
    /// <param name="sdData">SD payload (after the 16-byte SOME/IP header); may be empty.</param>
    /// <param name="hasContainer">True when the SD container (and the flags) are emitted: at least the 8-byte fixed part is present.</param>
    /// <param name="hasEntries">True when the entries array container is emitted: the declared entries array fits the payload.</param>
    /// <param name="hasOptions">True when the options array container is emitted: its length field is present, fits, and is not zero.</param>
    internal static void DetectGroups(
        ReadOnlySpan<byte> sdData, out bool hasContainer, out bool hasEntries, out bool hasOptions)
    {
        hasContainer = sdData.Length >= _SdMinHeaderSize;
        hasEntries = false;
        hasOptions = false;
        if (!hasContainer)
        {
            return;
        }

        // Entries array: unsigned comparison for the same reason as in Parse (no narrowing of a wire value).
        uint entriesLengthRaw = BinaryPrimitives.ReadUInt32BigEndian(sdData[4..8]);
        if (entriesLengthRaw > (uint)(sdData.Length - _SdMinHeaderSize))
        {
            return;
        }

        hasEntries = true;

        // Options array: no container when its length field is missing, does not fit, or is zero.
        int optionsLengthOffset = _SdMinHeaderSize + (int)entriesLengthRaw;
        if (optionsLengthOffset + 4 > sdData.Length)
        {
            return;
        }

        uint optionsLengthRaw = BinaryPrimitives.ReadUInt32BigEndian(sdData[optionsLengthOffset..(optionsLengthOffset + 4)]);
        int optionsStart = optionsLengthOffset + 4;
        hasOptions = optionsLengthRaw > 0 && optionsLengthRaw <= (uint)(sdData.Length - optionsStart);
    }

    /// <summary>
    /// Parses SOME/IP-SD payload and appends fields to the tree.
    /// Called from <see cref="SomeIpProtocol"/> when message ID == 0xFFFF8100.
    /// </summary>
    /// <param name="parent">The SOME/IP protocol container field.</param>
    /// <param name="sdData">SD payload data (after the 16-byte SOME/IP header).</param>
    /// <param name="fieldIds">Pre-registered field IDs for all SD sub-fields.</param>
    /// <returns>
    /// Zero on success (the caller already accounts for the whole SOME/IP message; the consumed count is not used),
    /// or a <see cref="ParseError"/> when the payload is shorter than the SD header or an array length points past
    /// the payload.
    /// </returns>
    internal static ParseResult Parse(in MutField parent, ReadOnlySpan<byte> sdData,
        in SomeIpSdFieldIds fieldIds)
    {
        // The fixed part (flags, reserved, entries length) must be present before anything is read.
        if (sdData.Length < _SdMinHeaderSize)
        {
            return ParseError.InsufficientDataWithInfo("someip_sd",
                _SdMinHeaderSize, (ulong)sdData.Length);
        }

        byte flags = sdData[0];
        // bytes 1..3 are reserved

        // Length of the entries array in bytes. It stays unsigned until it has been compared with the bytes that are
        // left: narrowing the raw value to int first would turn a wire value above int.MaxValue negative (and make
        // 8 + length wrap for values from 0x7FFFFFF8 up), so the bounds check below would pass and the later slices
        // would throw on a crafted payload.
        uint entriesLengthRaw = BinaryPrimitives.ReadUInt32BigEndian(sdData[4..8]);

        // Create SD container
        MutField sdField = parent.AppendWithCustomText(fieldIds.Container,
            FieldValue.None, "SOME/IP-SD");

        // -- Flags -- (bit 7 reboot, bit 6 unicast, bit 5 explicit initial data events request; remaining bits reserved)
        sdField.AppendWithCustomText(fieldIds.Flags,
            FieldValue.NewU64(flags), Helpers.DisplayTables.FormatHexU8(flags));
        sdField.Append(fieldIds.FlagsReboot, FieldValue.NewBool((flags & 0x80) != 0));
        sdField.Append(fieldIds.FlagsUnicast, FieldValue.NewBool((flags & 0x40) != 0));
        sdField.Append(fieldIds.FlagsInitialEvents, FieldValue.NewBool((flags & 0x20) != 0));

        // -- Entries array --
        // The declared array must lie inside the payload; otherwise the message is rejected (the SD container and
        // flags above have already been appended, so the partial tree is still visible for diagnosis).
        int entriesStart = 8; // after flags(1) + reserved(3) + entries_length(4)

        // sdData.Length is at least 8 here, so the number of bytes left after the entries length field is not negative.
        if (entriesLengthRaw > (uint)(sdData.Length - entriesStart))
        {
            return ParseError.InvalidData("someip_sd",
                "Entries array extends beyond SD payload");
        }

        // Now known to be at most the payload length, so the conversion and the sum below cannot overflow.
        int entriesLength = (int)entriesLengthRaw;
        int entriesEnd = entriesStart + entriesLength;

        // A trailing partial entry (length not a multiple of 16) is ignored: the count rounds down and the loop
        // below only visits complete 16-byte entries.
        int entryCount = entriesLength / _SdEntrySize;
        MutField entriesField = sdField.AppendWithCustomText(fieldIds.EntriesContainer,
            FieldValue.None,
            "Entries Array (", entryCount, entryCount == 1 ? " entry)" : " entries)");

        // Parse individual entries: fixed 16-byte stride, so no per-entry length to validate.
        int offset = entriesStart;
        while (offset + _SdEntrySize <= entriesEnd)
        {
            _ParseEntry(in entriesField, sdData[offset..(offset + _SdEntrySize)], in fieldIds);
            offset += _SdEntrySize;
        }

        // -- Options array -- (its 4-byte length field follows the entries array)
        int optionsLengthOffset = entriesEnd;
        if (optionsLengthOffset + 4 > sdData.Length)
        {
            // No options: tolerated (not an error) when there is no space for the options length field, so
            // a message that ends right after the entries array is shown with its entries only.
            return 0;
        }

        uint optionsLengthRaw = BinaryPrimitives.ReadUInt32BigEndian(
            sdData[optionsLengthOffset..(optionsLengthOffset + 4)]);

        // Same bounds rule as for the entries array, and for the same reason compared while still unsigned: the check
        // above guarantees that the options length field lies inside the payload, so the bytes left are not negative.
        int optionsStart = optionsLengthOffset + 4;

        if (optionsLengthRaw > (uint)(sdData.Length - optionsStart))
        {
            return ParseError.InvalidData("someip_sd",
                "Options array extends beyond SD payload");
        }

        int optionsLength = (int)optionsLengthRaw;
        int optionsEnd = optionsStart + optionsLength;

        // An empty options array (length 0) produces no container at all.
        if (optionsLength > 0)
        {
            MutField optionsField = sdField.AppendWithCustomText(fieldIds.OptionsContainer,
                FieldValue.None, "Options Array (", optionsLength, " bytes)");

            _ParseOptions(in optionsField, sdData, optionsStart, optionsEnd, in fieldIds);
        }

        return 0;
    }

    /// <summary>
    /// Parses a single 16-byte SD entry: type(1), index of first option run(1), index of second option run(1),
    /// number of options 1 (high nibble) and 2 (low nibble)(1), service id(2), instance id(2), major version(1),
    /// TTL(3), then 4 bytes whose meaning depends on the entry type.
    /// </summary>
    /// <param name="parent">Entries array container that receives the entry.</param>
    /// <param name="data">Exactly one entry (16 bytes).</param>
    /// <param name="f">Registered field ids.</param>
    private static void _ParseEntry(in MutField parent, ReadOnlySpan<byte> data,
        in SomeIpSdFieldIds f)
    {
        // Defensive: the caller slices exactly 16 bytes, so this guard only protects the fixed offsets below.
        if (data.Length < _SdEntrySize)
        {
            return;
        }

        byte entryType = data[0];
        byte index1 = data[1];
        byte index2 = data[2];
        byte numOpt1 = (byte)((data[3] >> 4) & 0x0F);
        byte numOpt2 = (byte)(data[3] & 0x0F);
        ushort serviceId = BinaryPrimitives.ReadUInt16BigEndian(data[4..6]);
        ushort instanceId = BinaryPrimitives.ReadUInt16BigEndian(data[6..8]);
        byte majorVer = data[8];
        // TTL is a 24-bit field spanning bytes 9-11
        uint ttl = (uint)((data[9] << 16) | (data[10] << 8) | data[11]);

        // Display name varies based on entry type and TTL (TTL = 0 turns offer/subscribe into stop/nack)
        string displayName = _GetEntryDisplayName(entryType, ttl);

        MutField entryField = parent.AppendWithCustomText(f.EntryContainer,
            FieldValue.None, displayName);

        // Entry type with display text
        entryField.AppendWithCustomText(f.EntryType,
            FieldValue.NewU64(entryType),
            SomeIpSdDisplayTables.GetEntryTypeDisplayText(entryType));

        // Option indices and counts
        entryField.Append(f.EntryIndex1, FieldValue.NewU64(index1));
        entryField.Append(f.EntryIndex2, FieldValue.NewU64(index2));
        entryField.Append(f.EntryNumOpt1, FieldValue.NewU64(numOpt1));
        entryField.Append(f.EntryNumOpt2, FieldValue.NewU64(numOpt2));

        // Service and Instance IDs (hex-formatted)
        entryField.AppendWithCustomText(f.EntryServiceId,
            FieldValue.NewU64(serviceId), Helpers.DisplayTables.FormatHexU16(serviceId));
        entryField.AppendWithCustomText(f.EntryInstanceId,
            FieldValue.NewU64(instanceId), Helpers.DisplayTables.FormatHexU16(instanceId));

        // Major version and TTL
        entryField.Append(f.EntryMajorVer, FieldValue.NewU64(majorVer));
        entryField.Append(f.EntryTtl, FieldValue.NewU64(ttl));

        // Last 4 bytes semantics depend on entry type (types below 0x04 are service entries, 0x06 and 0x07 are
        // eventgroup entries; 0x04 and 0x05 are unassigned and are decoded like eventgroup entries):
        //   Service entries (type < 0x04)      -> Minor Version (32-bit)
        //   Eventgroup entries (type >= 0x04)  -> reserved(12 bits) + counter(4 bits) + EventgroupID(2 bytes);
        //                                         only the EventgroupID is decoded, reserved/counter are not shown
        if (entryType < 0x04)
        {
            uint minorVer = BinaryPrimitives.ReadUInt32BigEndian(data[12..16]);
            entryField.Append(f.EntryMinorVer, FieldValue.NewU64(minorVer));
        }
        else
        {
            ushort eventgroupId = BinaryPrimitives.ReadUInt16BigEndian(data[14..16]);
            entryField.AppendWithCustomText(f.EntryEventgroupId,
                FieldValue.NewU64(eventgroupId), Helpers.DisplayTables.FormatHexU16(eventgroupId));
        }
    }

    /// <summary>
    /// Entry display name based on type and TTL. In SOME/IP-SD a TTL of 0 changes the meaning of an entry:
    /// OfferService becomes StopOfferService, SubscribeEventgroup becomes StopSubscribeEventgroup, and
    /// SubscribeEventgroupAck becomes SubscribeEventgroupNack. Other types keep their normal name.
    /// </summary>
    /// <param name="entryType">Entry type byte.</param>
    /// <param name="ttl">24-bit TTL of the entry.</param>
    /// <returns>Short display name for the entry container.</returns>
    private static string _GetEntryDisplayName(byte entryType, uint ttl)
    {
        if (ttl == 0)
        {
            return entryType switch
            {
                0x01 => "StopOfferService",
                0x06 => "StopSubscribeEventgroup",
                0x07 => "SubscribeEventgroupNack",
                _ => SomeIpSdDisplayTables.GetEntryTypeShortName(entryType),
            };
        }

        return SomeIpSdDisplayTables.GetEntryTypeShortName(entryType);
    }

    /// <summary>
    /// Parses all options in the options array. Walks the variable-length options one after another; parsing stops
    /// silently at the first option that is truncated by the array end (nothing after it can be located).
    /// </summary>
    /// <param name="parent">Options array container that receives each option.</param>
    /// <param name="fullData">The whole SD payload; offsets are relative to it.</param>
    /// <param name="start">Offset of the first option.</param>
    /// <param name="end">Exclusive end offset of the options array (already validated against the payload length).</param>
    /// <param name="f">Registered field ids.</param>
    private static void _ParseOptions(in MutField parent, ReadOnlySpan<byte> fullData,
        int start, int end, in SomeIpSdFieldIds f)
    {
        int offset = start;

        while (offset + 3 <= end) // minimum: length(2) + type(1)
        {
            // Option wire format:
            //   length (2 bytes) - number of bytes after the type field (the reserved byte is counted)
            //   type (1 byte)
            //   [reserved (1 byte)] - for most option types
            //   option data
            // Total wire size = length + 3 (per Wireshark packet-someip-sd.c). Every iteration advances by at
            // least 3 bytes, so the loop terminates even for crafted zero-length options.
            int optLength = BinaryPrimitives.ReadUInt16BigEndian(
                fullData[offset..(offset + 2)]);
            byte optType = fullData[offset + 2];

            // A declared size that runs past the array end ends the walk (cannot overflow: optLength <= 65535).
            int totalOptSize = optLength + 3;
            if (offset + totalOptSize > end)
            {
                break;
            }

            _ParseSingleOption(in parent, fullData[offset..(offset + totalOptSize)],
                optLength, optType, in f);
            offset += totalOptSize;
        }
    }

    /// <summary>
    /// Parses a single SD option. Unknown option types still get their container, length and type fields; only their
    /// type-specific data is not decoded. Options whose data is shorter than their type requires are shown
    /// without the missing fields (no error), because the option length was already validated by the caller.
    /// </summary>
    /// <param name="parent">Options array container.</param>
    /// <param name="data">Exactly one option, including its length and type bytes.</param>
    /// <param name="optLength">Length field of the option (bytes after the type byte).</param>
    /// <param name="optType">Option type byte.</param>
    /// <param name="f">Registered field ids.</param>
    private static void _ParseSingleOption(in MutField parent, ReadOnlySpan<byte> data,
        int optLength, byte optType, in SomeIpSdFieldIds f)
    {
        string displayText = SomeIpSdDisplayTables.GetOptionTypeShortName(optType);

        MutField optField = parent.AppendWithCustomText(f.OptionContainer,
            FieldValue.None, displayText);

        // Option length and type
        optField.Append(f.OptionLength, FieldValue.NewU64((ulong)optLength));
        optField.AppendWithCustomText(f.OptionType,
            FieldValue.NewU64(optType),
            SomeIpSdDisplayTables.GetOptionTypeDisplayText(optType));

        // Payload starts at byte 4: skip length(2) + type(1) + reserved(1)
        int payloadStart = 4;

        // Type-specific data. Each branch re-checks that enough bytes exist, because a crafted option can declare a
        // length that is valid for the array walk but too small for its type.

        switch (optType)
        {
            // IPv4 Endpoint / IPv4 Multicast / IPv4 SD Endpoint
            case 0x04 or 0x14 or 0x24:
                _ParseIpv4EndpointOption(in optField, data, payloadStart, in f);
                break;

            // IPv6 Endpoint / IPv6 Multicast / IPv6 SD Endpoint
            case 0x06 or 0x16 or 0x26:
                _ParseIpv6EndpointOption(in optField, data, payloadStart, in f);
                break;

            // Configuration option: the rest of the option is shown as text. Invalid UTF-8 is replaced with U+FFFD
            // by the decoder (no exception); the text is not split into its key/value items. The string length is
            // bounded by the 16-bit option length.
            case 0x01:
                if (data.Length > payloadStart)
                {
                    ReadOnlySpan<byte> configBytes = data[payloadStart..];
                    string configStr = System.Text.Encoding.UTF8.GetString(configBytes);
                    optField.Append(f.OptionConfigString,
                        FieldValue.NewString(configStr));
                }
                break;

            // Load Balancing option
            case 0x02:
                if (data.Length >= payloadStart + 4)
                {
                    ushort priority = BinaryPrimitives.ReadUInt16BigEndian(
                        data[payloadStart..(payloadStart + 2)]);
                    ushort weight = BinaryPrimitives.ReadUInt16BigEndian(
                        data[(payloadStart + 2)..(payloadStart + 4)]);
                    optField.Append(f.OptionLbPriority, FieldValue.NewU64(priority));
                    optField.Append(f.OptionLbWeight, FieldValue.NewU64(weight));
                }
                break;
        }
    }

    /// <summary>
    /// Parses IPv4 endpoint/multicast option: 4 bytes IPv4 + 1 reserved + 1 protocol + 2 port
    /// (12 bytes in total including length, type and the leading reserved byte). Shown without any field when
    /// shorter than that.
    /// </summary>
    /// <param name="optField">Option container that receives the fields.</param>
    /// <param name="data">The whole option, starting at its length field.</param>
    /// <param name="payloadStart">Offset of the first data byte inside <paramref name="data"/> (after the reserved byte).</param>
    /// <param name="f">Registered field ids.</param>
    private static void _ParseIpv4EndpointOption(in MutField optField, ReadOnlySpan<byte> data,
        int payloadStart, in SomeIpSdFieldIds f)
    {
        if (data.Length < payloadStart + 8)
        {
            return;
        }

        uint ipv4Raw = BinaryPrimitives.ReadUInt32BigEndian(
            data[payloadStart..(payloadStart + 4)]);
        optField.Append(f.OptionIpv4, FieldValue.NewIPv4(new IPv4Address(ipv4Raw)));

        byte proto = data[payloadStart + 5];
        optField.AppendWithCustomText(f.OptionProto,
            FieldValue.NewU64(proto),
            SomeIpSdDisplayTables.GetL4ProtoDisplayText(proto));

        ushort port = BinaryPrimitives.ReadUInt16BigEndian(
            data[(payloadStart + 6)..(payloadStart + 8)]);
        optField.Append(f.OptionPort, FieldValue.NewU64(port));
    }

    /// <summary>
    /// Parses IPv6 endpoint/multicast option: 16 bytes IPv6 + 1 reserved + 1 protocol + 2 port
    /// (24 bytes in total including length, type and the leading reserved byte). Shown without any field when
    /// shorter than that.
    /// </summary>
    /// <param name="optField">Option container that receives the fields.</param>
    /// <param name="data">The whole option, starting at its length field.</param>
    /// <param name="payloadStart">Offset of the first data byte inside <paramref name="data"/> (after the reserved byte).</param>
    /// <param name="f">Registered field ids.</param>
    private static void _ParseIpv6EndpointOption(in MutField optField, ReadOnlySpan<byte> data,
        int payloadStart, in SomeIpSdFieldIds f)
    {
        if (data.Length < payloadStart + 20)
        {
            return;
        }

        // The 128-bit address is read as two big-endian halves (high = first 8 bytes), the form IPv6Address expects.
        ulong high = BinaryPrimitives.ReadUInt64BigEndian(
            data[payloadStart..(payloadStart + 8)]);
        ulong low = BinaryPrimitives.ReadUInt64BigEndian(
            data[(payloadStart + 8)..(payloadStart + 16)]);
        optField.Append(f.OptionIpv6, FieldValue.NewIPv6(new IPv6Address(high, low)));

        byte proto = data[payloadStart + 17];
        optField.AppendWithCustomText(f.OptionProto,
            FieldValue.NewU64(proto),
            SomeIpSdDisplayTables.GetL4ProtoDisplayText(proto));

        ushort port = BinaryPrimitives.ReadUInt16BigEndian(
            data[(payloadStart + 18)..(payloadStart + 20)]);
        optField.Append(f.OptionPort, FieldValue.NewU64(port));
    }
}




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
internal static class SomeIpSdParser
{
    /// <summary>SD minimum header size: flags(1) + reserved(3) + entries_length(4).</summary>
    private const int SdMinHeaderSize = 8;

    /// <summary>SD entry size in bytes (fixed 16 bytes per entry).</summary>
    private const int SdEntrySize = 16;

    /// <summary>SD message ID (Service 0xFFFF, Method 0x8100).</summary>
    internal const uint SdMessageId = 0xFFFF_8100;

    /// <summary>
    /// Parses SOME/IP-SD payload and appends fields to the tree.
    /// Called from <see cref="SomeIpProtocol"/> when message ID == 0xFFFF8100.
    /// </summary>
    /// <param name="parent">The SOME/IP protocol container field.</param>
    /// <param name="sdData">SD payload data (after the 16-byte SOME/IP header).</param>
    /// <param name="fieldIds">Pre-registered field IDs for all SD sub-fields.</param>
    /// <param name="context">The parse context providing dispatch resolution and stack access.</param>
    /// <returns>ParseResult indicating success or error.</returns>
    internal static ParseResult Parse(in MutField parent, ReadOnlySpan<byte> sdData,
        in SomeIpSdFieldIds fieldIds, in ParseContext context)
    {
        if (sdData.Length < SdMinHeaderSize)
        {
            return ParseError.InsufficientDataWithInfo("someip_sd",
                SdMinHeaderSize, (ulong)sdData.Length);
        }

        byte flags = sdData[0];
        // bytes 1..3 are reserved

        int entriesLength = (int)BinaryPrimitives.ReadUInt32BigEndian(sdData[4..8]);

        // Create SD container
        MutField sdField = parent.AppendWithCustomText(fieldIds.Container,
            FieldValue.None, "SOME/IP-SD", in context);

        // ── Flags ──
        sdField.AppendWithCustomText(fieldIds.Flags,
            FieldValue.NewU64(flags), Helpers.DisplayTables.FormatHexU8(flags), in context);
        sdField.Append(fieldIds.FlagsReboot, FieldValue.NewBool((flags & 0x80) != 0), in context);
        sdField.Append(fieldIds.FlagsUnicast, FieldValue.NewBool((flags & 0x40) != 0), in context);
        sdField.Append(fieldIds.FlagsInitialEvents, FieldValue.NewBool((flags & 0x20) != 0), in context);

        // ── Entries array ──
        int entriesStart = 8; // after flags(1) + reserved(3) + entries_length(4)
        int entriesEnd = entriesStart + entriesLength;

        if (entriesEnd > sdData.Length)
        {
            return ParseError.InvalidData("someip_sd",
                "Entries array extends beyond SD payload");
        }

        int entryCount = entriesLength / SdEntrySize;
        MutField entriesField = sdField.AppendWithCustomText(fieldIds.EntriesContainer,
            FieldValue.None,
            entryCount == 1 ? "Entries Array (1 entry)" : ZA.Lazy("Entries Array (", entryCount, " entries)"), in context);

        // Parse individual entries
        int offset = entriesStart;
        while (offset + SdEntrySize <= entriesEnd)
        {
            ParseEntry(in entriesField, sdData[offset..(offset + SdEntrySize)], in fieldIds, in context);
            offset += SdEntrySize;
        }

        // ── Options array ──
        int optionsLengthOffset = entriesEnd;
        if (optionsLengthOffset + 4 > sdData.Length)
        {
            // No options — valid if there is no space for the options length field
            return 0;
        }

        int optionsLength = (int)BinaryPrimitives.ReadUInt32BigEndian(
            sdData[optionsLengthOffset..(optionsLengthOffset + 4)]);

        int optionsStart = optionsLengthOffset + 4;
        int optionsEnd = optionsStart + optionsLength;

        if (optionsEnd > sdData.Length)
        {
            return ParseError.InvalidData("someip_sd",
                "Options array extends beyond SD payload");
        }

        if (optionsLength > 0)
        {
            MutField optionsField = sdField.AppendWithCustomText(fieldIds.OptionsContainer,
                FieldValue.None, ZA.Lazy("Options Array (", optionsLength, " bytes)"), in context);

            ParseOptions(in optionsField, sdData, optionsStart, optionsEnd, in fieldIds, in context);
        }

        return 0;
    }

    /// <summary>Parses a single 16-byte SD entry.</summary>
    private static void ParseEntry(in MutField parent, ReadOnlySpan<byte> data,
        in SomeIpSdFieldIds f, in ParseContext context)
    {
        if (data.Length < SdEntrySize)
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

        // Display name varies based on entry type and TTL (TTL=0 → stop/nack)
        string displayName = GetEntryDisplayName(entryType, ttl);

        MutField entryField = parent.AppendWithCustomText(f.EntryContainer,
            FieldValue.None, displayName, in context);

        // Entry type with display text
        entryField.AppendWithCustomText(f.EntryType,
            FieldValue.NewU64(entryType),
            SomeIpSdDisplayTables.GetEntryTypeDisplayText(entryType), in context);

        // Option indices and counts
        entryField.Append(f.EntryIndex1, FieldValue.NewU64(index1), in context);
        entryField.Append(f.EntryIndex2, FieldValue.NewU64(index2), in context);
        entryField.Append(f.EntryNumOpt1, FieldValue.NewU64(numOpt1), in context);
        entryField.Append(f.EntryNumOpt2, FieldValue.NewU64(numOpt2), in context);

        // Service and Instance IDs (hex-formatted)
        entryField.AppendWithCustomText(f.EntryServiceId,
            FieldValue.NewU64(serviceId), Helpers.DisplayTables.FormatHexU16(serviceId), in context);
        entryField.AppendWithCustomText(f.EntryInstanceId,
            FieldValue.NewU64(instanceId), Helpers.DisplayTables.FormatHexU16(instanceId), in context);

        // Major version and TTL
        entryField.Append(f.EntryMajorVer, FieldValue.NewU64(majorVer), in context);
        entryField.Append(f.EntryTtl, FieldValue.NewU64(ttl), in context);

        // Last 4 bytes semantics depend on entry type:
        //   Service entries (type < 0x04) → Minor Version (32-bit)
        //   Eventgroup entries (type >= 0x04) → reserved(1) + flags(1) + EventgroupID(2)
        if (entryType < 0x04)
        {
            uint minorVer = BinaryPrimitives.ReadUInt32BigEndian(data[12..16]);
            entryField.Append(f.EntryMinorVer, FieldValue.NewU64(minorVer), in context);
        }
        else
        {
            ushort eventgroupId = BinaryPrimitives.ReadUInt16BigEndian(data[14..16]);
            entryField.AppendWithCustomText(f.EntryEventgroupId,
                FieldValue.NewU64(eventgroupId), Helpers.DisplayTables.FormatHexU16(eventgroupId), in context);
        }
    }

    /// <summary>
    /// Entry display name based on type and TTL. TTL=0 changes semantics:
    /// OfferService → StopOfferService, SubscribeEventgroup → StopSubscribe, etc.
    /// </summary>
    private static string GetEntryDisplayName(byte entryType, uint ttl)
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

    /// <summary>Parses all options in the options array.</summary>
    private static void ParseOptions(in MutField parent, ReadOnlySpan<byte> fullData,
        int start, int end, in SomeIpSdFieldIds f, in ParseContext context)
    {
        int offset = start;

        while (offset + 3 <= end) // minimum: length(2) + type(1)
        {
            // Option wire format:
            //   length (2 bytes) — bytes after the type field
            //   type (1 byte)
            //   [reserved (1 byte)] — for most option types
            //   option data
            // Total wire size = length + 3 (per Wireshark packet-someip-sd.c)
            int optLength = BinaryPrimitives.ReadUInt16BigEndian(
                fullData[offset..(offset + 2)]);
            byte optType = fullData[offset + 2];

            int totalOptSize = optLength + 3;
            if (offset + totalOptSize > end)
            {
                break;
            }

            ParseSingleOption(in parent, fullData[offset..(offset + totalOptSize)],
                optLength, optType, in f, in context);
            offset += totalOptSize;
        }
    }

    /// <summary>Parses a single SD option.</summary>
    private static void ParseSingleOption(in MutField parent, ReadOnlySpan<byte> data,
        int optLength, byte optType, in SomeIpSdFieldIds f, in ParseContext context)
    {
        string displayText = SomeIpSdDisplayTables.GetOptionTypeShortName(optType);

        MutField optField = parent.AppendWithCustomText(f.OptionContainer,
            FieldValue.None, displayText, in context);

        // Option length and type
        optField.Append(f.OptionLength, FieldValue.NewU64((ulong)optLength), in context);
        optField.AppendWithCustomText(f.OptionType,
            FieldValue.NewU64(optType),
            SomeIpSdDisplayTables.GetOptionTypeDisplayText(optType), in context);

        // Payload starts at byte 4: skip length(2) + type(1) + reserved(1)
        int payloadStart = 4;

        switch (optType)
        {
            // IPv4 Endpoint / IPv4 Multicast / IPv4 SD Endpoint
            case 0x04 or 0x14 or 0x24:
                ParseIpv4EndpointOption(in optField, data, payloadStart, in f, in context);
                break;

            // IPv6 Endpoint / IPv6 Multicast / IPv6 SD Endpoint
            case 0x06 or 0x16 or 0x26:
                ParseIpv6EndpointOption(in optField, data, payloadStart, in f, in context);
                break;

            // Configuration option
            case 0x01:
                if (data.Length > payloadStart)
                {
                    ReadOnlySpan<byte> configBytes = data[payloadStart..];
                    string configStr = System.Text.Encoding.UTF8.GetString(configBytes);
                    optField.Append(f.OptionConfigString,
                        FieldValue.NewString(configStr), in context);
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
                    optField.Append(f.OptionLbPriority, FieldValue.NewU64(priority), in context);
                    optField.Append(f.OptionLbWeight, FieldValue.NewU64(weight), in context);
                }
                break;
        }
    }

    /// <summary>
    /// Parses IPv4 endpoint/multicast option: 4 bytes IPv4 + 1 reserved + 1 protocol + 2 port.
    /// </summary>
    private static void ParseIpv4EndpointOption(in MutField optField, ReadOnlySpan<byte> data,
        int payloadStart, in SomeIpSdFieldIds f, in ParseContext context)
    {
        if (data.Length < payloadStart + 8)
        {
            return;
        }

        uint ipv4Raw = BinaryPrimitives.ReadUInt32BigEndian(
            data[payloadStart..(payloadStart + 4)]);
        optField.Append(f.OptionIpv4, FieldValue.NewIPv4(new IPv4Address(ipv4Raw)), in context);

        byte proto = data[payloadStart + 5];
        optField.AppendWithCustomText(f.OptionProto,
            FieldValue.NewU64(proto),
            SomeIpSdDisplayTables.GetL4ProtoDisplayText(proto), in context);

        ushort port = BinaryPrimitives.ReadUInt16BigEndian(
            data[(payloadStart + 6)..(payloadStart + 8)]);
        optField.Append(f.OptionPort, FieldValue.NewU64(port), in context);
    }

    /// <summary>
    /// Parses IPv6 endpoint/multicast option: 16 bytes IPv6 + 1 reserved + 1 protocol + 2 port.
    /// </summary>
    private static void ParseIpv6EndpointOption(in MutField optField, ReadOnlySpan<byte> data,
        int payloadStart, in SomeIpSdFieldIds f, in ParseContext context)
    {
        if (data.Length < payloadStart + 20)
        {
            return;
        }

        ulong high = BinaryPrimitives.ReadUInt64BigEndian(
            data[payloadStart..(payloadStart + 8)]);
        ulong low = BinaryPrimitives.ReadUInt64BigEndian(
            data[(payloadStart + 8)..(payloadStart + 16)]);
        optField.Append(f.OptionIpv6, FieldValue.NewIPv6(new IPv6Address(high, low)), in context);

        byte proto = data[payloadStart + 17];
        optField.AppendWithCustomText(f.OptionProto,
            FieldValue.NewU64(proto),
            SomeIpSdDisplayTables.GetL4ProtoDisplayText(proto), in context);

        ushort port = BinaryPrimitives.ReadUInt16BigEndian(
            data[(payloadStart + 18)..(payloadStart + 20)]);
        optField.Append(f.OptionPort, FieldValue.NewU64(port), in context);
    }
}

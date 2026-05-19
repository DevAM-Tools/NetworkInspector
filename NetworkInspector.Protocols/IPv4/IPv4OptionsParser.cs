// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Core.Fields;
using NetworkInspector.Protocols.Helpers;
using NetworkInspector.Values;

namespace NetworkInspector.Protocols;

/// <summary>
/// Parses IPv4 options from the options region of the header (bytes 20..headerLen).
/// <para>Supported option types (RFC 791, RFC 2113):</para>
/// <list type="bullet">
/// <item>End of Options List (EOOL, 0x00)</item>
/// <item>No-Operation (NOP, 0x01)</item>
/// <item>Record Route (RR, 0x07)</item>
/// <item>Loose Source Route (LSR, 0x83)</item>
/// <item>Strict Source Route (SSR, 0x89)</item>
/// <item>Internet Timestamp (TS, 0x44)</item>
/// <item>Router Alert (RA, 0x94)</item>
/// <item>Security (SEC, 0x82)</item>
/// <item>Stream Identifier (SID, 0x88)</item>
/// <item>Unknown options (displayed with raw data)</item>
/// </list>
/// </summary>
internal static class IPv4OptionsParser
{
    // IPv4 option type constants
    private const byte Eool = 0x00;
    private const byte Nop = 0x01;
    private const byte RecordRoute = 0x07;
    private const byte Timestamp = 0x44;
    private const byte Security = 0x82;
    private const byte LooseSourceRoute = 0x83;
    private const byte ExtendedSecurity = 0x85;
    private const byte StreamId = 0x88;
    private const byte StrictSourceRoute = 0x89;
    private const byte RouterAlert = 0x94;

    // Minimum option lengths (including type and length bytes)
    private const int MinRouteOptionLen = 3;
    private const int MinTimestampOptionLen = 4;
    private const int RouterAlertLen = 4;
    private const int StreamIdLen = 4;

    // Maximum total options length (IHL max 15 → 60 byte header − 20 byte fixed = 40 bytes)
    private const int MaxOptionsLen = 40;

    // Timestamp flag values
    private const byte TsFlagTimestampsOnly = 0;
    private const byte TsFlagTimestampAndAddr = 1;
    private const byte TsFlagPrespecified = 3;

    /// <summary>
    /// Parses all IPv4 options from the given data span and appends fields to the
    /// options container under <paramref name="optionsContainer"/>.
    /// </summary>
    /// <param name="optionsContainer">The parent field for all options (ip.options).</param>
    /// <param name="data">Options bytes (from offset 20 to header length).</param>
    /// <param name="fields">Field IDs for the option sub-fields.</param>
    /// <param name="context">The parse context providing dispatch resolution and stack access.</param>
    internal static void Parse(
        MutField optionsContainer,
        ReadOnlySpan<byte> data,
        in IPv4Protocol.OptionFieldIds fields, in ParseContext context)
    {
        int len = data.Length;
        if (len == 0 || len > MaxOptionsLen)
        {
            return;
        }

        int offset = 0;
        while (offset < len)
        {
            byte optType = data[offset];

            switch (optType)
            {
                case Eool:
                    // End of Options List — terminates parsing, remaining bytes are padding
                    optionsContainer.AppendWithCustomText(
                        fields.EolFieldId, FieldValue.None,
                        "End of Options List (EOL)", in context);

                    if (offset + 1 < len)
                    {
                        ReadOnlyMemory<byte> padding = data[(offset + 1)..len].ToArray();
                        optionsContainer.Append(fields.PaddingFieldId,
                            FieldValue.NewBytes(padding), in context);
                    }
                    return; // EOOL terminates option parsing

                case Nop:
                    // No-Operation — single byte used for alignment
                    optionsContainer.AppendWithCustomText(
                        fields.NopFieldId, FieldValue.None,
                        "No-Operation (NOP)", in context);
                    offset += 1;
                    break;

                default:
                    // Multi-byte option: type(1) + length(1) + data(N)
                    if (offset + 1 >= len)
                    {
                        return; // Malformed: no length byte
                    }

                    int optLen = data[offset + 1];
                    if (optLen < 2 || offset + optLen > len)
                    {
                        return; // Malformed: invalid length
                    }

                    ReadOnlySpan<byte> optData = data.Slice(offset, optLen);
                    ParseMultiByteOption(optionsContainer, optType, optData, in fields, in context);
                    offset += optLen;
                    break;
            }
        }
    }

    /// <summary>Dispatches a multi-byte option to the appropriate specific parser.</summary>
    private static void ParseMultiByteOption(
        MutField optionsContainer,
        byte optType,
        ReadOnlySpan<byte> optData,
        in IPv4Protocol.OptionFieldIds fields, in ParseContext context)
    {
        switch (optType)
        {
            case RecordRoute:
                ParseRecordRouteOption(optionsContainer, optData, in fields, in context);
                break;

            case LooseSourceRoute:
                ParseSourceRouteOption(optionsContainer, optData, fields.LooseSourceRouteFieldId, in fields, in context);
                break;

            case StrictSourceRoute:
                ParseSourceRouteOption(optionsContainer, optData, fields.StrictSourceRouteFieldId, in fields, in context);
                break;

            case Timestamp:
                ParseTimestampOption(optionsContainer, optData, in fields, in context);
                break;

            case RouterAlert:
                ParseRouterAlertOption(optionsContainer, optData, in fields, in context);
                break;

            case Security or ExtendedSecurity:
                ParseSecurityOption(optionsContainer, optData, in fields, in context);
                break;

            case StreamId:
                ParseStreamIdOption(optionsContainer, optData, in fields, in context);
                break;

            default:
                ParseUnknownOption(optionsContainer, optType, optData, in fields, in context);
                break;
        }
    }

    #region Specific option parsers

    /// <summary>
    /// Parses a Record Route option (type 0x07).
    /// Format: type(1) + length(1) + pointer(1) + route entries(4 bytes each).
    /// </summary>
    private static void ParseRecordRouteOption(
        MutField optionsContainer,
        ReadOnlySpan<byte> optData,
        in IPv4Protocol.OptionFieldIds fields, in ParseContext context)
    {
        int optLen = optData.Length;
        if (optLen < MinRouteOptionLen)
        {
            return;
        }

        string optName = DisplayTables.GetIpOptionTypeName(RecordRoute);
        byte pointer = optData[2];
        int addrCount = (optLen - 3) / 4;

        MutField container = optionsContainer.AppendWithCustomText(
            fields.RecordRouteFieldId, FieldValue.None,
            (string)ZA.String(optName, " (", optLen, " bytes, ", addrCount, " entries)"), in context);

        AppendOptionTypeFields(container, optData[0], in fields, in context);
        container.Append(fields.OptLenFieldId, FieldValue.NewU64((ulong)optLen), in context);
        container.Append(fields.OptPtrFieldId, FieldValue.NewU64(pointer), in context);

        // Parse recorded route addresses (each 4 bytes starting at offset 3)
        ParseIpv4Addresses(container, optData, 3, fields.OptAddrFieldId, in context);
    }

    /// <summary>
    /// Parses a Loose Source Route (0x83) or Strict Source Route (0x89) option.
    /// Format: type(1) + length(1) + pointer(1) + route entries(4 bytes each).
    /// </summary>
    private static void ParseSourceRouteOption(
        MutField optionsContainer,
        ReadOnlySpan<byte> optData,
        FieldId containerFieldId,
        in IPv4Protocol.OptionFieldIds fields, in ParseContext context)
    {
        int optLen = optData.Length;
        if (optLen < MinRouteOptionLen)
        {
            return;
        }

        string optName = DisplayTables.GetIpOptionTypeName(optData[0]);
        byte pointer = optData[2];
        int addrCount = (optLen - 3) / 4;

        MutField container = optionsContainer.AppendWithCustomText(
            containerFieldId, FieldValue.None,
            (string)ZA.String(optName, " (", optLen, " bytes, ", addrCount, " entries)"), in context);

        AppendOptionTypeFields(container, optData[0], in fields, in context);
        container.Append(fields.OptLenFieldId, FieldValue.NewU64((ulong)optLen), in context);
        container.Append(fields.OptPtrFieldId, FieldValue.NewU64(pointer), in context);

        ParseIpv4Addresses(container, optData, 3, fields.OptAddrFieldId, in context);
    }

    /// <summary>
    /// Parses a Timestamp option (type 0x44).
    /// Format: type(1) + length(1) + pointer(1) + oflw_flag(1) + entries.
    /// </summary>
    private static void ParseTimestampOption(
        MutField optionsContainer,
        ReadOnlySpan<byte> optData,
        in IPv4Protocol.OptionFieldIds fields, in ParseContext context)
    {
        int optLen = optData.Length;
        if (optLen < MinTimestampOptionLen)
        {
            return;
        }

        string optName = DisplayTables.GetIpOptionTypeName(Timestamp);
        byte pointer = optData[2];
        byte oflwFlag = optData[3];
        byte overflow = (byte)((oflwFlag >> 4) & 0x0F);
        byte flag = (byte)(oflwFlag & 0x0F);

        string flagDisplay = DisplayTables.GetIpTimestampFlagDisplayText(flag);

        MutField container = optionsContainer.AppendWithCustomText(
            fields.TimestampFieldId, FieldValue.None,
            (string)ZA.String(optName, " (", optLen, " bytes, ", flagDisplay, ")"), in context);

        AppendOptionTypeFields(container, optData[0], in fields, in context);
        container.Append(fields.OptLenFieldId, FieldValue.NewU64((ulong)optLen), in context);
        container.Append(fields.OptPtrFieldId, FieldValue.NewU64(pointer), in context);
        container.Append(fields.OptOverflowFieldId, FieldValue.NewU64(overflow), in context);
        container.AppendWithCustomText(fields.OptFlagFieldId,
            FieldValue.NewU64(flag), flagDisplay, in context);

        // Parse timestamp entries starting at offset 4
        int entryOffset = 4;
        switch (flag)
        {
            case TsFlagTimestampsOnly:
                // Timestamps only — each 4 bytes
                while (entryOffset + 4 <= optLen)
                {
                    uint tsVal = ReadU32BigEndian(optData, entryOffset);
                    container.Append(fields.OptTimeStampFieldId,
                        FieldValue.NewU64(tsVal), in context);
                    entryOffset += 4;
                }
                break;

            case TsFlagTimestampAndAddr or TsFlagPrespecified:
                // Address + timestamp pairs — each 8 bytes (4 addr + 4 ts)
                while (entryOffset + 8 <= optLen)
                {
                    IPv4Address addr = ReadIpv4Address(optData, entryOffset);
                    uint tsVal = ReadU32BigEndian(optData, entryOffset + 4);
                    container.Append(fields.OptTimeStampAddrFieldId,
                        FieldValue.NewIPv4(addr), in context);
                    container.Append(fields.OptTimeStampFieldId,
                        FieldValue.NewU64(tsVal), in context);
                    entryOffset += 8;
                }
                break;
        }
    }

    /// <summary>
    /// Parses a Router Alert option (type 0x94).
    /// Format: type(1) + length(1) + value(2).
    /// </summary>
    private static void ParseRouterAlertOption(
        MutField optionsContainer,
        ReadOnlySpan<byte> optData,
        in IPv4Protocol.OptionFieldIds fields, in ParseContext context)
    {
        if (optData.Length < RouterAlertLen)
        {
            return;
        }

        string optName = DisplayTables.GetIpOptionTypeName(RouterAlert);
        ushort raValue = (ushort)((optData[2] << 8) | optData[3]);

        // Use static text for known values, dynamic for unknown
        string raDisplay = DisplayTables.GetRouterAlertDisplayText(raValue)
            ?? (string)ZA.String("Unknown (", raValue, ")");

        MutField container = optionsContainer.AppendWithCustomText(
            fields.RouterAlertFieldId, FieldValue.None,
            (string)ZA.String(optName, ": ", raDisplay), in context);

        AppendOptionTypeFields(container, optData[0], in fields, in context);
        container.Append(fields.OptLenFieldId, FieldValue.NewU64((ulong)optData.Length), in context);
        container.AppendWithCustomText(fields.OptRaFieldId,
            FieldValue.NewU64(raValue), raDisplay, in context);
    }

    /// <summary>
    /// Parses a Security option (type 0x82) or Extended Security (type 0x85).
    /// Displays raw data since full classification parsing is rarely needed.
    /// </summary>
    private static void ParseSecurityOption(
        MutField optionsContainer,
        ReadOnlySpan<byte> optData,
        in IPv4Protocol.OptionFieldIds fields, in ParseContext context)
    {
        int optLen = optData.Length;
        string optName = DisplayTables.GetIpOptionTypeName(optData[0]);

        MutField container = optionsContainer.AppendWithCustomText(
            fields.SecurityFieldId, FieldValue.None,
            (string)ZA.String(optName, " (", optLen, " bytes)"), in context);

        AppendOptionTypeFields(container, optData[0], in fields, in context);
        container.Append(fields.OptLenFieldId, FieldValue.NewU64((ulong)optLen), in context);

        // Store raw option data (after type + length)
        if (optLen > 2)
        {
            ReadOnlyMemory<byte> rawData = optData[2..].ToArray();
            container.Append(fields.OptDataFieldId, FieldValue.NewBytes(rawData), in context);
        }
    }

    /// <summary>
    /// Parses a Stream Identifier option (type 0x88).
    /// Format: type(1) + length(1) + stream_id(2).
    /// </summary>
    private static void ParseStreamIdOption(
        MutField optionsContainer,
        ReadOnlySpan<byte> optData,
        in IPv4Protocol.OptionFieldIds fields, in ParseContext context)
    {
        if (optData.Length < StreamIdLen)
        {
            return;
        }

        string optName = DisplayTables.GetIpOptionTypeName(StreamId);
        ushort sidValue = (ushort)((optData[2] << 8) | optData[3]);

        MutField container = optionsContainer.AppendWithCustomText(
            fields.StreamIdFieldId, FieldValue.None,
            (string)ZA.String(optName, ": ", sidValue), in context);

        AppendOptionTypeFields(container, optData[0], in fields, in context);
        container.Append(fields.OptLenFieldId, FieldValue.NewU64((ulong)optData.Length), in context);
        container.Append(fields.OptSidFieldId, FieldValue.NewU64(sidValue), in context);
    }

    /// <summary>Parses an unknown option — displays type, length, and raw data.</summary>
    private static void ParseUnknownOption(
        MutField optionsContainer,
        byte optType,
        ReadOnlySpan<byte> optData,
        in IPv4Protocol.OptionFieldIds fields, in ParseContext context)
    {
        int optLen = optData.Length;
        string optName = DisplayTables.GetIpOptionTypeName(optType);
        string displayName = optName.Length > 0 ? optName : "Unknown";

        MutField container = optionsContainer.AppendWithCustomText(
            fields.UnknownFieldId, FieldValue.None,
            (string)ZA.String(displayName, " (type ", optType, ", ", optLen, " bytes)"), in context);

        AppendOptionTypeFields(container, optType, in fields, in context);
        container.Append(fields.OptLenFieldId, FieldValue.NewU64((ulong)optLen), in context);

        // Store raw option data (after type + length)
        if (optLen > 2)
        {
            ReadOnlyMemory<byte> rawData = optData[2..].ToArray();
            container.Append(fields.OptDataFieldId, FieldValue.NewBytes(rawData), in context);
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Appends the option type sub-fields (type byte, copy bit, class, number)
    /// with precomputed display text from static tables.
    /// </summary>
    private static void AppendOptionTypeFields(
        MutField parentField,
        byte optType,
        in IPv4Protocol.OptionFieldIds fields, in ParseContext context)
    {
        bool copy = (optType & 0x80) != 0;
        byte optClass = (byte)((optType >> 5) & 0x03);
        byte number = (byte)(optType & 0x1F);

        string typeDisplay = DisplayTables.GetIpOptionTypeDisplayText(optType);
        parentField.AppendWithCustomText(fields.OptTypeFieldId,
            FieldValue.NewU64(optType), typeDisplay, in context);

        parentField.AppendWithCustomText(fields.OptTypeCopyFieldId,
            FieldValue.NewBool(copy), copy ? "Set" : "Not Set", in context);

        string classDisplay = DisplayTables.GetIpOptionClassDisplayText(optClass);
        parentField.AppendWithCustomText(fields.OptTypeClassFieldId,
            FieldValue.NewU64(optClass), classDisplay, in context);

        parentField.Append(fields.OptTypeNumberFieldId, FieldValue.NewU64(number), in context);
    }

    /// <summary>
    /// Reads consecutive 4-byte big-endian IPv4 addresses starting at <paramref name="startOffset"/>
    /// and appends each to <paramref name="parentField"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ParseIpv4Addresses(
        MutField parentField,
        ReadOnlySpan<byte> data,
        int startOffset,
        FieldId addrFieldId, in ParseContext context)
    {
        int offset = startOffset;
        while (offset + 4 <= data.Length)
        {
            IPv4Address addr = ReadIpv4Address(data, offset);
            parentField.Append(addrFieldId, FieldValue.NewIPv4(addr), in context);
            offset += 4;
        }
    }

    /// <summary>Reads an IPv4 address from 4 big-endian bytes at the given offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IPv4Address ReadIpv4Address(ReadOnlySpan<byte> data, int offset)
        => IPv4Address.FromBytes(data.Slice(offset, 4));

    /// <summary>Reads a 32-bit big-endian unsigned integer at the given offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadU32BigEndian(ReadOnlySpan<byte> data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
         | ((uint)data[offset + 2] << 8) | data[offset + 3];
    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
/// <item>Security (SEC, 0x82) and Extended Security (0x85), shown as raw data</item>
/// <item>Stream Identifier (SID, 0x88)</item>
/// <item>Unknown options (displayed with raw data)</item>
/// </list>
/// <para>
/// Design: the parser is tolerant, because option bytes come from the network. A malformed option (missing or
/// invalid length byte, length beyond the options region) ends parsing: the options decoded before it stay, the rest
/// is dropped without an error field. A multi-byte option is never read beyond its own declared length.
/// </para>
/// <para><b>Thread-safety:</b> stateless; safe for concurrent use. It appends fields only to the container it is given.</para>
/// </summary>
internal static class IPv4OptionsParser
{
    // IPv4 option type constants
    private const byte _Eool = 0x00;
    private const byte _Nop = 0x01;
    private const byte _RecordRoute = 0x07;
    private const byte _Timestamp = 0x44;
    private const byte _Security = 0x82;
    private const byte _LooseSourceRoute = 0x83;
    private const byte _ExtendedSecurity = 0x85;
    private const byte _StreamId = 0x88;
    private const byte _StrictSourceRoute = 0x89;
    private const byte _RouterAlert = 0x94;

    // Minimum option lengths (including type and length bytes)
    private const int _MinRouteOptionLen = 3;
    private const int _MinTimestampOptionLen = 4;
    private const int _RouterAlertLen = 4;
    private const int _StreamIdLen = 4;

    // Maximum total options length (IHL max 15 → 60 byte header − 20 byte fixed = 40 bytes).
    // A constant of the format (IHL is a 4-bit count of 32-bit words), not a tunable limit: the caller derives the
    // options span from IHL, so a longer span cannot occur; this guard only protects against a caller error.
    private const int _MaxOptionsLen = 40;

    // Timestamp flag values (low nibble of the fourth option byte, RFC 791)
    private const byte _TsFlagTimestampsOnly = 0;
    private const byte _TsFlagTimestampAndAddr = 1;
    private const byte _TsFlagPrespecified = 3;

    /// <summary>
    /// Parses all IPv4 options from the given data span and appends fields to the
    /// options container under <paramref name="optionsContainer"/>.
    /// </summary>
    /// <param name="optionsContainer">The parent field for all options (ip.options).</param>
    /// <param name="data">Options bytes (from offset 20 to header length).</param>
    /// <param name="fields">Field IDs for the option sub-fields.</param>
    /// <remarks>
    /// An empty region, or one longer than 40 bytes (more than IHL allows), produces no fields and no error.
    /// </remarks>
    internal static void Parse(
        MutField optionsContainer,
        ReadOnlySpan<byte> data,
        in IPv4Protocol.OptionFieldIds fields)
    {
        int len = data.Length;
        if (len == 0 || len > _MaxOptionsLen)
        {
            return;
        }

        // One option per iteration. Every branch either advances offset by at least one byte or returns, so the loop
        // terminates; the single-byte options (EOOL, NOP) have no length byte, all others carry type + length + data.
        int offset = 0;
        while (offset < len)
        {
            byte optType = data[offset];

            switch (optType)
            {
                case _Eool:
                    // End of Options List — terminates parsing, remaining bytes are padding
                    optionsContainer.AppendWithCustomText(
                        fields.EolFieldId, FieldValue.None,
                        "End of Options List (EOL)");

                    // The field stores memory, not a span, so the padding is copied (rare: only when bytes follow EOOL).
                    if (offset + 1 < len)
                    {
                        ReadOnlyMemory<byte> padding = data[(offset + 1)..len].ToArray();
                        optionsContainer.Append(fields.PaddingFieldId,
                            FieldValue.NewBytes(padding));
                    }
                    return; // EOOL terminates option parsing

                case _Nop:
                    // No-Operation — single byte used for alignment
                    optionsContainer.AppendWithCustomText(
                        fields.NopFieldId, FieldValue.None,
                        "No-Operation (NOP)");
                    offset += 1;
                    break;

                default:
                    // Multi-byte option: type(1) + length(1) + data(N). The length byte counts the type and length
                    // bytes too (RFC 791), so values below 2 are invalid and a length past the region is truncated data.
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
                    _ParseMultiByteOption(optionsContainer, optType, optData, in fields);
                    offset += optLen;
                    break;
            }
        }
    }

    /// <summary>Dispatches a multi-byte option to the appropriate specific parser.</summary>
    /// <param name="optionsContainer">The parent field for all options.</param>
    /// <param name="optType">The option type byte.</param>
    /// <param name="optData">The whole option (type, length, data); its length is the validated option length (at least 2).</param>
    /// <param name="fields">The registered option sub-field ids used to append the decoded values.</param>
    private static void _ParseMultiByteOption(
        MutField optionsContainer,
        byte optType,
        ReadOnlySpan<byte> optData,
        in IPv4Protocol.OptionFieldIds fields)
    {
        switch (optType)
        {
            case _RecordRoute:
                _ParseRecordRouteOption(optionsContainer, optData, in fields);
                break;

            case _LooseSourceRoute:
                _ParseSourceRouteOption(optionsContainer, optData, fields.LooseSourceRouteFieldId, in fields);
                break;

            case _StrictSourceRoute:
                _ParseSourceRouteOption(optionsContainer, optData, fields.StrictSourceRouteFieldId, in fields);
                break;

            case _Timestamp:
                _ParseTimestampOption(optionsContainer, optData, in fields);
                break;

            case _RouterAlert:
                _ParseRouterAlertOption(optionsContainer, optData, in fields);
                break;

            case _Security or _ExtendedSecurity:
                _ParseSecurityOption(optionsContainer, optData, in fields);
                break;

            case _StreamId:
                _ParseStreamIdOption(optionsContainer, optData, in fields);
                break;

            default:
                _ParseUnknownOption(optionsContainer, optType, optData, in fields);
                break;
        }
    }

    #region Specific option parsers

    /// <summary>
    /// Parses a Record Route option (type 0x07).
    /// Format: type(1) + length(1) + pointer(1) + route entries(4 bytes each).
    /// </summary>
    private static void _ParseRecordRouteOption(
        MutField optionsContainer,
        ReadOnlySpan<byte> optData,
        in IPv4Protocol.OptionFieldIds fields)
    {
        int optLen = optData.Length;
        if (optLen < _MinRouteOptionLen)
        {
            return;
        }

        string optName = DisplayTables.GetIpOptionTypeName(_RecordRoute);
        byte pointer = optData[2];
        int addrCount = (optLen - 3) / 4;

        MutField container = optionsContainer.AppendWithCustomText(
            fields.RecordRouteFieldId, FieldValue.None,
            (string)ZA.String(optName, " (", optLen, " bytes, ", addrCount, " entries)"));

        _AppendOptionTypeFields(container, optData[0], in fields);
        container.Append(fields.OptLenFieldId, FieldValue.NewU64((ulong)optLen));
        container.Append(fields.OptPtrFieldId, FieldValue.NewU64(pointer));

        // Parse recorded route addresses (each 4 bytes starting at offset 3)
        _ParseIpv4Addresses(container, optData, 3, fields.OptAddrFieldId);
    }

    /// <summary>
    /// Parses a Loose Source Route (0x83) or Strict Source Route (0x89) option.
    /// Format: type(1) + length(1) + pointer(1) + route entries(4 bytes each).
    /// </summary>
    private static void _ParseSourceRouteOption(
        MutField optionsContainer,
        ReadOnlySpan<byte> optData,
        FieldId containerFieldId,
        in IPv4Protocol.OptionFieldIds fields)
    {
        int optLen = optData.Length;
        if (optLen < _MinRouteOptionLen)
        {
            return;
        }

        string optName = DisplayTables.GetIpOptionTypeName(optData[0]);
        byte pointer = optData[2];
        int addrCount = (optLen - 3) / 4;

        MutField container = optionsContainer.AppendWithCustomText(
            containerFieldId, FieldValue.None,
            (string)ZA.String(optName, " (", optLen, " bytes, ", addrCount, " entries)"));

        _AppendOptionTypeFields(container, optData[0], in fields);
        container.Append(fields.OptLenFieldId, FieldValue.NewU64((ulong)optLen));
        container.Append(fields.OptPtrFieldId, FieldValue.NewU64(pointer));

        _ParseIpv4Addresses(container, optData, 3, fields.OptAddrFieldId);
    }

    /// <summary>
    /// Parses a Timestamp option (type 0x44).
    /// Format: type(1) + length(1) + pointer(1) + oflw_flag(1) + entries.
    /// </summary>
    /// <remarks>
    /// The fourth byte holds the overflow counter in its high nibble and the flag in its low nibble. Entries are
    /// 32-bit values; with flag 1 or 3 each is preceded by a 4-byte address. Only whole entries are shown, and a flag
    /// other than 0, 1 or 3 shows no entries.
    /// </remarks>
    private static void _ParseTimestampOption(
        MutField optionsContainer,
        ReadOnlySpan<byte> optData,
        in IPv4Protocol.OptionFieldIds fields)
    {
        int optLen = optData.Length;
        if (optLen < _MinTimestampOptionLen)
        {
            return;
        }

        string optName = DisplayTables.GetIpOptionTypeName(_Timestamp);
        byte pointer = optData[2];
        byte oflwFlag = optData[3];
        byte overflow = (byte)((oflwFlag >> 4) & 0x0F);
        byte flag = (byte)(oflwFlag & 0x0F);

        string flagDisplay = DisplayTables.GetIpTimestampFlagDisplayText(flag);

        MutField container = optionsContainer.AppendWithCustomText(
            fields.TimestampFieldId, FieldValue.None,
            (string)ZA.String(optName, " (", optLen, " bytes, ", flagDisplay, ")"));

        _AppendOptionTypeFields(container, optData[0], in fields);
        container.Append(fields.OptLenFieldId, FieldValue.NewU64((ulong)optLen));
        container.Append(fields.OptPtrFieldId, FieldValue.NewU64(pointer));
        container.Append(fields.OptOverflowFieldId, FieldValue.NewU64(overflow));
        container.AppendWithCustomText(fields.OptFlagFieldId,
            FieldValue.NewU64(flag), flagDisplay);

        // Parse timestamp entries starting at offset 4
        int entryOffset = 4;
        switch (flag)
        {
            case _TsFlagTimestampsOnly:
                // Timestamps only — each 4 bytes
                while (entryOffset + 4 <= optLen)
                {
                    uint tsVal = _ReadU32BigEndian(optData, entryOffset);
                    container.Append(fields.OptTimeStampFieldId,
                        FieldValue.NewU64(tsVal));
                    entryOffset += 4;
                }
                break;

            case _TsFlagTimestampAndAddr or _TsFlagPrespecified:
                // Address + timestamp pairs — each 8 bytes (4 addr + 4 ts)
                while (entryOffset + 8 <= optLen)
                {
                    IPv4Address addr = _ReadIpv4Address(optData, entryOffset);
                    uint tsVal = _ReadU32BigEndian(optData, entryOffset + 4);
                    container.Append(fields.OptTimeStampAddrFieldId,
                        FieldValue.NewIPv4(addr));
                    container.Append(fields.OptTimeStampFieldId,
                        FieldValue.NewU64(tsVal));
                    entryOffset += 8;
                }
                break;
        }
    }

    /// <summary>
    /// Parses a Router Alert option (type 0x94).
    /// Format: type(1) + length(1) + value(2).
    /// </summary>
    private static void _ParseRouterAlertOption(
        MutField optionsContainer,
        ReadOnlySpan<byte> optData,
        in IPv4Protocol.OptionFieldIds fields)
    {
        if (optData.Length < _RouterAlertLen)
        {
            return;
        }

        string optName = DisplayTables.GetIpOptionTypeName(_RouterAlert);
        ushort raValue = (ushort)((optData[2] << 8) | optData[3]);

        // Use static text for known values, dynamic for unknown
        string raDisplay = DisplayTables.GetRouterAlertDisplayText(raValue)
            ?? (string)ZA.String("Unknown (", raValue, ")");

        MutField container = optionsContainer.AppendWithCustomText(
            fields.RouterAlertFieldId, FieldValue.None,
            (string)ZA.String(optName, ": ", raDisplay));

        _AppendOptionTypeFields(container, optData[0], in fields);
        container.Append(fields.OptLenFieldId, FieldValue.NewU64((ulong)optData.Length));
        container.AppendWithCustomText(fields.OptRaFieldId,
            FieldValue.NewU64(raValue), raDisplay);
    }

    /// <summary>
    /// Parses a Security option (type 0x82) or Extended Security (type 0x85).
    /// Displays raw data since full classification parsing is rarely needed.
    /// </summary>
    /// <remarks>The raw bytes are copied (<c>ToArray</c>) because the field value stores memory, not a span.</remarks>
    private static void _ParseSecurityOption(
        MutField optionsContainer,
        ReadOnlySpan<byte> optData,
        in IPv4Protocol.OptionFieldIds fields)
    {
        int optLen = optData.Length;
        string optName = DisplayTables.GetIpOptionTypeName(optData[0]);

        MutField container = optionsContainer.AppendWithCustomText(
            fields.SecurityFieldId, FieldValue.None,
            (string)ZA.String(optName, " (", optLen, " bytes)"));

        _AppendOptionTypeFields(container, optData[0], in fields);
        container.Append(fields.OptLenFieldId, FieldValue.NewU64((ulong)optLen));

        // Store raw option data (after type + length)
        if (optLen > 2)
        {
            ReadOnlyMemory<byte> rawData = optData[2..].ToArray();
            container.Append(fields.OptDataFieldId, FieldValue.NewBytes(rawData));
        }
    }

    /// <summary>
    /// Parses a Stream Identifier option (type 0x88).
    /// Format: type(1) + length(1) + stream_id(2).
    /// </summary>
    private static void _ParseStreamIdOption(
        MutField optionsContainer,
        ReadOnlySpan<byte> optData,
        in IPv4Protocol.OptionFieldIds fields)
    {
        if (optData.Length < _StreamIdLen)
        {
            return;
        }

        string optName = DisplayTables.GetIpOptionTypeName(_StreamId);
        ushort sidValue = (ushort)((optData[2] << 8) | optData[3]);

        MutField container = optionsContainer.AppendWithCustomText(
            fields.StreamIdFieldId, FieldValue.None,
            (string)ZA.String(optName, ": ", sidValue));

        _AppendOptionTypeFields(container, optData[0], in fields);
        container.Append(fields.OptLenFieldId, FieldValue.NewU64((ulong)optData.Length));
        container.Append(fields.OptSidFieldId, FieldValue.NewU64(sidValue));
    }

    /// <summary>
    /// Parses an unknown option — displays type, length, and raw data. The raw bytes are copied (<c>ToArray</c>)
    /// because the field value stores memory, not a span; unknown options are rare.
    /// </summary>
    private static void _ParseUnknownOption(
        MutField optionsContainer,
        byte optType,
        ReadOnlySpan<byte> optData,
        in IPv4Protocol.OptionFieldIds fields)
    {
        int optLen = optData.Length;
        string optName = DisplayTables.GetIpOptionTypeName(optType);
        string displayName = optName.Length > 0 ? optName : "Unknown";

        MutField container = optionsContainer.AppendWithCustomText(
            fields.UnknownFieldId, FieldValue.None,
            (string)ZA.String(displayName, " (type ", optType, ", ", optLen, " bytes)"));

        _AppendOptionTypeFields(container, optType, in fields);
        container.Append(fields.OptLenFieldId, FieldValue.NewU64((ulong)optLen));

        // Store raw option data (after type + length)
        if (optLen > 2)
        {
            ReadOnlyMemory<byte> rawData = optData[2..].ToArray();
            container.Append(fields.OptDataFieldId, FieldValue.NewBytes(rawData));
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Appends the option type sub-fields (type byte, copy bit, class, number)
    /// with precomputed display text from static tables.
    /// </summary>
    private static void _AppendOptionTypeFields(
        MutField parentField,
        byte optType,
        in IPv4Protocol.OptionFieldIds fields)
    {
        // Option type byte (RFC 791): bit 7 = copied into fragments, bits 6-5 = class, bits 4-0 = option number.
        bool copy = (optType & 0x80) != 0;
        byte optClass = (byte)((optType >> 5) & 0x03);
        byte number = (byte)(optType & 0x1F);

        string typeDisplay = DisplayTables.GetIpOptionTypeDisplayText(optType);
        parentField.AppendWithCustomText(fields.OptTypeFieldId,
            FieldValue.NewU64(optType), typeDisplay);

        parentField.AppendWithCustomText(fields.OptTypeCopyFieldId,
            FieldValue.NewBool(copy), copy ? "Set" : "Not Set");

        string classDisplay = DisplayTables.GetIpOptionClassDisplayText(optClass);
        parentField.AppendWithCustomText(fields.OptTypeClassFieldId,
            FieldValue.NewU64(optClass), classDisplay);

        parentField.Append(fields.OptTypeNumberFieldId, FieldValue.NewU64(number));
    }

    /// <summary>
    /// Reads consecutive 4-byte big-endian IPv4 addresses starting at <paramref name="startOffset"/>
    /// and appends each to <paramref name="parentField"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _ParseIpv4Addresses(
        MutField parentField,
        ReadOnlySpan<byte> data,
        int startOffset,
        FieldId addrFieldId)
    {
        int offset = startOffset;
        while (offset + 4 <= data.Length)
        {
            IPv4Address addr = _ReadIpv4Address(data, offset);
            parentField.Append(addrFieldId, FieldValue.NewIPv4(addr));
            offset += 4;
        }
    }

    /// <summary>Reads an IPv4 address from 4 big-endian bytes at the given offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IPv4Address _ReadIpv4Address(ReadOnlySpan<byte> data, int offset)
        => IPv4Address.FromBytes(data.Slice(offset, 4));

    /// <summary>Reads a 32-bit big-endian unsigned integer at the given offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint _ReadU32BigEndian(ReadOnlySpan<byte> data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
         | ((uint)data[offset + 2] << 8) | data[offset + 3];
    #endregion
}






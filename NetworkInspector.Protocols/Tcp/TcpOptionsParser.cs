// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Static parser for TCP options (variable-length area between the fixed 20-byte
/// TCP header and the payload). Supports the following option kinds:
/// <list type="bullet">
///   <item>EOL (0) - End of Option List</item>
///   <item>NOP (1) - No-Operation / padding</item>
///   <item>MSS (2) - Maximum Segment Size (RFC 879)</item>
///   <item>Window Scale (3) - Window Scale factor (RFC 1323, capped to 14)</item>
///   <item>SACK Permitted (4) - SACK support flag (RFC 2018)</item>
///   <item>SACK (5) - Selective Acknowledgment blocks (RFC 2018)</item>
///   <item>Timestamps (8) - TSval/TSecr (RFC 1323)</item>
///   <item>User Timeout (28) - RFC 5482</item>
///   <item>TCP Fast Open (34) - cookie (RFC 7413)</item>
///   <item>MPTCP (30) - Multipath TCP subtype (RFC 6824/8684)</item>
///   <item>MD5 Signature (19) - RFC 2385</item>
///   <item>TCP-AO (29) - Authentication Option (RFC 5925)</item>
///   <item>Unknown - catch-all for unrecognized options</item>
/// </list>
/// </summary>
internal static class TcpOptionsParser
{
    #region Option Kind Constants
    private const byte _OptEol = 0;
    private const byte _OptNop = 1;
    private const byte _OptMss = 2;
    private const byte _OptWindowScale = 3;
    private const byte _OptSackPermitted = 4;
    private const byte _OptSack = 5;
    private const byte _OptTimestamps = 8;
    private const byte _OptMd5Signature = 19;
    private const byte _OptUserTimeout = 28;
    private const byte _OptTcpAo = 29;
    private const byte _OptMptcp = 30;
    private const byte _OptFastOpen = 34;
    private const byte _OptEcho = 6;
    private const byte _OptEchoReply = 7;
    private const byte _OptCc = 11;
    private const byte _OptCcNew = 12;
    private const byte _OptCcEcho = 13;
    private const byte _OptScps = 20;
    private const byte _OptQs = 27;
    private const byte _OptAccEcn0 = 0xAC;
    private const byte _OptAccEcn1 = 0xAE;
    private const byte _OptExpFd = 0xFD;
    private const byte _OptExpFe = 0xFE;

    /// <summary>Maximum window scale shift count per RFC 7323.</summary>
    private const byte _MaxWindowScale = 14;

    /// <summary>
    /// Parses all TCP options from the options area and appends fields to the container.
    /// Returns parsed option values needed for stateful analysis (MSS, Window Scale, etc.).
    /// </summary>
    /// <param name="optionsData">The raw TCP options bytes (header[20..headerLen]).</param>
    /// <param name="container">The MutField to append option sub-fields to.</param>
    /// <param name="fieldIds">The registered field IDs for TCP options.</param>
    /// <returns>Parsed option information for analysis.</returns>
    internal static TcpOptionsInfo Parse(
        ReadOnlySpan<byte> optionsData,
        in MutField container,
        in TcpOptionsFieldIds fieldIds)
    {
        ushort? mss = null;
        byte? windowScale = null;
        bool sackPermitted = false;
        uint? tsVal = null;
        uint? tsEcr = null;

        int offset = 0;
        while (offset < optionsData.Length)
        {
            byte kind = optionsData[offset];

    #endregion

            #region Single-byte options (no length field)
            if (kind == _OptEol)
            {
                container.AppendWithCustomText(
                    fieldIds.Eol, FieldValue.None,
                    "End of Option List (EOL)");
                break; // EOL terminates option parsing
            }

            if (kind == _OptNop)
            {
                container.AppendWithCustomText(
                    fieldIds.Nop, FieldValue.None,
                    "No-Operation (NOP)");
                offset++;
                continue;
            }

            #endregion

            #region Multi-byte options: kind + length + data
            if (offset + 1 >= optionsData.Length)
            {
                break; // Truncated - no length byte
            }

            byte optLen = optionsData[offset + 1];
            if (optLen < 2 || offset + optLen > optionsData.Length)
            {
                break; // Invalid or truncated option
            }

            ReadOnlySpan<byte> optionBytes = optionsData.Slice(offset, optLen);

            switch (kind)
            {
                case _OptMss:
                    mss = _ParseMss(optionBytes, in container, in fieldIds);
                    break;
                case _OptWindowScale:
                    windowScale = _ParseWindowScale(optionBytes, in container, in fieldIds);
                    break;
                case _OptSackPermitted:
                    _ParseSackPermitted(in container, in fieldIds);
                    sackPermitted = true;
                    break;
                case _OptSack:
                    _ParseSack(optionBytes, in container, in fieldIds);
                    break;
                case _OptTimestamps:
                    (tsVal, tsEcr) = _ParseTimestamps(optionBytes, in container, in fieldIds);
                    break;
                case _OptUserTimeout:
                    _ParseUserTimeout(optionBytes, in container, in fieldIds);
                    break;
                case _OptFastOpen:
                    _ParseFastOpen(optionBytes, in container, in fieldIds);
                    break;
                case _OptMptcp:
                    _ParseMptcp(optionBytes, in container, in fieldIds);
                    break;
                case _OptMd5Signature:
                    _ParseMd5(optionBytes, in container, in fieldIds);
                    break;
                case _OptTcpAo:
                    _ParseTcpAo(optionBytes, in container, in fieldIds);
                    break;
                case _OptEcho:
                    _ParseEcho(optionBytes, in container, in fieldIds);
                    break;
                case _OptEchoReply:
                    _ParseEchoReply(optionBytes, in container, in fieldIds);
                    break;
                case _OptCc:
                    _ParseFixedU32(optionBytes, in container, in fieldIds, fieldIds.Cc, fieldIds.CcValue);
                    break;
                case _OptCcNew:
                    _ParseFixedU32(optionBytes, in container, in fieldIds, fieldIds.CcNew, fieldIds.CcNewValue);
                    break;
                case _OptCcEcho:
                    _ParseFixedU32(optionBytes, in container, in fieldIds, fieldIds.CcEcho, fieldIds.CcEchoValue);
                    break;
                case _OptScps:
                    _ParseScps(optionBytes, in container, in fieldIds);
                    break;
                case _OptQs:
                    _ParseQs(optionBytes, in container, in fieldIds);
                    break;
                case _OptAccEcn0:
                case _OptAccEcn1:
                    _ParseAccEcn(optionBytes, kind == _OptAccEcn0, in container, in fieldIds);
                    break;
                case _OptExpFd:
                case _OptExpFe:
                    _ParseExp(optionBytes, in container, in fieldIds);
                    break;
                default:
                    _ParseUnknown(kind, optionBytes, in container, in fieldIds);
                    break;
            }

            offset += optLen;
        }

        return new TcpOptionsInfo
        {
            Mss = mss,
            WindowScale = windowScale,
            SackPermitted = sackPermitted,
            TsVal = tsVal,
            TsEcr = tsEcr,
        };
    }

    /// <summary>Parses MSS option (kind 2, length 4): 2-byte MSS value.</summary>
    private static ushort? _ParseMss(
        ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        if (data.Length < 4)
        {
            return null;
        }

        ushort value = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        MutField mssField = container.AppendWithCustomText(
            ids.Mss, FieldValue.None,
            (string)ZA.String("Maximum Segment Size: ", value, " bytes"));
        mssField.Append(ids.MssVal, FieldValue.NewU64(value));
        return value;
    }

    /// <summary>
    /// Parses Window Scale option (kind 3, length 3): 1-byte shift count.
    /// Capped to 14 per RFC 7323.
    /// </summary>
    private static byte? _ParseWindowScale(
        ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        if (data.Length < 3)
        {
            return null;
        }

        byte shift = data[2];
        // RFC 7323 -2.3: shift count MUST NOT exceed 14
        byte effectiveShift = Math.Min(shift, _MaxWindowScale);
        uint multiplier = 1u << effectiveShift;

        MutField wsField = container.AppendWithCustomText(
            ids.WindowScale, FieldValue.None,
            (string)ZA.String("Window Scale: ", shift, " (multiply by ", multiplier, ")"));
        wsField.Append(ids.WindowScaleVal, FieldValue.NewU64(shift));
        wsField.Append(ids.WindowScaleMultiplier, FieldValue.NewU64(multiplier));

        return effectiveShift;
    }

    /// <summary>Parses SACK Permitted option (kind 4, length 2): no data.</summary>
    private static void _ParseSackPermitted(in MutField container, in TcpOptionsFieldIds ids)
    {
        container.AppendWithCustomText(
            ids.SackPermitted, FieldValue.None,
            "SACK Permitted");
    }

    /// <summary>
    /// Parses SACK option (kind 5, variable length): 1-4 SACK blocks,
    /// each containing a left edge and right edge (4 bytes each).
    /// </summary>
    private static void _ParseSack(
        ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        // SACK data starts at offset 2, each block is 8 bytes (LE + RE)
        int sackDataLen = data.Length - 2;
        int blockCount = sackDataLen / 8;

        if (blockCount <= 0)
        {
            return;
        }

        MutField sackField = container.AppendWithCustomText(
            ids.Sack, FieldValue.None,
            (string)ZA.String("SACK: ", blockCount, " block(s)"));
        sackField.Append(ids.SackCount, FieldValue.NewU64((ulong)blockCount));

        int blockOffset = 2;
        for (int i = 0; i < blockCount && blockOffset + 8 <= data.Length; i++)
        {
            uint leftEdge = BinaryPrimitives.ReadUInt32BigEndian(data[blockOffset..]);
            uint rightEdge = BinaryPrimitives.ReadUInt32BigEndian(data[(blockOffset + 4)..]);
            sackField.Append(ids.SackLeftEdge, FieldValue.NewU64(leftEdge));
            sackField.Append(ids.SackRightEdge, FieldValue.NewU64(rightEdge));
            blockOffset += 8;
        }
    }

    /// <summary>
    /// Parses Timestamps option (kind 8, length 10): TSval (4 bytes) + TSecr (4 bytes).
    /// </summary>
    private static (uint? TsVal, uint? TsEcr) _ParseTimestamps(
        ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        if (data.Length < 10)
        {
            return (null, null);
        }

        uint tsVal = BinaryPrimitives.ReadUInt32BigEndian(data[2..]);
        uint tsEcr = BinaryPrimitives.ReadUInt32BigEndian(data[6..]);

        MutField tsField = container.AppendWithCustomText(
            ids.Timestamps, FieldValue.None,
            (string)ZA.String("Timestamps: TSval=", tsVal, ", TSecr=", tsEcr));
        tsField.Append(ids.TimestampTsVal, FieldValue.NewU64(tsVal));
        tsField.Append(ids.TimestampTsEcr, FieldValue.NewU64(tsEcr));

        return (tsVal, tsEcr);
    }

    /// <summary>
    /// Parses User Timeout option (kind 28, length 4):
    /// 1-bit granularity (0=minutes, 1=seconds) + 15-bit value.
    /// </summary>
    private static void _ParseUserTimeout(
        ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        if (data.Length < 4)
        {
            return;
        }

        ushort raw = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        bool granularityIsMinutes = (raw & 0x8000) == 0;
        ushort value = (ushort)(raw & 0x7FFF);
        string unit = granularityIsMinutes ? "minutes" : "seconds";

        MutField utoField = container.AppendWithCustomText(
            ids.UserTimeout, FieldValue.None,
            (string)ZA.String("User Timeout: ", value, " ", unit));
        utoField.Append(ids.UserTimeoutGranularity, FieldValue.NewString(unit));
        utoField.Append(ids.UserTimeoutVal, FieldValue.NewU64(value));
    }

    /// <summary>
    /// Parses TCP Fast Open option (kind 34, variable length):
    /// Length 2 = request (no cookie), length > 2 = cookie present.
    /// </summary>
    private static void _ParseFastOpen(
        ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        if (data.Length == 2)
        {
            // TFO request (no cookie)
            MutField tfoField = container.AppendWithCustomText(
                ids.FastOpen, FieldValue.None,
                "TCP Fast Open: Cookie Request");
            tfoField.Append(ids.FastOpenRequest, FieldValue.NewBool(true));
        }
        else if (data.Length > 2)
        {
            // TFO with cookie
            ReadOnlyMemory<byte> cookie = data[2..].ToArray();
            MutField tfoField = container.AppendWithCustomText(
                ids.FastOpen, FieldValue.None,
                (string)ZA.String("TCP Fast Open: Cookie (", data.Length - 2, " bytes)"));
            tfoField.Append(ids.FastOpenCookie, FieldValue.NewBytes(cookie));
        }
    }

    /// <summary>
    /// Parses MPTCP option (kind 30, variable length): extracts subtype from first nibble.
    /// </summary>
    private static void _ParseMptcp(
        ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        if (data.Length < 3)
        {
            return;
        }

        // Subtype is in the upper 4 bits of byte 2
        byte subtype = (byte)(data[2] >> 4);
        string subtypeName = _GetMptcpSubtypeName(subtype);

        MutField mpField = container.AppendWithCustomText(
            ids.Mptcp, FieldValue.None,
            (string)ZA.String("Multipath TCP: ", subtypeName));
        mpField.Append(ids.MptcpSubtype, FieldValue.NewU64(subtype));
    }

    /// <summary>
    /// Parses MD5 Signature option (kind 19, length 18): 16-byte digest.
    /// </summary>
    private static void _ParseMd5(
        ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        if (data.Length < 18)
        {
            return;
        }

        ReadOnlyMemory<byte> digest = data[2..18].ToArray();
        MutField md5Field = container.AppendWithCustomText(
            ids.Md5, FieldValue.None,
            "MD5 Signature");
        md5Field.Append(ids.Md5Digest, FieldValue.NewBytes(digest));
    }

    /// <summary>
    /// Parses TCP-AO option (kind 29, variable length):
    /// KeyID (1 byte) + RNextKeyID (1 byte) + MAC (remaining).
    /// </summary>
    private static void _ParseTcpAo(
        ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        if (data.Length < 4)
        {
            return;
        }

        byte keyId = data[2];
        byte rNextKeyId = data[3];

        MutField aoField = container.AppendWithCustomText(
            ids.TcpAo, FieldValue.None,
            (string)ZA.String("TCP-AO: KeyID=", keyId, ", RNextKeyID=", rNextKeyId));
        aoField.Append(ids.TcpAoKeyId, FieldValue.NewU64(keyId));
        aoField.Append(ids.TcpAoRNextKeyId, FieldValue.NewU64(rNextKeyId));

        if (data.Length > 4)
        {
            ReadOnlyMemory<byte> mac = data[4..].ToArray();
            aoField.Append(ids.TcpAoMac, FieldValue.NewBytes(mac));
        }
    }

    /// <summary>Echo (kind 6) and Echo Reply (kind 7): length 6, big-endian uint at offset 2.</summary>
    private static void _ParseEcho(ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        if (data.Length != 6)
        {
            _ParseUnknown(data[0], data, in container, in ids);
            return;
        }

        uint value = BinaryPrimitives.ReadUInt32BigEndian(data[2..]);
        MutField echo = container.Append(ids.Echo, FieldValue.None);
        echo.Append(ids.EchoValue, FieldValue.NewU64(value));
    }

    private static void _ParseEchoReply(ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        if (data.Length != 6)
        {
            _ParseUnknown(data[0], data, in container, in ids);
            return;
        }

        uint value = BinaryPrimitives.ReadUInt32BigEndian(data[2..]);
        MutField echo = container.Append(ids.EchoReply, FieldValue.None);
        echo.Append(ids.EchoReplyValue, FieldValue.NewU64(value));
    }

    private static void _ParseFixedU32(
        ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids, FieldId containerField, FieldId valueField)
    {
        if (data.Length != 6)
        {
            _ParseUnknown(data[0], data, in container, in ids);
            return;
        }

        uint value = BinaryPrimitives.ReadUInt32BigEndian(data[2..]);
        MutField node = container.Append(containerField, FieldValue.None);
        node.Append(valueField, FieldValue.NewU64(value));
    }

    private static void _ParseScps(ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        if (data.Length != 4)
        {
            _ParseUnknown(data[0], data, in container, in ids);
            return;
        }

        byte caps = data[2];
        MutField scps = container.Append(ids.Scps, FieldValue.None);
        scps.Append(ids.ScpsBets, FieldValue.NewBool((caps & 0x80) != 0));
        scps.Append(ids.ScpsSnack1, FieldValue.NewBool((caps & 0x40) != 0));
        scps.Append(ids.ScpsSnack2, FieldValue.NewBool((caps & 0x20) != 0));
        scps.Append(ids.ScpsCompress, FieldValue.NewBool((caps & 0x10) != 0));
        scps.Append(ids.ScpsNlts, FieldValue.NewBool((caps & 0x08) != 0));
    }

    private static void _ParseQs(ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        if (data.Length != 8)
        {
            _ParseUnknown(data[0], data, in container, in ids);
            return;
        }

        MutField qs = container.Append(ids.Qs, FieldValue.None);
        qs.Append(ids.QsRate, FieldValue.NewU64((uint)(data[2] & 0x0F)));
        qs.Append(ids.QsTtlDiff, FieldValue.NewU64(data[3]));
    }

    private static void _ParseAccEcn(ReadOnlySpan<byte> data, bool order0, in MutField container, in TcpOptionsFieldIds ids)
    {
        int dataLen = data.Length - 2;
        if (dataLen is not (0 or 3 or 6 or 9))
        {
            _ParseUnknown(data[0], data, in container, in ids);
            return;
        }

        MutField node = container.Append(ids.AccEcn, FieldValue.None);
        if (dataLen >= 3)
        {
            uint first = _ReadU24(data[2..]);
            node.Append(order0 ? ids.AccEcnEe0b : ids.AccEcnEe1b, FieldValue.NewU64(first));
        }

        if (dataLen >= 6)
        {
            node.Append(ids.AccEcnEceb, FieldValue.NewU64(_ReadU24(data[5..])));
        }

        if (dataLen >= 9)
        {
            uint third = _ReadU24(data[8..]);
            node.Append(order0 ? ids.AccEcnEe1b : ids.AccEcnEe0b, FieldValue.NewU64(third));
        }
    }

    private static void _ParseExp(ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        if (data.Length < 4)
        {
            _ParseUnknown(data[0], data, in container, in ids);
            return;
        }

        ushort exid = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        MutField exp = container.Append(ids.Exp, FieldValue.None);
        exp.Append(ids.ExpExId, FieldValue.NewU64(exid));
        if (data.Length > 4)
        {
            exp.Append(ids.ExpData, FieldValue.NewBytes(data[4..].ToArray()));
        }

        if (exid is 0xACC0 or 0xACC1)
        {
            _ParseAccEcnCounters(data[4..], exid == 0xACC0, in exp, in ids);
        }
    }

    private static void _ParseAccEcnCounters(ReadOnlySpan<byte> counters, bool order0, in MutField node, in TcpOptionsFieldIds ids)
    {
        if (counters.Length is not (0 or 3 or 6 or 9))
        {
            return;
        }

        if (counters.Length >= 3)
        {
            node.Append(order0 ? ids.AccEcnEe0b : ids.AccEcnEe1b, FieldValue.NewU64(_ReadU24(counters)));
        }

        if (counters.Length >= 6)
        {
            node.Append(ids.AccEcnEceb, FieldValue.NewU64(_ReadU24(counters[3..])));
        }

        if (counters.Length >= 9)
        {
            node.Append(order0 ? ids.AccEcnEe1b : ids.AccEcnEe0b, FieldValue.NewU64(_ReadU24(counters[6..])));
        }
    }

    private static uint _ReadU24(ReadOnlySpan<byte> data) =>
        (uint)((data[0] << 16) | (data[1] << 8) | data[2]);

    /// <summary>Parses an unknown/unrecognized TCP option.</summary>
    private static void _ParseUnknown(
        byte kind, ReadOnlySpan<byte> data, in MutField container, in TcpOptionsFieldIds ids)
    {
        string displayText = DisplayTables.GetTcpOptionDisplayText(kind);
        MutField unknownField = container.AppendWithCustomText(
            ids.Unknown, FieldValue.None,
            (string)ZA.String("Unknown Option: ", displayText));

        if (data.Length > 2)
        {
            ReadOnlyMemory<byte> optData = data[2..].ToArray();
            unknownField.Append(ids.UnknownData, FieldValue.NewBytes(optData));
        }
    }

    /// <summary>Returns the name of an MPTCP subtype.</summary>
    private static string _GetMptcpSubtypeName(byte subtype)
    {
        return subtype switch
        {
            0 => "MP_CAPABLE",
            1 => "MP_JOIN",
            2 => "DSS",
            3 => "ADD_ADDR",
            4 => "REMOVE_ADDR",
            5 => "MP_PRIO",
            6 => "MP_FAIL",
            7 => "MP_FASTCLOSE",
            8 => "MP_TCPRST",
            _ => $"Unknown ({subtype})",
        };
    }
            #endregion
}




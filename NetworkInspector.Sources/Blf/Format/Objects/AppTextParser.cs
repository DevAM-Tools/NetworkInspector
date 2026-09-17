// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Objects;

/// <summary>
/// Parses BLF AppText objects to extract channel name metadata.
///
/// AppText (object type 65) is a metadata-only object written by Vector CANoe and CANalyzer
/// when starting a measurement. One AppText record per channel contains the user-defined
/// channel name, the channel number, and the bus type.
///
/// Payload layout (all fields little-endian):
/// <code>
///   [0..4)   source         — <see cref="BlfConstants.AppTextSourceChannel"/> (1) for channel names
///   [4..8)   reserved       — channel in bits 8–15, bus type in bits 16–23
///   [8..12)  textLength     — byte length of the UTF-8 text that follows the 16-byte header
///   [12..16) reserved2      — unused; must still be present
///   [16..)   text           — UTF-8; this parser uses the second <c>;</c>-separated token
/// </code>
///
/// Vector channel text is <c>Path;ClusterName;...</c>. The interface name is token index 1.
/// A missing or empty second token is not a channel name.
///
/// Only records whose <c>source</c> equals <see cref="BlfConstants.AppTextSourceChannel"/>
/// carry a channel name. Other AppText records (comments, metadata, XML) are ignored.
/// </summary>
/// <remarks>Not thread-safe. Caller synchronisation required.</remarks>
internal static class AppTextParser
{
    #region Constants

    /// <summary>
    /// AppText header size: source + reserved1 + textLength + reserved2 (4 × 4 bytes).
    /// </summary>
    private const int _AppTextHeaderSize = 16;

    #endregion

    #region Public API

    /// <summary>
    /// Tries to extract a channel name entry from an AppText payload.
    /// </summary>
    /// <param name="payload">
    /// Raw payload bytes starting immediately after the BLF object header
    /// (i.e. the slice returned by <see cref="BlfObjectInfo.Payload"/>).
    /// </param>
    /// <param name="channelNumber">
    /// On success, the channel number encoded in bits 8–15 of the reserved u32 at offset 4.
    /// </param>
    /// <param name="busType">
    /// On success, the bus-type byte encoded in bits 16–23 of the reserved u32 at offset 4.
    /// Compare against <see cref="BlfConstants.BusTypeCan"/>, <see cref="BlfConstants.BusTypeEthernet"/>, etc.
    /// </param>
    /// <param name="name">
    /// On success, the channel name string (second <c>;</c>-separated token).
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the record was a channel-name AppText entry and was parsed
    /// successfully; <see langword="false"/> if the payload is too short, not a channel-name
    /// record, the text length is inconsistent, or token index 1 is missing or empty.
    /// </returns>
    internal static bool TryParseChannelName(
        ReadOnlySpan<byte> payload,
        out byte channelNumber,
        out byte busType,
        out string? name)
    {
        channelNumber = 0;
        busType = 0;
        name = null;

        if (payload.Length < _AppTextHeaderSize)
        {
            return false;
        }

        uint source = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (source != BlfConstants.AppTextSourceChannel)
        {
            return false;
        }

        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        channelNumber = (byte)((reserved >> BlfConstants.AppTextReservedChannelShift) & BlfConstants.AppTextReservedByteMask);
        busType = (byte)((reserved >> BlfConstants.AppTextReservedBusTypeShift) & BlfConstants.AppTextReservedByteMask);

        uint textLengthUint = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        if (textLengthUint == 0
            || textLengthUint > (uint)int.MaxValue
            || textLengthUint > (uint)(payload.Length - _AppTextHeaderSize))
        {
            return false;
        }

        int textLength = (int)textLengthUint;
        ReadOnlySpan<byte> textBytes = payload.Slice(_AppTextHeaderSize, textLength);
        if (textBytes.Length > 0 && textBytes[^1] == 0)
        {
            textBytes = textBytes[..^1];
        }

        string fullText = Encoding.UTF8.GetString(textBytes);
        int firstSeparator = fullText.IndexOf(';');
        if (firstSeparator < 0)
        {
            return false;
        }

        int tokenStart = firstSeparator + 1;
        int secondSeparator = fullText.IndexOf(';', tokenStart);
        string token = secondSeparator < 0
            ? fullText[tokenStart..]
            : fullText[tokenStart..secondSeparator];
        if (token.Length == 0)
        {
            return false;
        }

        name = token;
        return true;
    }

    #endregion
}

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
/// <remarks>
/// Stateless and thread-safe: every call works on its own arguments. AppText records are metadata that occur a few times
/// per file (one per channel), so unlike the per-frame parsers this one allocates a string and does not use frame slabs.
/// </remarks>
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
    /// Tries to extract a channel name entry from an AppText payload. Never throws for malformed input; a malformed or
    /// foreign record is simply not a channel name.
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

        // Header guard: every field read below lies inside the fixed 16-byte header.
        if (payload.Length < _AppTextHeaderSize)
        {
            return false;
        }

        // Only source 1 carries a channel name; comments, metadata and XML records share object type 65 and are skipped.
        uint source = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (source != BlfConstants.AppTextSourceChannel)
        {
            return false;
        }

        // The channel number and bus type are packed into the "reserved" word. Both are single bytes, so the narrowing casts
        // after the mask cannot lose information.
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        // They are held in locals and only assigned to the out parameters on success, as documented: a record that is rejected
        // later (text length, missing name) must not hand a half-decoded channel and bus type to the caller.
        byte parsedChannel = (byte)((reserved >> BlfConstants.AppTextReservedChannelShift) & BlfConstants.AppTextReservedByteMask);
        byte parsedBusType = (byte)((reserved >> BlfConstants.AppTextReservedBusTypeShift) & BlfConstants.AppTextReservedByteMask);

        // textLength comes from the file and is untrusted. It is compared in uint arithmetic (no sign or overflow surprise)
        // against the bytes that really follow the header, so a crafted value can neither read out of range nor make the
        // string below larger than the payload that was already read. Zero is rejected: an empty text has no name.
        uint textLengthUint = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        if (textLengthUint == 0
            || textLengthUint > (uint)int.MaxValue
            || textLengthUint > (uint)(payload.Length - _AppTextHeaderSize))
        {
            return false;
        }

        // A trailing NUL counted in textLength (C-string style writers) is tolerated: drop one so it does not end up in the
        // name. This product's own exporter writes no terminator, so both forms round-trip.
        int textLength = (int)textLengthUint;
        ReadOnlySpan<byte> textBytes = payload.Slice(_AppTextHeaderSize, textLength);
        if (textBytes.Length > 0 && textBytes[^1] == 0)
        {
            textBytes = textBytes[..^1];
        }

        // Token 1 of "Path;ClusterName;..." is the interface name. Only the first two separators are located so the rest of
        // the (possibly long) text is not split into an array of substrings.
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

        channelNumber = parsedChannel;
        busType = parsedBusType;
        name = token;
        return true;
    }

    #endregion
}

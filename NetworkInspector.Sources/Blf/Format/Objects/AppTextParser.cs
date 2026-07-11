// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Objects;

/// <summary>
/// Parses BLF AppText objects to extract channel name metadata.
///
/// AppText (object type 65) is a metadata-only object written by Vector CANoe and CANalyzer
/// when starting a measurement. One AppText record per channel contains the user-defined
/// channel name, the channel number (1-based), and the bus type.
///
/// Payload layout (all fields little-endian, unsigned):
/// <code>
///   [0..4)   source      — composite field:
///                           bits  0..7  = channel number (0-based)
///                           bits  8..15 = bus type (BlfConstants.BusType*)
///                           bits 16..17 = source category; 0x00020000 = channel name entry
///   [4..8)   reserved    — unused field, skipped
///   [8..12)  textLength  — byte length of the following UTF-8 text (NUL not included)
///   [12..)   text        — UTF-8 (or ASCII) channel name string
/// </code>
///
/// Only records where bits 16–17 of <c>source</c> equal
/// <see cref="BlfConstants.AppTextSourceChannelName"/> carry a channel name.
/// Other AppText records (error messages, logger info, etc.) are silently ignored.
/// </summary>
/// <remarks>Not thread-safe. Caller synchronisation required.</remarks>
internal static class AppTextParser
{
    #region Constants

    /// <summary>Minimum payload size needed to read source, reserved, and textLength fields (3 × 4 bytes).</summary>
    private const int _MinPayloadSize = 12;

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
    /// On success, the 0-based channel number encoded in bits 0–7 of the source field.
    /// </param>
    /// <param name="busType">
    /// On success, the bus-type byte encoded in bits 8–15 of the source field.
    /// Compare against <see cref="BlfConstants.BusTypeCan"/>, <see cref="BlfConstants.BusTypeEthernet"/>, etc.
    /// </param>
    /// <param name="name">
    /// On success, the channel name string read from the payload.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the record was a channel-name AppText entry and was parsed
    /// successfully; <see langword="false"/> if the payload is too short, not a channel-name
    /// record, or the text length is inconsistent.
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

        if (payload.Length < _MinPayloadSize)
        {
            return false;
        }

        // Read the 32-bit composite source field (little-endian).
        // BinaryPrimitives.ReadUInt32LittleEndian is used instead of BitConverter.ToUInt32
        // because BLF fields are specified as little-endian and BitConverter is host-endian.
        uint source = BinaryPrimitives.ReadUInt32LittleEndian(payload);

        // Only channel-name entries carry a usable name — check that the source category
        // bits match AppTextSourceChannelName. Other AppText records (logger info, error
        // messages, etc.) share the same struct layout but have different category bits.
        if ((source & BlfConstants.AppTextSourceChannelName) == 0)
        {
            return false;
        }

        channelNumber = (byte)(source & BlfConstants.AppTextChannelMask);
        busType = (byte)((source >> BlfConstants.AppTextBusTypeShift) & BlfConstants.AppTextBusTypeMask);

        // Read textLength as uint first to detect both zero and values too large
        // to represent as int (> int.MaxValue). A direct (int) cast from ReadUInt32 silently
        // wraps values above int.MaxValue to negative, which would pass the > 0 check and
        // produce a negative Slice length, throwing an ArgumentOutOfRangeException later.
        uint textLengthUint = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);

        // Reject zero-length names, names larger than any addressable span (> int.MaxValue),
        // and names that would overrun the remaining payload bytes.
        if (textLengthUint == 0
            || textLengthUint > (uint)int.MaxValue
            || textLengthUint > (uint)(payload.Length - _MinPayloadSize))
        {
            return false;
        }

        int textLength = (int)textLengthUint;

        // Decode the UTF-8 channel name. Vector tools write ASCII in practice but the
        // BLF SDK documentation allows UTF-8 for localised names. Strip a trailing NUL
        // terminator if present (Vector tools write null-terminated strings).
        ReadOnlySpan<byte> textBytes = payload.Slice(_MinPayloadSize, textLength);
        if (textBytes.Length > 0 && textBytes[^1] == 0)
        {
            textBytes = textBytes[..^1];
        }

        name = Encoding.UTF8.GetString(textBytes);
        return true;
    }

    #endregion
}

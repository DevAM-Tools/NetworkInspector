// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// The fixed 16-byte SOME/IP header, all multi-byte fields big-endian (network order).
/// <para>
/// Wire layout: message id (service id 2 bytes, method id 2 bytes), length (4 bytes), request id (client id 2 bytes, session id
/// 2 bytes), protocol version (1), interface version (1), message type (1), return code (1). The length counts the bytes from the
/// client id to the end of the message, so the total size of a message is <c>length + 8</c>; the smallest legal length is 8
/// (a message without payload). The message id is kept next to its two halves because it is the key of the payload dispatch table.
/// </para>
/// <para><b>Thread-safety:</b> immutable value type.</para>
/// </summary>
/// <param name="ServiceId">Service id (upper half of the message id).</param>
/// <param name="MethodId">Method or event id (lower half of the message id).</param>
/// <param name="MessageId">Combined message id, <c>service id &lt;&lt; 16 | method id</c>.</param>
/// <param name="Length">Declared length in bytes from the client id to the end of the message (not validated here).</param>
/// <param name="ClientId">Client id (upper half of the request id).</param>
/// <param name="SessionId">Session id (lower half of the request id).</param>
/// <param name="ProtocolVersion">SOME/IP protocol version; 1 is the only defined value.</param>
/// <param name="InterfaceVersion">Major version of the service interface.</param>
/// <param name="MessageType">Message type byte; bit 5 (0x20) is the TP flag, bit 6 (0x40) the ACK flag.</param>
/// <param name="ReturnCode">Return code byte.</param>
internal readonly record struct SomeIpHeader(
    ushort ServiceId,
    ushort MethodId,
    uint MessageId,
    uint Length,
    ushort ClientId,
    ushort SessionId,
    byte ProtocolVersion,
    byte InterfaceVersion,
    byte MessageType,
    byte ReturnCode)
{
    #region Constants

    /// <summary>Size of the SOME/IP header in bytes.</summary>
    internal const int Size = 16;

    /// <summary>
    /// The smallest legal value of the length field: the request id, the two version bytes, the message type and the return code
    /// are 8 bytes that every message carries, so a message without payload declares exactly 8.
    /// </summary>
    internal const uint MinimumLength = 8;

    /// <summary>The number of bytes in front of the length-counted region: the message id (4) and the length field itself (4).</summary>
    internal const int LengthPrefixSize = 8;

    /// <summary>The only protocol version defined for the header.</summary>
    internal const byte SupportedProtocolVersion = 1;

    /// <summary>Message type bit that marks a segmented (TP) message.</summary>
    internal const byte TpFlag = 0x20;

    /// <summary>Message type bit that marks an acknowledgement variant.</summary>
    internal const byte AckFlag = 0x40;

    /// <summary>
    /// Mask that clears the TP flag and the ACK flag and keeps the base type: <c>0x9F</c> is every bit except <c>0x20</c> and
    /// <c>0x40</c>. Clearing both flags is what lets one comparison select a request whether or not it is segmented or
    /// acknowledged.
    /// </summary>
    internal const byte BaseTypeMask = 0x9F;

    #endregion

    #region Properties

    /// <summary>The message type without the TP and ACK flags (request, request without return, notification, response, error).</summary>
    internal byte BaseType => (byte)(MessageType & BaseTypeMask);

    /// <summary>Whether the TP flag (bit 5) is set.</summary>
    internal bool IsTp => (MessageType & TpFlag) != 0;

    /// <summary>Whether the ACK flag (bit 6) is set.</summary>
    internal bool IsAck => (MessageType & AckFlag) != 0;

    /// <summary>
    /// Whether this header looks like the header of a real message: a length of at least 8, the only defined protocol version and
    /// one of the five defined base types (0x00 request, 0x01 request without return, 0x02 notification, 0x80 response, 0x81
    /// error; the ACK and TP variants are accepted through <see cref="BaseType"/>). The test is a plausibility check, not a
    /// validation: it is cheap, has no false negatives for well-formed messages, and is used where the stream position or the
    /// protocol is not certain (content-based detection and resynchronization of a byte stream).
    /// </summary>
    internal bool IsPlausible =>
        Length >= MinimumLength
        && ProtocolVersion == SupportedProtocolVersion
        && BaseType is 0x00 or 0x01 or 0x02 or 0x80 or 0x81;

    #endregion

    #region Parsing

    /// <summary>
    /// Decodes the header from the first <see cref="Size"/> bytes of <paramref name="data"/>. The caller guarantees the length;
    /// use this on the hot path after its own length check so the check is not paid twice.
    /// </summary>
    /// <param name="data">At least <see cref="Size"/> bytes starting at the header.</param>
    /// <returns>The decoded header.</returns>
    internal static SomeIpHeader Parse(ReadOnlySpan<byte> data)
    {
        // Straight span reads, no allocation. The message id overlaps service id and method id (bytes 0..3), so it is read
        // as one word instead of being composed from the halves.
        return new SomeIpHeader(
            BinaryPrimitives.ReadUInt16BigEndian(data),
            BinaryPrimitives.ReadUInt16BigEndian(data[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(data),
            BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(data[8..]),
            BinaryPrimitives.ReadUInt16BigEndian(data[10..]),
            data[12],
            data[13],
            data[14],
            data[15]);
    }

    /// <summary>
    /// Attempts to decode the header from the first <see cref="Size"/> bytes of <paramref name="data"/>. Only the length of
    /// <paramref name="data"/> is checked; semantic validation (for example <see cref="MinimumLength"/>) is left to the caller.
    /// </summary>
    /// <param name="data">Message bytes starting at the header; may be longer than the header.</param>
    /// <param name="header">The decoded header, or <see langword="default"/> when too short.</param>
    /// <returns><see langword="true"/> when at least <see cref="Size"/> bytes were available.</returns>
    internal static bool TryParse(ReadOnlySpan<byte> data, out SomeIpHeader header)
    {
        if (data.Length < Size)
        {
            header = default;
            return false;
        }

        header = Parse(data);
        return true;
    }

    #endregion
}

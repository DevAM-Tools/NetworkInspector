// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// SOME/IP header (16 bytes, AUTOSAR SOME/IP specification), all multi-byte fields big-endian (network order).
/// </summary>
/// <remarks>
/// Wire layout: MessageId (ServiceId(2) + MethodId(2)), Length(4), RequestId (ClientId(2) + SessionId(2)),
/// ProtocolVersion(1), InterfaceVersion(1), MessageType(1), ReturnCode(1). <see cref="Length"/> counts the bytes
/// from <see cref="ClientId"/> to the end of the message, so the total message size is <c>Length + 8</c>.
/// <see cref="MessageId"/> is the combination of <see cref="ServiceId"/> and <see cref="MethodId"/> and is kept as
/// a separate member because it is the key of the payload dispatch table. Thread safety: immutable value type.
/// </remarks>
/// <param name="ServiceId">Service id (upper half of the message id).</param>
/// <param name="MethodId">Method or event id (lower half of the message id).</param>
/// <param name="MessageId">Combined message id, <c>ServiceId &lt;&lt; 16 | MethodId</c>.</param>
/// <param name="Length">Declared length in bytes from the client id to the end of the message (not validated here).</param>
/// <param name="ClientId">Client id (upper half of the request id).</param>
/// <param name="SessionId">Session id (lower half of the request id).</param>
/// <param name="ProtocolVersion">SOME/IP protocol version.</param>
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

    #endregion

    #region Parsing

    /// <summary>
    /// Attempts to parse a SOME/IP header from the first <see cref="Size"/> bytes of <paramref name="data"/>.
    /// Only the length is checked; semantic validation (for example the minimum <see cref="Length"/>) is left to the caller.
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

        // Straight span reads, no allocation. The message id overlaps service id and method id (bytes 0..3).
        ushort serviceId = BinaryPrimitives.ReadUInt16BigEndian(data);
        ushort methodId = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        uint messageId = BinaryPrimitives.ReadUInt32BigEndian(data);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        ushort clientId = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        ushort sessionId = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        byte protoVer = data[12];
        byte ifVer = data[13];
        byte msgType = data[14];
        byte returnCode = data[15];

        header = new SomeIpHeader(
            serviceId, methodId, messageId, length,
            clientId, sessionId, protoVer, ifVer, msgType, returnCode);
        return true;
    }

    #endregion
}

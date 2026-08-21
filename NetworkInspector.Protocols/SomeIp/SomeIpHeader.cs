// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// SOME/IP header (16 bytes, AUTOSAR SOME/IP specification).
/// </summary>
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

    /// <summary>Attempts to parse a SOME/IP header from the given data.</summary>
    internal static bool TryParse(ReadOnlySpan<byte> data, out SomeIpHeader header)
    {
        if (data.Length < Size)
        {
            header = default;
            return false;
        }

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

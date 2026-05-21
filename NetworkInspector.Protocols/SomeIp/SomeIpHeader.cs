// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// SOME/IP header (16 bytes, AUTOSAR SOME/IP specification).
/// <code>
/// +---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+
/// | Service ID    | Method ID     | Length                          |
/// +---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+
/// | Client ID     | Session ID    | PV  | IV  | MT  | RC            |
/// +---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+
/// PV = Protocol Version, IV = Interface Version, MT = Message Type, RC = Return Code
/// </code>
/// </summary>
internal readonly struct SomeIpHeader
{
    /// <summary>Size of the SOME/IP header in bytes.</summary>
    internal const int Size = 16;

    /// <summary>Service ID (upper 16 bits of Message ID).</summary>
    internal ushort ServiceId
    {
        get;
    }

    /// <summary>Method ID (lower 16 bits of Message ID).</summary>
    internal ushort MethodId
    {
        get;
    }

    /// <summary>Combined Message ID (ServiceId &lt;&lt; 16 | MethodId).</summary>
    internal uint MessageId
    {
        get;
    }

    /// <summary>Length of SOME/IP payload + 8 bytes (Client/Session/PV/IV/MT/RC).</summary>
    internal uint Length
    {
        get;
    }

    /// <summary>Client ID.</summary>
    internal ushort ClientId
    {
        get;
    }

    /// <summary>Session ID.</summary>
    internal ushort SessionId
    {
        get;
    }

    /// <summary>Protocol Version (should be 1).</summary>
    internal byte ProtocolVersion
    {
        get;
    }

    /// <summary>Interface Version.</summary>
    internal byte InterfaceVersion
    {
        get;
    }

    /// <summary>Message Type.</summary>
    internal byte MessageType
    {
        get;
    }

    /// <summary>Return Code.</summary>
    internal byte ReturnCode
    {
        get;
    }

    private SomeIpHeader(
        ushort serviceId, ushort methodId, uint messageId, uint length,
        ushort clientId, ushort sessionId,
        byte protocolVersion, byte interfaceVersion, byte messageType, byte returnCode)
    {
        ServiceId = serviceId;
        MethodId = methodId;
        MessageId = messageId;
        Length = length;
        ClientId = clientId;
        SessionId = sessionId;
        ProtocolVersion = protocolVersion;
        InterfaceVersion = interfaceVersion;
        MessageType = messageType;
        ReturnCode = returnCode;
    }

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
}

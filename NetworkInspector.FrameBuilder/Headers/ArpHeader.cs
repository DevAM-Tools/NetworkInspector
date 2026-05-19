// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.


namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// ARP header for Ethernet/IPv4 (28 bytes).
/// Layout per RFC 826: HardwareType(2), ProtocolType(2), HwAddrLen(1), ProtoAddrLen(1),
/// Opcode(2), SenderMac(6), SenderIp(4), TargetMac(6), TargetIp(4).
/// </summary>
[BinaryWritable]
internal readonly partial struct ArpHeader
{
    /// <summary>Size of the ARP header for Ethernet/IPv4.</summary>
    internal const int Size = 28;

    /// <summary>Hardware type (1 = Ethernet).</summary>
    internal U16BE HardwareType
    {
        get; init;
    }

    /// <summary>Protocol type (0x0800 = IPv4).</summary>
    internal U16BE ProtocolType
    {
        get; init;
    }

    /// <summary>Hardware address length (6 for Ethernet).</summary>
    internal byte HardwareAddrLen
    {
        get; init;
    }

    /// <summary>Protocol address length (4 for IPv4).</summary>
    internal byte ProtocolAddrLen
    {
        get; init;
    }

    /// <summary>ARP operation (1 = Request, 2 = Reply).</summary>
    internal U16BE Opcode
    {
        get; init;
    }

    /// <summary>Sender hardware (MAC) address.</summary>
    internal MacAddress SenderMac
    {
        get; init;
    }

    /// <summary>Sender protocol (IPv4) address.</summary>
    internal IPv4Address SenderIp
    {
        get; init;
    }

    /// <summary>Target hardware (MAC) address.</summary>
    internal MacAddress TargetMac
    {
        get; init;
    }

    /// <summary>Target protocol (IPv4) address.</summary>
    internal IPv4Address TargetIp
    {
        get; init;
    }

    /// <summary>
    /// Creates an ARP header for Ethernet/IPv4 with standard hardware/protocol settings.
    /// </summary>
    /// <param name="opcode">ARP operation code (1 = Request, 2 = Reply).</param>
    /// <param name="senderMac">Sender MAC address.</param>
    /// <param name="senderIp">Sender IPv4 address.</param>
    /// <param name="targetMac">Target MAC address.</param>
    /// <param name="targetIp">Target IPv4 address.</param>
    internal static ArpHeader Create(
        ushort opcode,
        MacAddress senderMac,
        IPv4Address senderIp,
        MacAddress targetMac,
        IPv4Address targetIp)
    {
        return new ArpHeader
        {
            HardwareType = (ushort)1,       // Ethernet
            ProtocolType = (ushort)0x0800,  // IPv4
            HardwareAddrLen = 6,
            ProtocolAddrLen = 4,
            Opcode = opcode,
            SenderMac = senderMac,
            SenderIp = senderIp,
            TargetMac = targetMac,
            TargetIp = targetIp,
        };
    }
}

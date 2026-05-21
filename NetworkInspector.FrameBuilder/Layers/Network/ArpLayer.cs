// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// ARP (Address Resolution Protocol) layer for Ethernet/IPv4 (28 bytes).
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IProtocolLayer"/> — sits directly on Ethernet (no transport layer above).</item>
///   <item><see cref="IProvidesProtocolType"/> — value 0x0806 so an outer Ethernet/VLAN
///   auto-patches its EtherType.</item>
///   <item><see cref="IProvidesNextProtocolValue"/> — outer link layer must patch us.</item>
/// </list>
/// <para>ARP carries no inner protocol and has no post-fix work.</para>
/// </remarks>
public readonly struct ArpLayer : IStatelessLayer, IPseudoHeaderIndependent, IProvidesProtocolType, IProvidesNextProtocolValue<EtherTypeKind>
{
    /// <summary>ARP operation: request.</summary>
    public const ushort OpcodeRequest = 1;

    /// <summary>ARP operation: reply.</summary>
    public const ushort OpcodeReply = 2;

    private readonly ushort _Opcode;
    private readonly MacAddress _SenderMac;
    private readonly IPv4Address _SenderIp;
    private readonly MacAddress _TargetMac;
    private readonly IPv4Address _TargetIp;

    /// <summary>Creates an ARP layer.</summary>
    /// <param name="opcode">ARP operation (<see cref="OpcodeRequest"/> or <see cref="OpcodeReply"/>).</param>
    /// <param name="senderMac">Sender hardware address.</param>
    /// <param name="senderIp">Sender IPv4 address.</param>
    /// <param name="targetMac">Target hardware address (MAC zero for requests).</param>
    /// <param name="targetIp">Target IPv4 address.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArpLayer(ushort opcode, MacAddress senderMac, IPv4Address senderIp, MacAddress targetMac, IPv4Address targetIp)
    {
        _Opcode = opcode;
        _SenderMac = senderMac;
        _SenderIp = senderIp;
        _TargetMac = targetMac;
        _TargetIp = targetIp;
    }

    /// <summary>Creates an ARP layer accepting raw <see langword="uint"/> values for the IPv4 addresses.</summary>
    /// <param name="opcode">ARP operation (<see cref="OpcodeRequest"/> or <see cref="OpcodeReply"/>).</param>
    /// <param name="senderMac">Sender hardware address.</param>
    /// <param name="senderIp">Sender IPv4 address as a 32-bit big-endian value.</param>
    /// <param name="targetMac">Target hardware address (MAC zero for requests).</param>
    /// <param name="targetIp">Target IPv4 address as a 32-bit big-endian value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArpLayer(ushort opcode, MacAddress senderMac, uint senderIp, MacAddress targetMac, uint targetIp)
        : this(opcode, senderMac, new IPv4Address(senderIp), targetMac, new IPv4Address(targetIp))
    {
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ArpHeader.Size;
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => EtherTypes.Arp;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        ArpHeader hdr = ArpHeader.Create(_Opcode, _SenderMac, _SenderIp, _TargetMac, _TargetIp);
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // ARP has no post-fix work.
    }
}

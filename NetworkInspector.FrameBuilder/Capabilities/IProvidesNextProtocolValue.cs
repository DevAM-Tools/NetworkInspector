// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability: the layer offers its <see cref="IProvidesProtocolType.ProtocolType"/>
/// as the value to be written into an outer
/// <see cref="IConsumesNextProtocolValue"/> slot (Ethernet EtherType,
/// IPv4 Protocol, IPv6 NextHeader, VLAN inner EtherType, …).
/// </summary>
/// <remarks>
/// <para>
/// The value flow is inner → outer: this inner layer publishes a value;
/// the outer’s <see cref="IConsumesNextProtocolValue.PatchNextProtocol"/>
/// writes it into the outer’s field, unless the outer’s field was pinned
/// to an explicit value (in which case the explicit value wins and this
/// publication is silently ignored).
/// </para>
/// <para>
/// Used as a generic constraint on the new layer in capability-typed
/// <c>Then(...)</c> overloads.  Without this marker the capability-typed
/// overloads do not match and the compiler refuses the call.
/// </para>
/// </remarks>
public interface IProvidesNextProtocolValue : IProvidesProtocolType
{
}

/// <summary>
/// Compile-time-typed capability: the layer publishes a value in the
/// namespace identified by <typeparamref name="TKind"/> (e.g.
/// <see cref="EtherTypeKind"/> for IP/ARP over Ethernet,
/// <see cref="IpNextProtocolKind"/> for TCP/UDP/ICMP over IP).
/// </summary>
/// <typeparam name="TKind">Phantom-type discriminator for the value namespace.</typeparam>
/// <remarks>
/// A layer may implement this interface for more than one
/// <typeparamref name="TKind"/> when its protocol-type value lives in
/// several namespaces simultaneously (e.g. a hypothetical IP-in-IP bridge
/// publishing both an EtherType and an IP-next-protocol number).
/// </remarks>
public interface IProvidesNextProtocolValue<TKind> : IProvidesNextProtocolValue
    where TKind : struct
{
}

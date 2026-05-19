// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Phantom-type discriminator for the IP next-protocol namespace
/// (IPv4 Protocol field, IPv6 NextHeader, IPv6 extension-header NextHeader).
/// </summary>
/// <remarks>
/// <para>
/// Used as <c>TKind</c> on <see cref="IConsumesNextProtocolValue{TKind}"/>
/// and <see cref="IProvidesNextProtocolValue{TKind}"/> so that the C# compiler
/// statically prevents stacking a layer that lives in the EtherType namespace
/// (e.g. an inner IPv4) on top of an IP-next-protocol publisher (e.g. another
/// IPv4) — except where it is meaningful and intended (IP-in-IP tunneling,
/// where the inner IP layer additionally publishes an IP-protocol number).
/// </para>
/// <para>
/// Concrete values used in this namespace include 1 (ICMPv4),
/// 4 (IPv4-in-IP), 6 (TCP), 17 (UDP), 41 (IPv6-in-IP), 43 (Routing),
/// 44 (Fragment), 50 (ESP), 51 (AH), 58 (ICMPv6), 59 (NoNextHeader),
/// 60 (DestinationOptions).
/// </para>
/// </remarks>
public readonly struct IpNextProtocolKind
{
}

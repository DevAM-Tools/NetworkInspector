// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Phantom-type discriminator for the IEEE 802.3 EtherType / 802.1Q
/// inner-EtherType namespace (16-bit values in the Ethernet/VLAN header).
/// </summary>
/// <remarks>
/// <para>
/// Used as <c>TKind</c> on <see cref="IConsumesNextProtocolValue{TKind}"/>
/// and <see cref="IProvidesNextProtocolValue{TKind}"/> so that the C# compiler
/// statically prevents stacking a layer that lives in a different
/// next-protocol namespace (e.g. UDP, an IP-protocol-namespace consumer)
/// directly on top of an EtherType-publishing layer.
/// </para>
/// <para>
/// Concrete values used in this namespace include 0x0800 (IPv4),
/// 0x0806 (ARP), 0x86DD (IPv6), 0x8100 (802.1Q VLAN), 0x88A8 (QinQ).
/// </para>
/// </remarks>
public readonly struct EtherTypeKind
{
}

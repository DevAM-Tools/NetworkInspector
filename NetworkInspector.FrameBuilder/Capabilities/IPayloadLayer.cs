// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability: the layer is a terminal payload carrier — it does not require
/// a specific next-protocol field to be patched on the outer layer.
/// </summary>
/// <remarks>
/// <para>
/// Used to mark application-level layers (SOME/IP, DNS, DHCP, TLS records,
/// HTTP, Signal-PDU, …) so the capability-typed payload <c>Then(...)</c>
/// overload can attach them onto an outer layer without requiring the outer
/// layer to publish a next-protocol field.
/// </para>
/// <para>
/// Unlike the removed positional slot tag <c>IApplicationLayer</c>, this is
/// a real semantic capability: the implementing layer declares it has no
/// next-protocol coupling to its outer layer.  It does not constrain
/// <em>where</em> the layer may live — only that it can be appended without
/// a protocol-field patch handshake.
/// </para>
/// </remarks>
public interface IPayloadLayer : IProtocolLayer
{
}

// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability: the layer can be appended to any outer layer without
/// requiring the outer layer to publish a pseudo-header.  Implemented by
/// every protocol layer except those that also carry
/// <see cref="IRequiresPseudoHeader"/> (TCP, UDP, ICMPv6 — the
/// transport-checksum-bearing layers).
/// </summary>
/// <remarks>
/// <para>
/// This marker exists because C# generic constraints cannot express the
/// negation of an interface.  To enforce at compile time that a
/// pseudo-header-requiring layer is only placed onto an outer layer that
/// publishes a pseudo-header, the <c>Then(...)</c> overload set is split
/// into two mutually exclusive shapes:
/// </para>
/// <list type="bullet">
///   <item>The "loose" overload requires
///   <see cref="IPseudoHeaderIndependent"/> on the new layer and imposes
///   no pseudo-header constraint on the outer.</item>
///   <item>The "strict" overload requires
///   <see cref="IRequiresPseudoHeader"/> on the new layer and
///   <see cref="IProvidesPseudoHeader"/> on the outer.</item>
/// </list>
/// <para>
/// Because no layer implements both <see cref="IPseudoHeaderIndependent"/>
/// and <see cref="IRequiresPseudoHeader"/>, exactly one of the two
/// overloads matches for any given <c>TNew</c>.  When neither matches
/// (e.g. <c>EthernetLayer.Then(udp)</c>), the call is rejected at compile
/// time with the correct rationale: UDP requires a pseudo-header, but the
/// outer Ethernet layer does not provide one.
/// </para>
/// </remarks>
public interface IPseudoHeaderIndependent : IProtocolLayer
{
}

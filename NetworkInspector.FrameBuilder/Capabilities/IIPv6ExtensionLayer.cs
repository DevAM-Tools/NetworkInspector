// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Marker interface for an IPv6 extension header layer (HopByHop, Routing,
/// DestinationOptions, Fragment).  Extension layers slot between
/// <see cref="IPv6Layer"/> and the transport layer; they all carry an
/// 8-bit NextHeader field that is patched from the next-inner layer's
/// <see cref="IProvidesProtocolType"/>, and they all forward the outer
/// IPv6's pseudo-header to the transport layer (concept §4.2 / RFC 8200).
/// </summary>
/// <remarks>
/// <para>
/// In post-fix phase <see cref="FixPhase.PublishPseudoHeader"/>, every
/// extension layer overrides <see cref="PostFixContext.PseudoProtocol"/>
/// with its own NextHeader byte (the actual upper-layer protocol once the
/// extension chain is unwound) and advances
/// <see cref="PostFixContext.TransportOffset"/> past its own header.
/// </para>
/// <para>
/// Position class: an IPv6 extension layer always carries the IP-next-protocol
/// namespace (publishes <see cref="IpNextProtocolKind"/> *and* requires
/// <see cref="IpNextProtocolKind"/> from its outer layer), so the generic
/// capability-typed <c>Then(...)</c> overloads handle the chaining without a
/// dedicated extension-only overload.
/// </para>
/// </remarks>
public interface IIPv6ExtensionLayer : IProtocolLayer
{
    /// <summary>
    /// IPv6 protocol number identifying this extension header
    /// (e.g. 0 for HopByHop, 43 for Routing, 60 for DestOpts, 44 for Fragment).
    /// Reused as the value patched into the outer layer's NextHeader.
    /// </summary>
    byte ExtensionProtocol
    {
        get;
    }
}

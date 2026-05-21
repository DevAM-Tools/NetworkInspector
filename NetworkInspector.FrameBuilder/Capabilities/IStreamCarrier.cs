// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability marker: the layer is a byte-stream carrier — it accepts an
/// arbitrary payload byte stream of any length and is responsible for
/// segmenting it into one or more wire frames.  Carried by TCP-like
/// transports.
/// </summary>
/// <remarks>
/// <para>
/// This capability is purely a marker.  It does not introduce any new
/// virtual members on the layer; it signals to higher-level composition
/// helpers (notably <see cref="TcpConnection{TC2S,TS2C}"/>) that the layer
/// is a valid terminal carrier for an <see cref="IStreamProducer"/> and
/// may be driven by a stream-to-segment splitter.
/// </para>
/// <para>
/// Implemented by <see cref="TcpLayer"/>, <see cref="TcpLayerWithOptions"/>,
/// <see cref="TcpLayerWithAutoSequence"/> and the dedicated
/// <see cref="TcpStreamLayer"/>.  Datagram transports such as
/// <see cref="UdpLayer"/> deliberately do <em>not</em> carry this
/// capability: their semantic boundary is the datagram, not a byte stream.
/// </para>
/// <para>
/// The marker is independent of the existing pseudo-header /
/// next-protocol capability set; it composes orthogonally with
/// <see cref="IRequiresPseudoHeader"/>, <see cref="IProvidesProtocolType"/>
/// and friends.  Stream-carrying TCP layers continue to require an outer
/// IP layer that publishes a pseudo-header.
/// </para>
/// </remarks>
public interface IStreamCarrier : IProtocolLayer
{
}

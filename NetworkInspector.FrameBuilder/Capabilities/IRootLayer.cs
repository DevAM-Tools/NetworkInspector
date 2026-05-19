// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Marker capability: the layer is eligible to be the root of a frame
/// (position 0, no outer container required).
/// </summary>
/// <remarks>
/// <para>
/// Carried by link-/bus-layers (Ethernet, SocketCAN, FlexRay, LIN) and by
/// raw-IP layers (when used in tun-interface scenarios).  Required by
/// <see cref="FrameStack.Start{TLink}(in TLink)"/>.
/// </para>
/// <para>
/// Replaces the old positional slot tag <c>ILinkLayer</c>.  C# has no
/// negative generic constraint ("<c>where T : !IProvidesNextProtocolValue</c>")
/// so an explicit positive marker is the pragmatic way to gate
/// <see cref="FrameStack.Start{TLink}(in TLink)"/> against transports/apps
/// being used as a root.
/// </para>
/// </remarks>
public interface IRootLayer : IProtocolLayer
{
}

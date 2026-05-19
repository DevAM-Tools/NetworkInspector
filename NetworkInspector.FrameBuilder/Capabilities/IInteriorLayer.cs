// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability: the layer accepts an inner layer beneath it in the stack.
/// In other words, the layer is <em>not</em> a terminal payload carrier
/// (<see cref="IPayloadLayer"/>) and may legally appear as the outer
/// (<c>TOld</c>) operand of a <c>Then(...)</c> composition.
/// </summary>
/// <remarks>
/// <para>
/// This is the only structural compile-time gate on the outer operand of
/// <c>Then(...)</c>: it prevents stacking anything onto a terminal payload
/// layer (e.g. <c>FrameStack.Start(eth).Then(ip).Then(udp).Then(someIp).Then(udp)</c>
/// is rejected because <c>SomeIpLayer</c> is not <see cref="IInteriorLayer"/>).
/// </para>
/// <para>
/// A layer that is <see cref="IInteriorLayer"/> must not also be
/// <see cref="IPayloadLayer"/>; the two markers are mutually exclusive by
/// design.  Pure terminal layers (ARP, ICMP echo, SOME/IP) implement
/// neither <see cref="IInteriorLayer"/> nor <see cref="IPayloadLayer"/>
/// is exclusively reserved for application-payload layers.
/// </para>
/// </remarks>
public interface IInteriorLayer : IProtocolLayer
{
}

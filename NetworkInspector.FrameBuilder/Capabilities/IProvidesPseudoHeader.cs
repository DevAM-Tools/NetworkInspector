// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability: the layer publishes a pseudo-header (source/destination
/// address, IP protocol number, IP/v6 flag) into <see cref="PostFixContext"/>
/// during <see cref="FixPhase.PublishPseudoHeader"/> so an inner transport
/// layer can compute its checksum.
/// </summary>
public interface IProvidesPseudoHeader : IProtocolLayer
{
}

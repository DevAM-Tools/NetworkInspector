// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability: the layer requires an outer <see cref="IProvidesPseudoHeader"/>
/// to compute its checksum during <see cref="FixPhase.InnerChecksum"/>.
/// </summary>
public interface IRequiresPseudoHeader : IProtocolLayer
{
}

// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability: the layer requires an outer <see cref="IProvidesPseudoHeader"/>
/// to compute its checksum during <see cref="FixPhase.InnerChecksum"/>.
/// </summary>
public interface IRequiresPseudoHeader : IProtocolLayer
{
}

// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability: the layer needs an outer <see cref="IProvidesMtu"/> anchor
/// somewhere in the cons-list to size segments / fragments.
/// </summary>
public interface IRequiresMtu : IProtocolLayer
{
}

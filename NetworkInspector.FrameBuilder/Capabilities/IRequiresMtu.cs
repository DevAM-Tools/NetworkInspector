// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability: the layer needs an outer <see cref="IProvidesMtu"/> anchor
/// somewhere in the cons-list to size segments / fragments.
/// </summary>
public interface IRequiresMtu : IProtocolLayer
{
}

// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability: the layer advertises a link-layer MTU (in bytes) to inner
/// fragmentable layers.  Anchored at the link or first-MTU-bearing layer.
/// </summary>
public interface IProvidesMtu : IProtocolLayer
{
    /// <summary>Link-layer MTU in bytes.</summary>
    ushort LinkMtu
    {
        get;
    }
}

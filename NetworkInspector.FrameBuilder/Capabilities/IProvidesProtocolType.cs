// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability: the layer needs the predecessor's
/// <see cref="IConsumesNextProtocolValue"/> to be patched with its
/// <see cref="ProtocolType"/> value.
/// </summary>
public interface IProvidesProtocolType : IProtocolLayer
{
    /// <summary>
    /// Protocol-type value that the preceding <see cref="IConsumesNextProtocolValue"/>
    /// must encode (e.g. 0x0800 for IPv4, 6 for TCP).
    /// </summary>
    ushort ProtocolType
    {
        get;
    }
}

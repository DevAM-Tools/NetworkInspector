// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

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

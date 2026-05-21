// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Well-known ARP opcode constants used when constructing <see cref="ArpLayer"/> frames.
/// </summary>
public static class ArpWriter
{
    /// <summary>ARP Request opcode (1).</summary>
    public const ushort OpcodeRequest = 1;

    /// <summary>ARP Reply opcode (2).</summary>
    public const ushort OpcodeReply = 2;
}

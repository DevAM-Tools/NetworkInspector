// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Represents a single DHCPv4 / BOOTP TLV option (type + data).
/// </summary>
/// <remarks>
/// Thread safety: immutable value type; safe for concurrent use after construction.
/// </remarks>
public readonly struct DhcpV4Option(byte type, ReadOnlyMemory<byte> data)
{
    /// <summary>Option type code.</summary>
    public byte Type { get; } = type;

    /// <summary>Option data bytes (length is implicit from <see cref="Data"/> size).</summary>
    public ReadOnlyMemory<byte> Data { get; } = data;
}

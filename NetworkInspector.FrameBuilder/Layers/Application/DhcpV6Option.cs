// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Represents a single DHCPv6 TLV option (code + data, per RFC 8415 §7.3).
/// </summary>
/// <remarks>
/// Thread safety: immutable value type; safe for concurrent use after construction.
/// </remarks>
public readonly struct DhcpV6Option(ushort code, ReadOnlyMemory<byte> data)
{
    /// <summary>Option code (2-byte big-endian field per RFC 8415 §7.3).</summary>
    public ushort Code { get; } = code;

    /// <summary>Option data bytes (length is implicit from <see cref="Data"/> size).</summary>
    public ReadOnlyMemory<byte> Data { get; } = data;
}

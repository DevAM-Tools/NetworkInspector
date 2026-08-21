// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Represents a single DHCPv6 TLV option (code + data, per RFC 8415 §7.3).
/// </summary>
/// <remarks>
/// Thread safety: immutable value type; safe for concurrent use after construction.
/// </remarks>
/// <param name="Code">Option code (2-byte big-endian field per RFC 8415 §7.3).</param>
/// <param name="Data">Option data bytes (length is implicit from <see cref="Data"/> size).</param>
public readonly record struct DhcpV6Option(ushort Code, ReadOnlyMemory<byte> Data);

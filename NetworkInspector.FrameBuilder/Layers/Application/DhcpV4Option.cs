// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Represents a single DHCPv4 / BOOTP TLV option (type + data).
/// </summary>
/// <remarks>
/// Thread safety: immutable value type; safe for concurrent use after construction.
/// </remarks>
/// <param name="Type">Option type code.</param>
/// <param name="Data">Option data bytes (length is implicit from <see cref="Data"/> size).</param>
public readonly record struct DhcpV4Option(byte Type, ReadOnlyMemory<byte> Data);

// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// Identifies the key type used when a protocol dispatch table invoked the current protocol.
/// Stored inside <see cref="DispatchContext"/> which is embedded in <see cref="ParseContext"/>.
/// <para>
/// <c>None = 0</c> is the default value (<see langword="default"/>), meaning no dispatch
/// context is present — the protocol was called directly rather than via a dispatch table.
/// </para>
/// </summary>
public enum DispatchKeyKind : byte
{
    /// <summary>No dispatch context. The protocol was called directly, not via a dispatch table.</summary>
    None = 0,

    /// <summary>Dispatched via a <see cref="ulong"/> key (e.g., CAN ID, FlexRay frame ID, LIN ID).</summary>
    U64 = 1,

    /// <summary>Dispatched via a <see cref="string"/> key (e.g., protocol name).</summary>
    String = 2,

    /// <summary>Dispatched via a byte-sequence key.</summary>
    Bytes = 3,

    /// <summary>Dispatched via a <see cref="bool"/> key.</summary>
    Bool = 4,

    /// <summary>Dispatched via a wildcard match (any key in the table).</summary>
    Any = 5,
}
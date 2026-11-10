// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tables;

/// <summary>Key type for protocol dispatch tables.</summary>
public enum ProtocolTableKeyType : byte
{
    /// <summary>Numeric key (port numbers, protocol values), compared as an unsigned 64-bit integer.</summary>
    U64 = 0,
    /// <summary>Text-based identifier, compared ordinally (case-sensitive).</summary>
    String = 1,
    /// <summary>Binary data (signatures, prefixes), compared by content as a <see cref="BytesKey"/>.</summary>
    Bytes = 2,
    /// <summary>Boolean branching: one list of protocols for <see langword="true"/> and one for <see langword="false"/>.</summary>
    Bool = 3,
    /// <summary>Catch-all: no key, a single list of protocols that applies to every dispatch.</summary>
    Any = 4,
}

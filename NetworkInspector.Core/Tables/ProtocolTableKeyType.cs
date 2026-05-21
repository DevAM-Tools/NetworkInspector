// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tables;

/// <summary>Key type for protocol dispatch tables.</summary>
public enum ProtocolTableKeyType : byte
{
    #region Enum Values

    /// <summary>Numeric key (port numbers, protocol values).</summary>
    U64 = 0,
    /// <summary>Text-based identifier.</summary>
    String = 1,
    /// <summary>Binary data (signatures, prefixes).</summary>
    Bytes = 2,
    /// <summary>Boolean branching.</summary>
    Bool = 3,
    /// <summary>Catch-all (single entry).</summary>
    Any = 4,

    #endregion
}

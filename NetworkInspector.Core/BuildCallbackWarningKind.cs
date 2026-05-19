// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core;

/// <summary>
/// Identifies which kind of entity was referenced by an unresolved deferred
/// callback during <see cref="StackBuilder.Build"/>.
/// </summary>
public enum BuildCallbackWarningKind
{
    #region Enum Values

    /// <summary>A protocol referenced via <c>WhenProtocolRegistered</c> was never registered.</summary>
    Protocol,

    /// <summary>A field referenced via <c>WhenFieldRegistered</c> was never registered.</summary>
    Field,

    /// <summary>A protocol table referenced via <c>WhenProtocolTableRegistered</c> was never registered.</summary>
    ProtocolTable,

    #endregion
}
// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Ast;

/// <summary>
/// Base class for every parsed filter node. Nodes are immutable so a single AST can be
/// re-bound and re-compiled against a different <see cref="IStack"/> by
/// <see cref="Filter.TryDerive"/> without re-lexing.
/// </summary>
internal abstract class FilterNode
{
    #region Construction

    /// <summary>Records the node's source span.</summary>
    protected FilterNode(int position, int length)
    {
        Position = position;
        Length = length;
    }

    #endregion

    #region Properties

    /// <summary>Start offset of this node in the original expression.</summary>
    public int Position { get; }

    /// <summary>Length of this node in the original expression.</summary>
    public int Length { get; }

    #endregion
}

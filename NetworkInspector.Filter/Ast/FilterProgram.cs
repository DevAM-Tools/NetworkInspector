// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Ast;

/// <summary>
/// A parsed filter expression: the root predicate plus the language features it uses.
/// The program is immutable and stack-agnostic, so it can be re-bound and re-compiled for a
/// different <see cref="IStack"/> without re-lexing.
/// <para><b>Thread-safety:</b> immutable, so a program is shared by the original and every derived filter.</para>
/// </summary>
internal sealed class FilterProgram(string original, FilterNode root, FilterFeature features)
{
    #region Properties

    /// <summary>The original expression text.</summary>
    public string Original { get; } = original;

    /// <summary>The root predicate.</summary>
    public FilterNode Root { get; } = root;

    /// <summary>Language features used anywhere in the tree.</summary>
    public FilterFeature Features { get; } = features;

    /// <summary>
    /// Whether evaluation carries state across packets (currently only <c>flank</c>). Stateful programs must see every
    /// packet in order, so they are never pruned by the presence index and always use the match cache.
    /// </summary>
    public bool IsStateful => (Features & FilterFeature.Flank) != 0;

    #endregion
}

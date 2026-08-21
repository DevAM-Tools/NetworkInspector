// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Ast;

#region Comparison

/// <summary>Relational and equality operators available on field comparisons and flank endpoints.</summary>
internal enum CompareOp : byte
{
    /// <summary><c>==</c>.</summary>
    Equal = 0,

    /// <summary><c>!=</c>.</summary>
    NotEqual = 1,

    /// <summary><c>&lt;</c>.</summary>
    LessThan = 2,

    /// <summary><c>&lt;=</c>.</summary>
    LessEqual = 3,

    /// <summary><c>&gt;</c>.</summary>
    GreaterThan = 4,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterEqual = 5,
}

#endregion

#region Logical

/// <summary>Short-circuiting boolean connectives.</summary>
internal enum LogicalOp : byte
{
    /// <summary><c>&amp;&amp;</c>.</summary>
    And = 0,

    /// <summary><c>||</c>.</summary>
    Or = 1,
}

#endregion

#region String

/// <summary>Text predicates applied to string-valued fields.</summary>
internal enum StringOp : byte
{
    /// <summary><c>contains</c> — ordinal substring test.</summary>
    Contains = 0,

    /// <summary><c>matches</c> — regular-expression test.</summary>
    Matches = 1,
}

#endregion

#region Feature classification

/// <summary>Language features used by a compiled program; drives statefulness and cost decisions.</summary>
[Flags]
internal enum FilterFeature : byte
{
    /// <summary>Presence, comparisons, sets, ranges, slices and string predicates.</summary>
    Classic = 0,

    /// <summary><c>flank(…)</c> edge detection — makes the filter stateful.</summary>
    Flank = 1,

    /// <summary><c>$Name[i?] { … }</c> subtree scope.</summary>
    Scope = 2,
}

#endregion

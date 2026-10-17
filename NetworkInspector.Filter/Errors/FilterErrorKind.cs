// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Errors;

/// <summary>Categorizes filter errors by the phase that produced them.</summary>
public enum FilterErrorKind : byte
{
    #region Front-end

    /// <summary>Unexpected character or malformed token.</summary>
    LexerError = 0,

    /// <summary>Malformed expression or unexpected token.</summary>
    SyntaxError = 1,

    /// <summary>Malformed literal value.</summary>
    InvalidValue = 2,

    /// <summary>Language construct that was deliberately removed in v1 (<c>seq</c>, <c>stream</c>, <c>window</c>, <c>let</c>, <c>where</c>, <c>nav</c>).</summary>
    UnsupportedFeature = 3,

    #endregion

    #region Binding

    /// <summary>
    /// Referenced field, alias or protocol does not exist on the compile-time stack. Unknown protocol names
    /// report this kind too, so callers have one failure kind for bind misses; <see cref="FilterError.Suggestion"/>
    /// names the closest known name when one is near enough. Value 5 is unused (it was the never-produced
    /// <c>UnknownProtocol</c>); the remaining values keep their numbers.
    /// </summary>
    UnknownField = 4,

    /// <summary>Operator applied to incompatible operand kinds.</summary>
    TypeMismatch = 6,

    /// <summary>A non-empty expression was compiled without a protocol stack.</summary>
    StackRequired = 7,

    #endregion

    #region Back-end and runtime

    /// <summary>Internal code-generation failure.</summary>
    CompilerError = 8,

    /// <summary>A user-supplied compile callback threw.</summary>
    CallbackFailed = 9,

    /// <summary>Error raised while evaluating a packet.</summary>
    RuntimeError = 10,

    /// <summary>A stateful filter was queried with a packet id below the highest evaluated id without a preceding state reset.</summary>
    OutOfOrder = 11,

    /// <summary>The packet was parsed with <see cref="NetworkInspector.Core.FieldTreeMode.Skip"/> and has no field tree to evaluate.</summary>
    NoFieldTree = 12,

    /// <summary>The expression selects a subtree, which the tree-free observer cannot run.</summary>
    NeedsFieldTree = 13,

    #endregion
}

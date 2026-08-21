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

    /// <summary>Referenced field does not exist on the compile-time stack.</summary>
    UnknownField = 4,

    /// <summary>Referenced protocol does not exist on the compile-time stack.</summary>
    UnknownProtocol = 5,

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

    #endregion
}

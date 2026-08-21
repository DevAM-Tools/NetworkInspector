// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Lexer;

/// <summary>A single lexical token with its source span and raw (or unescaped) text.</summary>
/// <param name="Kind">The token classification.</param>
/// <param name="Span">Source span of the token.</param>
/// <param name="Text">
/// The token text. For <see cref="TokenKind.StringLiteral"/> this is the unescaped
/// content without the surrounding quotes; for all other kinds it is the raw source text.
/// </param>
internal readonly record struct Token(TokenKind Kind, FilterSpan Span, string Text)
{
    #region Properties

    /// <summary>Start offset of the token in the source expression.</summary>
    public int Position => Span.Start;

    /// <summary>Length of the token in the source expression.</summary>
    public int Length => Span.Length;

    /// <summary>Exclusive end offset of the token in the source expression.</summary>
    public int End => Span.End;

    #endregion

    #region Factories

    /// <summary>Creates the end-of-input token at the given position.</summary>
    public static Token Eof(int position) => new(TokenKind.Eof, new FilterSpan(position, 0), string.Empty);

    #endregion

    #region Formatting

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind}('{Text}')@{Span}");

    #endregion
}

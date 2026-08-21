// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Lexer;

/// <summary>
/// Classifies each token produced by <see cref="FilterLexer"/>.
/// <para>
/// The v1 token set deliberately omits the Dev tokens for <c>seq</c>, <c>stream</c>,
/// <c>window</c>, <c>nav</c>, <c>step</c>, <c>let</c>, <c>where</c> and
/// <c>group</c>. Those words lex as ordinary <see cref="Identifier"/> tokens; the parser
/// rejects them with <see cref="FilterErrorKind.UnsupportedFeature"/> when they appear in
/// their removed syntactic position. The identifier <c>by</c> is a named <c>flank</c>
/// argument (<c>by:</c>), not a removed call.
/// </para>
/// </summary>
internal enum TokenKind : byte
{
    #region Literals

    /// <summary>Integer literal: <c>80</c>, <c>0xFF</c>, <c>0b1010</c>, <c>0o755</c>.</summary>
    Integer,

    /// <summary>Floating-point literal: <c>3.14</c>.</summary>
    Float,

    /// <summary>Quoted string literal: <c>"hello"</c>.</summary>
    StringLiteral,

    /// <summary>Dotted identifier: <c>tcp</c>, <c>udp.port</c>.</summary>
    Identifier,

    /// <summary>IPv4 address literal: <c>192.168.1.1</c>.</summary>
    Ipv4Address,

    /// <summary>IPv6 address literal: <c>2001:db8::1</c>.</summary>
    Ipv6Address,

    /// <summary>MAC address literal (exactly six hex pairs): <c>00:11:22:33:44:55</c>.</summary>
    MacAddress,

    /// <summary>Colon-separated hex byte sequence with a length other than six: <c>00:11:22</c>.</summary>
    HexBytes,

    /// <summary>Duration or packet-count literal: <c>100ms</c>, <c>5s</c>, <c>10packets</c>.</summary>
    Duration,

    #endregion

    #region Comparison operators

    /// <summary><c>==</c>.</summary>
    Equal,

    /// <summary><c>!=</c>.</summary>
    NotEqual,

    /// <summary><c>&lt;</c>.</summary>
    LessThan,

    /// <summary><c>&lt;=</c>.</summary>
    LessEqual,

    /// <summary><c>&gt;</c>.</summary>
    GreaterThan,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterEqual,

    #endregion

    #region Logical operators

    /// <summary><c>&amp;&amp;</c>, <c>&amp;</c> or <c>and</c>.</summary>
    And,

    /// <summary><c>||</c>, <c>|</c> or <c>or</c>.</summary>
    Or,

    /// <summary><c>!</c> or <c>not</c>.</summary>
    Not,

    #endregion

    #region Punctuation

    /// <summary><c>(</c>.</summary>
    LeftParen,

    /// <summary><c>)</c>.</summary>
    RightParen,

    /// <summary><c>[</c>.</summary>
    LeftBracket,

    /// <summary><c>]</c>.</summary>
    RightBracket,

    /// <summary><c>{</c>.</summary>
    LeftBrace,

    /// <summary><c>}</c>.</summary>
    RightBrace,

    /// <summary><c>,</c>.</summary>
    Comma,

    /// <summary><c>:</c>.</summary>
    Colon,

    /// <summary><c>..</c> range operator.</summary>
    Range,

    /// <summary><c>$</c> scope-anchor prefix.</summary>
    Dollar,

    #endregion

    #region Keywords

    /// <summary><c>in</c>.</summary>
    In,

    /// <summary><c>contains</c>.</summary>
    Contains,

    /// <summary><c>matches</c>.</summary>
    Matches,

    /// <summary><c>true</c>.</summary>
    True,

    /// <summary><c>false</c>.</summary>
    False,

    /// <summary><c>flank</c>.</summary>
    Flank,

    #endregion

    #region Special

    /// <summary>End of input.</summary>
    Eof,

    #endregion
}

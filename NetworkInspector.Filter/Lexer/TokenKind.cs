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
    Integer = 0,

    /// <summary>Floating-point literal: <c>3.14</c>.</summary>
    Float = 1,

    /// <summary>Quoted string literal: <c>"hello"</c>.</summary>
    StringLiteral = 2,

    /// <summary>Dotted identifier: <c>tcp</c>, <c>udp.port</c>.</summary>
    Identifier = 3,

    /// <summary>IPv4 address literal: <c>192.168.1.1</c>.</summary>
    Ipv4Address = 4,

    /// <summary>IPv6 address literal: <c>2001:db8::1</c>.</summary>
    Ipv6Address = 5,

    /// <summary>MAC address literal (exactly six hex pairs): <c>00:11:22:33:44:55</c>.</summary>
    MacAddress = 6,

    /// <summary>Colon-separated hex byte sequence with a length other than six: <c>00:11:22</c>.</summary>
    HexBytes = 7,

    /// <summary>Duration or packet-count literal: <c>100ms</c>, <c>5s</c>, <c>10packets</c>.</summary>
    Duration = 8,

    #endregion

    #region Comparison operators

    /// <summary><c>==</c>.</summary>
    Equal = 9,

    /// <summary><c>!=</c>.</summary>
    NotEqual = 10,

    /// <summary><c>&lt;</c>.</summary>
    LessThan = 11,

    /// <summary><c>&lt;=</c>.</summary>
    LessEqual = 12,

    /// <summary><c>&gt;</c>.</summary>
    GreaterThan = 13,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterEqual = 14,

    #endregion

    #region Logical operators

    /// <summary><c>&amp;&amp;</c>, <c>&amp;</c> or <c>and</c>.</summary>
    And = 15,

    /// <summary><c>||</c>, <c>|</c> or <c>or</c>.</summary>
    Or = 16,

    /// <summary><c>!</c> or <c>not</c>.</summary>
    Not = 17,

    #endregion

    #region Punctuation

    /// <summary><c>(</c>.</summary>
    LeftParen = 18,

    /// <summary><c>)</c>.</summary>
    RightParen = 19,

    /// <summary><c>[</c>.</summary>
    LeftBracket = 20,

    /// <summary><c>]</c>.</summary>
    RightBracket = 21,

    /// <summary><c>{</c>.</summary>
    LeftBrace = 22,

    /// <summary><c>}</c>.</summary>
    RightBrace = 23,

    /// <summary><c>,</c>.</summary>
    Comma = 24,

    /// <summary><c>:</c>.</summary>
    Colon = 25,

    /// <summary><c>..</c> range operator.</summary>
    Range = 26,

    /// <summary><c>$</c> scope-anchor prefix.</summary>
    Dollar = 27,

    #endregion

    #region Keywords

    /// <summary><c>in</c>.</summary>
    In = 28,

    /// <summary><c>contains</c>.</summary>
    Contains = 29,

    /// <summary><c>matches</c>.</summary>
    Matches = 30,

    /// <summary><c>true</c>.</summary>
    True = 31,

    /// <summary><c>false</c>.</summary>
    False = 32,

    /// <summary><c>flank</c>.</summary>
    Flank = 33,

    #endregion

    #region Special

    /// <summary>End of input.</summary>
    Eof = 34,

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Lexer;

/// <summary>Covers tokenization, literal disambiguation and lexer errors.</summary>
internal sealed class FilterLexerTests
{
    #region Helpers

    private static List<Token> _Tokenize(string source)
    {
        FilterLexer lexer = new(source);
        FilterResult<List<Token>> result = lexer.Tokenize();
        if (!result.TryGetValue(out List<Token>? tokens))
        {
            throw new InvalidOperationException($"Expected '{source}' to tokenize but got {result.Error}");
        }
        return tokens;
    }

    private static FilterError _TokenizeError(string source)
    {
        FilterLexer lexer = new(source);
        FilterResult<List<Token>> result = lexer.Tokenize();
        if (result.IsSuccess)
        {
            throw new InvalidOperationException($"Expected '{source}' to fail tokenizing.");
        }
        return result.Error;
    }

    #endregion

    #region Basics

    [Test]
    public async Task Tokenize_Empty_ProducesOnlyEof()
    {
        List<Token> tokens = _Tokenize(string.Empty);

        await Assert.That(tokens.Count).IsEqualTo(1);
        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.Eof);
    }

    [Test]
    public async Task Tokenize_Identifier_ProducesDottedPath()
    {
        List<Token> tokens = _Tokenize("udp.srcport");

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.Identifier);
        await Assert.That(tokens[0].Text).IsEqualTo("udp.srcport");
        await Assert.That(tokens[0].End).IsEqualTo(11);
    }

    [Test]
    [Arguments("==", TokenKind.Equal)]
    [Arguments("!=", TokenKind.NotEqual)]
    [Arguments("<", TokenKind.LessThan)]
    [Arguments("<=", TokenKind.LessEqual)]
    [Arguments(">", TokenKind.GreaterThan)]
    [Arguments(">=", TokenKind.GreaterEqual)]
    [Arguments("&&", TokenKind.And)]
    [Arguments("&", TokenKind.And)]
    [Arguments("||", TokenKind.Or)]
    [Arguments("|", TokenKind.Or)]
    [Arguments("!", TokenKind.Not)]
    [Arguments("(", TokenKind.LeftParen)]
    [Arguments(")", TokenKind.RightParen)]
    [Arguments("[", TokenKind.LeftBracket)]
    [Arguments("]", TokenKind.RightBracket)]
    [Arguments("{", TokenKind.LeftBrace)]
    [Arguments("}", TokenKind.RightBrace)]
    [Arguments(",", TokenKind.Comma)]
    [Arguments(":", TokenKind.Colon)]
    [Arguments("..", TokenKind.Range)]
    [Arguments("$", TokenKind.Dollar)]
    public async Task Tokenize_Operator_ProducesExpectedKind(string source, TokenKind expected)
    {
        List<Token> tokens = _Tokenize(source);

        await Assert.That(tokens[0].Kind).IsEqualTo(expected);
    }

    [Test]
    [Arguments("and", TokenKind.And)]
    [Arguments("or", TokenKind.Or)]
    [Arguments("not", TokenKind.Not)]
    [Arguments("in", TokenKind.In)]
    [Arguments("contains", TokenKind.Contains)]
    [Arguments("matches", TokenKind.Matches)]
    [Arguments("true", TokenKind.True)]
    [Arguments("false", TokenKind.False)]
    [Arguments("flank", TokenKind.Flank)]
    public async Task Tokenize_Keyword_ProducesExpectedKind(string source, TokenKind expected)
    {
        List<Token> tokens = _Tokenize(source);

        await Assert.That(tokens[0].Kind).IsEqualTo(expected);
    }

    [Test]
    [Arguments("seq")]
    [Arguments("stream")]
    [Arguments("window")]
    [Arguments("nav")]
    [Arguments("let")]
    [Arguments("where")]
    [Arguments("step")]
    public async Task Tokenize_RemovedKeyword_LexesAsIdentifier(string source)
    {
        List<Token> tokens = _Tokenize(source);

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.Identifier);
    }

    #endregion

    #region Literals

    [Test]
    [Arguments("80")]
    [Arguments("0xFF")]
    [Arguments("0b1010")]
    [Arguments("0o755")]
    [Arguments("0X1f")]
    [Arguments("0B1_0")]
    [Arguments("0O0_7")]
    [Arguments("0xAB_CD")]
    [Arguments("1_000")]
    [Arguments("-2")]
    public async Task Tokenize_Integer_ProducesIntegerToken(string source)
    {
        List<Token> tokens = _Tokenize(source);

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.Integer);
    }

    [Test]
    public async Task Tokenize_SignedInteger_PreservesLeadingMinus()
    {
        List<Token> tokens = _Tokenize("-2");

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.Integer);
        await Assert.That(tokens[0].Text).IsEqualTo("-2");
    }

    [Test]
    public async Task Tokenize_Float_ProducesFloatToken()
    {
        List<Token> tokens = _Tokenize("3.14");

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.Float);
    }

    [Test]
    public async Task Tokenize_RangeAfterInteger_SplitsIntoThreeTokens()
    {
        List<Token> tokens = _Tokenize("1..10");

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.Integer);
        await Assert.That(tokens[1].Kind).IsEqualTo(TokenKind.Range);
        await Assert.That(tokens[2].Kind).IsEqualTo(TokenKind.Integer);
    }

    [Test]
    public async Task Tokenize_Ipv4Address_ProducesAddressToken()
    {
        List<Token> tokens = _Tokenize("192.168.1.1");

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.Ipv4Address);
    }

    [Test]
    public async Task Tokenize_InvalidIpv4Address_ReportsError()
    {
        FilterError error = _TokenizeError("999.999.999.999");

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.InvalidValue);
    }

    [Test]
    [Arguments("2001:db8::1")]
    [Arguments("::1")]
    public async Task Tokenize_Ipv6Address_ProducesAddressToken(string source)
    {
        List<Token> tokens = _Tokenize(source);

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.Ipv6Address);
    }

    [Test]
    public async Task Tokenize_InvalidIpv6Address_ReportsError()
    {
        FilterError error = _TokenizeError(":::::");

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.InvalidValue);
    }

    [Test]
    public async Task Tokenize_MacAddress_ProducesMacToken()
    {
        List<Token> tokens = _Tokenize("00:11:22:33:44:55");

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.MacAddress);
    }

    [Test]
    public async Task Tokenize_ThreeHexPairs_ProducesHexBytes()
    {
        List<Token> tokens = _Tokenize("00:11:22");

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.HexBytes);
    }

    [Test]
    public async Task Tokenize_MacStartingWithLetters_ProducesMacToken()
    {
        List<Token> tokens = _Tokenize("aa:bb:cc:dd:ee:ff");

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.MacAddress);
    }

    [Test]
    [Arguments("100ms")]
    [Arguments("5s")]
    [Arguments("10packets")]
    [Arguments("2h")]
    [Arguments("7ns")]
    [Arguments("7us")]
    [Arguments("3m")]
    [Arguments("1packet")]
    public async Task Tokenize_Duration_ProducesDurationToken(string source)
    {
        List<Token> tokens = _Tokenize(source);

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.Duration);
    }

    [Test]
    public async Task Tokenize_UnknownSuffix_FallsBackToInteger()
    {
        List<Token> tokens = _Tokenize("5abc");

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.Integer);
        await Assert.That(tokens[1].Kind).IsEqualTo(TokenKind.Identifier);
    }

    [Test]
    public async Task Tokenize_String_UnescapesContent()
    {
        List<Token> tokens = _Tokenize("\"a\\nb\\t\\\\c\\\"d\\0e\\r\"");

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.StringLiteral);
        await Assert.That(tokens[0].Text).IsEqualTo("a\nb\t\\c\"d\0e\r");
    }

    [Test]
    public async Task Tokenize_UnterminatedString_ReportsError()
    {
        FilterError error = _TokenizeError("\"abc");

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.LexerError);
    }

    [Test]
    public async Task Tokenize_UnterminatedEscape_ReportsError()
    {
        FilterError error = _TokenizeError("\"abc\\");

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.LexerError);
    }

    #endregion

    #region Errors

    [Test]
    [Arguments("=")]
    [Arguments("#")]
    [Arguments(".x")]
    [Arguments("-")]
    public async Task Tokenize_InvalidCharacter_ReportsLexerError(string source)
    {
        FilterError error = _TokenizeError(source);

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.LexerError);
    }

    #endregion

    #region Ambiguous prefixes

    [Test]
    [Arguments("1:2:3", TokenKind.Integer)]
    [Arguments("00:11g", TokenKind.Integer)]
    [Arguments("ab:xyz", TokenKind.Identifier)]
    [Arguments("db8::1", TokenKind.Ipv6Address)]
    [Arguments("aa:bb", TokenKind.HexBytes)]
    public async Task Tokenize_AmbiguousPrefix_PicksTheLongestValidLiteral(string source, TokenKind expected)
    {
        List<Token> tokens = _Tokenize(source);

        await Assert.That(tokens[0].Kind).IsEqualTo(expected);
    }

    [Test]
    public async Task Tokenize_IdentifierFollowedByRange_StopsAtTheDots()
    {
        List<Token> tokens = _Tokenize("udp..5");

        await Assert.That(tokens[0].Kind).IsEqualTo(TokenKind.Identifier);
        await Assert.That(tokens[0].Text).IsEqualTo("udp");
        await Assert.That(tokens[1].Kind).IsEqualTo(TokenKind.Range);
        await Assert.That(tokens[2].Kind).IsEqualTo(TokenKind.Integer);
    }

    [Test]
    public async Task Tokenize_HexSegmentWithInvalidIpv6_ReportsError()
    {
        FilterError error = _TokenizeError("ab:::1");

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.InvalidValue);
    }

    #endregion

    #region Token and span values

    [Test]
    public async Task Token_Equality_ComparesKindSpanAndText()
    {
        Token left = new(TokenKind.Integer, new FilterSpan(0, 2), "80");
        Token right = new(TokenKind.Integer, new FilterSpan(0, 2), "80");
        Token other = new(TokenKind.Integer, new FilterSpan(1, 2), "80");

        await Assert.That(left == right).IsTrue();
        await Assert.That(left != other).IsTrue();
        await Assert.That(left.Equals((object)right)).IsTrue();
        await Assert.That(left.Equals("not a token")).IsFalse();
        await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
        await Assert.That(left.ToString()).IsEqualTo("Integer('80')@[0..2)");
    }

    [Test]
    public async Task FilterSpan_Equality_ComparesStartAndLength()
    {
        FilterSpan left = new(2, 3);
        FilterSpan right = new(2, 3);
        FilterSpan other = new(2, 4);

        await Assert.That(left == right).IsTrue();
        await Assert.That(left != other).IsTrue();
        await Assert.That(left.Equals((object)right)).IsTrue();
        await Assert.That(left.Equals("not a span")).IsFalse();
        await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
        await Assert.That(left.End).IsEqualTo(5);
        await Assert.That(left.ToString()).IsEqualTo("[2..5)");
    }

    #endregion
}

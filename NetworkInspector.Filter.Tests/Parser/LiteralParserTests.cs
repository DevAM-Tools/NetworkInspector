// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Parser;

/// <summary>
/// Drives literal conversion directly with hand-made tokens so the defensive paths the lexer
/// normally prevents are exercised too.
/// </summary>
internal sealed class LiteralParserTests
{
    #region Helpers

    private static Token _Token(TokenKind kind, string text) =>
        new(kind, new FilterSpan(0, text.Length), text);

    private static FilterError _Error(TokenKind kind, string text)
    {
        FilterResult<FieldValueData> result = LiteralParser.Parse(_Token(kind, text));
        if (result.IsSuccess)
        {
            throw new InvalidOperationException($"Expected '{text}' to fail as {kind}.");
        }
        return result.Error;
    }

    #endregion

    #region Values

    [Test]
    [Arguments("0", 0UL)]
    [Arguments("53", 53UL)]
    [Arguments("1_000", 1000UL)]
    [Arguments("0xFF", 255UL)]
    [Arguments("0Xff", 255UL)]
    [Arguments("0b1010", 10UL)]
    [Arguments("0o17", 15UL)]
    public async Task Parse_Integer_ProducesUnsigned(string text, ulong expected)
    {
        FilterResult<FieldValueData> result = LiteralParser.Parse(_Token(TokenKind.Integer, text));

        await Assert.That(result.IsSuccess).IsTrue();
        _ = result.Value.TryGetAsU64(out ulong value);
        await Assert.That(value).IsEqualTo(expected);
    }

    [Test]
    public async Task Parse_SignedInteger_ProducesI64()
    {
        FilterResult<FieldValueData> result = LiteralParser.Parse(_Token(TokenKind.Integer, "-2"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Type).IsEqualTo(FieldType.I64);
        _ = result.Value.TryGetAsI64(out long value);
        await Assert.That(value).IsEqualTo(-2L);
    }

    [Test]
    public async Task Parse_SignedInteger_OutOfRange_ReportsInvalidValue()
    {
        FilterError error = _Error(TokenKind.Integer, "-9223372036854775809");

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.InvalidValue);
    }

    [Test]
    public async Task Parse_Float_ProducesDouble()
    {
        FilterResult<FieldValueData> result = LiteralParser.Parse(_Token(TokenKind.Float, "1.5"));

        _ = result.Value.TryGetAsF64(out double value);
        await Assert.That(value).IsEqualTo(1.5);
    }

    [Test]
    public async Task Parse_StringAndBooleans()
    {
        FilterResult<FieldValueData> text = LiteralParser.Parse(_Token(TokenKind.StringLiteral, "abc"));
        FilterResult<FieldValueData> yes = LiteralParser.Parse(_Token(TokenKind.True, "true"));
        FilterResult<FieldValueData> no = LiteralParser.Parse(_Token(TokenKind.False, "false"));

        await Assert.That(text.Value.Type).IsEqualTo(FieldType.String);
        _ = yes.Value.TryGetAsBool(out bool yesValue);
        _ = no.Value.TryGetAsBool(out bool noValue);
        await Assert.That(yesValue).IsTrue();
        await Assert.That(noValue).IsFalse();
    }

    [Test]
    public async Task Parse_Addresses()
    {
        FilterResult<FieldValueData> mac = LiteralParser.Parse(_Token(TokenKind.MacAddress, "00:11:22:33:44:55"));
        FilterResult<FieldValueData> ipv4 = LiteralParser.Parse(_Token(TokenKind.Ipv4Address, "10.0.0.1"));
        FilterResult<FieldValueData> ipv6 = LiteralParser.Parse(_Token(TokenKind.Ipv6Address, "2001:db8::1"));

        await Assert.That(mac.Value.Type).IsEqualTo(FieldType.MacAddress);
        await Assert.That(ipv4.Value.Type).IsEqualTo(FieldType.IPv4Address);
        await Assert.That(ipv6.Value.Type).IsEqualTo(FieldType.IPv6Address);
    }

    [Test]
    [Arguments("de:ad", 2)]
    [Arguments("DE:AD:BE", 3)]
    [Arguments("0a", 1)]
    public async Task Parse_HexBytes_ProducesBytes(string text, int expectedLength)
    {
        FilterResult<FieldValueData> result = LiteralParser.Parse(_Token(TokenKind.HexBytes, text));

        _ = result.Value.TryGetAsBytes(out ReadOnlyMemory<byte> bytes);
        await Assert.That(bytes.Length).IsEqualTo(expectedLength);
    }

    [Test]
    [Arguments(TokenKind.Integer)]
    [Arguments(TokenKind.Float)]
    [Arguments(TokenKind.HexBytes)]
    [Arguments(TokenKind.MacAddress)]
    [Arguments(TokenKind.True)]
    public async Task IsLiteralStart_AcceptsLiteralKinds(TokenKind kind)
    {
        await Assert.That(LiteralParser.IsLiteralStart(kind)).IsTrue();
    }

    [Test]
    public async Task IsLiteralStart_RejectsOperators()
    {
        await Assert.That(LiteralParser.IsLiteralStart(TokenKind.And)).IsFalse();
    }

    #endregion

    #region Failures

    [Test]
    [Arguments(TokenKind.Integer, "99999999999999999999999")]
    [Arguments(TokenKind.Integer, "0b1210")]
    [Arguments(TokenKind.Integer, "0xFFFFFFFFFFFFFFFFF")]
    [Arguments(TokenKind.Float, "abc")]
    [Arguments(TokenKind.MacAddress, "zz")]
    [Arguments(TokenKind.Ipv4Address, "999.1.1.1")]
    [Arguments(TokenKind.Ipv6Address, "zzzz::")]
    [Arguments(TokenKind.HexBytes, "de:a")]
    [Arguments(TokenKind.HexBytes, "zz:11")]
    public async Task Parse_InvalidLiteral_ReportsInvalidValue(TokenKind kind, string text)
    {
        FilterError error = _Error(kind, text);

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.InvalidValue);
        await Assert.That(error.HasPosition).IsTrue();
    }

    [Test]
    public async Task Parse_NonLiteralToken_ReportsSyntaxError()
    {
        FilterError error = _Error(TokenKind.And, "&&");

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.SyntaxError);
    }

    [Test]
    public async Task Parse_EofToken_ReportsSyntaxErrorWithNonZeroLength()
    {
        FilterError error = _Error(TokenKind.Eof, string.Empty);

        await Assert.That(error.Length).IsEqualTo(1);
    }

    #endregion

    #region Windows

    [Test]
    [Arguments("5ns", 5L)]
    [Arguments("5us", 5_000L)]
    [Arguments("5ms", 5_000_000L)]
    [Arguments("5s", 5_000_000_000L)]
    [Arguments("2m", 120_000_000_000L)]
    [Arguments("1h", 3_600_000_000_000L)]
    [Arguments("1.5s", 1_500_000_000L)]
    public async Task ParseWindow_Durations(string text, long expectedNanos)
    {
        FilterResult<FlankWindow> result = LiteralParser.ParseWindow(_Token(TokenKind.Duration, text));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.IsPacketCount).IsFalse();
        await Assert.That(result.Value.Nanoseconds).IsEqualTo(expectedNanos);
    }

    [Test]
    [Arguments("1packet", 1)]
    [Arguments("10packets", 10)]
    public async Task ParseWindow_PacketCounts(string text, int expected)
    {
        FilterResult<FlankWindow> result = LiteralParser.ParseWindow(_Token(TokenKind.Duration, text));

        await Assert.That(result.Value.IsPacketCount).IsTrue();
        await Assert.That(result.Value.PacketCount).IsEqualTo(expected);
    }

    [Test]
    public async Task ParseWindow_UnknownUnit_ReportsInvalidValue()
    {
        FilterResult<FlankWindow> result = LiteralParser.ParseWindow(_Token(TokenKind.Duration, "5weeks"));

        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.InvalidValue);
    }

    [Test]
    public async Task ParseWindow_MissingAmount_ReportsInvalidValue()
    {
        FilterResult<FlankWindow> result = LiteralParser.ParseWindow(_Token(TokenKind.Duration, "abcs"));

        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.InvalidValue);
    }

    [Test]
    public async Task ParseWindow_NonDurationToken_ReportsSyntaxError()
    {
        FilterResult<FlankWindow> result = LiteralParser.ParseWindow(_Token(TokenKind.Integer, "5"));

        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.SyntaxError);
    }

    #endregion
}

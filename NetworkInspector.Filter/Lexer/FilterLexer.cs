// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Lexer;

/// <summary>
/// Tokenizes a v1 filter expression.
/// <para>
/// <b>Disambiguation order.</b> Several literal forms share a prefix, so the scanner probes
/// from most specific to least specific:
/// </para>
/// <list type="number">
///   <item><description>Radix-prefixed integers (<c>0x</c>, <c>0b</c>, <c>0o</c>).</description></item>
///   <item><description>Colon-separated hex pairs — six groups produce
///     <see cref="TokenKind.MacAddress"/>, any other count produces
///     <see cref="TokenKind.HexBytes"/>.</description></item>
///   <item><description>IPv6 (hex groups and colons, including the <c>::</c> shorthand).</description></item>
///   <item><description>Dotted decimal groups — four groups produce
///     <see cref="TokenKind.Ipv4Address"/>.</description></item>
///   <item><description>Decimal integer/float, optionally followed by a duration or
///     packet-count suffix. A leading minus immediately followed by a digit is a signed
///     integer (<c>-2</c>).</description></item>
///   <item><description>Dotted identifiers and the small keyword set.</description></item>
/// </list>
/// <para>
/// Between <c>[</c> and <c>]</c> steps 2 and 3 are skipped: brackets only hold integers (slice bounds and
/// scope occurrences), so <c>[10:12]</c> lexes as <c>10</c> <c>:</c> <c>12</c> rather than as a byte literal.
/// </para>
/// <para>
/// Removed v1 constructs (<c>seq</c>, <c>stream</c>, <c>window</c>, <c>nav</c>, <c>let</c>,
/// <c>where</c>, <c>step</c>) are intentionally <b>not</b> keywords here: they lex as plain
/// identifiers so that field paths such as <c>tcp.window_size</c> keep working. The parser
/// raises <see cref="FilterErrorKind.UnsupportedFeature"/> when one of them appears in its
/// removed syntactic position.
/// </para>
/// <para>
/// Cost model: a lexer runs once per expression compilation or completion request and never on the per-packet
/// evaluation path, so it allocates freely (one <see cref="string"/> per token, a <see cref="StringBuilder"/> per
/// string literal) and favours simple probes over a table-driven scanner.
/// </para>
/// <para>
/// Thread safety: not thread-safe. An instance holds the scan position and token list and is single-use; create one
/// per expression. The lexer enforces no limit on expression length or token count; the caller owns that trust
/// boundary.
/// </para>
/// </summary>
internal sealed class FilterLexer
{
    #region Fields

    /// <summary>
    /// Keyword lookup for undotted identifiers. Case-insensitive so <c>AND</c> and <c>and</c> are the same operator.
    /// Field paths never reach this table (a dotted path such as <c>flank.x</c> is always an identifier), and the
    /// removed v1 words are deliberately absent (see the class remarks). Written once at type initialization and only
    /// read afterwards, so concurrent lexers share it safely.
    /// </summary>
    private static readonly Dictionary<string, TokenKind> _Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["and"] = TokenKind.And,
        ["or"] = TokenKind.Or,
        ["not"] = TokenKind.Not,
        ["in"] = TokenKind.In,
        ["contains"] = TokenKind.Contains,
        ["matches"] = TokenKind.Matches,
        ["true"] = TokenKind.True,
        ["false"] = TokenKind.False,
        ["flank"] = TokenKind.Flank,
    };

    /// <summary>The source expression.</summary>
    private readonly string _Source;

    /// <summary>Accumulated tokens.</summary>
    private readonly List<Token> _Tokens;

    /// <summary>Current read offset into <see cref="_Source"/>.</summary>
    private int _Position;

    /// <summary>
    /// Whether the scanner is between a <c>[</c> and its <c>]</c>. Only integers are legal there (slice bounds
    /// <c>[10:12]</c>, scope occurrences <c>$udp[1]</c>), so the hex-pair and IPv6 probes must not claim a
    /// <c>10:12</c> run as a byte literal.
    /// </summary>
    private bool _InBrackets;

    #endregion

    #region Construction

    /// <summary>Creates a lexer over <paramref name="source"/>.</summary>
    /// <param name="source">
    /// The filter expression. Not validated for <see langword="null"/> (internal type; the only callers pass the
    /// expression they were already given and validated).
    /// </param>
    public FilterLexer(string source)
    {
        _Source = source;
        _Tokens = new List<Token>(16);
        _Position = 0;
    }

    #endregion

    #region Entry point

    /// <summary>
    /// Tokenizes the whole source. On success the list always ends with <see cref="TokenKind.Eof"/> (the parser
    /// relies on that sentinel instead of bounds checks). Lexical errors are returned, not thrown; the first error
    /// wins and no further tokens are produced. Call at most once per instance.
    /// </summary>
    /// <returns>The token list (owned by the caller afterwards) or the first lexical error.</returns>
    public FilterResult<List<Token>> Tokenize()
    {
        // Scan until the input is exhausted. _Append (not _Tokens.Add) must be used: it tracks the bracket state
        // that decides how the following digits are classified.
        while (true)
        {
            _SkipWhitespace();
            if (_Position >= _Source.Length)
            {
                break;
            }

            FilterResult<Token> next = _NextToken();
            if (!next.TryGetValue(out Token token))
            {
                return FilterResult.Fail<List<Token>>(next.Error);
            }

            _Append(token);
        }

        _Tokens.Add(Token.Eof(_Position));
        return FilterResult.Ok<List<Token>>(_Tokens);
    }

    /// <summary>
    /// Tokenizes as far as the source is lexically valid and stops silently at the first error. Used by
    /// completion, which must read the context of a half-typed expression such as <c>udp.port =</c>. Unlike
    /// <see cref="Tokenize"/> it appends no <see cref="TokenKind.Eof"/>, so the caller can tell where valid input ended.
    /// Call at most once per instance.
    /// </summary>
    /// <param name="unterminatedString">
    /// Receives <see langword="true"/> when lexing stopped inside an open string literal, so the caret is inside
    /// a string and no name completion applies.
    /// </param>
    /// <returns>The tokens before the first error, without a trailing <see cref="TokenKind.Eof"/>.</returns>
    public List<Token> TokenizeLenient(out bool unterminatedString)
    {
        unterminatedString = false;
        while (true)
        {
            _SkipWhitespace();
            if (_Position >= _Source.Length)
            {
                return _Tokens;
            }

            int start = _Position;
            FilterResult<Token> next = _NextToken();
            if (!next.TryGetValue(out Token token))
            {
                // A failure that began at a quote can only be the unterminated-string or unterminated-escape error
                // (_LexString is the only scanner that starts on '"'), so the caret is inside a string literal.
                unterminatedString = _Source[start] == '"';
                return _Tokens;
            }

            _Append(token);
        }
    }

    #endregion

    #region Token dispatch

    /// <summary>
    /// Scans one token starting at <see cref="_Position"/>, which must be inside the source and not on whitespace.
    /// Single-character and operator tokens are decided here; anything that can share a prefix with another literal
    /// form is delegated to the scanner that applies the disambiguation order from the class remarks.
    /// </summary>
    private FilterResult<Token> _NextToken()
    {
        char c = _Source[_Position];

        switch (c)
        {
            case '(':
                return _Make(TokenKind.LeftParen, 1);
            case ')':
                return _Make(TokenKind.RightParen, 1);
            case '[':
                return _Make(TokenKind.LeftBracket, 1);
            case ']':
                return _Make(TokenKind.RightBracket, 1);
            case '{':
                return _Make(TokenKind.LeftBrace, 1);
            case '}':
                return _Make(TokenKind.RightBrace, 1);
            case ',':
                return _Make(TokenKind.Comma, 1);
            case '$':
                return _Make(TokenKind.Dollar, 1);

            case ':':
                // "::" starts a compressed IPv6 literal such as ::1; a single ':' is punctuation.
                if (_Peek(1) == ':')
                {
                    return _LexIpv6From(_Position);
                }
                return _Make(TokenKind.Colon, 1);

            case '=':
                // A lone '=' is rejected instead of being accepted as '==': it is a common typo for assignment and
                // silently treating it as a comparison would hide that mistake.
                if (_Peek(1) == '=')
                {
                    return _Make(TokenKind.Equal, 2);
                }
                return FilterError.Lexer("Expected '==' — assignment is not part of the filter language", _Position, 1);

            case '!':
                if (_Peek(1) == '=')
                {
                    return _Make(TokenKind.NotEqual, 2);
                }
                return _Make(TokenKind.Not, 1);

            case '<':
                if (_Peek(1) == '=')
                {
                    return _Make(TokenKind.LessEqual, 2);
                }
                return _Make(TokenKind.LessThan, 1);

            case '>':
                if (_Peek(1) == '=')
                {
                    return _Make(TokenKind.GreaterEqual, 2);
                }
                return _Make(TokenKind.GreaterThan, 1);

            case '&':
                // The single and doubled forms are synonyms (like the words "and" / "or"); the parser sees one kind.
                return _Make(TokenKind.And, _Peek(1) == '&' ? 2 : 1);

            case '|':
                return _Make(TokenKind.Or, _Peek(1) == '|' ? 2 : 1);

            case '.':
                if (_Peek(1) == '.')
                {
                    return _Make(TokenKind.Range, 2);
                }
                return FilterError.Lexer("Unexpected '.' — field paths must start with an identifier", _Position, 1);

            case '"':
                return _LexString();

            case '-':
                // '-' exists only as the sign of a decimal integer; there is no subtraction operator.
                if (char.IsAsciiDigit(_Peek(1)))
                {
                    return _LexSignedInteger();
                }

                return FilterError.Lexer("Unexpected character '-'", _Position, 1);

            default:
                // Digits start numbers and all digit-led addresses; letters and '_' start identifiers, which may
                // still turn out to be hex-led MAC or IPv6 literals such as "ab:cd:ef:01:02:03" or "fe80::1".
                if (char.IsAsciiDigit(c))
                {
                    return _LexNumberOrAddress();
                }
                if (_IsIdentStart(c))
                {
                    return _LexIdentifierOrAddress();
                }
                return FilterError.Lexer($"Unexpected character '{c}'", _Position, 1);
        }
    }

    #endregion

    #region Strings

    /// <summary>
    /// Lexes a double-quoted string literal. The token text is the unescaped content (see <see cref="Token"/>), while
    /// the span covers the quotes and the raw escapes. Supported escapes are <c>\n</c>, <c>\r</c>, <c>\t</c> and
    /// <c>\0</c>; any other escaped character stands for itself, which is how <c>\"</c> and <c>\\</c> work. There is
    /// no single-quote form, no raw-string form and no <c>\x</c> / <c>\u</c> escape. Unterminated input is an error
    /// whose span runs to the end of the source so the caller can highlight the open string.
    /// </summary>
    private FilterResult<Token> _LexString()
    {
        int start = _Position;
        _Position++;

        // Copy characters one by one so escapes can be resolved; a slice of the source cannot be used because the
        // text differs from the source as soon as one escape appears.
        StringBuilder content = new();
        while (_Position < _Source.Length)
        {
            char c = _Source[_Position];
            if (c == '\\')
            {
                // A backslash as the very last character leaves nothing to escape: report it instead of dropping it.
                _Position++;
                if (_Position >= _Source.Length)
                {
                    return FilterError.Lexer("Unterminated escape sequence", start, _Position - start);
                }
                char escaped = _Source[_Position];
                content.Append(escaped switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '0' => '\0',
                    _ => escaped,
                });
                _Position++;
                continue;
            }

            if (c == '"')
            {
                _Position++;
                return new Token(TokenKind.StringLiteral, new FilterSpan(start, _Position - start), content.ToString());
            }

            content.Append(c);
            _Position++;
        }

        return FilterError.Lexer("Unterminated string literal", start, _Position - start);
    }

    #endregion

    #region Numbers and addresses

    /// <summary>
    /// Scans a token that starts with a decimal digit and decides between an integer, float, duration, IPv4, MAC,
    /// hex-byte or IPv6 literal. The probes run in the order documented on the class, most specific first, because
    /// <c>10</c>, <c>10:11</c>, <c>10:11:12:13:14:15</c> and <c>10::1</c> all begin with the same characters.
    /// </summary>
    private FilterResult<Token> _LexNumberOrAddress()
    {
        int start = _Position;

        // Radix prefixes are checked first: "0b1" and "0xAB" would otherwise be read as a decimal "0" followed by an
        // identifier. Digits are validated by the literal parser, which also rejects a prefix without digits.
        if (_Source[_Position] == '0' && _Position + 1 < _Source.Length)
        {
            char prefix = _Source[_Position + 1];
            if (prefix is 'x' or 'X')
            {
                return _LexRadix(start, 2, static c => char.IsAsciiHexDigit(c) || c == '_');
            }
            if (prefix is 'b' or 'B')
            {
                return _LexRadix(start, 2, static c => c is '0' or '1' or '_');
            }
            if (prefix is 'o' or 'O')
            {
                return _LexRadix(start, 2, static c => c is >= '0' and <= '7' or '_');
            }
        }

        // Inside "[…]" a digit run can only be a slice bound or a scope occurrence, never a MAC, byte or IPv6
        // literal: "udp.payload[10:12]" must stay Integer ':' Integer. Literals after '==' / 'in' are unaffected.
        if (!_InBrackets)
        {
            if (_TryLexHexGroups(start, out Token hexToken))
            {
                return hexToken;
            }

            if (_TryLexIpv6(start, out Token ipv6Token))
            {
                return ipv6Token;
            }
        }

        return _LexDecimalOrIpv4(start);
    }

    /// <summary>
    /// Lexes a decimal integer with a leading minus (<c>-2</c>). Hex prefixes and duration
    /// suffixes are not part of the signed form; <c>-0x10</c> and <c>-2s</c> are not tokens.
    /// Digit separators (<c>-1_000</c>) are allowed the same way as in unsigned decimals.
    /// </summary>
    private FilterResult<Token> _LexSignedInteger()
    {
        int start = _Position;
        _Position++;
        _ConsumeDigitsWithSeparators();
        return new Token(TokenKind.Integer, new FilterSpan(start, _Position - start), _Slice(start));
    }

    /// <summary>
    /// Speculatively lexes an IPv6 literal that begins with decimal digits, such as
    /// <c>2001:db8::1</c>. The probe only commits when the run holds at least two colons and
    /// parses as an address, so single-colon forms such as the slice bound in <c>[0:2]</c> stay
    /// numeric.
    /// </summary>
    private bool _TryLexIpv6(int start, out Token token)
    {
        token = default;

        // Scan the maximal run of hex digits and colons while counting colons. Only _Position is committed on
        // success; on failure the lexer state is untouched so the caller can fall back to the decimal scanner.
        int probe = start;
        int colons = 0;
        while (probe < _Source.Length && (_Source[probe] == ':' || char.IsAsciiHexDigit(_Source[probe])))
        {
            if (_Source[probe] == ':')
            {
                colons++;
            }
            probe++;
        }

        // Two colons is the smallest shape of an IPv6 literal ("1::"); a single colon as in "1:2" is a slice bound or
        // hex pair. Full validation is delegated to the address parser so the lexer never duplicates the grammar.
        if (colons < 2)
        {
            return false;
        }

        string text = _Source[start..probe];
        if (!IPv6Address.TryParse(text, out _))
        {
            return false;
        }

        _Position = probe;
        token = new Token(TokenKind.Ipv6Address, new FilterSpan(start, text.Length), text);
        return true;
    }

    /// <summary>
    /// Consumes the digits of a radix-prefixed integer. <paramref name="isDigit"/> also accepts <c>_</c> as a digit
    /// separator; the callers pass non-capturing <c>static</c> lambdas, so no delegate or closure is allocated per call.
    /// The scan is permissive (it may produce <c>0x</c> or <c>0b_</c> with no real digit); the literal parser rejects
    /// those so the error is reported once, with the value's context.
    /// </summary>
    /// <param name="start">Offset of the leading <c>0</c>.</param>
    /// <param name="prefixLength">Length of the prefix (<c>0x</c>, <c>0b</c> or <c>0o</c>), which is skipped.</param>
    /// <param name="isDigit">Predicate for characters that belong to the literal.</param>
    private FilterResult<Token> _LexRadix(int start, int prefixLength, Func<char, bool> isDigit)
    {
        _Position += prefixLength;
        while (_Position < _Source.Length && isDigit(_Source[_Position]))
        {
            _Position++;
        }
        return new Token(TokenKind.Integer, new FilterSpan(start, _Position - start), _Slice(start));
    }

    /// <summary>
    /// Probes for a colon-separated hex-pair sequence (<c>HH:HH:…</c>). Six groups yield a MAC
    /// address; two or more groups with a different count yield a raw byte sequence used by
    /// slice comparisons such as <c>eth.src[0:3] == 00:11:22</c>.
    /// </summary>
    private bool _TryLexHexGroups(int start, out Token token)
    {
        token = default;

        // Walk "HH:HH:HH…" group by group. Nothing is committed to _Position until the whole run is accepted, so a
        // rejected probe costs only the scan and the caller can try IPv6 or decimal next.
        int probe = start;
        int groups = 0;
        while (true)
        {
            if (probe + 1 >= _Source.Length
                || !char.IsAsciiHexDigit(_Source[probe])
                || !char.IsAsciiHexDigit(_Source[probe + 1]))
            {
                break;
            }

            // Reject three-or-more digit groups: those belong to IPv6, not a byte sequence.
            if (probe + 2 < _Source.Length && char.IsAsciiHexDigit(_Source[probe + 2]))
            {
                break;
            }

            probe += 2;
            groups++;

            // Continue only when a colon is followed by another hex digit; a trailing ':' ends the run and is
            // rejected below, so "00:11:" is not silently shortened.
            if (probe < _Source.Length && _Source[probe] == ':' && _Peek2(probe + 1) is char n && char.IsAsciiHexDigit(n))
            {
                probe++;
                continue;
            }
            break;
        }

        // A single pair ("12") is a plain number, not a byte sequence.
        if (groups < 2)
        {
            return false;
        }

        // A trailing identifier character means this was not a standalone literal.
        if (probe < _Source.Length && (_IsIdentChar(_Source[probe]) || _Source[probe] == ':'))
        {
            return false;
        }

        _Position = probe;
        TokenKind kind = groups == 6 ? TokenKind.MacAddress : TokenKind.HexBytes;
        token = new Token(kind, new FilterSpan(start, _Position - start), _Slice(start));
        return true;
    }

    /// <summary>
    /// Commits to an IPv6 literal that starts at <paramref name="start"/> (a <c>::</c> prefix, or a hex-led
    /// identifier segment already confirmed to be followed by a colon). Unlike <see cref="_TryLexIpv6"/> this does not
    /// fall back: an unparsable run is a lexical error, because no other token form can start this way. The scan
    /// stops at the first character that is neither a hex digit nor a colon, so dotted IPv4-suffix notation such as
    /// <c>::ffff:1.2.3.4</c> is not one token.
    /// </summary>
    private FilterResult<Token> _LexIpv6From(int start)
    {
        int probe = start;
        while (probe < _Source.Length && (_Source[probe] == ':' || char.IsAsciiHexDigit(_Source[probe])))
        {
            probe++;
        }

        _Position = probe;
        string text = _Slice(start);
        if (!IPv6Address.TryParse(text, out _))
        {
            return FilterError.InvalidValue($"Invalid IPv6 address '{text}'", start, text.Length);
        }
        return new Token(TokenKind.Ipv6Address, new FilterSpan(start, text.Length), text);
    }

    /// <summary>
    /// Lexes a digit run that is not hex-pair or IPv6 shaped: a plain integer, a float (<c>3.14</c>), a duration, or a
    /// dotted-quad IPv4 address. The dot decides: <c>..</c> ends the integer (range operator), a dot followed by a
    /// digit starts either more octets or a fraction, any other dot belongs to the next token.
    /// </summary>
    private FilterResult<Token> _LexDecimalOrIpv4(int start)
    {
        _ConsumeDigitsWithSeparators();

        if (_Position < _Source.Length && _Source[_Position] == '.')
        {
            // "1..10" is a range, not a float.
            if (_Peek(1) == '.')
            {
                return new Token(TokenKind.Integer, new FilterSpan(start, _Position - start), _Slice(start));
            }

            if (char.IsAsciiDigit(_Peek(1)))
            {
                // Count dot-separated digit groups (at most four) to tell an address from a fraction. IPv4 octets
                // never use digit separators — keep a plain digit probe.
                int probe = _Position;
                int groups = 1;
                while (probe < _Source.Length && _Source[probe] == '.' && groups < 4)
                {
                    int afterDot = probe + 1;
                    if (afterDot >= _Source.Length || !char.IsAsciiDigit(_Source[afterDot]))
                    {
                        break;
                    }
                    probe = afterDot;
                    while (probe < _Source.Length && char.IsAsciiDigit(_Source[probe]))
                    {
                        probe++;
                    }
                    groups++;
                }

                // Exactly four groups: commit and let the address parser enforce the 0..255 octet range, so an
                // invalid address like 300.1.1.1 is reported as such instead of being read as a number.
                if (groups == 4)
                {
                    _Position = probe;
                    string ipText = _Slice(start);
                    if (!IPv4Address.TryParse(ipText, out _))
                    {
                        return FilterError.InvalidValue($"Invalid IPv4 address '{ipText}'", start, ipText.Length);
                    }
                    return new Token(TokenKind.Ipv4Address, new FilterSpan(start, ipText.Length), ipText);
                }

                // Two dot-separated groups form a decimal fraction (separators allowed in each run).
                _Position++;
                _ConsumeDigitsWithSeparators();
                return _FinishNumber(start);
            }
        }

        return _FinishNumber(start);
    }

    /// <summary>
    /// Classifies a finished digit run as <see cref="TokenKind.Duration"/> when an identifier suffix directly follows
    /// and is a known unit, otherwise as Float (contains a dot) or Integer. An unknown suffix is not consumed, so
    /// <c>5abc</c> lexes as <c>5</c> then an identifier and the parser reports the problem at the right token.
    /// </summary>
    private FilterResult<Token> _FinishNumber(int start)
    {
        if (_Position < _Source.Length && _IsIdentStart(_Source[_Position]))
        {
            int suffixStart = _Position;
            while (_Position < _Source.Length && _IsIdentChar(_Source[_Position]))
            {
                _Position++;
            }

            string suffix = _Source[suffixStart.._Position];
            if (_IsDurationSuffix(suffix))
            {
                return new Token(TokenKind.Duration, new FilterSpan(start, _Position - start), _Slice(start));
            }

            _Position = suffixStart;
        }

        string text = _Slice(start);
        TokenKind kind = text.Contains('.', StringComparison.Ordinal) ? TokenKind.Float : TokenKind.Integer;
        return new Token(kind, new FilterSpan(start, text.Length), text);
    }

    /// <summary>
    /// Whether <paramref name="suffix"/> is a window unit: nanoseconds, microseconds, milliseconds, seconds, minutes,
    /// hours, or a packet count. Case-sensitive, so <c>5S</c> is not a duration. The whole suffix is compared, which is
    /// what separates minutes (<c>m</c>) from milliseconds (<c>ms</c>).
    /// </summary>
    private static bool _IsDurationSuffix(string suffix) =>
        suffix is "ns" or "us" or "ms" or "s" or "m" or "h" or "packet" or "packets";

    #endregion

    #region Identifiers

    /// <summary>
    /// Lexes a letter- or underscore-led word: a keyword, a dotted field path, or (when it is all hex digits and
    /// followed by a colon) the start of a MAC, hex-byte or IPv6 literal such as <c>ab:cd:ef:01:02:03</c> or
    /// <c>fe80::1</c>. Keywords are recognised only for undotted words.
    /// </summary>
    private FilterResult<Token> _LexIdentifierOrAddress()
    {
        int start = _Position;
        while (_Position < _Source.Length && _IsIdentChar(_Source[_Position]))
        {
            _Position++;
        }

        string firstSegment = _Source[start.._Position];

        // A hex-looking segment followed by ':' may begin a MAC or IPv6 literal — but not inside "[…]", where
        // "[ab:cd]" stays identifier ':' identifier so the parser reports the bad bound at the right span.
        if (!_InBrackets && _Position < _Source.Length && _Source[_Position] == ':' && _IsAllHex(firstSegment))
        {
            // _TryLexHexGroups works from the absolute start offset and moves _Position only when it succeeds, so
            // _Position still points at the ':' for the lookahead below when the probe is rejected.
            if (_TryLexHexGroups(start, out Token hexToken))
            {
                return hexToken;
            }

            if (char.IsAsciiHexDigit(_Peek(1)) || _Peek(1) == ':')
            {
                return _LexIpv6From(start);
            }
        }

        // Extend over ".segment" parts to form a dotted field path. A dot that is not followed by an identifier start
        // (for example the first dot of "name..") is left for the next token, so ".." still lexes as the range operator.
        while (_Position < _Source.Length && _Source[_Position] == '.')
        {
            int dotPosition = _Position;
            _Position++;
            if (_Position >= _Source.Length || !_IsIdentStart(_Source[_Position]))
            {
                _Position = dotPosition;
                break;
            }
            while (_Position < _Source.Length && _IsIdentChar(_Source[_Position]))
            {
                _Position++;
            }
        }

        // Keyword lookup is skipped for dotted paths so a field such as "tcp.flank" can never be misread as a keyword.
        string text = _Slice(start);
        if (!text.Contains('.', StringComparison.Ordinal) && _Keywords.TryGetValue(text, out TokenKind keyword))
        {
            return new Token(keyword, new FilterSpan(start, text.Length), text);
        }

        return new Token(TokenKind.Identifier, new FilterSpan(start, text.Length), text);
    }

    #endregion

    #region Helpers

    /// <summary>Advances over Unicode whitespace; whitespace separates tokens but is otherwise not significant.</summary>
    private void _SkipWhitespace()
    {
        while (_Position < _Source.Length && char.IsWhiteSpace(_Source[_Position]))
        {
            _Position++;
        }
    }

    /// <summary>Appends a token and tracks the bracket state the number probes depend on.</summary>
    private void _Append(Token token)
    {
        _Tokens.Add(token);
        if (token.Kind == TokenKind.LeftBracket)
        {
            _InBrackets = true;
        }
        else if (token.Kind == TokenKind.RightBracket)
        {
            _InBrackets = false;
        }
    }

    /// <summary>
    /// Consumes a decimal digit run that may include <c>_</c> separators between digits
    /// (<c>1_000</c>). A trailing or doubled underscore stops the run so the next token
    /// can report the bad form.
    /// </summary>
    private void _ConsumeDigitsWithSeparators()
    {
        bool sawDigit = false;
        while (_Position < _Source.Length)
        {
            char c = _Source[_Position];
            if (char.IsAsciiDigit(c))
            {
                sawDigit = true;
                _Position++;
                continue;
            }

            if (c == '_'
                && sawDigit
                && _Position + 1 < _Source.Length
                && char.IsAsciiDigit(_Source[_Position + 1]))
            {
                _Position++;
                continue;
            }

            break;
        }
    }

    /// <summary>Returns the source text from <paramref name="start"/> up to (excluding) the current position.</summary>
    private string _Slice(int start) => _Source[start.._Position];

    /// <summary>
    /// Returns the character <paramref name="offset"/> positions after the current one, or <c>'\0'</c> past the end.
    /// The NUL sentinel avoids a bounds check at every call site; it cannot be confused with input because every
    /// predicate applied to it (digit, hex digit, <c>':'</c>, <c>'='</c>, ...) is false for NUL.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char _Peek(int offset)
    {
        int index = _Position + offset;
        return index < _Source.Length
            ? _Source[index]
            : '\0';
    }

    /// <summary>Like <see cref="_Peek"/> but takes an absolute index into the source instead of an offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char _Peek2(int absoluteIndex) =>
        absoluteIndex < _Source.Length
            ? _Source[absoluteIndex]
            : '\0';

    /// <summary>Creates a fixed-length operator or punctuation token at the current position and advances past it.</summary>
    private FilterResult<Token> _Make(TokenKind kind, int length)
    {
        Token token = new(kind, new FilterSpan(_Position, length), _Source.Substring(_Position, length));
        _Position += length;
        return token;
    }

    /// <summary>Whether <paramref name="c"/> can start an identifier: any Unicode letter or underscore.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    /// <summary>Whether <paramref name="c"/> can continue an identifier: any Unicode letter or digit, or underscore.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Whether an identifier segment could also be read as hex digits. The only caller passes a
    /// segment that starts with an identifier character, so the segment is never empty.
    /// </summary>
    private static bool _IsAllHex(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Parser;

/// <summary>
/// Recursive-descent parser for the v1 filter language.
/// <para>
/// Grammar (precedence low to high):
/// </para>
/// <code>
/// expr      := orExpr
/// orExpr    := andExpr ( ( '||' | 'or' ) andExpr )*
/// andExpr   := unary  ( ( '&amp;&amp;' | 'and' ) unary )*
/// unary     := ( '!' | 'not' ) unary | primary
/// primary   := '(' expr ')' | 'true' | 'false' | scope | flank | term
/// scope     := '$' Name [ '[' Integer ']' ] '{' expr '}'
/// flank     := 'flank' '(' Name ( ',' flankArg )* ')'
/// flankArg  := 'from:' endpoint | 'to:' endpoint | 'by:' delta | 'changed' | 'within:' window | 'when:' expr
/// term      := operand [ cmpOp literal
///                      | 'in' ( '{' literalList '}' | literal '..' literal )
///                      | 'contains' String
///                      | 'matches' String ]
/// operand   := Name [ '[' Integer ':' Integer ']' ] | 'len' '(' Name ')'
/// </code>
/// <para>
/// Every field, protocol and scope-anchor name is reported through
/// <see cref="FilterFieldNameSpanCallback"/> as soon as it is recognised — before any stack
/// binding — so an editor still receives spans for expressions that fail to parse.
/// </para>
/// </summary>
internal sealed class FilterParser
{
    #region Fields

    /// <summary>Constructs removed in v1 that used to be call expressions.</summary>
    private static readonly HashSet<string> _RemovedCalls =
        new(StringComparer.OrdinalIgnoreCase) { "seq", "stream", "window", "nav" };

    /// <summary>Constructs removed in v1 that used to introduce bindings.</summary>
    private static readonly HashSet<string> _RemovedBindings =
        new(StringComparer.OrdinalIgnoreCase) { "let", "where", "step" };

    private readonly List<Token> _Tokens;
    private readonly string _Source;
    private readonly FilterFieldNameSpanCallback? _OnFieldNameSpan;

    private int _Index;
    private FilterFeature _Features;
    private FilterError? _CallbackError;

    #endregion

    #region Construction

    /// <summary>Creates a parser over an already tokenized expression.</summary>
    public FilterParser(List<Token> tokens, string source, FilterFieldNameSpanCallback? onFieldNameSpan)
    {
        _Tokens = tokens;
        _Source = source;
        _OnFieldNameSpan = onFieldNameSpan;
        _Index = 0;
        _Features = FilterFeature.Classic;
    }

    #endregion

    #region Entry point

    /// <summary>Parses the token stream into a program.</summary>
    public FilterResult<FilterProgram> Parse()
    {
        FilterResult<FilterNode> root = _ParseOr();
        if (!root.TryGetValue(out FilterNode? node))
        {
            return _Failure(root.Error);
        }

        if (_CallbackError is not null)
        {
            return FilterResult.Fail<FilterProgram>(_CallbackError);
        }

        Token current = _Current;
        if (current.Kind != TokenKind.Eof)
        {
            return _Failure(FilterError.Syntax(
                $"Unexpected '{current.Text}' after a complete expression",
                current.Position,
                Math.Max(current.Length, 1)));
        }

        return new FilterProgram(_Source, node, _Features);
    }

    /// <summary>
    /// Finishes a failed parse: gives the completion callback one last chance at the trailing
    /// name, then reports whichever error came first.
    /// </summary>
    private FilterResult<FilterProgram> _Failure(FilterError error)
    {
        _ReportTrailingIncompleteName();
        if (_CallbackError is not null)
        {
            return FilterResult.Fail<FilterProgram>(_CallbackError);
        }
        return FilterResult.Fail<FilterProgram>(error);
    }

    #endregion

    #region Boolean composition

    private FilterResult<FilterNode> _ParseOr()
    {
        FilterResult<FilterNode> left = _ParseAnd();
        if (!left.TryGetValue(out FilterNode? node))
        {
            return left;
        }

        while (_Current.Kind == TokenKind.Or)
        {
            _Advance();
            FilterResult<FilterNode> right = _ParseAnd();
            if (!right.TryGetValue(out FilterNode? rightNode))
            {
                return right;
            }
            node = new LogicalNode(LogicalOp.Or, node, rightNode, node.Position, rightNode.Position + rightNode.Length - node.Position);
        }

        return FilterResult.Ok<FilterNode>(node);
    }

    private FilterResult<FilterNode> _ParseAnd()
    {
        FilterResult<FilterNode> left = _ParseUnary();
        if (!left.TryGetValue(out FilterNode? node))
        {
            return left;
        }

        while (_Current.Kind == TokenKind.And)
        {
            _Advance();
            FilterResult<FilterNode> right = _ParseUnary();
            if (!right.TryGetValue(out FilterNode? rightNode))
            {
                return right;
            }
            node = new LogicalNode(LogicalOp.And, node, rightNode, node.Position, rightNode.Position + rightNode.Length - node.Position);
        }

        return FilterResult.Ok<FilterNode>(node);
    }

    private FilterResult<FilterNode> _ParseUnary()
    {
        if (_Current.Kind == TokenKind.Not)
        {
            Token notToken = _Current;
            _Advance();
            FilterResult<FilterNode> operand = _ParseUnary();
            if (!operand.TryGetValue(out FilterNode? node))
            {
                return operand;
            }
            return FilterResult.Ok<FilterNode>(
                new NotNode(node, notToken.Position, node.Position + node.Length - notToken.Position));
        }

        return _ParsePrimary();
    }

    #endregion

    #region Primary

    private FilterResult<FilterNode> _ParsePrimary()
    {
        Token token = _Current;

        switch (token.Kind)
        {
            case TokenKind.LeftParen:
            {
                _Advance();
                FilterResult<FilterNode> inner = _ParseOr();
                if (!inner.TryGetValue(out FilterNode? node))
                {
                    return inner;
                }
                if (_Current.Kind != TokenKind.RightParen)
                {
                    return _Expected(")");
                }
                _Advance();
                return FilterResult.Ok<FilterNode>(node);
            }

            case TokenKind.True:
                _Advance();
                return FilterResult.Ok<FilterNode>(new BoolConstantNode(true, token.Position, token.Length));

            case TokenKind.False:
                _Advance();
                return FilterResult.Ok<FilterNode>(new BoolConstantNode(false, token.Position, token.Length));

            case TokenKind.Flank:
                return _ParseFlank();

            case TokenKind.Dollar:
                return _ParseScope();

            case TokenKind.Identifier:
                return _ParseTerm();

            default:
                return FilterError.Syntax(
                    token.Kind == TokenKind.Eof
                        ? "Unexpected end of expression"
                        : $"Unexpected '{token.Text}'",
                    token.Position,
                    Math.Max(token.Length, 1));
        }
    }

    #endregion

    #region Terms

    private FilterResult<FilterNode> _ParseTerm()
    {
        Token nameToken = _Current;
        string name = nameToken.Text;

        if (_RemovedBindings.Contains(name))
        {
            return FilterError.UnsupportedFeature(name, nameToken.Position, nameToken.Length);
        }

        if (_RemovedCalls.Contains(name) && _Peek(1).Kind == TokenKind.LeftParen)
        {
            return FilterError.UnsupportedFeature(name, nameToken.Position, nameToken.Length);
        }

        FilterResult<OperandNode> operandResult = _ParseOperand();
        if (!operandResult.TryGetValue(out OperandNode? operand))
        {
            return FilterResult.Fail<FilterNode>(operandResult.Error);
        }

        Token op = _Current;
        switch (op.Kind)
        {
            case TokenKind.Equal:
            case TokenKind.NotEqual:
            case TokenKind.LessThan:
            case TokenKind.LessEqual:
            case TokenKind.GreaterThan:
            case TokenKind.GreaterEqual:
            {
                _ReportName(operand, FilterFieldNameKind.FieldPath);
                _Advance();
                FilterResult<FieldValueData> literal = _ParseLiteral();
                if (!literal.TryGetValue(out FieldValueData value))
                {
                    return FilterResult.Fail<FilterNode>(literal.Error);
                }
                Token last = _Previous;
                return FilterResult.Ok<FilterNode>(
                    new CompareNode(operand, _ToCompareOp(op.Kind), value, operand.Position, last.End - operand.Position));
            }

            case TokenKind.In:
                _ReportName(operand, FilterFieldNameKind.FieldPath);
                _Advance();
                return _ParseInTail(operand);

            case TokenKind.Contains:
            case TokenKind.Matches:
            {
                _ReportName(operand, FilterFieldNameKind.FieldPath);
                StringOp stringOp = op.Kind == TokenKind.Contains ? StringOp.Contains : StringOp.Matches;
                _Advance();
                if (_Current.Kind != TokenKind.StringLiteral)
                {
                    return _Expected("a quoted string");
                }
                Token pattern = _Current;
                _Advance();
                if (stringOp == StringOp.Matches && !_IsValidRegex(pattern.Text))
                {
                    return FilterError.InvalidValue(
                        $"Invalid regular expression '{pattern.Text}'", pattern.Position, pattern.Length);
                }
                return FilterResult.Ok<FilterNode>(
                    new StringPredicateNode(
                        operand, stringOp, pattern.Text, operand.Position, pattern.End - operand.Position));
            }

            default:
                if (operand is not FieldOperandNode)
                {
                    return FilterError.Syntax(
                        "A slice or len(...) operand requires a comparison",
                        operand.Position,
                        operand.Length);
                }
                _ReportName(operand, FilterFieldNameKind.ProtocolName);
                return FilterResult.Ok<FilterNode>(
                    new PresenceNode(operand.Name, operand.Position, operand.Length));
        }
    }

    private FilterResult<FilterNode> _ParseInTail(OperandNode operand)
    {
        if (_Current.Kind == TokenKind.LeftBrace)
        {
            _Advance();
            List<FieldValueData> values = [];
            while (true)
            {
                FilterResult<FieldValueData> literal = _ParseLiteral();
                if (!literal.TryGetValue(out FieldValueData value))
                {
                    return FilterResult.Fail<FilterNode>(literal.Error);
                }
                values.Add(value);

                if (_Current.Kind == TokenKind.Comma)
                {
                    _Advance();
                    continue;
                }
                break;
            }

            if (_Current.Kind != TokenKind.RightBrace)
            {
                return _Expected("}");
            }
            Token close = _Current;
            _Advance();
            return FilterResult.Ok<FilterNode>(
                new InSetNode(operand, [.. values], operand.Position, close.End - operand.Position));
        }

        FilterResult<FieldValueData> low = _ParseLiteral();
        if (!low.TryGetValue(out FieldValueData lowValue))
        {
            return FilterResult.Fail<FilterNode>(low.Error);
        }

        if (_Current.Kind != TokenKind.Range)
        {
            return _Expected("'..' to complete the range");
        }
        _Advance();

        FilterResult<FieldValueData> high = _ParseLiteral();
        if (!high.TryGetValue(out FieldValueData highValue))
        {
            return FilterResult.Fail<FilterNode>(high.Error);
        }

        Token last = _Previous;
        return FilterResult.Ok<FilterNode>(
            new InRangeNode(operand, lowValue, highValue, operand.Position, last.End - operand.Position));
    }

    /// <summary>
    /// Parses a field reference, optionally with a <c>[a:b]</c> slice or wrapped in <c>len(...)</c>.
    /// The only caller reaches this after seeing an identifier, so the name token needs no check.
    /// </summary>
    private FilterResult<OperandNode> _ParseOperand()
    {
        Token nameToken = _Current;
        if (string.Equals(nameToken.Text, "len", StringComparison.OrdinalIgnoreCase)
            && _Peek(1).Kind == TokenKind.LeftParen)
        {
            _Advance();
            _Advance();
            if (_Current.Kind != TokenKind.Identifier)
            {
                return FilterResult.Fail<OperandNode>(_ExpectedError("a field name inside len(...)"));
            }
            Token inner = _Current;
            _Advance();
            if (_Current.Kind != TokenKind.RightParen)
            {
                return FilterResult.Fail<OperandNode>(_ExpectedError(")"));
            }
            Token close = _Current;
            _Advance();
            return FilterResult.Ok<OperandNode>(
                new LengthOperandNode(inner.Text, nameToken.Position, close.End - nameToken.Position));
        }

        _Advance();

        if (_Current.Kind != TokenKind.LeftBracket)
        {
            return FilterResult.Ok<OperandNode>(
                new FieldOperandNode(nameToken.Text, nameToken.Position, nameToken.Length));
        }

        _Advance();
        FilterResult<int> start = _ParseNonNegativeInteger();
        if (!start.TryGetValue(out int startValue))
        {
            return FilterResult.Fail<OperandNode>(start.Error);
        }

        if (_Current.Kind != TokenKind.Colon)
        {
            return FilterResult.Fail<OperandNode>(_ExpectedError("':' in a byte slice such as [0:3]"));
        }
        _Advance();

        FilterResult<int> end = _ParseNonNegativeInteger();
        if (!end.TryGetValue(out int endValue))
        {
            return FilterResult.Fail<OperandNode>(end.Error);
        }

        if (_Current.Kind != TokenKind.RightBracket)
        {
            return FilterResult.Fail<OperandNode>(_ExpectedError("]"));
        }
        Token bracketClose = _Current;
        _Advance();

        if (endValue <= startValue)
        {
            return FilterResult.Fail<OperandNode>(FilterError.InvalidValue(
                "Slice end must be greater than slice start",
                nameToken.Position,
                bracketClose.End - nameToken.Position));
        }

        return FilterResult.Ok<OperandNode>(new SliceOperandNode(
            nameToken.Text, startValue, endValue, nameToken.Position, bracketClose.End - nameToken.Position));
    }

    #endregion

    #region Scope

    private FilterResult<FilterNode> _ParseScope()
    {
        Token dollar = _Current;
        _Advance();

        if (_Current.Kind != TokenKind.Identifier)
        {
            return _Expected("a scope anchor name after '$'");
        }

        Token nameToken = _Current;
        _Advance();
        _ReportSpan(nameToken.Position, nameToken.Length, FilterFieldNameKind.ScopeAnchor);

        int? occurrence = null;
        if (_Current.Kind == TokenKind.LeftBracket)
        {
            _Advance();
            FilterResult<int> index = _ParseNonNegativeInteger();
            if (!index.TryGetValue(out int indexValue))
            {
                return FilterResult.Fail<FilterNode>(index.Error);
            }
            if (_Current.Kind != TokenKind.RightBracket)
            {
                return _Expected("]");
            }
            _Advance();
            occurrence = indexValue;
        }

        if (_Current.Kind != TokenKind.LeftBrace)
        {
            return _Expected("'{' to open the scope body");
        }
        _Advance();

        FilterResult<FilterNode> body = _ParseOr();
        if (!body.TryGetValue(out FilterNode? bodyNode))
        {
            return body;
        }

        if (_Current.Kind != TokenKind.RightBrace)
        {
            return _Expected("'}' to close the scope body");
        }
        Token close = _Current;
        _Advance();

        _Features |= FilterFeature.Scope;
        return FilterResult.Ok<FilterNode>(
            new ScopeNode(nameToken.Text, occurrence, bodyNode, dollar.Position, close.End - dollar.Position));
    }

    #endregion

    #region Flank

    private FilterResult<FilterNode> _ParseFlank()
    {
        Token flankToken = _Current;
        _Advance();

        if (_Current.Kind != TokenKind.LeftParen)
        {
            return _Expected("'(' after 'flank'");
        }
        _Advance();

        if (_Current.Kind != TokenKind.Identifier)
        {
            return _Expected("a field name as the first flank argument");
        }
        Token fieldToken = _Current;
        _Advance();
        _ReportSpan(fieldToken.Position, fieldToken.Length, FilterFieldNameKind.FieldPath);

        FlankEndpoint? from = null;
        FlankEndpoint? to = null;
        FlankDelta? by = null;
        bool changed = false;
        FlankWindow? window = null;
        FilterNode? when = null;

        while (_Current.Kind == TokenKind.Comma)
        {
            _Advance();

            if (_Current.Kind != TokenKind.Identifier)
            {
                return _Expected("a flank argument name (from, to, by, changed, within, when)");
            }

            Token argToken = _Current;
            string argName = argToken.Text;
            _Advance();

            if (string.Equals(argName, "changed", StringComparison.OrdinalIgnoreCase))
            {
                changed = true;
                continue;
            }

            if (_Current.Kind != TokenKind.Colon)
            {
                return _Expected($"':' after '{argName}'");
            }
            _Advance();

            switch (argName.ToLowerInvariant())
            {
                case "from":
                {
                    FilterResult<FlankEndpoint> endpoint = _ParseFlankEndpoint();
                    if (!endpoint.TryGetValue(out FlankEndpoint fromEndpoint))
                    {
                        return FilterResult.Fail<FilterNode>(endpoint.Error);
                    }
                    from = fromEndpoint;
                    break;
                }

                case "to":
                {
                    FilterResult<FlankEndpoint> endpoint = _ParseFlankEndpoint();
                    if (!endpoint.TryGetValue(out FlankEndpoint toEndpoint))
                    {
                        return FilterResult.Fail<FilterNode>(endpoint.Error);
                    }
                    to = toEndpoint;
                    break;
                }

                case "by":
                {
                    FilterResult<FlankDelta> delta = _ParseFlankDelta();
                    if (!delta.TryGetValue(out FlankDelta parsedDelta))
                    {
                        return FilterResult.Fail<FilterNode>(delta.Error);
                    }
                    by = parsedDelta;
                    break;
                }

                case "within":
                {
                    FilterResult<FlankWindow> parsed = LiteralParser.ParseWindow(_Current);
                    if (!parsed.TryGetValue(out FlankWindow parsedWindow))
                    {
                        return FilterResult.Fail<FilterNode>(parsed.Error);
                    }
                    _Advance();
                    window = parsedWindow;
                    break;
                }

                case "when":
                {
                    FilterResult<FilterNode> gate = _ParseOr();
                    if (!gate.TryGetValue(out FilterNode? gateNode))
                    {
                        return gate;
                    }
                    when = gateNode;
                    break;
                }

                default:
                    return FilterError.Syntax(
                        $"Unknown flank argument '{argName}'", argToken.Position, argToken.Length);
            }
        }

        if (_Current.Kind != TokenKind.RightParen)
        {
            return _Expected("')' to close the flank expression");
        }
        Token close = _Current;
        _Advance();

        if (window is null)
        {
            return FilterError.Syntax(
                "'flank' requires a 'within:' window",
                flankToken.Position,
                close.End - flankToken.Position);
        }

        if (changed && (from is not null || to is not null || by is not null))
        {
            return FilterError.Syntax(
                "'changed' cannot be combined with 'from:', 'to:' or 'by:'",
                flankToken.Position,
                close.End - flankToken.Position);
        }

        if (by is not null && to is not null && from is null)
        {
            return FilterError.Syntax(
                "'by:' with 'to:' requires 'from:'",
                flankToken.Position,
                close.End - flankToken.Position);
        }

        bool anyChange = changed || (from is null && to is null && by is null);

        _Features |= FilterFeature.Flank;
        return FilterResult.Ok<FilterNode>(new FlankNode(
            fieldToken.Text,
            from,
            to,
            by,
            anyChange,
            window.Value,
            when,
            flankToken.Position,
            close.End - flankToken.Position));
    }

    private FilterResult<FlankEndpoint> _ParseFlankEndpoint()
    {
        CompareOp op = CompareOp.Equal;
        if (_IsCompareToken(_Current.Kind))
        {
            op = _ToCompareOp(_Current.Kind);
            _Advance();
        }

        FilterResult<FieldValueData> literal = _ParseLiteral();
        if (!literal.TryGetValue(out FieldValueData value))
        {
            return FilterResult.Fail<FlankEndpoint>(literal.Error);
        }

        return FilterResult.Ok<FlankEndpoint>(new FlankEndpoint(op, value));
    }

    private FilterResult<FlankDelta> _ParseFlankDelta()
    {
        CompareOp op = CompareOp.Equal;
        if (_IsCompareToken(_Current.Kind))
        {
            op = _ToCompareOp(_Current.Kind);
            _Advance();
        }

        Token literalToken = _Current;
        FilterResult<FieldValueData> literal = _ParseLiteral();
        if (!literal.TryGetValue(out FieldValueData value))
        {
            return FilterResult.Fail<FlankDelta>(literal.Error);
        }

        if (value.Type is not FieldType.I64 and not FieldType.U64)
        {
            return FilterError.InvalidValue(
                "'by:' expects an integer literal",
                literalToken.Position,
                Math.Max(literalToken.Length, 1));
        }

        return FilterResult.Ok<FlankDelta>(new FlankDelta(op, value));
    }

    #endregion

    #region Literals and helpers

    private FilterResult<FieldValueData> _ParseLiteral()
    {
        Token token = _Current;
        if (!LiteralParser.IsLiteralStart(token.Kind))
        {
            return FilterError.Syntax(
                token.Kind == TokenKind.Eof
                    ? "Unexpected end of expression — a literal value is required here"
                    : $"Expected a literal value but found '{token.Text}'",
                token.Position,
                Math.Max(token.Length, 1));
        }

        FilterResult<FieldValueData> parsed = LiteralParser.Parse(token);
        if (parsed.IsSuccess)
        {
            _Advance();
        }
        return parsed;
    }

    private FilterResult<int> _ParseNonNegativeInteger()
    {
        Token token = _Current;
        if (token.Kind != TokenKind.Integer)
        {
            return FilterError.Syntax(
                $"Expected a non-negative integer but found '{token.Text}'",
                token.Position,
                Math.Max(token.Length, 1));
        }

        FilterResult<FieldValueData> parsed = LiteralParser.Parse(token);
        if (!parsed.TryGetValue(out FieldValueData value))
        {
            return FilterResult.Fail<int>(parsed.Error);
        }
        if (!value.TryGetAsU64(out ulong raw) || raw > int.MaxValue)
        {
            return FilterError.InvalidValue($"Index '{token.Text}' is out of range", token.Position, token.Length);
        }

        _Advance();
        return (int)raw;
    }

    private static bool _IsCompareToken(TokenKind kind) => kind
        is TokenKind.Equal
        or TokenKind.NotEqual
        or TokenKind.LessThan
        or TokenKind.LessEqual
        or TokenKind.GreaterThan
        or TokenKind.GreaterEqual;

    private static CompareOp _ToCompareOp(TokenKind kind) => kind switch
    {
        TokenKind.Equal => CompareOp.Equal,
        TokenKind.NotEqual => CompareOp.NotEqual,
        TokenKind.LessThan => CompareOp.LessThan,
        TokenKind.LessEqual => CompareOp.LessEqual,
        TokenKind.GreaterThan => CompareOp.GreaterThan,
        _ => CompareOp.GreaterEqual,
    };

    /// <summary>
    /// Whether a pattern is well-formed. Only construction is attempted, which performs the full
    /// pattern parse and throws on bad syntax; unlike matching it can never time out.
    /// </summary>
    private static bool _IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private Token _Current => _Tokens[_Index];

    private Token _Previous => _Tokens[_Index > 0 ? _Index - 1 : 0];

    private Token _Peek(int offset)
    {
        int index = _Index + offset;
        return index < _Tokens.Count
            ? _Tokens[index]
            : _Tokens[^1];
    }

    private void _Advance()
    {
        if (_Index < _Tokens.Count - 1)
        {
            _Index++;
        }
    }

    private FilterResult<FilterNode> _Expected(string what) =>
        FilterResult.Fail<FilterNode>(_ExpectedError(what));

    private FilterError _ExpectedError(string what)
    {
        Token token = _Current;
        return FilterError.Syntax(
            token.Kind == TokenKind.Eof
                ? $"Unexpected end of expression — expected {what}"
                : $"Expected {what} but found '{token.Text}'",
            token.Position,
            Math.Max(token.Length, 1));
    }

    #endregion

    #region Name-span reporting

    private void _ReportName(OperandNode operand, FilterFieldNameKind kind)
    {
        // For len(name) and name[a:b] the operand span covers the whole syntax; report the
        // bare name so completion sees exactly the identifier the user typed.
        int start = operand switch
        {
            LengthOperandNode => operand.Position + 4,
            _ => operand.Position,
        };
        _ReportSpan(start, operand.Name.Length, kind);
    }

    private void _ReportSpan(int start, int length, FilterFieldNameKind kind)
    {
        if (_OnFieldNameSpan is null || _CallbackError is not null)
        {
            return;
        }

        try
        {
            _OnFieldNameSpan(_Source.AsSpan(), start, length, kind);
        }
        catch (Exception ex)
        {
            _CallbackError = FilterError.CallbackFailed(ex.Message);
        }
    }

    /// <summary>
    /// After a failed parse, surfaces a trailing identifier (for example the <c>tcp.po</c> in
    /// <c>"udp &amp;&amp; tcp.po"</c>) so completion still has a prefix to work with.
    /// </summary>
    private void _ReportTrailingIncompleteName()
    {
        if (_OnFieldNameSpan is null || _Tokens.Count < 2)
        {
            return;
        }

        Token last = _Tokens[^2];
        if (last.Kind == TokenKind.Identifier)
        {
            _ReportSpan(last.Position, last.Length, FilterFieldNameKind.Incomplete);
        }
    }

    #endregion
}

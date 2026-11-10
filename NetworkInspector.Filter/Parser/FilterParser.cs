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
/// endpoint  := [ cmpOp ] literal            (the operator defaults to '==')
/// delta     := [ cmpOp ] Integer            (the operator defaults to '==')
/// </code>
/// <para>
/// Every field, protocol and scope-anchor name is reported through
/// <see cref="FilterFieldNameSpanCallback"/> as soon as it is recognised — before any stack
/// binding — so an editor still receives spans for expressions that fail to parse.
/// </para>
/// <para>
/// <b>Trust-boundary limits.</b> Expressions come from users, the CLI and UI editors. The parser and every
/// later phase (code generation, dependency analysis, observer compile and evaluation) recurse over the tree,
/// and a stack overflow cannot be caught, so the tree is bounded here: at most <see cref="MaxNestingDepth"/>
/// nested <c>(</c>, <c>!</c>, scope bodies and <c>when:</c> gates, and at most <see cref="MaxTermCount"/> terms
/// in one expression. Both are constants; no caller needs deeper or larger trees, and long value lists belong
/// in <c>in { … }</c>. Posture: secure by default and not configurable, because the input is untrusted; an expression
/// that exceeds a limit fails with a positioned syntax error that names the limit. Accepted residual risk: the bounds
/// are fixed numbers and are not derived from the stack size of the calling thread.
/// </para>
/// <para>
/// Parsing is a single forward pass over the token list with one token of lookahead; there is no backtracking, so
/// the cost is linear in the token count. The parser relies on the final <see cref="TokenKind.Eof"/> token as a
/// sentinel: <c>_Advance</c> never moves past it and <c>_Peek</c> clamps to it, which keeps every grammar rule free of
/// end-of-input bounds checks. Thread safety: not thread-safe; an instance is single-use (<c>Parse</c> once).
/// </para>
/// </summary>
internal sealed class FilterParser
{
    #region Fields

    /// <summary>Maximum nesting of <c>(</c>, <c>!</c> / <c>not</c>, <c>$x { … }</c> bodies and <c>when:</c> gates.</summary>
    internal const int MaxNestingDepth = 64;

    /// <summary>
    /// Maximum number of terms (comparisons, presence tests, <c>true</c> / <c>false</c>, <c>flank(…)</c> and scopes)
    /// in one expression. Bounds the length of <c>&amp;&amp;</c> / <c>||</c> chains, which build trees as deep as
    /// they are long.
    /// </summary>
    internal const int MaxTermCount = 1024;

    /// <summary>Constructs removed in v1 that used to be call expressions.</summary>
    private static readonly HashSet<string> _RemovedCalls =
        new(StringComparer.OrdinalIgnoreCase) { "seq", "stream", "window", "nav" };

    /// <summary>Constructs removed in v1 that used to introduce bindings.</summary>
    private static readonly HashSet<string> _RemovedBindings =
        new(StringComparer.OrdinalIgnoreCase) { "let", "where", "step" };

    private readonly List<Token> _Tokens;
    private readonly string _Source;
    private readonly FilterFieldNameSpanCallback? _OnFieldNameSpan;
    private readonly TimeSpan _RegexTimeout;

    /// <summary>Index of the current token in <see cref="_Tokens"/>; never beyond the final Eof token.</summary>
    private int _Index;

    /// <summary>
    /// Current nesting level, checked against <see cref="MaxNestingDepth"/>. Every successful <c>_TryEnter</c> is
    /// paired with a decrement after the nested part has been parsed.
    /// </summary>
    private int _Depth;

    /// <summary>Terms parsed so far, checked against <see cref="MaxTermCount"/>.</summary>
    private int _TermCount;

    /// <summary>Language features seen so far (scope, flank), reported on the program (for example to refuse a scope in a tree-free observer).</summary>
    private FilterFeature _Features;

    /// <summary>
    /// First exception raised by the editor name-span callback, converted to an error. No further callbacks are made
    /// after it; the parse itself is not aborted at that point, the stored error is returned at the end and takes
    /// precedence over a syntax error (see <see cref="_Failure"/> and <see cref="Parse"/>).
    /// </summary>
    private FilterError? _CallbackError;

    #endregion

    #region Construction

    /// <summary>Creates a parser over an already tokenized expression.</summary>
    /// <param name="tokens">Tokens ending with <see cref="TokenKind.Eof"/>.</param>
    /// <param name="source">The expression the tokens came from.</param>
    /// <param name="onFieldNameSpan">Optional editor callback for name spans.</param>
    /// <param name="regexTimeout">
    /// Match timeout for <c>matches</c> patterns that need the backtracking engine; the pattern is compiled once
    /// here and shared by every back end and derived filter.
    /// </param>
    public FilterParser(
        List<Token> tokens,
        string source,
        FilterFieldNameSpanCallback? onFieldNameSpan,
        TimeSpan regexTimeout)
    {
        _Tokens = tokens;
        _Source = source;
        _OnFieldNameSpan = onFieldNameSpan;
        _RegexTimeout = regexTimeout;
        _Index = 0;
        _Features = FilterFeature.Classic;
    }

    #endregion

    #region Entry point

    /// <summary>
    /// Parses the token stream into a program. Syntax errors are returned, not thrown. Call at most once per instance.
    /// </summary>
    /// <returns>
    /// The program, or the first error: an editor callback failure takes precedence over a syntax error.
    /// </returns>
    public FilterResult<FilterProgram> Parse()
    {
        // Entry point of the grammar: parse one complete expression, then require that nothing follows it.
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
                Invariant($"Unexpected '{current.Text}' after a complete expression"),
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

    /// <summary>
    /// Parses <c>and</c>-chains separated by <c>or</c>. Both binary levels build a left-leaning tree in a loop (not by
    /// recursion), so a long chain costs no parser stack; the term limit bounds the tree depth later phases recurse
    /// through. <c>and</c> binds tighter than <c>or</c> because <c>or</c> sits one level higher in the grammar.
    /// </summary>
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

    /// <summary>Parses a chain of unary expressions joined by <c>and</c>; see <see cref="_ParseOr"/>.</summary>
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

    /// <summary>
    /// Parses an optional run of <c>!</c> / <c>not</c> followed by a primary. Negation recurses (<c>!!!x</c>), so each
    /// level counts against <see cref="MaxNestingDepth"/> to keep the parser's own stack bounded.
    /// </summary>
    private FilterResult<FilterNode> _ParseUnary()
    {
        if (_Current.Kind == TokenKind.Not)
        {
            Token notToken = _Current;
            if (!_TryEnter(notToken, out FilterError? tooDeep))
            {
                return tooDeep;
            }

            _Advance();
            FilterResult<FilterNode> operand = _ParseUnary();
            _Depth--;
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

    /// <summary>Parses a parenthesised group, a boolean constant, a flank, a scope or a single term.</summary>
    private FilterResult<FilterNode> _ParsePrimary()
    {
        Token token = _Current;

        // Every primary except a parenthesised group is one term of the expression. Counting here, before the
        // dispatch, keeps the term limit independent of which production the term turns out to be.
        bool isTerm = token.Kind is TokenKind.True or TokenKind.False or TokenKind.Flank or TokenKind.Dollar or TokenKind.Identifier;
        if (isTerm && !_TryCountTerm(token, out FilterError? tooMany))
        {
            return tooMany;
        }

        switch (token.Kind)
        {
            case TokenKind.LeftParen:
            {
                if (!_TryEnter(token, out FilterError? tooDeep))
                {
                    return tooDeep;
                }

                _Advance();
                FilterResult<FilterNode> inner = _ParseOr();
                _Depth--;
                if (!inner.TryGetValue(out FilterNode? node))
                {
                    return inner;
                }
                if (_Current.Kind != TokenKind.RightParen)
                {
                    return _Expected(Invariant($"')' to close the '(' at offset {token.Position.ToString(CultureInfo.InvariantCulture)}"));
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
                        : Invariant($"Unexpected '{token.Text}'"),
                    token.Position,
                    Math.Max(token.Length, 1));
        }
    }

    #endregion

    #region Terms

    /// <summary>
    /// Parses a term that starts with an identifier: a comparison, a set or range test, a string predicate, or a bare
    /// name (presence test). The token after the operand selects the production.
    /// </summary>
    private FilterResult<FilterNode> _ParseTerm()
    {
        Token nameToken = _Current;
        string name = nameToken.Text;

        // Removed v1 constructs lex as plain identifiers (see FilterLexer). Binding words are rejected wherever a
        // term starts. Call words are rejected only when they look like a call, so a field or protocol that happens
        // to be named like one ("window" without parentheses) keeps working.
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
                    return _Expected(Invariant($"a quoted string after '{op.Text}'"));
                }
                Token pattern = _Current;
                _Advance();

                // Compile the pattern exactly once: this both validates it and yields the instance every back end
                // and every derived filter shares. Security: the pattern is untrusted input (ReDoS). TryBuildRegex
                // prefers the linear-time non-backtracking engine and bounds the fallback by the regex timeout;
                // a pattern that matches too slowly surfaces later as a runtime error, never as a stall.
                Regex? regex = null;
                if (stringOp == StringOp.Matches && !FilterBinder.TryBuildRegex(pattern.Text, _RegexTimeout, out regex))
                {
                    return FilterError.InvalidValue(
                        Invariant($"Invalid regular expression '{pattern.Text}'"), pattern.Position, pattern.Length);
                }
                return FilterResult.Ok<FilterNode>(
                    new StringPredicateNode(
                        operand, stringOp, pattern.Text, regex, operand.Position, pattern.End - operand.Position));
            }

            default:
                // No operator follows: the bare name is a presence test (protocol or field). A slice or len()
                // is a computed value and means nothing without a comparison.
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

    /// <summary>
    /// Parses what follows <c>in</c>: either a brace-enclosed value set <c>{ a, b, c }</c> or an inclusive literal range
    /// <c>low .. high</c>. The set list is unbounded by the grammar; it counts as one term, which is why long value
    /// lists belong here instead of in an <c>or</c> chain (see <see cref="MaxTermCount"/>).
    /// </summary>
    private FilterResult<FilterNode> _ParseInTail(OperandNode operand)
    {
        if (_Current.Kind == TokenKind.LeftBrace)
        {
            _Advance();

            // Collect literals until no comma follows. At least one literal is required: an empty set is a
            // syntax error from _ParseLiteral.
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

        // Range form: "low .. high". The lexer keeps ".." out of numbers ("1..10"), so the bounds arrive as two literals.
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
    /// A slice <c>[start:end]</c> is half-open: it selects <c>end - start</c> bytes, so <c>end</c> must be greater than
    /// <c>start</c> (an empty or reversed slice is rejected here rather than evaluating to nothing at run time).
    /// </summary>
    private FilterResult<OperandNode> _ParseOperand()
    {
        Token nameToken = _Current;

        // "len" is a function only when a '(' follows, so a field that happens to be called len stays a field path.
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
                new LengthOperandNode(inner.Text, inner.Position, nameToken.Position, close.End - nameToken.Position));
        }

        _Advance();

        // Without a '[' the operand is the whole field value.
        if (_Current.Kind != TokenKind.LeftBracket)
        {
            return FilterResult.Ok<OperandNode>(
                new FieldOperandNode(nameToken.Text, nameToken.Position, nameToken.Length));
        }

        // Byte slice "[start:end]". The lexer already lexes the bracket content as Integer ':' Integer.
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

    /// <summary>
    /// Parses <c>$Name [ [n] ] { expr }</c>: the body is evaluated against the subtree of the named anchor (optionally
    /// its n-th occurrence). The body is a nested expression, so it counts as one nesting level. Marks the program
    /// with <see cref="FilterFeature.Scope"/>, which needs a field tree (the tree-free observer refuses it).
    /// </summary>
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
        Token open = _Current;
        if (!_TryEnter(open, out FilterError? tooDeep))
        {
            return tooDeep;
        }
        _Advance();

        FilterResult<FilterNode> body = _ParseOr();
        _Depth--;
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

    /// <summary>
    /// Parses <c>flank(field, args…)</c>, a stateful detector of a value transition of one field. Arguments may
    /// appear in any order and each later duplicate overwrites the earlier one. The cross-argument rules are checked
    /// after the closing parenthesis so the error can cover the whole expression: a <c>within:</c> window is mandatory
    /// (the transition must complete inside it, by time or packet count), <c>changed</c> excludes <c>from:</c> / <c>to:</c> /
    /// <c>by:</c>, and <c>by:</c> with <c>to:</c> needs <c>from:</c>. With none of <c>from:</c>, <c>to:</c>, <c>by:</c>
    /// the flank fires on any change. The <c>when:</c> gate is a nested expression and counts as one nesting level.
    /// </summary>
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

        // Arguments are collected first and validated together afterwards, so their order does not matter.
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

            // "changed" is the only argument without a value; all others are "name: value".
            if (string.Equals(argName, "changed", StringComparison.OrdinalIgnoreCase))
            {
                changed = true;
                continue;
            }

            if (_Current.Kind != TokenKind.Colon)
            {
                return _Expected(Invariant($"':' after '{argName}'"));
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
                    // ParseWindow validates the token without consuming it; advance only after it succeeded.
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
                    if (!_TryEnter(argToken, out FilterError? tooDeep))
                    {
                        return tooDeep;
                    }

                    FilterResult<FilterNode> gate = _ParseOr();
                    _Depth--;
                    if (!gate.TryGetValue(out FilterNode? gateNode))
                    {
                        return gate;
                    }
                    when = gateNode;
                    break;
                }

                default:
                    return FilterError.Syntax(
                        Invariant($"Unknown flank argument '{argName}'"), argToken.Position, argToken.Length);
            }
        }

        if (_Current.Kind != TokenKind.RightParen)
        {
            return _Expected("')' to close the flank expression");
        }
        Token close = _Current;
        _Advance();

        // Cross-argument rules, reported over the whole flank expression.
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

        // A flank without any condition (or with "changed") fires on every change of the value.
        bool anyChange = changed || (from is null && to is null && by is null);

        _Features |= FilterFeature.Flank;
        return FilterResult.Ok<FilterNode>(new FlankNode(
            fieldToken.Text,
            fieldToken.Position,
            from,
            to,
            by,
            anyChange,
            window.Value,
            when,
            flankToken.Position,
            close.End - flankToken.Position));
    }

    /// <summary>Parses a <c>from:</c> / <c>to:</c> endpoint: an optional comparison operator (default <c>==</c>) and a literal.</summary>
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

    /// <summary>
    /// Parses a <c>by:</c> delta: an optional comparison operator (default <c>==</c>) and an integer literal. The delta
    /// is computed as a signed 64-bit difference at run time, so only integer literals make sense.
    /// </summary>
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

    /// <summary>
    /// Parses and consumes one literal token. The token is consumed only when it converted successfully, so an error
    /// is reported at the offending token.
    /// </summary>
    private FilterResult<FieldValueData> _ParseLiteral()
    {
        Token token = _Current;
        if (!LiteralParser.IsLiteralStart(token.Kind))
        {
            return FilterError.Syntax(
                token.Kind == TokenKind.Eof
                    ? "Unexpected end of expression — a literal value is required here"
                    : Invariant($"Expected a literal value but found '{token.Text}'"),
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

    /// <summary>
    /// Parses a non-negative integer used as a slice bound or scope occurrence. The literal parser yields an unsigned
    /// value for every non-negative literal and a signed one for <c>-1</c>-style literals, so a negative value fails
    /// the unsigned extraction below and is reported as out of range.
    /// </summary>
    private FilterResult<int> _ParseNonNegativeInteger()
    {
        Token token = _Current;
        if (token.Kind != TokenKind.Integer)
        {
            return FilterError.Syntax(
                Invariant($"Expected a non-negative integer but found '{token.Text}'"),
                token.Position,
                Math.Max(token.Length, 1));
        }

        FilterResult<FieldValueData> parsed = LiteralParser.Parse(token);
        if (!parsed.TryGetValue(out FieldValueData value))
        {
            return FilterResult.Fail<int>(parsed.Error);
        }
        // Bounded to ArrayIndexIdRange.MaxValue (below int.MaxValue), so a caller may add one — the scope
        // occurrence limit "occurrence + 1" and slice widths — without overflow.
        if (!value.TryGetAsU64(out ulong raw) || raw > (ulong)ArrayIndexIdRange.MaxValue)
        {
            return FilterError.InvalidValue(
                Invariant($"Index '{token.Text}' is out of range (maximum {ArrayIndexIdRange.MaxValue.ToString(CultureInfo.InvariantCulture)})"),
                token.Position,
                token.Length);
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

    /// <summary>Counts one nesting level; fails with a positioned syntax error beyond <see cref="MaxNestingDepth"/>.</summary>
    private bool _TryEnter(Token at, [NotNullWhen(false)] out FilterError? failure)
    {
        if (_Depth >= MaxNestingDepth)
        {
            failure = FilterError.Syntax(
                Invariant($"Expression nests deeper than {MaxNestingDepth.ToString(CultureInfo.InvariantCulture)} levels ") +
                "of '(', '!', scope bodies or 'when:' gates",
                at.Position,
                Math.Max(at.Length, 1));
            return false;
        }

        _Depth++;
        failure = null;
        return true;
    }

    /// <summary>Counts one term; fails with a positioned syntax error beyond <see cref="MaxTermCount"/>.</summary>
    private bool _TryCountTerm(Token at, [NotNullWhen(false)] out FilterError? failure)
    {
        if (_TermCount >= MaxTermCount)
        {
            failure = FilterError.Syntax(
                Invariant($"Expression has more than {MaxTermCount.ToString(CultureInfo.InvariantCulture)} terms; ") +
                "use 'in { … }' for long value lists",
                at.Position,
                Math.Max(at.Length, 1));
            return false;
        }

        _TermCount++;
        failure = null;
        return true;
    }

    /// <summary>The current token. Always valid: <see cref="_Index"/> never moves past the trailing Eof token.</summary>
    private Token _Current => _Tokens[_Index];

    /// <summary>The token before the current one (the first token at the start); used to compute end offsets.</summary>
    private Token _Previous => _Tokens[_Index > 0 ? _Index - 1 : 0];

    /// <summary>Looks <paramref name="offset"/> tokens ahead without consuming; clamps to the Eof token past the end.</summary>
    private Token _Peek(int offset)
    {
        int index = _Index + offset;
        return index < _Tokens.Count
            ? _Tokens[index]
            : _Tokens[^1];
    }

    /// <summary>Consumes the current token, staying on the Eof token once the end is reached (see the class remarks).</summary>
    private void _Advance()
    {
        if (_Index < _Tokens.Count - 1)
        {
            _Index++;
        }
    }

    /// <summary>Failed node result for "expected <paramref name="what"/>" at the current token.</summary>
    private FilterResult<FilterNode> _Expected(string what) =>
        FilterResult.Fail<FilterNode>(_ExpectedError(what));

    /// <summary>Builds the "expected X" error at the current token; at end of input it says so instead of quoting Eof.</summary>
    private FilterError _ExpectedError(string what)
    {
        Token token = _Current;
        return FilterError.Syntax(
            token.Kind == TokenKind.Eof
                ? Invariant($"Unexpected end of expression — expected {what}")
                : Invariant($"Expected {what} but found '{token.Text}'"),
            token.Position,
            Math.Max(token.Length, 1));
    }

    #endregion

    #region Name-span reporting

    /// <summary>Reports the name inside <paramref name="operand"/> to the editor callback.</summary>
    private void _ReportName(OperandNode operand, FilterFieldNameKind kind)
    {
        // For len(name) and name[a:b] the operand span covers the whole syntax; report the
        // bare name so completion sees exactly the identifier the user typed. len may be
        // followed by whitespace, so its name carries its own position.
        int start = operand is LengthOperandNode length
            ? length.NamePosition
            : operand.Position;
        _ReportSpan(start, operand.Name.Length, kind);
    }

    /// <summary>
    /// Invokes the editor callback for one name span. The callback is caller-supplied code, so this is a
    /// trust boundary: any exception it throws is converted into a <see cref="FilterErrorKind.CallbackFailed"/> error
    /// (message only, no stack trace) instead of escaping the parser, and the callback is not called again afterwards.
    /// The source is passed as a span over the original string, so no copy is made per name.
    /// </summary>
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

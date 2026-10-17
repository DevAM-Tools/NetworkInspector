// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Completion;

/// <summary>
/// Computes <see cref="FilterCompletions"/> for a caret position in a possibly incomplete expression.
/// <para>
/// <b>How the context is found.</b> The word (or symbolic operator) touching the caret is located directly in the
/// text, because a half-typed word such as <c>udp.</c> is not yet a valid token. Everything before that word is
/// lexed leniently (<see cref="FilterLexer.TokenizeLenient"/>), and a single forward pass over those tokens tracks
/// the open brackets — group, <c>flank(</c>, <c>len(</c>, scope body, value set, slice, scope index — so the last
/// token and its enclosing group decide what may follow: an expression, a name, a flank argument, an operator, a
/// connective, or a literal value (for which nothing is offered).
/// </para>
/// <para>
/// <b>Cost.</b> Linear in the expression length plus a binary search in the stack's cached
/// <see cref="FilterNameCatalog"/>; no parse tree is built, so no recursion depth depends on the input.
/// </para>
/// <para><b>Thread-safety:</b> stateless; safe to call from any thread.</para>
/// </summary>
internal static class FilterCompletionEngine
{
    #region Vocabulary

    private static readonly FilterCompletionItem[] _ExpressionKeywords =
    [
        new("false", FilterCompletionKind.Keyword, FieldType.None),
        new("flank", FilterCompletionKind.Keyword, FieldType.None),
        new("len", FilterCompletionKind.Keyword, FieldType.None),
        new("not", FilterCompletionKind.Keyword, FieldType.None),
        new("true", FilterCompletionKind.Keyword, FieldType.None),
    ];

    private static readonly FilterCompletionItem[] _Connectives =
    [
        new("&&", FilterCompletionKind.Operator, FieldType.None),
        new("and", FilterCompletionKind.Keyword, FieldType.None),
        new("or", FilterCompletionKind.Keyword, FieldType.None),
        new("||", FilterCompletionKind.Operator, FieldType.None),
    ];

    private static readonly FilterCompletionItem[] _OperandOperators =
    [
        new("!=", FilterCompletionKind.Operator, FieldType.None),
        new("<", FilterCompletionKind.Operator, FieldType.None),
        new("<=", FilterCompletionKind.Operator, FieldType.None),
        new("==", FilterCompletionKind.Operator, FieldType.None),
        new(">", FilterCompletionKind.Operator, FieldType.None),
        new(">=", FilterCompletionKind.Operator, FieldType.None),
        new("contains", FilterCompletionKind.Keyword, FieldType.None),
        new("in", FilterCompletionKind.Keyword, FieldType.None),
        new("matches", FilterCompletionKind.Keyword, FieldType.None),
    ];

    private static readonly FilterCompletionItem[] _FlankArguments =
    [
        new("by:", FilterCompletionKind.FlankArgument, FieldType.None),
        new("changed", FilterCompletionKind.FlankArgument, FieldType.None),
        new("from:", FilterCompletionKind.FlankArgument, FieldType.None),
        new("to:", FilterCompletionKind.FlankArgument, FieldType.None),
        new("when:", FilterCompletionKind.FlankArgument, FieldType.None),
        new("within:", FilterCompletionKind.FlankArgument, FieldType.None),
    ];

    #endregion

    #region Context model

    /// <summary>What may start at the caret.</summary>
    private enum Expect : byte
    {
        /// <summary>Nothing completable: a literal value, punctuation, or the inside of a string.</summary>
        Nothing = 0,

        /// <summary>A new term: any name plus the expression keywords.</summary>
        Expression = 1,

        /// <summary>A scope anchor after <c>$</c>: any name.</summary>
        AnyName = 2,

        /// <summary>A value source (field or alias): <c>flank(</c> field, <c>len(</c> argument.</summary>
        ValueName = 3,

        /// <summary>A named flank argument after a comma inside <c>flank(…)</c>.</summary>
        FlankArgument = 4,

        /// <summary>After an operand: comparison, membership and string operators, or a connective.</summary>
        AfterOperand = 5,

        /// <summary>After a complete term: a connective.</summary>
        AfterTerm = 6,
    }

    /// <summary>Kind of an open bracket.</summary>
    private enum Group : byte
    {
        Paren = 0,
        FlankArgs = 1,
        LenArg = 2,
        ScopeBody = 3,
        ValueSet = 4,
        Slice = 5,
        ScopeIndex = 6,
    }

    /// <summary>One open bracket; <see cref="InWhen"/> marks a flank group whose current argument is <c>when:</c>.</summary>
    private struct OpenGroup
    {
        public Group Kind;
        public bool InWhen;
    }

    #endregion

    #region Entry point

    /// <summary>Computes the completions; see <see cref="Filter.GetCompletions"/> for the contract.</summary>
    public static FilterCompletions Complete(string expression, int caretPosition, IStack? stack)
    {
        // An operator run right before the caret ("udp.port =") is completed as an operator only where an
        // operator may stand. Elsewhere it is a finished token such as the '!' of "!udp", and the caret starts
        // a new, still empty word.
        bool isOperator = caretPosition > 0 && _IsOperatorChar(expression[caretPosition - 1]);
        if (isOperator)
        {
            FilterCompletions? asOperator = _Complete(expression, caretPosition, stack, isOperator: true, operatorsOnly: true);
            if (asOperator is not null)
            {
                return asOperator;
            }
        }

        return _Complete(expression, caretPosition, stack, isOperator: false, operatorsOnly: false)!;
    }

    /// <summary>
    /// Completes the word (or operator run) at the caret. With <paramref name="operatorsOnly"/> it returns
    /// <see langword="null"/> when the context does not accept an operator, so the caller can retry as a word.
    /// </summary>
    private static FilterCompletions? _Complete(string expression, int caretPosition, IStack? stack, bool isOperator, bool operatorsOnly)
    {
        // Locate the word or operator run the caret touches. Names may contain '.', operators are runs of
        // = ! < > & |; the replace range extends past the caret so accepting mid-word replaces the whole word.
        Func<char, bool> belongs = isOperator ? _IsOperatorChar : _IsNameChar;
        int start = caretPosition;
        while (start > 0 && belongs(expression[start - 1]))
        {
            start--;
        }

        int end = caretPosition;
        while (end < expression.Length && belongs(expression[end]))
        {
            end++;
        }

        string prefix = expression[start..caretPosition];

        // A word that starts with a digit or a dot is a number, address or byte literal — never a name.
        if (!isOperator && prefix.Length > 0 && !_IsNameStart(prefix[0]))
        {
            return _Empty(start, end, prefix);
        }

        // Read the context from everything before the word.
        FilterLexer lexer = new(expression[..start]);
        List<Token> tokens = lexer.TokenizeLenient(out bool insideString);
        if (insideString)
        {
            return _Empty(start, end, prefix);
        }

        Expect expect = _Decide(tokens);
        if (operatorsOnly && expect is not (Expect.AfterOperand or Expect.AfterTerm))
        {
            return null;
        }

        List<FilterCompletionItem> items = [];
        _Collect(expect, prefix, stack, items);
        items.Sort(static (left, right) => FilterNameCatalog.NameOrder(left.Text, right.Text));
        return new FilterCompletions(start, end - start, prefix, [.. items]);
    }

    #endregion

    #region Context decision

    private static Expect _Decide(List<Token> tokens)
    {
        if (tokens.Count == 0)
        {
            return Expect.Expression;
        }

        // One forward pass tracks the open brackets and what the last closed bracket was.
        List<OpenGroup> open = [];
        Group? lastClosed = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            TokenKind kind = tokens[i].Kind;
            TokenKind previous = i > 0 ? tokens[i - 1].Kind : TokenKind.Eof;
            switch (kind)
            {
                case TokenKind.LeftParen:
                    open.Add(new OpenGroup
                    {
                        Kind = previous == TokenKind.Flank
                            ? Group.FlankArgs
                            : _IsLen(tokens, i - 1) ? Group.LenArg : Group.Paren,
                    });
                    break;

                case TokenKind.LeftBrace:
                    open.Add(new OpenGroup { Kind = previous == TokenKind.In ? Group.ValueSet : Group.ScopeBody });
                    break;

                case TokenKind.LeftBracket:
                    bool scopeIndex = previous == TokenKind.Identifier && i > 1 && tokens[i - 2].Kind == TokenKind.Dollar;
                    open.Add(new OpenGroup { Kind = scopeIndex ? Group.ScopeIndex : Group.Slice });
                    break;

                case TokenKind.RightParen:
                case TokenKind.RightBrace:
                case TokenKind.RightBracket:
                    lastClosed = open.Count > 0 ? open[^1].Kind : null;
                    if (open.Count > 0)
                    {
                        open.RemoveAt(open.Count - 1);
                    }

                    break;

                case TokenKind.Colon when open.Count > 0 && open[^1].Kind == Group.FlankArgs:
                    // "when:" opens an expression that runs to the next comma at this level.
                    if (_IsWord(tokens, i - 1, "when"))
                    {
                        open[^1] = open[^1] with { InWhen = true };
                    }

                    break;

                case TokenKind.Comma when open.Count > 0 && open[^1].Kind == Group.FlankArgs:
                    open[^1] = open[^1] with { InWhen = false };
                    break;
            }
        }

        OpenGroup? top = open.Count > 0 ? open[^1] : null;
        bool inFlankArgs = top is { Kind: Group.FlankArgs, InWhen: false };
        Token last = tokens[^1];
        switch (last.Kind)
        {
            case TokenKind.Not:
            case TokenKind.And:
            case TokenKind.Or:
                return Expect.Expression;

            case TokenKind.LeftParen:
                return top?.Kind switch
                {
                    Group.FlankArgs or Group.LenArg => Expect.ValueName,
                    _ => Expect.Expression,
                };

            case TokenKind.LeftBrace:
                return top?.Kind == Group.ScopeBody
                    ? Expect.Expression
                    : Expect.Nothing;

            case TokenKind.Dollar:
                return Expect.AnyName;

            case TokenKind.Comma:
                return top?.Kind == Group.FlankArgs
                    ? Expect.FlankArgument
                    : Expect.Nothing;

            case TokenKind.Colon:
                return top is { Kind: Group.FlankArgs, InWhen: true }
                    ? Expect.Expression
                    : Expect.Nothing;

            case TokenKind.Identifier:
                // A completed name followed by a separator. Inside flank arguments, len(…) or after "$name" a
                // bracket or comma must follow, not an operator.
                if (inFlankArgs || top?.Kind == Group.LenArg || (tokens.Count > 1 && tokens[^2].Kind == TokenKind.Dollar))
                {
                    return Expect.Nothing;
                }

                return Expect.AfterOperand;

            case TokenKind.RightBracket:
                return lastClosed == Group.Slice
                    ? Expect.AfterOperand
                    : Expect.Nothing;

            case TokenKind.RightParen:
                return lastClosed == Group.LenArg
                    ? Expect.AfterOperand
                    : Expect.AfterTerm;

            case TokenKind.RightBrace:
                return Expect.AfterTerm;

            case TokenKind.Integer:
            case TokenKind.Float:
            case TokenKind.StringLiteral:
            case TokenKind.Ipv4Address:
            case TokenKind.Ipv6Address:
            case TokenKind.MacAddress:
            case TokenKind.HexBytes:
            case TokenKind.Duration:
            case TokenKind.True:
            case TokenKind.False:
                // A literal completes a term — unless it sits in a list, a flank argument, or is a range's low end.
                if (inFlankArgs || top?.Kind is Group.ValueSet or Group.Slice or Group.ScopeIndex
                    || (tokens.Count > 1 && tokens[^2].Kind == TokenKind.In))
                {
                    return Expect.Nothing;
                }

                return Expect.AfterTerm;

            default:
                // Comparison and membership operators, "..", "[", "flank" before its "(": a value or punctuation follows.
                return Expect.Nothing;
        }
    }

    private static bool _IsLen(List<Token> tokens, int index) => _IsWord(tokens, index, "len");

    private static bool _IsWord(List<Token> tokens, int index, string word) =>
        index >= 0
        && tokens[index].Kind == TokenKind.Identifier
        && string.Equals(tokens[index].Text, word, StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Candidates

    private static void _Collect(Expect expect, string prefix, IStack? stack, List<FilterCompletionItem> items)
    {
        switch (expect)
        {
            case Expect.Expression:
                _AddNames(stack, prefix, valueSourcesOnly: false, items);
                _AddWords(_ExpressionKeywords, prefix, items);
                break;

            case Expect.AnyName:
                _AddNames(stack, prefix, valueSourcesOnly: false, items);
                break;

            case Expect.ValueName:
                _AddNames(stack, prefix, valueSourcesOnly: true, items);
                break;

            case Expect.FlankArgument:
                _AddWords(_FlankArguments, prefix, items);
                break;

            case Expect.AfterOperand:
                _AddWords(_OperandOperators, prefix, items);
                _AddWords(_Connectives, prefix, items);
                break;

            case Expect.AfterTerm:
                _AddWords(_Connectives, prefix, items);
                break;

            default:
                break;
        }
    }

    private static void _AddNames(IStack? stack, string prefix, bool valueSourcesOnly, List<FilterCompletionItem> items)
    {
        // Without a stack only the language itself can be completed.
        if (stack is not null)
        {
            FilterNameCatalog.For(stack).AddMatches(prefix, valueSourcesOnly, items);
        }
    }

    private static void _AddWords(FilterCompletionItem[] words, string prefix, List<FilterCompletionItem> items)
    {
        foreach (FilterCompletionItem word in words)
        {
            if (word.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(word);
            }
        }
    }

    #endregion

    #region Character classes

    private static FilterCompletions _Empty(int start, int end, string prefix) => new(start, end - start, prefix, []);

    /// <summary>Same identifier start rule as the lexer.</summary>
    private static bool _IsNameStart(char c) => char.IsLetter(c) || c == '_';

    /// <summary>Identifier characters plus the path separator.</summary>
    private static bool _IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.';

    private static bool _IsOperatorChar(char c) => c is '=' or '!' or '<' or '>' or '&' or '|';

    #endregion
}

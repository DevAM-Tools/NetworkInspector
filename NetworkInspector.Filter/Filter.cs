// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter;

/// <summary>
/// Compiles filter expressions and evaluates them against packets.
/// <para>
/// A compilation runs lex → parse → bind/code-generate → dependency analysis. The front end is
/// stack-agnostic, so <see cref="TryDerive"/> can re-bind an existing filter to another
/// <see cref="IStack"/> without re-parsing.
/// </para>
/// <para>
/// <b>Empty expressions.</b> An empty or whitespace-only expression compiles to
/// <see cref="AlwaysMatch"/> and needs no stack at all. This keeps "no filter applied" free at
/// every call site instead of forcing callers to special-case a null filter.
/// </para>
/// <para>
/// <b>Poisoning.</b> A runtime fault recorded in the eval context (for example a regex timeout)
/// marks the instance poisoned for both stateful and classic filters, so callers see one sticky
/// failure mode until <see cref="ResetState"/> or <see cref="TryDerive"/>. Unexpected exceptions
/// from the compiled evaluation root are caught only when the filter is stateful (flank state may be dirty);
/// on a classic filter they propagate after the eval context is unbound.
/// </para>
/// <para>
/// <b>Thread-safety:</b> <see cref="AlwaysMatch"/> is immutable and safe to share across threads.
/// Every other instance is single-threaded — see <see cref="IFilter"/>.
/// </para>
/// </summary>
public sealed class Filter : IFilter
{
    #region Fields

    /// <inheritdoc />
    public string Expression { get; }

    /// <inheritdoc />
    public bool IsAlwaysMatch { get; }

    /// <inheritdoc />
    public IStack? Stack { get; }

    private readonly FilterProgram? _Program;
    private readonly CompiledFilterProgram? _Compiled;
    private readonly FilterEvalContext? _Context;
    private readonly DependencyNode? _Dependencies;

    /// <summary>Presence program for live pruning; null for always-match, stateful, or never-prunable programs.</summary>
    private readonly PrunePlan? _Prune;

    /// <summary>Null on <see cref="AlwaysMatch"/> — that singleton must stay immutable.</summary>
    private readonly MatchCache? _Cache;
    private readonly bool _EnableMatchCache;

    /// <inheritdoc />
    public FilterError? PoisonError { get; private set; }
    private int _HighestEvaluatedId = -1;

    /// <summary>Whether the program contains a <c>matches</c> predicate (computed once in the constructor).</summary>
    private readonly bool _UsesRegex;

    #endregion

    #region Construction

    /// <summary>Creates the always-match singleton (no mutable cache or eval state).</summary>
    private Filter()
    {
        Expression = string.Empty;
        IsAlwaysMatch = true;
        _Cache = null;
    }

    /// <summary>Creates a compiled filter.</summary>
    private Filter(
        string expression,
        IStack stack,
        FilterProgram program,
        CompiledFilterProgram compiled,
        DependencyNode dependencies,
        ProtocolId[] fieldOwners,
        bool enableMatchCache)
    {
        Expression = expression;
        Stack = stack;
        _Program = program;
        _Compiled = compiled;
        _Dependencies = dependencies;

        // Stateful programs must observe every packet in order, so they never get a prune program.
        _Prune = compiled.IsStateful ? null : PrunePlan.Create(dependencies);
        _Context = new FilterEvalContext(fieldOwners);
        _EnableMatchCache = enableMatchCache;
        _Cache = new();
        _UsesRegex = _ContainsMatches(program.Root);
    }

    #endregion

    #region Static API

    /// <summary>
    /// The filter that accepts every packet. Requires no stack and holds no mutable state.
    /// Safe to share across threads; <see cref="ResetState"/> is a no-op.
    /// </summary>
    public static Filter AlwaysMatch { get; } = new();

    /// <summary>
    /// Compiles an expression against a stack.
    /// An empty or whitespace-only expression yields <see cref="AlwaysMatch"/> and ignores
    /// <paramref name="stack"/>.
    /// </summary>
    /// <param name="expression">The filter expression.</param>
    /// <param name="stack">The stack to bind names against; may be <see langword="null"/> only for empty expressions.</param>
    /// <param name="options">Optional compilation inputs.</param>
    public static FilterResult<Filter> Compile(
        string expression,
        IStack? stack,
        FilterCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(expression);

        if (string.IsNullOrWhiteSpace(expression))
        {
            return AlwaysMatch;
        }

        if (stack is null)
        {
            return FilterError.StackRequired();
        }

        FilterResult<FilterProgram> parsed = _Parse(expression, options);
        if (!parsed.TryGetValue(out FilterProgram? program))
        {
            return FilterResult.Fail<Filter>(parsed.Error);
        }

        return _Bind(expression, program, stack, options);
    }

    /// <summary>Compiles an expression that must be empty, yielding <see cref="AlwaysMatch"/>.</summary>
    public static FilterResult<Filter> Compile(string expression) => Compile(expression, null);

    /// <summary>
    /// Compiles an expression into a tree-free <see cref="FilterObserver"/>.
    /// Does not construct a <see cref="Filter"/>. An empty expression yields
    /// <see cref="FilterObserver.AlwaysMatch"/> and ignores <paramref name="stack"/>.
    /// A subtree scope fails with <see cref="FilterErrorKind.NeedsFieldTree"/>.
    /// </summary>
    /// <param name="expression">The filter expression.</param>
    /// <param name="stack">The stack to bind names against. Null is allowed only for an empty expression.</param>
    /// <param name="options">Selects this overload and carries the same compile inputs as a field-tree compile.</param>
    public static FilterResult<FilterObserver> Compile(
        string expression,
        IStack? stack,
        in FilterObserverOptions options)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (string.IsNullOrWhiteSpace(expression))
        {
            return FilterObserver.AlwaysMatch;
        }

        if (stack is null)
        {
            return FilterError.StackRequired();
        }

        FilterResult<FilterProgram> parsed = _Parse(expression, options.Compile);
        if (!parsed.TryGetValue(out FilterProgram? program))
        {
            return parsed.Error;
        }

        return FilterObserverCompiler.Compile(expression, program, stack);
    }

    /// <summary>
    /// Compiles an observer, reporting failures through <paramref name="failure"/>.
    /// </summary>
    public static bool TryCompile(
        string expression,
        IStack? stack,
        in FilterObserverOptions options,
        [NotNullWhen(true)] out FilterObserver? observer,
        [NotNullWhen(false)] out FilterError? failure)
    {
        FilterResult<FilterObserver> result = Compile(expression, stack, in options);
        if (result.TryGetValue(out FilterObserver? compiled))
        {
            observer = compiled;
            failure = null;
            return true;
        }

        observer = null;
        failure = result.Error;
        return false;
    }

    /// <summary>
    /// Compiles an expression, reporting failures through <paramref name="failure"/>.
    /// </summary>
    public static bool TryCompile(
        string expression,
        IStack? stack,
        [NotNullWhen(true)] out Filter? filter,
        [NotNullWhen(false)] out FilterError? failure)
    {
        FilterResult<Filter> result = Compile(expression, stack);
        if (result.TryGetValue(out Filter? compiled))
        {
            filter = compiled;
            failure = null;
            return true;
        }

        filter = null;
        failure = result.Error;
        return false;
    }

    /// <summary>
    /// Parses an expression without binding it to a stack.
    /// <para>
    /// Intended for editors: the parse reports every field, protocol and scope-anchor span through
    /// <see cref="FilterCompileOptions.OnFieldNameSpan"/>, including a trailing incomplete name,
    /// so completion works while the user is still typing. Because no stack is consulted, unknown
    /// names are <b>not</b> reported here.
    /// </para>
    /// </summary>
    public static bool TryParse(
        string expression,
        FilterCompileOptions? options,
        [NotNullWhen(false)] out FilterError? failure)
    {
        ArgumentNullException.ThrowIfNull(expression);

        if (string.IsNullOrWhiteSpace(expression))
        {
            failure = null;
            return true;
        }

        FilterResult<FilterProgram> parsed = _Parse(expression, options);
        if (parsed.IsSuccess)
        {
            failure = null;
            return true;
        }

        failure = parsed.Error;
        return false;
    }

    /// <summary>Parses an expression without binding it to a stack.</summary>
    public static bool TryParse(string expression, FilterCompileOptions? options = null) =>
        TryParse(expression, options, out _);

    /// <summary>
    /// Lists what may be typed at <paramref name="caretPosition"/>, for editor auto-completion while the user types.
    /// <para>
    /// The expression may be incomplete or invalid. The result names the word (or symbolic operator) touching the
    /// caret as <see cref="FilterCompletions.ReplaceStart"/> / <see cref="FilterCompletions.ReplaceLength"/>, the
    /// part typed before the caret as <see cref="FilterCompletions.Prefix"/>, and every candidate that starts with
    /// that prefix (ignoring case) in alphabetical order. The candidates follow the grammar at the caret: protocol,
    /// field and alias names plus <c>flank</c>, <c>len</c>, <c>not</c>, <c>true</c>, <c>false</c> where a term may
    /// start; only fields and aliases inside <c>flank(</c> and <c>len(</c>; flank argument names after a comma in
    /// <c>flank(…)</c>; comparison, <c>in</c>, <c>contains</c>, <c>matches</c> and connectives after an operand;
    /// connectives after a complete term. Where a literal value is expected, or inside a string, the list is empty.
    /// </para>
    /// <para>
    /// Without a <paramref name="stack"/> only keywords and operators are offered. Names come from a per-stack catalog
    /// that is built once and cached for the stack's lifetime, so calling this on every keystroke is cheap. Errors
    /// of a finished expression, with position and a "Did you mean …?" <see cref="FilterError.Suggestion"/> for
    /// unknown names, come from <see cref="Compile(string, IStack?, FilterCompileOptions?)"/>.
    /// </para>
    /// <para><b>Thread-safety:</b> safe to call concurrently, also for the same stack.</para>
    /// </summary>
    /// <param name="expression">The text in the editor.</param>
    /// <param name="caretPosition">Caret offset in UTF-16 code units, <c>0 … expression.Length</c>.</param>
    /// <param name="stack">The stack whose names to offer; <see langword="null"/> offers the language words only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="caretPosition"/> is outside the expression.</exception>
    public static FilterCompletions GetCompletions(string expression, int caretPosition, IStack? stack)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentOutOfRangeException.ThrowIfNegative(caretPosition);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(caretPosition, expression.Length);

        return FilterCompletionEngine.Complete(expression, caretPosition, stack);
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public bool IsStateful
    {
        get
        {
            if (_Compiled is null)
            {
                return false;
            }
            return _Compiled.IsStateful;
        }
    }

    /// <inheritdoc />
    public bool IsPoisoned => PoisonError is not null;

    /// <summary>Number of packets with a cached verdict (always 0 for <see cref="AlwaysMatch"/>).</summary>
    public long EvaluatedCount
    {
        get
        {
            MatchCache? cache = _Cache;
            if (cache is null)
            {
                return 0;
            }

            return cache.EvaluatedCount;
        }
    }

    /// <summary>Packets that matched so far (empty for <see cref="AlwaysMatch"/>).</summary>
    public ReadOnlyRoaringBitmap MatchedPackets
    {
        get
        {
            MatchCache? cache = _Cache;
            if (cache is null)
            {
                return ReadOnlyRoaringBitmap.Empty;
            }

            return cache.Matched;
        }
    }

    /// <summary>Packets that have been evaluated so far (empty for <see cref="AlwaysMatch"/>).</summary>
    public ReadOnlyRoaringBitmap EvaluatedPackets
    {
        get
        {
            MatchCache? cache = _Cache;
            if (cache is null)
            {
                return ReadOnlyRoaringBitmap.Empty;
            }

            return cache.Evaluated;
        }
    }

    #endregion

    #region Evaluation

    /// <inheritdoc />
    public bool TryIsMatch(Packet packet, out bool matched, [NotNullWhen(false)] out FilterError? failure)
        => TryIsMatch<PacketIndex>(packet, null, out matched, out failure);

    /// <inheritdoc />
    public bool TryIsMatch<TIndex>(Packet packet, TIndex? index, out bool matched, [NotNullWhen(false)] out FilterError? failure)
        where TIndex : IPacketIndexReader
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (IsAlwaysMatch)
        {
            matched = true;
            failure = null;
            return true;
        }

        if (!packet.HasFieldTree)
        {
            matched = false;
            failure = FilterError.NoFieldTree();
            return false;
        }

        if (PoisonError is FilterError poison)
        {
            matched = false;
            failure = poison;
            return false;
        }

        MatchCache cache = _Cache!;
        if (_EnableMatchCache && cache.TryGet(packet.Id, out bool cached))
        {
            matched = cached;
            failure = null;
            return true;
        }

        int packetId = packet.Id.Value;
        if (IsStateful && packetId < _HighestEvaluatedId)
        {
            return _Poison(FilterError.OutOfOrder(packetId, _HighestEvaluatedId), out matched, out failure);
        }

        FilterEvalContext context = _Context!;
        bool result;
        context.Bind(packet, index);
        try
        {
            try
            {
                result = _Compiled!.Root(context);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && IsStateful)
            {
                // Expected runtime faults (e.g. regex) already land in context.Error via FilterRuntime.
                // Unexpected exceptions on stateful filters poison so flank state cannot continue dirty.
                // Stateless filters are not caught here — programming bugs must surface to the caller.
                // Unbind always runs in the outer finally so a propagating throw cannot leave Bind sticky.
                return _Poison(
                    FilterError.Runtime(
                        $"Evaluating packet {packetId.ToString(CultureInfo.InvariantCulture)} threw {exception.GetType().Name}: {exception.Message}"),
                    out matched,
                    out failure);
            }

            FilterError? runtimeError = context.Error;
            if (runtimeError is not null)
            {
                return _Poison(runtimeError, out matched, out failure);
            }

            if (_EnableMatchCache)
            {
                cache.Store(packet.Id, result);
            }

            if (packetId > _HighestEvaluatedId)
            {
                _HighestEvaluatedId = packetId;
            }

            matched = result;
            failure = null;
            return true;
        }
        finally
        {
            context.Unbind();
        }
    }

    /// <inheritdoc />
    public bool TryBuildCandidates<TIndex>(TIndex index, [NotNullWhen(true)] out RoaringBitmap? candidates)
        where TIndex : IPacketIndexReader
    {
        _ThrowIfNullReader(index);

        if (IsAlwaysMatch || _Dependencies is null)
        {
            candidates = null;
            return false;
        }

        if (!ReferenceEquals(index.Stack, Stack))
        {
            candidates = null;
            return false;
        }

        return CandidateBitmapBuilder.TryBuild(_Dependencies, index, out candidates);
    }

    /// <inheritdoc />
    public bool TryIsPresenceCandidate<TIndex>(TIndex index, uint packetId, out bool isCandidate)
        where TIndex : IPacketIndexReader
    {
        _ThrowIfNullReader(index);

        // Always-match keeps no plan; stateful and never-prunable programs have none either.
        if (_Prune is null || !ReferenceEquals(index.Stack, Stack))
        {
            isCandidate = true;
            return false;
        }

        return _Prune.TryIsCandidate(index, packetId, out isCandidate);
    }

    /// <summary>
    /// The cached verdict of <paramref name="packetId"/>, for read paths that want to skip work for known packets.
    /// False when this filter is always-match, poisoned, keeps no cache, or has not evaluated the id.
    /// </summary>
    internal bool TryGetCachedVerdict(PacketId packetId, out bool matched)
    {
        if (IsAlwaysMatch || PoisonError is not null || !_EnableMatchCache)
        {
            matched = false;
            return false;
        }

        return _Cache!.TryGet(packetId, out matched);
    }

    /// <summary>
    /// Records a verdict decided by an equivalent evaluator (the session's skip-parse pre-check), with the bookkeeping
    /// of a successful <see cref="TryIsMatch{TIndex}"/>: match cache and highest evaluated id.
    /// </summary>
    /// <exception cref="InvalidOperationException">On a stateful or always-match filter (caller bug).</exception>
    internal void RecordVerdict(PacketId packetId, bool matched)
    {
        if (IsAlwaysMatch || IsStateful)
        {
            throw new InvalidOperationException("External verdicts are only valid for stateless compiled filters.");
        }

        if (_EnableMatchCache)
        {
            _Cache!.Store(packetId, matched);
        }

        if (packetId.Value > _HighestEvaluatedId)
        {
            _HighestEvaluatedId = packetId.Value;
        }
    }

    /// <summary>
    /// A tree-free observer with this filter's verdicts, compiled from the retained program (no re-parse), or null when
    /// equivalence does not hold by construction: always-match, stateful, scoped, <c>matches</c> (a regex can time out
    /// on a term the field-tree filter short-circuits), or an observer compile refusal (for example a protocol without
    /// a container field).
    /// </summary>
    internal FilterObserver? CreatePrecheckObserver()
    {
        if (IsAlwaysMatch || IsStateful || _UsesRegex || (_Program!.Features & FilterFeature.Scope) != 0)
        {
            return null;
        }

        FilterResult<FilterObserver> compiled = FilterObserverCompiler.Compile(Expression, _Program, Stack!);
        return compiled.TryGetValue(out FilterObserver? observer)
            ? observer
            : null;
    }

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public void ResetState()
    {
        // AlwaysMatch is a shared immutable singleton — never mutate it.
        if (IsAlwaysMatch)
        {
            return;
        }

        PoisonError = null;
        _HighestEvaluatedId = -1;
        _Cache!.Clear();
        _Compiled?.ResetState();
    }

    /// <summary>
    /// Re-binds the already parsed expression to another stack and returns a fresh <see cref="Filter"/> with
    /// empty state. The source filter is left untouched, including its poison flag. The match-cache policy and
    /// the compiled <c>matches</c> patterns (with their timeout) carry over from the source.
    /// </summary>
    /// <param name="stack">The stack to bind names against.</param>
    /// <param name="derived">Receives the new filter on success.</param>
    /// <param name="failure">Receives the bind error, for example a name the new stack does not know.</param>
    public bool TryDerive(
        IStack stack,
        [NotNullWhen(true)] out Filter? derived,
        [NotNullWhen(false)] out FilterError? failure)
    {
        ArgumentNullException.ThrowIfNull(stack);

        if (IsAlwaysMatch)
        {
            derived = AlwaysMatch;
            failure = null;
            return true;
        }

        // Keep the caller's cache policy across rebinds (forward-only convert stays forward-only).
        FilterCompileOptions deriveOptions = new() { EnableMatchCache = _EnableMatchCache };
        FilterResult<Filter> result = _Bind(Expression, _Program!, stack, deriveOptions);
        if (result.TryGetValue(out Filter? filter))
        {
            derived = filter;
            failure = null;
            return true;
        }

        derived = null;
        failure = result.Error;
        return false;
    }

    /// <inheritdoc />
    bool IFilter.TryDerive(IStack stack, [NotNullWhen(true)] out IFilter? derived, [NotNullWhen(false)] out FilterError? failure)
    {
        bool ok = TryDerive(stack, out Filter? concrete, out failure);
        derived = concrete;
        return ok;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Rejects a missing reader without boxing <see cref="PacketIndexReaderView"/>.
    /// <c>ArgumentNullException.ThrowIfNull</c> takes <see cref="object"/> and
    /// would box the struct on the way in.
    /// </summary>
    private static void _ThrowIfNullReader<TIndex>(TIndex index)
        where TIndex : IPacketIndexReader
    {
        if (index is PacketIndexReaderView view)
        {
            ArgumentNullException.ThrowIfNull(view.Source, nameof(index));
            return;
        }

        if (index is null)
        {
            throw new ArgumentNullException(nameof(index));
        }
    }

    private static FilterResult<FilterProgram> _Parse(string expression, FilterCompileOptions? options)
    {
        FilterLexer lexer = new(expression);
        FilterResult<List<Token>> tokens = lexer.Tokenize();
        if (!tokens.TryGetValue(out List<Token>? tokenList))
        {
            return FilterResult.Fail<FilterProgram>(tokens.Error);
        }

        FilterParser parser = new(
            tokenList,
            expression,
            options?.OnFieldNameSpan,
            options?.RegexTimeout ?? FilterBinder.DefaultRegexTimeout);
        return parser.Parse();
    }

    private static FilterResult<Filter> _Bind(
        string expression,
        FilterProgram program,
        IStack stack,
        FilterCompileOptions? options)
    {
        SymbolResolver resolver = new(stack);
        IFilterCodegen codegen = options?.Codegen ?? new EvalTreeCodegen();

        FilterResult<CompiledFilterProgram> compiled = codegen.Compile(program, resolver, options);
        if (!compiled.TryGetValue(out CompiledFilterProgram? compiledProgram))
        {
            return FilterResult.Fail<Filter>(compiled.Error);
        }

        DependencyNode dependencies = DependencyAnalyzer.Analyze(program, resolver);

        // A stateful program needs the cache to keep the documented "re-query never replays the tracker"
        // guarantee: without it a repeated id passes the ascending-order guard (== is not <) and feeds the
        // same sample twice. Stateless programs may opt out for forward-only streaming.
        bool enableMatchCache = (options?.EnableMatchCache ?? true) || compiledProgram.IsStateful;
        return new Filter(
            expression,
            stack,
            program,
            compiledProgram,
            dependencies,
            resolver.FieldOwners,
            enableMatchCache);
    }

    private static bool _ContainsMatches(FilterNode node) => node switch
    {
        StringPredicateNode text => text.Op == StringOp.Matches,
        NotNode not => _ContainsMatches(not.Operand),
        LogicalNode logical => _ContainsMatches(logical.Left) || _ContainsMatches(logical.Right),
        FlankNode { When: FilterNode gate } => _ContainsMatches(gate),
        ScopeNode scope => _ContainsMatches(scope.Body),
        _ => false,
    };

    private bool _Poison(FilterError reason, out bool matched, out FilterError? failure)
    {
        PoisonError = reason;
        matched = false;
        failure = reason;
        return false;
    }

    #endregion
}

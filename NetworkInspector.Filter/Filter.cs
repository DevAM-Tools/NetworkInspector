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
/// from the JIT root are caught only when the filter is stateful (flank state may be dirty);
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

    /// <summary>The filter returned for empty expressions.</summary>
    private static readonly Filter _AlwaysMatch = new();

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
    /// <summary>Null on <see cref="AlwaysMatch"/> — that singleton must stay immutable.</summary>
    private readonly MatchCache? _Cache;

    /// <inheritdoc />
    public FilterError? PoisonError { get; private set; }
    private int _HighestEvaluatedId = -1;

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
        ProtocolId[] fieldOwners)
    {
        Expression = expression;
        Stack = stack;
        _Program = program;
        _Compiled = compiled;
        _Dependencies = dependencies;
        _Context = new FilterEvalContext(fieldOwners);
        _Cache = new();
    }

    #endregion

    #region Static API

    /// <summary>
    /// The filter that accepts every packet. Requires no stack and holds no mutable state.
    /// Safe to share across threads; <see cref="ResetState"/> is a no-op.
    /// </summary>
    public static Filter AlwaysMatch => _AlwaysMatch;

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
            return _AlwaysMatch;
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

        if (PoisonError is FilterError poison)
        {
            matched = false;
            failure = poison;
            return false;
        }

        MatchCache cache = _Cache!;
        if (cache.TryGet(packet.Id, out bool cached))
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

            cache.Store(packet.Id, result);
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

        if (IsAlwaysMatch || _Dependencies is null || IsStateful)
        {
            isCandidate = true;
            return false;
        }

        if (!ReferenceEquals(index.Stack, Stack))
        {
            isCandidate = true;
            return false;
        }

        return CandidateBitmapBuilder.TryIsCandidate(_Dependencies, index, packetId, out isCandidate);
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

    /// <inheritdoc />
    public bool TryDerive(
        IStack stack,
        [NotNullWhen(true)] out Filter? derived,
        [NotNullWhen(false)] out FilterError? failure)
    {
        ArgumentNullException.ThrowIfNull(stack);

        if (IsAlwaysMatch)
        {
            derived = _AlwaysMatch;
            failure = null;
            return true;
        }

        FilterResult<Filter> result = _Bind(Expression, _Program!, stack, null);
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

        FilterParser parser = new(tokenList, expression, options?.OnFieldNameSpan);
        return parser.Parse();
    }

    private static FilterResult<Filter> _Bind(
        string expression,
        FilterProgram program,
        IStack stack,
        FilterCompileOptions? options)
    {
        SymbolResolver resolver = new(stack);
        IFilterCodegen codegen = options?.Codegen ?? new ExpressionTreeCodegen();

        FilterResult<CompiledFilterProgram> compiled = codegen.Compile(program, resolver, options);
        if (!compiled.TryGetValue(out CompiledFilterProgram? compiledProgram))
        {
            return FilterResult.Fail<Filter>(compiled.Error);
        }

        DependencyNode dependencies = DependencyAnalyzer.Analyze(program, resolver);
        return new Filter(expression, stack, program, compiledProgram, dependencies, resolver.FieldOwners);
    }

    private bool _Poison(FilterError reason, out bool matched, out FilterError? failure)
    {
        PoisonError = reason;
        matched = false;
        failure = reason;
        return false;
    }

    #endregion
}

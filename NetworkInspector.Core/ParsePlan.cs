// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// One parse configuration for all consumers of a run: the deepest demanded depth, every observer in demand order, at most one
/// value cache and one index.
/// <para>
/// <b>Why one plan:</b> each packet is parsed once whatever the number of consumers; observers see the same parse, and no
/// consumer forces more work than the deepest one needs (no field tree unless a consumer demands it). <b>Observer order</b>
/// is the demand order: a pipeline lists its filter before its exporter so the exporter's staging can be committed or discarded
/// after the filter verdict. <b>Allocation:</b> the observer array is created once here and reused by every parse.
/// </para>
/// <para>
/// <b>Why in Core:</b> <see cref="ParseOptions"/> is Core, and every pipeline — the command line, sessions, library users —
/// must combine needs the same way; a helper in one pipeline would leave the others to re-invent it.
/// </para>
/// <para>
/// <b>Thread-safety:</b> immutable after creation. The options reference observers that are not thread-safe: use one plan per
/// parse thread and per stack (the observers have no stack, so the plan cannot check that; a cache of another stack is refused).
/// </para>
/// </summary>
public sealed class ParsePlan
{
    #region Configuration and state

    /// <summary>
    /// Number of distinct requested field ids over all watches; -1 when a watch accepts everything. Describes the plan for
    /// <see cref="ToString"/> only; the parse itself reads <see cref="Options"/>.
    /// </summary>
    private readonly int _RequestedFieldCount;

    /// <summary>Number of demands that carry an observer; the length of <c>Options.Observers</c>.</summary>
    private readonly int _ObserverCount;

    /// <summary>Whether <see cref="Options"/> carries a value cache; read by <see cref="ToString"/>.</summary>
    private readonly bool _HasCache;

    /// <summary>Whether <see cref="Options"/> carries a packet index; read by <see cref="ToString"/>.</summary>
    private readonly bool _HasIndex;

    /// <summary>Creates a plan from the combined result of <see cref="TryCreate"/>; nothing is validated here.</summary>
    /// <param name="depth">The combined depth.</param>
    /// <param name="options">The options every parse of the run uses.</param>
    /// <param name="observerCount">Number of observers in <paramref name="options"/>.</param>
    /// <param name="requestedFieldCount">Distinct requested field ids, or -1 when a watch accepts everything.</param>
    /// <param name="hasCache">Whether <paramref name="options"/> carries a value cache.</param>
    /// <param name="hasIndex">Whether <paramref name="options"/> carries a packet index.</param>
    private ParsePlan(ParseDepth depth, ParseOptions options, int observerCount, int requestedFieldCount, bool hasCache, bool hasIndex)
    {
        Depth = depth;
        Options = options;
        _ObserverCount = observerCount;
        _RequestedFieldCount = requestedFieldCount;
        _HasCache = hasCache;
        _HasIndex = hasIndex;
    }

    /// <summary>The combined depth.</summary>
    public ParseDepth Depth { get; }

    /// <summary>Whether frames must be parsed at all.</summary>
    public bool NeedsParse => Depth != ParseDepth.None;

    /// <summary>
    /// Options for every parse of the run: build mode for <see cref="ParseDepth.FieldTree"/>, skip mode otherwise; the cache, the
    /// index, and the observers in demand order. A property returning a struct: copy it once and pass it with <c>in</c>.
    /// </summary>
    public ParseOptions Options { get; }

    #endregion

    #region Combining demands

    /// <summary>
    /// Combines <paramref name="demands"/>. Expected failures (two different caches or indexes, a cache of another stack, a
    /// watch without observer, an undefined depth, one observer instance in two demands) are returned as
    /// <paramref name="error"/>; no plan is created then.
    /// </summary>
    /// <param name="stack">The stack every packet of the run is parsed on.</param>
    /// <param name="demands">Zero or more demands; order defines the observer order.</param>
    /// <param name="plan">The plan on success; null on false.</param>
    /// <param name="error">The failure on false; <see cref="ParsePlanErrorCode.None"/> on success.</param>
    /// <returns><see langword="true"/> when a plan was created.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack"/> is null.</exception>
    public static bool TryCreate(
        IStack stack,
        ReadOnlySpan<ParseDemand> demands,
        [NotNullWhen(true)] out ParsePlan? plan,
        out ParsePlanError error)
    {
        ArgumentNullException.ThrowIfNull(stack);
        plan = null;
        ParseDepth depth = ParseDepth.None;
        ValueCache? cache = null;
        bool recordOnReplay = false;
        PacketIndex? index = null;
        int observerCount = 0;

        // Pass 1: validate and size. A demand with an observer, cache, or index needs at least a packet parse. Every refusal
        // names the demand index or the conflict, so a caller with many consumers knows which one to fix.
        for (int i = 0; i < demands.Length; i++)
        {
            ref readonly ParseDemand demand = ref demands[i];

            // An undefined depth would be taken as the maximum and then parsed as Packet; refuse it with its index.
            if (!Enum.IsDefined(demand.Depth))
            {
                error = new ParsePlanError(
                    ParsePlanErrorCode.InvalidDepth,
                    string.Create(CultureInfo.InvariantCulture, $"Demand {i} has the undefined depth {(byte)demand.Depth}."));
                return false;
            }

            bool hasObserver = demand.Observer.Observer is not null;

            // One observer instance is not thread-safe and keeps per-packet state; two entries would interleave its calls.
            // Quadratic over the demands of one run (a handful), paid once per plan.
            for (int j = 0; hasObserver && j < i; j++)
            {
                if (ReferenceEquals(demands[j].Observer.Observer, demand.Observer.Observer))
                {
                    error = new ParsePlanError(
                        ParsePlanErrorCode.DuplicateObserver,
                        string.Create(CultureInfo.InvariantCulture, $"Demands {j} and {i} share one observer instance."));
                    return false;
                }
            }

            if (!hasObserver && (!demand.Observer.Watch.IsNone || !demand.Observer.DisplayText.IsNone))
            {
                error = new ParsePlanError(
                    ParsePlanErrorCode.NullObserver,
                    string.Create(CultureInfo.InvariantCulture, $"Demand {i} has a watch but no observer."));
                return false;
            }

            if (demand.Cache is { } demandCache)
            {
                // The same instance in two demands is fine (two consumers of one cache); a second instance is a conflict.
                if (!ReferenceEquals(demandCache.Stack, stack))
                {
                    error = new ParsePlanError(
                        ParsePlanErrorCode.CacheStackMismatch,
                        string.Create(CultureInfo.InvariantCulture, $"Demand {i} has a value cache of another stack."));
                    return false;
                }

                if (cache is not null && !ReferenceEquals(cache, demandCache))
                {
                    error = new ParsePlanError(
                        ParsePlanErrorCode.MultipleValueCaches,
                        string.Create(CultureInfo.InvariantCulture, $"A parse fills one value cache; demand {i} brings a different one."));
                    return false;
                }

                cache = demandCache;
                recordOnReplay |= demand.RecordOnReplay;
            }

            if (demand.Index is { } demandIndex)
            {
                // Same rule as for the cache: the parser refuses an index of another stack per packet
                // (ParseFailure.IndexStackMismatch), so the plan refuses it once, before the run starts.
                if (!ReferenceEquals(demandIndex.Stack, stack))
                {
                    error = new ParsePlanError(
                        ParsePlanErrorCode.IndexStackMismatch,
                        string.Create(CultureInfo.InvariantCulture, $"Demand {i} has a packet index of another stack."));
                    return false;
                }

                if (index is not null && !ReferenceEquals(index, demandIndex))
                {
                    error = new ParsePlanError(
                        ParsePlanErrorCode.MultipleIndexes,
                        string.Create(CultureInfo.InvariantCulture, $"A parse fills one packet index; demand {i} brings a different one."));
                    return false;
                }

                index = demandIndex;
            }

            ParseDepth needed = hasObserver || demand.Cache is not null || demand.Index is not null
                ? (ParseDepth)Math.Max((byte)demand.Depth, (byte)ParseDepth.Packet)
                : demand.Depth;
            depth = (ParseDepth)Math.Max((byte)depth, (byte)needed);
            observerCount += hasObserver
                ? 1
                : 0;
        }

        // Pass 2: the observer array, in demand order, created once for every parse of the run. The requested field count
        // is the number of distinct ids over all watches (-1 as soon as one watch accepts everything).
        ParseObserver[] observers = observerCount == 0
            ? []
            : new ParseObserver[observerCount];
        HashSet<int> requested = [];
        bool all = false;
        int next = 0;
        for (int i = 0; i < demands.Length; i++)
        {
            if (demands[i].Observer.Observer is null)
            {
                continue;
            }

            observers[next++] = demands[i].Observer;
            FieldWatch watch = demands[i].Observer.Watch;
            if (watch.IsAll)
            {
                all = true;
            }
            else
            {
                watch.CollectRequested(requested);
            }
        }

        ParseOptions options = new(
            fieldTree: depth == ParseDepth.FieldTree
                ? FieldTreeMode.Build
                : FieldTreeMode.Skip,
            cache: cache,
            recordOnReplay: recordOnReplay,
            index: index,
            observers: observers);
        int requestedCount = all
            ? -1
            : requested.Count;
        plan = new ParsePlan(depth, options, observerCount, requestedCount, cache is not null, index is not null);
        error = default;
        return true;
    }

    #endregion

    #region Description

    /// <summary>
    /// One line for verbose output: <c>none</c>, <c>fields only, no tree (2 fields, 1 observer)</c>,
    /// <c>fields only, no tree (all fields, 1 observer)</c>, <c>packet only, no tree</c> (depth Packet without observer:
    /// packet-level data such as the info text and the frame length, a value cache, or an index), <c>field tree (1 observer)</c>
    /// (<c>field tree</c> when no observer exists), followed by <c>, value cache</c> and <c>, index</c> when present. The field
    /// count is the number of requested fields (before the extraction watch widens them), distinct over all observers.
    /// </summary>
    /// <returns>The description line; allocates a few small strings, so it belongs to verbose output, not to a per-packet path.</returns>
    public override string ToString()
    {
        if (Depth == ParseDepth.None)
        {
            return "none";
        }

        string observers = _ObserverCount == 1
            ? "1 observer"
            : string.Create(CultureInfo.InvariantCulture, $"{_ObserverCount} observers");
        string core;
        if (Depth == ParseDepth.FieldTree)
        {
            core = _ObserverCount == 0
                ? "field tree"
                : string.Create(CultureInfo.InvariantCulture, $"field tree ({observers})");
        }
        else if (_ObserverCount == 0)
        {
            core = "packet only, no tree";
        }
        else
        {
            string fields = _RequestedFieldCount < 0
                ? "all fields"
                : _RequestedFieldCount == 1
                    ? "1 field"
                    : string.Create(CultureInfo.InvariantCulture, $"{_RequestedFieldCount} fields");
            core = string.Create(CultureInfo.InvariantCulture, $"fields only, no tree ({fields}, {observers})");
        }

        string cacheText = _HasCache
            ? ", value cache"
            : string.Empty;
        string indexText = _HasIndex
            ? ", index"
            : string.Empty;
        return string.Concat(core, cacheText, indexText);
    }

    #endregion
}

/// <summary>Why <see cref="ParsePlan.TryCreate"/> refused a set of demands.</summary>
/// <param name="Code">The failure kind.</param>
/// <param name="Message">A message naming the demand index or the conflict.</param>
public readonly record struct ParsePlanError(ParsePlanErrorCode Code, string Message);

/// <summary>Failure kinds of <see cref="ParsePlan.TryCreate"/>.</summary>
public enum ParsePlanErrorCode
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>Two demands bring different value caches (a parse fills one).</summary>
    MultipleValueCaches = 1,

    /// <summary>Two demands bring different packet indexes (a parse fills one).</summary>
    MultipleIndexes = 2,

    /// <summary>A value cache belongs to another stack.</summary>
    CacheStackMismatch = 3,

    /// <summary>A demand has a watch but no observer.</summary>
    NullObserver = 4,

    /// <summary>A demand of depth <see cref="ParseDepth"/> outside the defined values.</summary>
    InvalidDepth = 5,

    /// <summary>Two demands carry the same observer instance.</summary>
    DuplicateObserver = 6,

    /// <summary>A demand's packet index was created for another stack.</summary>
    IndexStackMismatch = 7,
}

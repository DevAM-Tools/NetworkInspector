// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// How much of a packet a consumer needs. Ordered: a <see cref="ParsePlan"/> takes the deepest demand of its consumers,
/// because one parse per packet must serve all of them.
/// </summary>
public enum ParseDepth : byte
{
    /// <summary>Frames only: no parse (frame exporters without filter).</summary>
    None = 0,

    /// <summary>
    /// Parse without field tree: observers, value cache, index, and <see cref="Packet.Info"/> work; no field slab is
    /// written, so this is the cheap depth for consumers that need a few values.
    /// </summary>
    Packet = 1,

    /// <summary>Parse with field tree: consumers that walk or query the tree (hierarchy exports, tree filters).</summary>
    FieldTree = 2,
}

/// <summary>
/// What one consumer needs from the parse of every packet of a run. Combine demands with <see cref="ParsePlan.TryCreate"/>.
/// <para>
/// <b>Why demands exist:</b> before the parse plan the pipeline fixed the parse work (the CLI always built a field tree) and
/// every consumer adapted to it, so an export of two values paid for the tree of every packet. A demand turns this around: each
/// consumer states what it needs, and the plan takes the least work that serves all of them.
/// </para>
/// <para>
/// A demand that carries an observer, a cache, or an index needs at least <see cref="ParseDepth.Packet"/>; the plan raises a
/// lower depth accordingly, so <c>new ParseDemand { Cache = cache }</c> is valid as written.
/// </para>
/// <para><b>Thread-safety:</b> a readonly struct; the observer, cache, and index it references have their own contracts.</para>
/// </summary>
public readonly struct ParseDemand
{
    /// <summary>Requested depth; see the type remarks for the automatic raise.</summary>
    public ParseDepth Depth { get; init; }

    /// <summary>The consumer's observer and its watches; <c>Observer</c> null means no observer.</summary>
    public ParseObserver Observer { get; init; }

    /// <summary>Value cache to fill, or null.</summary>
    public ValueCache? Cache { get; init; }

    /// <summary>
    /// Whether <see cref="Cache"/> is filled on replays too (a session cache fills from replays); ignored without a cache. A
    /// plan ORs this flag over its demands, because the parse has one flag for the one cache.
    /// </summary>
    public bool RecordOnReplay { get; init; }

    /// <summary>Packet index to fill, or null.</summary>
    public PacketIndex? Index { get; init; }

    /// <summary>Frames only: the default value. A run whose consumers all demand this does not parse at all.</summary>
    public static ParseDemand FramesOnly => default;

    /// <summary>A consumer that needs the field tree.</summary>
    public static ParseDemand FieldTree => new() { Depth = ParseDepth.FieldTree };

    /// <summary>A consumer that receives fields through <paramref name="observer"/> during a parse without field tree.</summary>
    /// <param name="observer">Observer and watches; the observer must not be null.</param>
    /// <exception cref="ArgumentException"><paramref name="observer"/> has no observer.</exception>
    public static ParseDemand Fields(in ParseObserver observer)
    {
        if (observer.Observer is null)
        {
            throw new ArgumentException("A fields demand needs an observer.", nameof(observer));
        }

        return new() { Depth = ParseDepth.Packet, Observer = observer };
    }
}

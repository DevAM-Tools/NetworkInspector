// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Immutable snapshot of the session's random-access sources by <see cref="FrameSourceId"/>. Replaced as a whole when a
/// source is added or removed (copy-on-write); a reader takes one snapshot per call.
/// </summary>
/// <remarks>
/// <b>Why not a dictionary.</b> A session resolves the source of every packet it reads, per id or per run, on a hot path,
/// and has one to a few sources. A sorted <see cref="int"/> array searched with <see cref="Array.BinarySearch{T}(T[], T)"/>
/// needs no hashing and no per-lookup state; the table is replaced as a whole on the rare add or remove (copy-on-write), so a
/// reader needs no lock. <c>Session.RandomAccessLookup</c> adds a one-entry cache on top, so a run over one source does
/// one search. Immutable; thread-safe.
/// </remarks>
internal sealed class RandomAccessSourceTable
{
    #region Fields

    private readonly int[] _Ids;
    private readonly IRandomAccessFrameSource[] _Sources;

    #endregion

    #region Constructors

    private RandomAccessSourceTable(int[] ids, IRandomAccessFrameSource[] sources)
    {
        _Ids = ids;
        _Sources = sources;
    }

    #endregion

    #region Properties

    /// <summary>The table without sources.</summary>
    internal static RandomAccessSourceTable Empty { get; } = new([], []);

    /// <summary>Number of sources.</summary>
    internal int Count => _Ids.Length;

    #endregion

    #region Public API

    /// <summary>The source registered under <paramref name="id"/>.</summary>
    internal bool TryGet(FrameSourceId id, [NotNullWhen(true)] out IRandomAccessFrameSource? source)
    {
        int index = Array.BinarySearch(_Ids, id.Value);
        source = index >= 0
            ? _Sources[index]
            : null;
        return source is not null;
    }

    /// <summary>A new table with <paramref name="source"/> under <paramref name="id"/> (replacing an existing entry).</summary>
    internal RandomAccessSourceTable With(FrameSourceId id, IRandomAccessFrameSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        int index = Array.BinarySearch(_Ids, id.Value);
        if (index >= 0)
        {
            IRandomAccessFrameSource[] replaced = (IRandomAccessFrameSource[])_Sources.Clone();
            replaced[index] = source;
            return new((int[])_Ids.Clone(), replaced);
        }

        int insert = ~index;
        int[] ids = new int[_Ids.Length + 1];
        IRandomAccessFrameSource[] sources = new IRandomAccessFrameSource[_Ids.Length + 1];
        Array.Copy(_Ids, 0, ids, 0, insert);
        Array.Copy(_Sources, 0, sources, 0, insert);
        ids[insert] = id.Value;
        sources[insert] = source;
        Array.Copy(_Ids, insert, ids, insert + 1, _Ids.Length - insert);
        Array.Copy(_Sources, insert, sources, insert + 1, _Ids.Length - insert);
        return new(ids, sources);
    }

    /// <summary>A new table without <paramref name="id"/>; this instance when the id is absent.</summary>
    internal RandomAccessSourceTable Without(FrameSourceId id)
    {
        int index = Array.BinarySearch(_Ids, id.Value);
        if (index < 0)
        {
            return this;
        }

        int[] ids = new int[_Ids.Length - 1];
        IRandomAccessFrameSource[] sources = new IRandomAccessFrameSource[_Ids.Length - 1];
        Array.Copy(_Ids, 0, ids, 0, index);
        Array.Copy(_Sources, 0, sources, 0, index);
        Array.Copy(_Ids, index + 1, ids, index, _Ids.Length - index - 1);
        Array.Copy(_Sources, index + 1, sources, index, _Ids.Length - index - 1);
        return new(ids, sources);
    }

    #endregion
}

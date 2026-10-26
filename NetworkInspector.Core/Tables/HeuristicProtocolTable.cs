// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tables;

/// <summary>
/// Heuristic protocol dispatch table that tests data against registered parsers.
/// First match wins for single-match queries.
/// <para>
/// Order matters: entries are tested in registration order, so an earlier registration wins over a later one when both
/// accept the same data. Lifecycle and thread safety: like <see cref="ProtocolTable"/>, the table is filled during stack
/// building (single-threaded) and only read afterwards; it has no synchronization, concurrent readers are safe because
/// nothing mutates it after building, and registering while another thread reads is not supported. The parsers' own
/// <c>Test</c> implementations must be safe to call concurrently.
/// </para>
/// </summary>
internal sealed class HeuristicProtocolTable
{
    #region Fields

    /// <summary>Registered parsers in registration order; the order is the test order.</summary>
    private readonly List<HeuristicParserEntry> _Entries = [];

    #endregion

    #region Constructors

    /// <summary>Creates a heuristic table from its metadata.</summary>
    /// <param name="info">The table metadata.</param>
    internal HeuristicProtocolTable(HeuristicProtocolTableInfo info)
    {
        Info = info;
    }

    #endregion

    #region Properties

    /// <summary>The table metadata.</summary>
    internal HeuristicProtocolTableInfo Info { get; }

    /// <summary>The table ID.</summary>
    internal HeuristicProtocolTableId Id => Info.Id;

    /// <summary>The machine-readable table name.</summary>
    internal string Name => Info.Name;

    /// <summary>The display name of the table.</summary>
    internal string UiName => Info.UiName;

    /// <summary>The description of the table.</summary>
    internal string? Description => Info.Description;

    /// <summary>Number of registered parsers.</summary>
    internal int Count => _Entries.Count;

    /// <summary>Whether no parser is registered.</summary>
    internal bool IsEmpty => _Entries.Count == 0;

    #endregion

    #region Internal API

    /// <summary>Appends a parser; it is tested after every parser registered before it. Build phase only.</summary>
    /// <param name="entry">The parser entry; not validated here (the builder creates it from a non-null parser).</param>
    internal void AddEntry(HeuristicParserEntry entry) => _Entries.Add(entry);

    /// <summary>The registered entries in test order (a live view, not a copy).</summary>
    internal IReadOnlyList<HeuristicParserEntry> Entries => _Entries;

    /// <summary>Finds an entry by parser name.</summary>
    internal HeuristicParserEntry? FindByName(string name)
    {
        for (int i = 0; i < _Entries.Count; i++)
        {
            if (string.Equals(_Entries[i].Name, name, StringComparison.Ordinal))
            {
                return _Entries[i];
            }
        }
        return null;
    }

    /// <summary>
    /// Tests data against all parsers, returns first match. Runs per packet for protocols that detect their payload
    /// heuristically, hence an indexed loop over the list (no enumerator allocation) and no allocation at all.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ProtocolId? TryMatch(ReadOnlyMemory<byte> data)
    {
        for (int i = 0; i < _Entries.Count; i++)
        {
            if (_Entries[i].Test(data))
            {
                return _Entries[i].ProtocolId;
            }
        }
        return null;
    }

    /// <summary>Tests data against all parsers, returns first match with name.</summary>
    internal (ProtocolId Id, string Name)? TryMatchWithName(ReadOnlyMemory<byte> data)
    {
        for (int i = 0; i < _Entries.Count; i++)
        {
            if (_Entries[i].Test(data))
            {
                return (_Entries[i].ProtocolId, _Entries[i].Name);
            }
        }
        return null;
    }

    /// <summary>
    /// Tests data against all parsers, returns all matches in test order. Allocates the result list, so it is for
    /// diagnostics, not for the per-packet path (use <see cref="TryMatch"/> there).
    /// </summary>
    internal List<ProtocolId> TryMatchAll(ReadOnlyMemory<byte> data)
    {
        List<ProtocolId> results = new(1);
        for (int i = 0; i < _Entries.Count; i++)
        {
            if (_Entries[i].Test(data))
            {
                results.Add(_Entries[i].ProtocolId);
            }
        }
        return results;
    }

    #endregion
}

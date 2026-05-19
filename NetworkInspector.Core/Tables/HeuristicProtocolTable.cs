// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Tables;

/// <summary>
/// Heuristic protocol dispatch table that tests data against registered parsers.
/// First match wins for single-match queries.
/// </summary>
internal sealed class HeuristicProtocolTable
{
    #region Fields

    private readonly HeuristicProtocolTableInfo _Info;
    private readonly List<HeuristicParserEntry> _Entries = [];

    #endregion

    #region Constructors

    /// <summary>Creates a heuristic table from its metadata.</summary>
    internal HeuristicProtocolTable(HeuristicProtocolTableInfo info)
    {
        _Info = info;
    }

    #endregion

    #region Properties

    internal HeuristicProtocolTableInfo Info => _Info;
    internal HeuristicProtocolTableId Id => _Info.Id;
    internal string Name => _Info.Name;
    internal string UiName => _Info.UiName;
    internal string? Description => _Info.Description;
    internal int Count => _Entries.Count;
    internal bool IsEmpty => _Entries.Count == 0;

    #endregion

    #region Internal API

    internal void AddEntry(HeuristicParserEntry entry) => _Entries.Add(entry);

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

    /// <summary>Tests data against all parsers, returns first match.</summary>
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

    /// <summary>Tests data against all parsers, returns all matches.</summary>
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
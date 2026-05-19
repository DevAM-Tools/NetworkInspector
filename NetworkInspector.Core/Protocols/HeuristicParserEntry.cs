// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// Entry in a heuristic protocol table wrapping an <see cref="IHeuristicParser"/>.
/// </summary>
internal sealed class HeuristicParserEntry(IHeuristicParser parser)
{
    #region Properties

    internal IHeuristicParser Parser { get; } = parser;
    internal ProtocolId ProtocolId => Parser.ProtocolId;
    internal string Name => Parser.Name;
    internal string UiName => Parser.UiName;
    internal string? Description => Parser.Description;

    #endregion

    #region Methods

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Test(ReadOnlyMemory<byte> data) => Parser.Test(data);

    #endregion
}
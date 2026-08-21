// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// Entry in a heuristic protocol table wrapping an <see cref="IHeuristicParser"/>.
/// <para><b>Thread-safety:</b> Delegates to <see cref="Parser"/>; not thread-safe unless that instance is.</para>
/// </summary>
internal sealed class HeuristicParserEntry
{
    #region Constructors

    internal HeuristicParserEntry(IHeuristicParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        Parser = parser;
    }

    #endregion

    #region Properties

    /// <summary>The wrapped heuristic parser.</summary>
    internal IHeuristicParser Parser { get; }

    /// <summary>The protocol id of the wrapped parser.</summary>
    internal ProtocolId ProtocolId => Parser.ProtocolId;

    /// <summary>The machine-readable name of the wrapped parser.</summary>
    internal string Name => Parser.Name;

    /// <summary>The display name of the wrapped parser.</summary>
    internal string UiName => Parser.UiName;

    /// <summary>The description of the wrapped parser.</summary>
    internal string? Description => Parser.Description;

    #endregion

    #region Methods

    /// <inheritdoc cref="IHeuristicParser.Test"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Test(ReadOnlyMemory<byte> data) => Parser.Test(data);

    #endregion
}

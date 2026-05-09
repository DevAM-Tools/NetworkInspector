// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// Contract for heuristic protocol parsers that identify protocols by inspecting data.
/// </summary>
public interface IHeuristicParser
{
    #region Methods

    /// <summary>Tests whether this parser can handle the given data.</summary>
    bool Test(ReadOnlyMemory<byte> data);

    #endregion

    #region Properties

    /// <summary>The protocol this parser identifies.</summary>
    ProtocolId ProtocolId
    {
        get;
    }

    /// <summary>Machine-readable parser name.</summary>
    string Name
    {
        get;
    }

    /// <summary>Human-readable display name.</summary>
    string UiName
    {
        get;
    }

    /// <summary>Optional description.</summary>
    string? Description => null;

    #endregion
}

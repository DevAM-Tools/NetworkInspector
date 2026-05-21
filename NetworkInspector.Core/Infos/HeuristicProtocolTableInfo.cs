// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>Metadata for a registered heuristic protocol dispatch table.</summary>
public sealed class HeuristicProtocolTableInfo(
    HeuristicProtocolTableId id,
    string name,
    string uiName,
    string? description,
    ProtocolId owningProtocolId)
{
    #region Properties

    /// <summary>Unique heuristic table identifier.</summary>
    public HeuristicProtocolTableId Id { get; } = id;

    /// <summary>Machine-readable table name (e.g., "tcp.heuristic").</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable display name.</summary>
    public string UiName { get; } = uiName;

    /// <summary>Optional description text.</summary>
    public string? Description { get; } = description;

    /// <summary>Protocol that owns this heuristic table.</summary>
    public ProtocolId OwningProtocolId { get; } = owningProtocolId;

    #endregion
}

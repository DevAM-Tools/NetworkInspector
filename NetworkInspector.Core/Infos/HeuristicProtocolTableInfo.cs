// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>Metadata for a registered heuristic protocol dispatch table. Immutable record; safe to share between threads.</summary>
/// <param name="Id">Unique heuristic table identifier.</param>
/// <param name="Name">Machine-readable table name.</param>
/// <param name="UiName">Human-readable display name.</param>
/// <param name="Description">Optional description text.</param>
/// <param name="OwningProtocolId">Protocol that owns this table.</param>
public sealed record HeuristicProtocolTableInfo(
    HeuristicProtocolTableId Id,
    string Name,
    string UiName,
    string? Description,
    ProtocolId OwningProtocolId);

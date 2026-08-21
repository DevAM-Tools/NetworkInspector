// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>Metadata for a registered heuristic protocol dispatch table.</summary>
public sealed record HeuristicProtocolTableInfo(
    HeuristicProtocolTableId Id,
    string Name,
    string UiName,
    string? Description,
    ProtocolId OwningProtocolId);

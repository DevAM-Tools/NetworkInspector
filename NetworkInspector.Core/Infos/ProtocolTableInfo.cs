// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>Metadata for a registered protocol dispatch table.</summary>
public sealed record ProtocolTableInfo(
    ProtocolTableId Id,
    string Name,
    string UiName,
    ProtocolTableKeyType KeyType,
    string? Description);

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>Metadata for a registered protocol dispatch table. Immutable record; safe to share between threads.</summary>
/// <param name="Id">Unique protocol table identifier.</param>
/// <param name="Name">Machine-readable table name.</param>
/// <param name="UiName">Human-readable display name.</param>
/// <param name="KeyType">The kind of key the table dispatches on (number, string, bytes, bool or catch-all).</param>
/// <param name="Description">Optional description text.</param>
public sealed record ProtocolTableInfo(
    ProtocolTableId Id,
    string Name,
    string UiName,
    ProtocolTableKeyType KeyType,
    string? Description);

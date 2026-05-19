// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Infos;

/// <summary>Metadata for a registered protocol dispatch table.</summary>
public sealed class ProtocolTableInfo(
    ProtocolTableId id,
    string name,
    string uiName,
    ProtocolTableKeyType keyType,
    string? description)
{
    #region Properties

    /// <summary>Unique table identifier.</summary>
    public ProtocolTableId Id { get; } = id;

    /// <summary>Machine-readable table name (e.g., "eth.type", "ip.proto").</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable display name (e.g., "Ethernet Type").</summary>
    public string UiName { get; } = uiName;

    /// <summary>The key type used for protocol dispatch.</summary>
    public ProtocolTableKeyType KeyType { get; } = keyType;

    /// <summary>Optional description text.</summary>
    public string? Description { get; } = description;

    #endregion
}
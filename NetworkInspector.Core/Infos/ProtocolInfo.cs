// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Infos;

/// <summary>Metadata for a registered protocol.</summary>
public sealed class ProtocolInfo(ProtocolId id, string name, string uiName, string? description)
{
    #region Properties

    /// <summary>Unique protocol identifier.</summary>
    public ProtocolId Id { get; } = id;

    /// <summary>Machine-readable protocol name (e.g., "eth", "ip").</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable display name (e.g., "Ethernet", "Internet Protocol").</summary>
    public string UiName { get; } = uiName;

    /// <summary>Optional description text.</summary>
    public string? Description { get; } = description;

    #endregion
}

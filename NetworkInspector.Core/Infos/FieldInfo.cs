// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Infos;

/// <summary>Metadata for a registered field definition.</summary>
public sealed class FieldInfo
{
    #region Constructors

    /// <summary>Creates field metadata during stack registration.</summary>
    internal FieldInfo(
        FieldId id,
        ProtocolId protocolId,
        string name,
        string uiName,
        FieldType fieldType,
        string? description,
        IndexGroupId? indexGroup)
    {
        Id = id;
        ProtocolId = protocolId;
        Name = name;
        UiName = uiName;
        FieldType = fieldType;
        Description = description;
        IndexGroup = indexGroup;
    }

    #endregion

    #region Properties

    /// <summary>Unique field identifier.</summary>
    public FieldId Id
    {
        get;
    }

    /// <summary>Protocol that owns this field.</summary>
    public ProtocolId ProtocolId
    {
        get;
    }

    /// <summary>Machine-readable field name (e.g., "ip.src").</summary>
    public string Name
    {
        get;
    }

    /// <summary>Human-readable display name (e.g., "Source Address").</summary>
    public string UiName
    {
        get;
    }

    /// <summary>The data type of values stored in this field.</summary>
    public FieldType FieldType
    {
        get;
    }

    /// <summary>Optional description text.</summary>
    public string? Description
    {
        get;
    }

    /// <summary>Optional index group for cross-packet presence indexing.</summary>
    public IndexGroupId? IndexGroup
    {
        get; internal set;
    }

    #endregion
}
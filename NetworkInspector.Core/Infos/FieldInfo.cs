// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>
/// Metadata for a registered field definition.
/// <para>
/// <b>Thread-safety:</b> Effectively immutable. Only <see cref="IndexGroup"/> is assigned after construction, by
/// <see cref="StackBuilder"/> during registration (<c>RegisterFieldInGroup</c>); once the stack is built nothing
/// writes to the instance, so concurrent reads need no synchronization.
/// </para>
/// </summary>
public sealed class FieldInfo
{
    #region Constructors

    /// <summary>Creates field metadata during stack registration.</summary>
    /// <param name="id">The unique identifier assigned by the builder.</param>
    /// <param name="protocolId">The protocol that owns this field.</param>
    /// <param name="name">Machine-readable field name.</param>
    /// <param name="uiName">Human-readable display name.</param>
    /// <param name="fieldType">The data type of values stored in this field.</param>
    /// <param name="description">Optional description text.</param>
    /// <param name="indexGroup">The index group, or <see langword="null"/> to assign it later through <see cref="IndexGroup"/>.</param>
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

    /// <summary>
    /// Human-readable display name (e.g., "Source Address"); never empty, because registration refuses an empty UI name or one
    /// with control characters (<see cref="InvalidUiNameRegistrationException"/>). It is the label part of the default display
    /// line of <see cref="Field"/> and <see cref="MutField"/>; a field id that the stack does not know has no
    /// <see cref="FieldInfo"/> and so no label.
    /// </summary>
    /// <remarks>
    /// Sync: EXPORTER_GUIDE § Rebuilding the display line (a reader of an export gets the label from the catalog as <c>ui_name</c>).
    /// </remarks>
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

    /// <summary>
    /// Optional index group for cross-packet presence indexing. <see langword="null"/> when the field was registered
    /// without a group. The setter is internal because the group is only known after the field itself is registered
    /// (the builder creates the field first, then resolves or creates the named group and assigns it here).
    /// </summary>
    public IndexGroupId? IndexGroup
    {
        get; internal set;
    }

    #endregion
}

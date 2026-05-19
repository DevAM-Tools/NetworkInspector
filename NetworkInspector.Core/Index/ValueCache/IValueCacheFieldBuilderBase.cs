// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Non-generic base interface for per-field builders, allowing the orchestrator
/// to call append/build without knowing the concrete value type.
/// </summary>
internal interface IValueCacheFieldBuilderBase
{
    #region Properties

    /// <summary>The field ID this builder records values for.</summary>
    FieldId FieldId
    {
        get;
    }

    /// <summary>The original field type.</summary>
    FieldType OriginalFieldType
    {
        get;
    }

    /// <summary>The storage mode.</summary>
    ValueCacheStorageMode StorageMode
    {
        get;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Appends a value extracted from a <see cref="FieldValueData"/>.
    /// Performs type-specific conversion and compact-mode clamping internally.
    /// </summary>
    void AppendFromFieldValue(long timestamp, int packetId, in FieldValueData value);

    /// <summary>Marks that a duplicate value was dropped for the current packet.</summary>
    void MarkDuplicateDrop();

    /// <summary>Marks that an evicted packet was encountered during retroactive build.</summary>
    void MarkEvictedPacket();

    /// <summary>Finalizes the builder and returns an immutable <see cref="ValueCacheSeries"/>.</summary>
    ValueCacheSeries BuildSeries();

    #endregion
}
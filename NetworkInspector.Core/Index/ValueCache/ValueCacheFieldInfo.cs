// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Diagnostic information about a single cached field series.
/// Used for profiling and monitoring.
/// </summary>
public readonly struct ValueCacheFieldInfo
{
    #region Properties

    /// <summary>The field identifier.</summary>
    public FieldId FieldId
    {
        get; init;
    }

    /// <summary>The original field type before any compact conversion.</summary>
    public FieldType OriginalFieldType
    {
        get; init;
    }

    /// <summary>The storage mode used for this series.</summary>
    public ValueCacheStorageMode StorageMode
    {
        get; init;
    }

    /// <summary>Number of entries (data points) in the series.</summary>
    public int EntryCount
    {
        get; init;
    }

    /// <summary>Estimated memory usage in bytes.</summary>
    public long MemoryUsage
    {
        get; init;
    }

    /// <summary>Completeness flags for this series.</summary>
    public ValueCacheCompleteness Completeness
    {
        get; init;
    }

    #endregion
}

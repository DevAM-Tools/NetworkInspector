// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Describes a single field that should be value-cached from the start of parsing.
/// Created by parsing the value-cache setting string before the <see cref="PacketIndex"/> is constructed.
/// </summary>
/// <param name="FieldId">The resolved field identifier.</param>
/// <param name="FieldType">The field's data type (used for compatibility validation).</param>
/// <param name="StorageMode">How values are stored in the cache (default: <see cref="ValueCacheStorageMode.Native"/>).</param>
public readonly record struct ValueCacheFieldConfig(
    FieldId FieldId,
    FieldType FieldType,
    ValueCacheStorageMode StorageMode);

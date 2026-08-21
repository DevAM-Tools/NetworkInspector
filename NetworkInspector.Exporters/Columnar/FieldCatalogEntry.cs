// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// Metadata for one field column in a <see cref="ColumnarPacketBatch"/>, describing how the
/// column should be named and typed in a columnar sink (Parquet file, DuckDB table, PBF
/// field column). Derived from Core <see cref="FieldInfo"/> plus a stable per-field
/// <see cref="TableName"/> for analytics sinks.
/// <para>
/// <see cref="FieldIdValue"/> and <see cref="ProtocolIdValue"/> are bare <see cref="int"/>s
/// (Core <c>FieldId.Value</c> / <c>ProtocolId.Value</c>) at the analytics boundary so sink
/// schemas remain primitive integers without wrapper types.
/// </para>
/// </summary>
/// <param name="FieldIdValue">The registered field identifier.</param>
/// <param name="Name">Machine-readable field name (e.g. "ip.src").</param>
/// <param name="UiName">Human-readable display name (e.g. "Source Address").</param>
/// <param name="FieldType">The data type of values stored in this field.</param>
/// <param name="ProtocolIdValue">Identifier of the protocol that owns this field, or -1 if unknown.</param>
/// <param name="TableName">Stable per-field table/column name, formatted as <c>field_{FieldIdValue}</c>.</param>
internal sealed record FieldCatalogEntry(
    int FieldIdValue,
    string Name,
    string UiName,
    FieldType FieldType,
    int ProtocolIdValue,
    string TableName);

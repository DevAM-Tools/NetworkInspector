// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// One row of the flattened field-tree topology for a single packet.
/// <para>
/// Node identifiers are assigned by a depth-first traversal of the packet's field tree,
/// scoped to a single packet (they restart at 0 for every packet). Fields with no parent
/// (direct children of the packet's invisible root field) use <see cref="ParentNodeId"/>
/// equal to <c>-1</c>. This flat parent-pointer representation lets columnar sinks (Parquet,
/// DuckDB, PBF) reconstruct the field hierarchy with a simple self-join instead of nested
/// containers, and lets field value rows reference their originating node via
/// <see cref="NodeId"/> to disambiguate repeated fields within the same packet.
/// </para>
/// <para>
/// Identifiers are bare <see cref="int"/> values at the analytics boundary (not Core
/// <c>PacketId</c> / <c>FieldId</c> wrappers) so Parquet/DuckDB/PBF column schemas stay
/// primitive INT32 without per-row boxing or custom converters.
/// </para>
/// </summary>
/// <param name="PacketId">
/// Identifier of the packet this node belongs to — same numeric range as Core
/// <c>PacketId.Value</c> (<see cref="int"/>, bounded by array index limits).
/// </param>
/// <param name="NodeId">Depth-first node identifier, unique within <paramref name="PacketId"/>.</param>
/// <param name="FieldId">Registered field identifier (Core <c>FieldId.Value</c> as bare int).</param>
/// <param name="ParentNodeId">The parent node's <see cref="NodeId"/>, or <c>-1</c> for top-level fields.</param>
internal readonly record struct TopologyNode(int PacketId, int NodeId, int FieldId, int ParentNodeId);

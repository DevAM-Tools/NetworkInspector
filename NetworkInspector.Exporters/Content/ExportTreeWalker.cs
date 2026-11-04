// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Content;

/// <summary>
/// Walks a packet's field tree in depth-first pre-order with <c>materialize: true</c> (lazy containers expand) and feeds the
/// values to the collector of a flat shape. Core offers no replay of a tree into an observer; the hierarchy layouts walk the tree
/// themselves because they need node ids and parents.
/// </summary>
/// <remarks>
/// Recursion depth equals the nesting depth of the tree, which the Core per-packet field limit bounds.
/// </remarks>
/// <seealso cref="ExportFieldCollector"/>
internal static class ExportTreeWalker
{
    #region Value walk

    /// <summary>Feeds every node's value to the collector (flat shapes fed from a tree packet).</summary>
    /// <param name="packet">A packet with a field tree.</param>
    /// <param name="collector">The collector that applies the selection and the occurrence policy.</param>
    internal static void WalkValues(Packet packet, ExportFieldCollector collector)
    {
        foreach (Field child in packet.RootField().Children(materialize: true))
        {
            _WalkValues(child, collector);
        }
    }

    /// <summary>Feeds one node and then its children to the collector, recursively in pre-order.</summary>
    /// <param name="field">The node to feed.</param>
    /// <param name="collector">The collector that applies the selection and the occurrence policy.</param>
    private static void _WalkValues(Field field, ExportFieldCollector collector)
    {
        // One value read per node (the slot read copies a 32-byte struct). Perf: the custom text is read only when the export
        // writes it — reading it can complete a deferred text — and then through the field, so a deferred text caches in the
        // slab slot.
        FieldValue value = field.Value;
        LazyString text = default;
        if (collector.WantsCustomText)
        {
            text = field.CustomText;
        }

        collector.Accept(field.FieldId.Value, in value, text);
        foreach (Field child in field.Children(materialize: true))
        {
            _WalkValues(child, collector);
        }
    }

    #endregion
}

// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Exporters.Pbf.Columnar;

/// <summary>
/// Encodes a hierarchical field tree into flat topology arrays for columnar storage.
/// The topology is represented as parallel arrays of field IDs and child counts,
/// enabling reconstruction of the tree structure from column data.
/// </summary>
internal static class TopologyEncoder
{
    /// <summary>
    /// Encodes the field tree of a packet into flat topology arrays.
    /// </summary>
    /// <param name="rootField">The root field of the packet.</param>
    /// <param name="fieldIds">Output list of field IDs in depth-first order.</param>
    /// <param name="childCounts">Output list of child counts for each field.</param>
    internal static void Encode(Field rootField, List<int> fieldIds, List<int> childCounts)
    {
        if (!rootField.HasChildren)
        {
            return;
        }

        foreach (Field child in rootField.Children())
        {
            EncodeField(child, fieldIds, childCounts);
        }
    }

    /// <summary>Recursively encodes a single field and its children.</summary>
    private static void EncodeField(Field field, List<int> fieldIds, List<int> childCounts)
    {
        fieldIds.Add(field.FieldId.Value);

        int count = 0;
        if (field.HasChildren)
        {
            foreach (Field child in field.Children())
            {
                count++;
                EncodeField(child, fieldIds, childCounts);
            }
        }
        childCounts.Add(count);
    }
}

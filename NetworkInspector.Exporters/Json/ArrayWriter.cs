// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Writes packets as flat JSON objects (each packet on one line, no indentation) with full
/// human-readable keys and no deduplication, so every object is self-contained and can be read without the preceding
/// packets. <see cref="JsonExporter"/> places the objects in one JSON array separated by commas and blank lines, so the
/// file as a whole is a single JSON document, not NDJSON; each object on its own line is easy to grep and diff.
/// <para>
/// Packet keys: <c>id</c>, <c>timestamp</c> (nanoseconds since the Unix epoch; values exceed 2^53, so consumers that
/// parse JSON numbers as IEEE doubles lose precision), <c>info</c> (omitted when empty), <c>fields</c> (omitted when the
/// packet has no fields).
/// Field keys: <c>field_id</c>, <c>name</c>, <c>ui_name</c>, <c>type</c> (these three only for registered fields),
/// <c>value</c> (omitted for fields without a value),<c>custom_representation</c>, <c>custom_text</c>, <c>children</c>.
/// </para>
/// <para>
/// <b>Thread safety:</b> Stateless; safe to call from multiple threads as long as each call has its own buffer.
/// </para>
/// </summary>
internal static class ArrayWriter
{
    /// <summary>
    /// Writes a single packet as a flat (non-indented) JSON object.
    /// </summary>
    /// <param name="packet">The packet to serialize.</param>
    /// <param name="buffer">Target output buffer.</param>
    /// <exception cref="InvalidOperationException">The packet has no field tree (a skip packet).</exception>
    internal static void WritePacket(Packet packet, ref PooledBuffer buffer)
    {
        // A skip packet has no parsed field tree to serialize: fail loudly instead of writing an empty object.
        if (!packet.HasFieldTree)
        {
            throw new InvalidOperationException("Skip packets cannot be exported (HasFieldTree is false).");
        }

        buffer.WriteByte((byte)'{');

        // "id": value
        buffer.Write("\"id\":"u8);
        JsonHelpers.WriteI64(ref buffer, packet.Id.Value);

        // "timestamp": value
        buffer.Write(",\"timestamp\":"u8);
        JsonHelpers.WriteI64(ref buffer, packet.Timestamp.AsNanos);

        // "info": "value"
        string info = packet.Info;
        if (info.Length > 0)
        {
            buffer.Write(",\"info\":"u8);
            JsonHelpers.WriteJsonString(ref buffer, info);
        }

        // "fields": [...] — materialize: true so lazy protocol trees are exported.
        Field root = packet.RootField();
        if (root.HasChildren(materialize: true))
        {
            buffer.Write(",\"fields\":["u8);
            bool first = true;
            foreach (Field child in root.Children(materialize: true))
            {
                if (!first)
                {
                    buffer.WriteByte((byte)',');
                }
                first = false;
                _WriteFieldFlat(child, ref buffer);
            }
            buffer.WriteByte((byte)']');
        }

        buffer.WriteByte((byte)'}');
    }

    /// <summary>
    /// Writes a single field as a flat JSON object (no indentation), then its children recursively (recursion depth
    /// equals the nesting depth of the field tree). Children are expanded (<c>materialize: true</c>) so lazily built
    /// subtrees are exported too.
    /// </summary>
    private static void _WriteFieldFlat(Field field, ref PooledBuffer buffer)
    {
        buffer.WriteByte((byte)'{');

        // "field_id": value
        buffer.Write("\"field_id\":"u8);
        JsonHelpers.WriteI64(ref buffer, field.FieldId.Value);

        // "name", "ui_name", "type"
        FieldInfo? info = field.FieldInfo;
        if (info is not null)
        {
            buffer.Write(",\"name\":"u8);
            JsonHelpers.WriteJsonString(ref buffer, info.Name);

            buffer.Write(",\"ui_name\":"u8);
            JsonHelpers.WriteJsonString(ref buffer, info.UiName);

            buffer.Write(",\"type\":"u8);
            JsonHelpers.WriteFieldTypeName(ref buffer, info.FieldType);
        }

        // "value": ...
        FieldValue value = field.Value;
        if (value.Type != FieldType.None)
        {
            buffer.Write(",\"value\":"u8);
            JsonHelpers.WriteFieldValue(ref buffer, value);
        }

        // "custom_representation": "..."
        if (!value.CustomRepresentation.IsNull)
        {
            buffer.Write(",\"custom_representation\":"u8);
            JsonHelpers.WriteJsonString(ref buffer, value.CustomRepresentation.AsString);
        }

        // "custom_text": "..."
        LazyString customText = field.CustomText;
        if (!customText.IsNull)
        {
            buffer.Write(",\"custom_text\":"u8);
            JsonHelpers.WriteJsonString(ref buffer, customText.AsString);
        }

        // "children": [...] — materialize: true so nested lazy fields are exported.
        if (field.HasChildren(materialize: true))
        {
            buffer.Write(",\"children\":["u8);
            bool firstChild = true;
            foreach (Field child in field.Children(materialize: true))
            {
                if (!firstChild)
                {
                    buffer.WriteByte((byte)',');
                }
                firstChild = false;
                _WriteFieldFlat(child, ref buffer);
            }
            buffer.WriteByte((byte)']');
        }

        buffer.WriteByte((byte)'}');
    }
}

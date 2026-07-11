// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Writes packets as flat JSON objects (one per line, no indentation) with full
/// human-readable keys. Designed for line-oriented consumers (NDJSON / JSON Lines).
/// <para>
/// Packet keys: <c>id</c>, <c>timestamp</c>, <c>info</c>, <c>fields</c>.
/// Field keys: <c>field_id</c>, <c>name</c>, <c>ui_name</c>, <c>type</c>,
/// <c>value</c>, <c>custom_representation</c>, <c>custom_text</c>, <c>children</c>.
/// </para>
/// </summary>
internal static class ArrayWriter
{
    /// <summary>
    /// Writes a single packet as a flat (non-indented) JSON object.
    /// </summary>
    /// <param name="packet">The packet to serialize.</param>
    /// <param name="buffer">Target output buffer.</param>
    internal static void WritePacket(Packet packet, ref PooledBuffer buffer)
    {
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

        // "fields": [...]
        Field root = packet.RootField();
        if (root.HasChildren)
        {
            buffer.Write(",\"fields\":["u8);
            bool first = true;
            foreach (Field child in root.Children())
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

    /// <summary>Writes a single field as a flat JSON object (no indentation).</summary>
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
            JsonHelpers.WriteU64(ref buffer, (ulong)info.FieldType);
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

        // "children": [...]
        if (field.HasChildren)
        {
            buffer.Write(",\"children\":["u8);
            bool firstChild = true;
            foreach (Field child in field.Children())
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

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Writes packets in pretty-printed JSON format with full human-readable keys
/// and 2-space indentation. No deduplication or same-as-previous optimization.
/// <para>
/// Packet keys: <c>id</c>, <c>timestamp</c>, <c>info</c>, <c>fields</c>.
/// Field keys: <c>field_id</c>, <c>name</c>, <c>ui_name</c>, <c>type</c>,
/// <c>value</c>, <c>custom_representation</c>, <c>custom_text</c>, <c>children</c>.
/// </para>
/// </summary>
internal static class PrettyWriter
{
    /// <summary>Newline separator.</summary>
    private static ReadOnlySpan<byte> Nl => "\n"u8;

    /// <summary>
    /// Writes a single packet in pretty-printed JSON format with 2-space indentation.
    /// </summary>
    /// <param name="packet">The packet to serialize.</param>
    /// <param name="buffer">Target output buffer.</param>
    internal static void WritePacket(Packet packet, ref PooledBuffer buffer)
    {
        // Opening brace at indent level 1 (inside the array)
        buffer.Write("  {\n"u8);

        // "id": value
        WriteIndent(ref buffer, 2);
        buffer.Write("\"id\": "u8);
        JsonHelpers.WriteI64(ref buffer, packet.Id.Value);

        // "timestamp": value
        buffer.Write(",\n"u8);
        WriteIndent(ref buffer, 2);
        buffer.Write("\"timestamp\": "u8);
        JsonHelpers.WriteI64(ref buffer, packet.Timestamp.AsNanos);

        // "info": "value"
        string info = packet.Info;
        if (info.Length > 0)
        {
            buffer.Write(",\n"u8);
            WriteIndent(ref buffer, 2);
            buffer.Write("\"info\": "u8);
            JsonHelpers.WriteJsonString(ref buffer, info);
        }

        // "fields": [...]
        Field root = packet.RootField();
        if (root.HasChildren)
        {
            buffer.Write(",\n"u8);
            WriteIndent(ref buffer, 2);
            buffer.Write("\"fields\": [\n"u8);

            bool first = true;
            foreach (Field child in root.Children())
            {
                if (!first)
                {
                    buffer.Write(",\n"u8);
                }
                first = false;
                WriteFieldPretty(child, ref buffer, 3);
            }
            buffer.Write(Nl);
            WriteIndent(ref buffer, 2);
            buffer.WriteByte((byte)']');
        }

        buffer.Write(Nl);
        buffer.Write("  }"u8);
    }

    /// <summary>Writes a single field in pretty format at the given indentation depth.</summary>
    private static void WriteFieldPretty(Field field, ref PooledBuffer buffer, int depth)
    {
        WriteIndent(ref buffer, depth);
        buffer.Write("{\n"u8);

        // "field_id": value
        WriteIndent(ref buffer, depth + 1);
        buffer.Write("\"field_id\": "u8);
        JsonHelpers.WriteI64(ref buffer, field.FieldId.Value);

        // "name" and "ui_name"
        FieldInfo? info = field.FieldInfo;
        if (info is not null)
        {
            buffer.Write(",\n"u8);
            WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"name\": "u8);
            JsonHelpers.WriteJsonString(ref buffer, info.Name);

            buffer.Write(",\n"u8);
            WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"ui_name\": "u8);
            JsonHelpers.WriteJsonString(ref buffer, info.UiName);

            buffer.Write(",\n"u8);
            WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"type\": "u8);
            JsonHelpers.WriteU64(ref buffer, (ulong)info.FieldType);
        }

        // "value": ...
        FieldValue value = field.Value;
        if (value.Type != FieldType.None)
        {
            buffer.Write(",\n"u8);
            WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"value\": "u8);
            JsonHelpers.WriteFieldValue(ref buffer, value);
        }

        // "custom_representation": "..."
        if (!value.CustomRepresentation.IsNull)
        {
            buffer.Write(",\n"u8);
            WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"custom_representation\": "u8);
            JsonHelpers.WriteJsonString(ref buffer, value.CustomRepresentation.AsString);
        }

        // "custom_text": "..."
        LazyString customText = field.CustomText;
        if (!customText.IsNull)
        {
            buffer.Write(",\n"u8);
            WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"custom_text\": "u8);
            JsonHelpers.WriteJsonString(ref buffer, customText.AsString);
        }

        // "children": [...]
        if (field.HasChildren)
        {
            buffer.Write(",\n"u8);
            WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"children\": [\n"u8);

            bool firstChild = true;
            foreach (Field child in field.Children())
            {
                if (!firstChild)
                {
                    buffer.Write(",\n"u8);
                }
                firstChild = false;
                WriteFieldPretty(child, ref buffer, depth + 2);
            }
            buffer.Write(Nl);
            WriteIndent(ref buffer, depth + 1);
            buffer.WriteByte((byte)']');
        }

        buffer.Write(Nl);
        WriteIndent(ref buffer, depth);
        buffer.WriteByte((byte)'}');
    }

    /// <summary>Writes 2-space indentation for the given depth level.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteIndent(ref PooledBuffer buffer, int depth)
    {
        // Each depth level = 2 spaces
        for (int i = 0; i < depth; i++)
        {
            buffer.Write("  "u8);
        }
    }
}

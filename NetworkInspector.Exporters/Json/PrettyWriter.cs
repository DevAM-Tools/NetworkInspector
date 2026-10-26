// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Writes packets in pretty-printed JSON format with full human-readable keys
/// and 2-space indentation. No deduplication or same-as-previous optimization.
/// <para>
/// Packet keys: <c>id</c>, <c>timestamp</c> (raw nanoseconds since the Unix epoch as a number), <c>info</c>, <c>fields</c>.
/// Field keys: <c>field_id</c>, <c>name</c>, <c>ui_name</c>, <c>type</c>,
/// <c>value</c>, <c>custom_representation</c>, <c>custom_text</c>, <c>children</c>.
/// Optional keys are omitted, not written as <c>null</c>, when the packet or field has nothing to show.
/// </para>
/// <para>
/// This is the readable counterpart to the compact JSON writers: it trades speed and size for output a person can
/// read and diff. Every byte still goes through <see cref="JsonHelpers"/> into a <see cref="PooledBuffer"/>, so the
/// writer adds no per-field allocation of its own (only the lazily materialized strings it reads allocate).
/// Thread safety: stateless; safe from any thread with its own buffer.
/// </para>
/// </summary>
internal static class PrettyWriter
{
    /// <summary>Newline separator.</summary>
    private static ReadOnlySpan<byte> _Nl => "\n"u8;

    /// <summary>
    /// Writes a single packet in pretty-printed JSON format with 2-space indentation.
    /// </summary>
    /// <param name="packet">The packet to serialize.</param>
    /// <param name="buffer">Target output buffer.</param>
    /// <exception cref="InvalidOperationException">The packet is a skip packet without a field tree.</exception>
    internal static void WritePacket(Packet packet, ref PooledBuffer buffer)
    {
        // Skip packets carry no field tree; callers must filter them out, so reaching here is a caller bug.
        if (!packet.HasFieldTree)
        {
            throw new InvalidOperationException("Skip packets cannot be exported (HasFieldTree is false).");
        }

        // Opening brace at indent level 1 (inside the array). The separator between packets belongs to the caller
        // (JsonExporter); this method emits exactly one object and no trailing newline.
        buffer.Write("  {\n"u8);

        // "id": value
        _WriteIndent(ref buffer, 2);
        buffer.Write("\"id\": "u8);
        JsonHelpers.WriteI64(ref buffer, packet.Id.Value);

        // "timestamp": value
        buffer.Write(",\n"u8);
        _WriteIndent(ref buffer, 2);
        buffer.Write("\"timestamp\": "u8);
        JsonHelpers.WriteI64(ref buffer, packet.Timestamp.AsNanos);

        // "info": "value" — omitted when empty. Every later key starts with ",\n" so that no key needs to know
        // whether it is the last one.
        string info = packet.Info;
        if (info.Length > 0)
        {
            buffer.Write(",\n"u8);
            _WriteIndent(ref buffer, 2);
            buffer.Write("\"info\": "u8);
            JsonHelpers.WriteJsonString(ref buffer, info);
        }

        // "fields": [...] — materialize: true so lazy protocol trees are exported.
        Field root = packet.RootField();
        if (root.HasChildren(materialize: true))
        {
            buffer.Write(",\n"u8);
            _WriteIndent(ref buffer, 2);
            buffer.Write("\"fields\": [\n"u8);

            bool first = true;
            foreach (Field child in root.Children(materialize: true))
            {
                if (!first)
                {
                    buffer.Write(",\n"u8);
                }
                first = false;
                _WriteFieldPretty(child, ref buffer, 3);
            }
            buffer.Write(_Nl);
            _WriteIndent(ref buffer, 2);
            buffer.WriteByte((byte)']');
        }

        buffer.Write(_Nl);
        buffer.Write("  }"u8);
    }

    /// <summary>
    /// Writes a single field object (and, recursively, its children) at the given indentation depth.
    /// Recursion depth equals the nesting depth of the field tree, which is bounded by protocol nesting.
    /// </summary>
    private static void _WriteFieldPretty(Field field, ref PooledBuffer buffer, int depth)
    {
        _WriteIndent(ref buffer, depth);
        buffer.Write("{\n"u8);

        // "field_id": value
        _WriteIndent(ref buffer, depth + 1);
        buffer.Write("\"field_id\": "u8);
        JsonHelpers.WriteI64(ref buffer, field.FieldId.Value);

        // "name", "ui_name" and "type" come from the stack's field registry; when the id is not registered there
        // (FieldInfo is null) the field is still exported with its id, value and children, just without those keys.
        FieldInfo? info = field.FieldInfo;
        if (info is not null)
        {
            buffer.Write(",\n"u8);
            _WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"name\": "u8);
            JsonHelpers.WriteJsonString(ref buffer, info.Name);

            buffer.Write(",\n"u8);
            _WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"ui_name\": "u8);
            JsonHelpers.WriteJsonString(ref buffer, info.UiName);

            buffer.Write(",\n"u8);
            _WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"type\": "u8);
            JsonHelpers.WriteFieldTypeName(ref buffer, info.FieldType);
        }

        // "value": ... — FieldType.None means the field carries no value of its own, so the key is omitted.
        FieldValue value = field.Value;
        if (value.Type != FieldType.None)
        {
            buffer.Write(",\n"u8);
            _WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"value\": "u8);
            JsonHelpers.WriteFieldValue(ref buffer, value);
        }

        // "custom_representation": "..."
        if (!value.CustomRepresentation.IsNull)
        {
            buffer.Write(",\n"u8);
            _WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"custom_representation\": "u8);
            JsonHelpers.WriteJsonString(ref buffer, value.CustomRepresentation.AsString);
        }

        // "custom_text": "..."
        LazyString customText = field.CustomText;
        if (!customText.IsNull)
        {
            buffer.Write(",\n"u8);
            _WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"custom_text\": "u8);
            JsonHelpers.WriteJsonString(ref buffer, customText.AsString);
        }

        // "children": [...] — materialize: true so nested lazy fields are exported.
        if (field.HasChildren(materialize: true))
        {
            buffer.Write(",\n"u8);
            _WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"children\": [\n"u8);

            bool firstChild = true;
            foreach (Field child in field.Children(materialize: true))
            {
                if (!firstChild)
                {
                    buffer.Write(",\n"u8);
                }
                firstChild = false;
                _WriteFieldPretty(child, ref buffer, depth + 2);
            }
            buffer.Write(_Nl);
            _WriteIndent(ref buffer, depth + 1);
            buffer.WriteByte((byte)']');
        }

        buffer.Write(_Nl);
        _WriteIndent(ref buffer, depth);
        buffer.WriteByte((byte)'}');
    }

    /// <summary>Writes 2-space indentation for the given depth level.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _WriteIndent(ref PooledBuffer buffer, int depth)
    {
        // Each depth level = 2 spaces. Written in a loop over a constant span to avoid allocating an indent string.
        for (int i = 0; i < depth; i++)
        {
            buffer.Write("  "u8);
        }
    }
}

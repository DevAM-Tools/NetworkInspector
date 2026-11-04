// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Writes packets in pretty-printed JSON format with full human-readable keys
/// and 2-space indentation. No deduplication or same-as-previous optimization.
/// <para>
/// Packet keys: <c>id</c>, <c>timestamp</c> (raw nanoseconds since the Unix epoch as a number), <c>info</c>, <c>frame_bytes</c>
/// (only when the export asks for frames: the frame as standard Base64), <c>buffers</c> (only when the export asks for buffers and
/// the packet has an additional buffer: an array of <c>{"index": n, "bytes": "&lt;Base64&gt;"}</c> records in ascending index
/// order), <c>fields</c>.
/// Field keys: <c>field_id</c>, <c>name</c>, <c>ui_name</c>, <c>type</c>,
/// <c>value</c>, <c>custom_representation</c>, <c>custom_text</c>, <c>children</c>.
/// Optional keys are omitted, not written as <c>null</c>, when the packet or field has nothing to show: <c>custom_text</c> and
/// <c>custom_representation</c> exist only when the export asks for them and the dissector set one (an empty text is
/// <c>""</c>).
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
    #region Packet writing

    /// <summary>Newline separator.</summary>
    private static ReadOnlySpan<byte> _Nl => "\n"u8;

    /// <summary>
    /// Writes a single packet in pretty-printed JSON format with 2-space indentation.
    /// </summary>
    /// <param name="packet">The packet to serialize.</param>
    /// <param name="buffer">Target output buffer.</param>
    /// <param name="includeInfo">Whether the <c>info</c> key (the packet summary text) is written; it is also omitted when empty.</param>
    /// <param name="includeCustomText">Whether the <c>custom_text</c> key is written; when <see langword="false"/> no text is read.</param>
    /// <param name="includeCustomRepresentation">
    /// Whether the <c>custom_representation</c> key is written; when <see langword="false"/> no representation is read.
    /// </param>
    /// <param name="includeFrameBytes">Whether the <c>frame_bytes</c> key (the frame as standard Base64) is written.</param>
    /// <param name="includeBuffers">
    /// Whether the <c>buffers</c> key (the additional packet buffers as Base64 records) is written; the key is omitted for a packet
    /// without additional buffers.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The packet is a skip packet without a field tree, or a buffer inside its buffer count is missing (a broken invariant).
    /// </exception>
    internal static void WritePacket(
        Packet packet,
        ref PooledBuffer buffer,
        bool includeInfo,
        bool includeCustomText,
        bool includeCustomRepresentation,
        bool includeFrameBytes,
        bool includeBuffers)
    {
        // Skip packets carry no field tree; callers must filter them out, so reaching here is a caller bug.
        if (!packet.HasFieldTree)
        {
            JsonHelpers.ThrowSkipPacket();
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
        if (includeInfo)
        {
            string info = packet.Info;
            if (info.Length > 0)
            {
                buffer.Write(",\n"u8);
                _WriteIndent(ref buffer, 2);
                buffer.Write("\"info\": "u8);
                JsonHelpers.WriteJsonString(ref buffer, info);
            }
        }

        // "frame_bytes": "<Base64>" — opt-in copy of the frame (the field tree already holds the parsed bytes). An empty frame is
        // the empty string, so the key is present in every packet of an export that asked for frames. The bytes are encoded into the
        // buffer here, inside the exporter's OnPacket, because the packet's buffers are recycled afterwards.
        if (includeFrameBytes)
        {
            buffer.Write(",\n"u8);
            _WriteIndent(ref buffer, 2);
            buffer.Write("\"frame_bytes\": "u8);
            JsonHelpers.WriteBase64String(ref buffer, packet.Frame.Data.Span);
        }

        // "buffers": [...] — opt-in additional buffers (reassembled datagrams, TCP PDUs, decoded bodies), one record per line in
        // ascending index order. Omitted for a packet without additional buffers; a bound empty buffer is "bytes": "". Encoded here,
        // inside OnPacket, because the packet's buffers are recycled afterwards. The field tree below is written the same way
        // with the option on or off (the field tree leads).
        if (includeBuffers && packet.BufferCount > 1)
        {
            buffer.Write(",\n"u8);
            _WriteIndent(ref buffer, 2);
            buffer.Write("\"buffers\": [\n"u8);
            int bufferCount = packet.BufferCount;
            for (int index = 1; index < bufferCount; index++)
            {
                if (index > 1)
                {
                    buffer.Write(",\n"u8);
                }

                _WriteIndent(ref buffer, 3);
                buffer.Write("{\"index\": "u8);
                JsonHelpers.WriteU64(ref buffer, (ulong)index);
                buffer.Write(", \"bytes\": "u8);
                JsonHelpers.WriteBase64String(ref buffer, PacketBuffers.Get(packet, index));
                buffer.WriteByte((byte)'}');
            }

            buffer.Write(_Nl);
            _WriteIndent(ref buffer, 2);
            buffer.WriteByte((byte)']');
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
                _WriteFieldPretty(child, ref buffer, 3, includeCustomText, includeCustomRepresentation);
            }
            buffer.Write(_Nl);
            _WriteIndent(ref buffer, 2);
            buffer.WriteByte((byte)']');
        }

        buffer.Write(_Nl);
        buffer.Write("  }"u8);
    }

    #endregion

    #region Field writing

    /// <summary>
    /// Writes a single field object (and, recursively, its children) at the given indentation depth.
    /// Recursion depth equals the nesting depth of the field tree, which is bounded by protocol nesting.
    /// </summary>
    /// <param name="field">The field to write.</param>
    /// <param name="buffer">Target output buffer.</param>
    /// <param name="depth">Indentation depth of the field object (2 spaces per level).</param>
    /// <param name="includeCustomText">Whether the <c>custom_text</c> key is written (only when the field has a text).</param>
    /// <param name="includeCustomRepresentation">Whether the <c>custom_representation</c> key is written (only when the value has one).</param>
    private static void _WriteFieldPretty(
        Field field, ref PooledBuffer buffer, int depth, bool includeCustomText, bool includeCustomRepresentation)
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

        // "custom_representation" / "custom_text": written only when the export asks for them and the dissector set them; an
        // absent text is an absent key (EXPORTER_GUIDE § Rebuilding the display line), an empty one is "". A switched-off text is
        // not even read (reading can complete a deferred text).
        if (includeCustomRepresentation && !value.CustomRepresentation.IsNull)
        {
            buffer.Write(",\n"u8);
            _WriteIndent(ref buffer, depth + 1);
            buffer.Write("\"custom_representation\": "u8);
            JsonHelpers.WriteJsonString(ref buffer, value.CustomRepresentation.AsString);
        }

        if (includeCustomText)
        {
            ref readonly LazyString customText = ref field.CustomText;
            if (!customText.IsNull)
            {
                buffer.Write(",\n"u8);
                _WriteIndent(ref buffer, depth + 1);
                buffer.Write("\"custom_text\": "u8);
                JsonHelpers.WriteJsonString(ref buffer, customText.AsString);
            }
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
                _WriteFieldPretty(child, ref buffer, depth + 2, includeCustomText, includeCustomRepresentation);
            }
            buffer.Write(_Nl);
            _WriteIndent(ref buffer, depth + 1);
            buffer.WriteByte((byte)']');
        }

        buffer.Write(_Nl);
        _WriteIndent(ref buffer, depth);
        buffer.WriteByte((byte)'}');
    }

    #endregion

    #region Indentation

    /// <summary>Writes 2-space indentation for the given depth level.</summary>
    /// <param name="buffer">Target output buffer.</param>
    /// <param name="depth">Number of indentation levels; 0 writes nothing.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _WriteIndent(ref PooledBuffer buffer, int depth)
    {
        // Each depth level = 2 spaces. Written in a loop over a constant span to avoid allocating an indent string.
        for (int i = 0; i < depth; i++)
        {
            buffer.Write("  "u8);
        }
    }

    #endregion
}

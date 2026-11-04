// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Writes packets as flat JSON objects (each packet on one line, no indentation) with full
/// human-readable keys and no deduplication, so every object is self-contained and can be read without the preceding
/// packets. <see cref="JsonExporter"/> places the objects in one JSON array separated by commas and blank lines, so the
/// file as a whole is a single JSON document, not NDJSON; each object on its own line is easy to grep and diff.
/// <para>
/// Packet keys: <c>id</c>, <c>timestamp</c> (nanoseconds since the Unix epoch; values exceed 2^53, so consumers that
/// parse JSON numbers as IEEE doubles lose precision), <c>info</c> (omitted when empty), <c>frame_bytes</c> (only when the export
/// asks for frames: the frame as standard Base64), <c>buffers</c> (only when the export asks for buffers and the packet has an
/// additional buffer: <c>[{"index":n,"bytes":"&lt;Base64&gt;"}]</c> in ascending index order), <c>fields</c> (omitted when the
/// packet has no fields).
/// Field keys: <c>field_id</c>, <c>name</c>, <c>ui_name</c>, <c>type</c> (these three only for registered fields),
/// <c>value</c> (omitted for fields without a value), <c>custom_representation</c>, <c>custom_text</c> (both only when the
/// export asks for them and the dissector set one; an empty text is <c>""</c>), <c>children</c>.
/// </para>
/// <para>
/// <b>Thread safety:</b> Stateless; safe to call from multiple threads as long as each call has its own buffer.
/// </para>
/// </summary>
internal static class ArrayWriter
{
    #region Packet writing

    /// <summary>
    /// Writes a single packet as a flat (non-indented) JSON object.
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
    /// The packet has no field tree (a skip packet), or a buffer inside its buffer count is missing (a broken invariant).
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
        // A skip packet has no parsed field tree to serialize: fail loudly instead of writing an empty object.
        if (!packet.HasFieldTree)
        {
            JsonHelpers.ThrowSkipPacket();
        }

        buffer.WriteByte((byte)'{');

        // "id": value
        buffer.Write("\"id\":"u8);
        JsonHelpers.WriteI64(ref buffer, packet.Id.Value);

        // "timestamp": value
        buffer.Write(",\"timestamp\":"u8);
        JsonHelpers.WriteI64(ref buffer, packet.Timestamp.AsNanos);

        // "info": "value"
        if (includeInfo)
        {
            string info = packet.Info;
            if (info.Length > 0)
            {
                buffer.Write(",\"info\":"u8);
                JsonHelpers.WriteJsonString(ref buffer, info);
            }
        }

        // "frame_bytes": "<Base64>" — opt-in frame copy; same rule as PrettyWriter (an empty frame is "").
        if (includeFrameBytes)
        {
            buffer.Write(",\"frame_bytes\":"u8);
            JsonHelpers.WriteBase64String(ref buffer, packet.Frame.Data.Span);
        }

        // "buffers": [...] — opt-in additional buffers; omitted for a packet without any, same rule as PrettyWriter.
        if (includeBuffers && packet.BufferCount > 1)
        {
            buffer.Write(",\"buffers\":"u8);
            JsonHelpers.WriteBuffers(ref buffer, packet);
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
                _WriteFieldFlat(child, ref buffer, includeCustomText, includeCustomRepresentation);
            }
            buffer.WriteByte((byte)']');
        }

        buffer.WriteByte((byte)'}');
    }

    #endregion

    #region Field writing

    /// <summary>
    /// Writes a single field as a flat JSON object (no indentation), then its children recursively (recursion depth
    /// equals the nesting depth of the field tree). Children are expanded (<c>materialize: true</c>) so lazily built
    /// subtrees are exported too.
    /// </summary>
    /// <param name="field">The field to write.</param>
    /// <param name="buffer">Target output buffer.</param>
    /// <param name="includeCustomText">Whether the <c>custom_text</c> key is written (only when the field has a text).</param>
    /// <param name="includeCustomRepresentation">Whether the <c>custom_representation</c> key is written (only when the value has one).</param>
    private static void _WriteFieldFlat(Field field, ref PooledBuffer buffer, bool includeCustomText, bool includeCustomRepresentation)
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

        // "custom_representation" / "custom_text": written only when the export asks for them and the dissector set them; an
        // absent text is an absent key (EXPORTER_GUIDE § Rebuilding the display line), an empty one is "". A switched-off text is
        // not even read (reading can complete a deferred text).
        if (includeCustomRepresentation && !value.CustomRepresentation.IsNull)
        {
            buffer.Write(",\"custom_representation\":"u8);
            JsonHelpers.WriteJsonString(ref buffer, value.CustomRepresentation.AsString);
        }

        if (includeCustomText)
        {
            ref readonly LazyString customText = ref field.CustomText;
            if (!customText.IsNull)
            {
                buffer.Write(",\"custom_text\":"u8);
                JsonHelpers.WriteJsonString(ref buffer, customText.AsString);
            }
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
                _WriteFieldFlat(child, ref buffer, includeCustomText, includeCustomRepresentation);
            }
            buffer.WriteByte((byte)']');
        }

        buffer.WriteByte((byte)'}');
    }

    #endregion
}

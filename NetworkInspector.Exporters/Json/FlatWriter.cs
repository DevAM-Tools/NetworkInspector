// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Writes the flat shapes (selected fields, all fields) of the JSON exporter from a committed <see cref="RowFieldStage"/>.
/// <list type="bullet">
///   <item><b>Selected fields:</b> <c>{"id":5,"timestamp":…[,"info":…],"fields":{"ip.src":["10.0.0.1","192.168.0.1"],"udp.dstport":53}}</c>
///     — a map keyed by field name; the key is omitted when the packet lacks the field.</item>
///   <item><b>All fields:</b> <c>{"id":5,"timestamp":…[,"info":…],"fields":[{"name":"ip.src","occurrence":0,"value":"10.0.0.1"},…]}</c>
///     — one entry per occurrence in production order.</item>
///   <item>Both shapes write <c>"frame_bytes":"…"</c> (the frame as standard Base64) after <c>info</c> when the content asks for it,
///     and <c>"buffers":[{"index":1,"bytes":"…"}]</c> (the additional packet buffers) after that when the content asks for them and
///     the packet has one.</item>
/// </list>
/// <para>
/// <b>Why a separate writer:</b> the tree writers walk a field tree; the flat shapes have no tree (they come from a tree-free
/// parse) and no same-as-previous state. Keys are the full names in every <see cref="JsonExportFormat"/> (the compact format's
/// short keys and deduplication exist to shrink repeated tree records, which flat records do not have). <c>Pretty</c> indents with
/// two spaces; <c>Compact</c> and <c>Array</c> write one line per packet.
/// </para>
/// <para>
/// <b>Types:</b> a field selected with <see cref="Occurrences.All"/> is always an array (also with one value), so a consumer can
/// rely on one JSON type per key; <see cref="Occurrences.First"/> and <see cref="Occurrences.Last"/> are scalars. Values use the
/// same mapping as the tree writers (<see cref="JsonHelpers.WriteFieldValueData"/>): numbers, booleans, base-64 bytes, quoted
/// addresses and timestamps. With custom texts on, an occurrence is an object
/// <c>{"value":…,"custom_representation":"…","custom_text":"…"}</c> (a text key is omitted when the value has no such text);
/// with both off it is the plain value.
/// </para>
/// <para><b>Thread safety:</b> stateless; every call works on the buffer it is given.</para>
/// </summary>
internal static class FlatWriter
{
    #region Selected fields

    /// <summary>Writes one packet of the selected-fields shape.</summary>
    /// <param name="packet">The committed packet (id, timestamp, info).</param>
    /// <param name="stage">The committed stage of that packet.</param>
    /// <param name="selection">The selected fields (order and occurrence choice).</param>
    /// <param name="content">The content (info and text members).</param>
    /// <param name="indent">Whether to indent with two spaces (the <c>Pretty</c> format).</param>
    /// <param name="buffer">Target buffer; the packet object is appended without a trailing newline.</param>
    internal static void WriteSelected(
        Packet packet, RowFieldStage stage, FieldSelection selection, ExportContent content, bool indent, ref PooledBuffer buffer)
    {
        int level = _WritePacketHeader(packet, content, indent, ref buffer);
        buffer.Write("\"fields\":"u8);
        _Space(indent, ref buffer);
        buffer.Write("{"u8);

        bool firstKey = true;

        // Perf: an indexed loop; foreach over the IReadOnlyList would box an enumerator for every packet.
        IReadOnlyList<SelectedField> fields = selection.Fields;
        for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
        {
            SelectedField field = fields[fieldIndex];

            // The entries of one field are found by field id (a selection has a few fields and a packet a few entries for them, so
            // a scan per field is cheaper than an index) and are already in occurrence order.
            int count = _CountEntries(stage, field.Id.Value);
            if (count == 0)
            {
                continue;
            }

            if (!firstKey)
            {
                buffer.WriteByte((byte)',');
            }

            firstKey = false;
            _Break(indent, level + 2, ref buffer);
            JsonHelpers.WriteJsonString(ref buffer, field.Name);
            buffer.WriteByte((byte)':');
            _Space(indent, ref buffer);

            // All is always an array; First and Last are scalars (one value each by construction of the collector).
            bool asArray = field.Occurrences == Occurrences.All;
            if (asArray)
            {
                buffer.WriteByte((byte)'[');
            }

            bool firstValue = true;
            foreach (ref readonly RowFieldStage.Entry entry in stage.Entries)
            {
                if (entry.FieldId != field.Id.Value)
                {
                    continue;
                }

                if (asArray)
                {
                    if (!firstValue)
                    {
                        buffer.WriteByte((byte)',');
                    }

                    _Break(indent, level + 3, ref buffer);
                }

                firstValue = false;
                _WriteOccurrence(stage, in entry, content, ref buffer);
            }

            if (asArray)
            {
                _Break(indent, level + 2, ref buffer);
                buffer.WriteByte((byte)']');
            }
        }

        if (!firstKey)
        {
            _Break(indent, level + 1, ref buffer);
        }

        buffer.Write("}"u8);
        _Break(indent, level, ref buffer);
        buffer.WriteByte((byte)'}');
    }

    /// <summary>Counts the staged entries of one field.</summary>
    /// <param name="stage">The committed stage of the packet.</param>
    /// <param name="fieldId">The numeric field id to count.</param>
    /// <returns>The number of entries of that field; 0 when the packet lacks it.</returns>
    private static int _CountEntries(RowFieldStage stage, int fieldId)
    {
        int count = 0;
        foreach (ref readonly RowFieldStage.Entry entry in stage.Entries)
        {
            if (entry.FieldId == fieldId)
            {
                count++;
            }
        }

        return count;
    }

    #endregion

    #region All fields

    /// <summary>Writes one packet of the all-fields shape.</summary>
    /// <param name="packet">The committed packet (id, timestamp, info, field names).</param>
    /// <param name="stage">The committed stage of that packet.</param>
    /// <param name="content">The content (info and text members).</param>
    /// <param name="indent">Whether to indent with two spaces (the <c>Pretty</c> format).</param>
    /// <param name="buffer">Target buffer; the packet object is appended without a trailing newline.</param>
    internal static void WriteAll(Packet packet, RowFieldStage stage, ExportContent content, bool indent, ref PooledBuffer buffer)
    {
        int level = _WritePacketHeader(packet, content, indent, ref buffer);
        buffer.Write("\"fields\":"u8);
        _Space(indent, ref buffer);
        buffer.Write("["u8);

        bool first = true;
        foreach (ref readonly RowFieldStage.Entry entry in stage.Entries)
        {
            if (!first)
            {
                buffer.WriteByte((byte)',');
            }

            first = false;
            _Break(indent, level + 2, ref buffer);
            buffer.Write("{\"name\":"u8);
            _Space(indent, ref buffer);
            JsonHelpers.WriteJsonString(ref buffer, _NameOf(packet.Stack, entry.FieldId));
            buffer.Write(",\"occurrence\":"u8);
            _Space(indent, ref buffer);
            JsonHelpers.WriteI64(ref buffer, entry.Occurrence);
            buffer.Write(",\"value\":"u8);
            _Space(indent, ref buffer);
            _WriteValue(stage, in entry, ref buffer);
            _WriteTexts(in entry, content, ref buffer);
            buffer.WriteByte((byte)'}');
        }

        if (!first)
        {
            _Break(indent, level + 1, ref buffer);
        }

        buffer.Write("]"u8);
        _Break(indent, level, ref buffer);
        buffer.WriteByte((byte)'}');
    }

    /// <summary>The registered name of a field id; an unregistered id is written as its number.</summary>
    /// <param name="stack">The stack of the packet; its registry names the field.</param>
    /// <param name="fieldId">The numeric field id.</param>
    /// <returns>The field name, or the decimal id when the stack has no such field.</returns>
    private static string _NameOf(Stack stack, int fieldId)
    {
        FieldInfo? info = stack.GetField(new FieldId(fieldId));
        if (info is null)
        {
            return fieldId.ToString(CultureInfo.InvariantCulture);
        }

        return info.Name;
    }

    #endregion

    #region Packet header

    /// <summary>
    /// Writes the opening brace and the packet keys (<c>id</c>, <c>timestamp</c>, <c>info</c> when the content asks for it and
    /// the packet has one, <c>frame_bytes</c> when the content asks for it, and <c>buffers</c> when the content asks for it and the
    /// packet has an additional buffer); each key is followed by a comma, so the caller writes <c>fields</c> next.
    /// </summary>
    /// <param name="packet">The packet (id, timestamp, info, frame, additional buffers).</param>
    /// <param name="content">The content; decides whether <c>info</c>, <c>frame_bytes</c>, and <c>buffers</c> are written.</param>
    /// <param name="indent">Whether to indent with two spaces.</param>
    /// <param name="buffer">Target buffer.</param>
    /// <returns>The indent level of the packet object.</returns>
    private static int _WritePacketHeader(Packet packet, ExportContent content, bool indent, ref PooledBuffer buffer)
    {
        // Pretty output sits inside the outer array, one level in, like the tree writer; the other formats start at the margin.
        int level = indent
            ? 1
            : 0;
        _Break(indent, level, ref buffer, leadingNewline: false);
        buffer.Write("{"u8);
        _Break(indent, level + 1, ref buffer);
        buffer.Write("\"id\":"u8);
        _Space(indent, ref buffer);
        JsonHelpers.WriteI64(ref buffer, packet.Id.Value);
        buffer.WriteByte((byte)',');
        _Break(indent, level + 1, ref buffer);
        buffer.Write("\"timestamp\":"u8);
        _Space(indent, ref buffer);
        JsonHelpers.WriteI64(ref buffer, packet.Timestamp.AsNanos);
        buffer.WriteByte((byte)',');

        // "info" is omitted when empty, like in the tree writers. Reading it costs the summary line of every packet, so a content
        // without info never touches it.
        if (content.Info)
        {
            string info = packet.Info;
            if (info.Length > 0)
            {
                _Break(indent, level + 1, ref buffer);
                buffer.Write("\"info\":"u8);
                _Space(indent, ref buffer);
                JsonHelpers.WriteJsonString(ref buffer, info);
                buffer.WriteByte((byte)',');
            }
        }

        // "frame_bytes" is the opt-in copy of the frame as standard Base64, the same key and form as in the tree writers; an empty
        // frame is "". It is encoded here, inside OnPacket, because the packet's buffers are recycled afterwards.
        if (content.FrameBytes)
        {
            _Break(indent, level + 1, ref buffer);
            buffer.Write("\"frame_bytes\":"u8);
            _Space(indent, ref buffer);
            JsonHelpers.WriteBase64String(ref buffer, packet.Frame.Data.Span);
            buffer.WriteByte((byte)',');
        }

        // "buffers" is the opt-in list of the additional packet buffers (see JsonHelpers.WriteBuffers), the same key and form as in the
        // tree writers, omitted for a packet without any. The packet's buffers are the same whether the packet was parsed with or
        // without a field tree, so the flat shapes list the same buffers as the tree shapes.
        if (content.Buffers && packet.BufferCount > 1)
        {
            _Break(indent, level + 1, ref buffer);
            buffer.Write("\"buffers\":"u8);
            _Space(indent, ref buffer);
            JsonHelpers.WriteBuffers(ref buffer, packet);
            buffer.WriteByte((byte)',');
        }

        _Break(indent, level + 1, ref buffer);
        return level;
    }

    #endregion

    #region Values and texts

    /// <summary>Writes one occurrence: the plain value, or an object with the texts when the content asks for them.</summary>
    /// <param name="stage">The committed stage; owns the bytes arena of the entry.</param>
    /// <param name="entry">The staged occurrence.</param>
    /// <param name="content">The content; decides whether the text members are written.</param>
    /// <param name="buffer">Target buffer.</param>
    private static void _WriteOccurrence(RowFieldStage stage, in RowFieldStage.Entry entry, ExportContent content, ref PooledBuffer buffer)
    {
        if (!content.CustomText && !content.CustomRepresentation)
        {
            _WriteValue(stage, in entry, ref buffer);
            return;
        }

        buffer.Write("{\"value\":"u8);
        _WriteValue(stage, in entry, ref buffer);
        _WriteTexts(in entry, content, ref buffer);
        buffer.WriteByte((byte)'}');
    }

    /// <summary>Writes the optional text members of an occurrence object (a key is omitted when the value has no such text).</summary>
    /// <param name="entry">The staged occurrence.</param>
    /// <param name="content">The content; decides which text members are written.</param>
    /// <param name="buffer">Target buffer.</param>
    private static void _WriteTexts(in RowFieldStage.Entry entry, ExportContent content, ref PooledBuffer buffer)
    {
        if (content.CustomRepresentation && entry.Representation is not null)
        {
            buffer.Write(",\"custom_representation\":"u8);
            JsonHelpers.WriteJsonString(ref buffer, entry.Representation);
        }

        if (content.CustomText && entry.Text is not null)
        {
            buffer.Write(",\"custom_text\":"u8);
            JsonHelpers.WriteJsonString(ref buffer, entry.Text);
        }
    }

    /// <summary>Writes the value of an entry with the mapping of the tree writers.</summary>
    /// <param name="stage">The committed stage; owns the bytes arena of the entry.</param>
    /// <param name="entry">The staged occurrence.</param>
    /// <param name="buffer">Target buffer.</param>
    private static void _WriteValue(RowFieldStage stage, in RowFieldStage.Entry entry, ref PooledBuffer buffer)
    {
        // A bytes payload lives in the stage's arena and is encoded straight from that span (Perf: no array per value); scalars,
        // strings, and addresses are inline in the entry and go through the shared value mapping.
        if (entry.Type == FieldType.Bytes)
        {
            JsonHelpers.WriteBase64String(ref buffer, stage.GetBytes(in entry));
            return;
        }

        JsonHelpers.WriteFieldValueData(ref buffer, in entry.Value);
    }

    #endregion

    #region Indentation

    /// <summary>A space after a colon in the indented format.</summary>
    /// <param name="indent">Whether the output is indented; nothing is written otherwise.</param>
    /// <param name="buffer">Target buffer.</param>
    private static void _Space(bool indent, ref PooledBuffer buffer)
    {
        if (indent)
        {
            buffer.WriteByte((byte)' ');
        }
    }

    /// <summary>A line break and the indentation of <paramref name="level"/> in the indented format; nothing otherwise.</summary>
    /// <param name="indent">Whether the output is indented; nothing is written otherwise.</param>
    /// <param name="level">Indentation level (2 spaces each).</param>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="leadingNewline">Whether a line break precedes the indentation; <see langword="false"/> for the first line.</param>
    private static void _Break(bool indent, int level, ref PooledBuffer buffer, bool leadingNewline = true)
    {
        if (!indent)
        {
            return;
        }

        if (leadingNewline)
        {
            buffer.WriteByte((byte)'\n');
        }

        for (int i = 0; i < level; i++)
        {
            buffer.Write("  "u8);
        }
    }

    #endregion
}

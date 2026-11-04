// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Writes packets in compact JSON format with short single/two-character keys
/// and same-as-previous optimization for minimum output size.
/// <para>
/// Packet keys: <c>ID</c>, <c>TS</c>, <c>IN</c>, <c>SF</c>, <c>frame_bytes</c> (only when the export asks for frames), <c>buffers</c>
/// (only when the export asks for buffers and the packet has an additional buffer), <c>CH</c>.
/// Field keys: <c>FI</c>, <c>NA</c>, <c>UI</c>, <c>TY</c>, <c>VA</c>, <c>CR</c>, <c>CT</c>, <c>SF</c>, <c>CH</c>. <c>CR</c> and
/// <c>CT</c> exist only when the export asks for them and the dissector set one (an empty text is <c>""</c>); a text equal to the
/// previous occurrence's is replaced by its same-flag.
/// </para>
/// <para>
/// <b>Size reductions (the reason for this format):</b> short keys; the field name, UI name and type only on the first
/// occurrence of a field id (<c>NA</c>, <c>UI</c>, <c>TY</c>); and a same-as-previous mask <c>SF</c> that replaces a value,
/// custom representation, custom text, or packet info string identical to the previous occurrence. "Previous" is the last
/// occurrence in write order over the whole document (kept in <see cref="JsonExporterState"/>), so the output can only be
/// decoded by replaying it from the start. Field ids are dense per protocol stack, so one document holds the packets of one stack
/// only: <see cref="JsonExporter"/> refuses a packet of another stack. The packet timestamp <c>TS</c> is nanoseconds since the Unix epoch; such values
/// exceed 2^53, so consumers that parse JSON numbers as IEEE doubles lose precision.
/// </para>
/// <para>
/// <b>Thread safety:</b> Stateless apart from the <see cref="JsonExporterState"/> passed in; not thread-safe for one state.
/// </para>
/// </summary>
internal static class CompactWriter
{
    #region Key literals

    // Key literals are UTF-8 spans over static data, each with its leading comma (except the first key of an object), so
    // writing a key is a single buffer append with no formatting and no allocation.

    /// <summary>Packet key <c>ID</c>: the packet id; the first key of the packet object, so it has no leading comma.</summary>
    private static ReadOnlySpan<byte> _KeyId => "\"ID\":"u8;

    /// <summary>Packet key <c>TS</c>: the timestamp in nanoseconds since the Unix epoch.</summary>
    private static ReadOnlySpan<byte> _KeyTs => ",\"TS\":"u8;

    /// <summary>Packet key <c>IN</c>: the packet info string, omitted when it is empty or the same as the previous packet's.</summary>
    private static ReadOnlySpan<byte> _KeyIn => ",\"IN\":"u8;

    /// <summary>Packet key <c>SF</c>: the same-as-previous flags of the packet.</summary>
    private static ReadOnlySpan<byte> _KeySf => ",\"SF\":"u8;

    /// <summary>
    /// Packet key <c>frame_bytes</c>: the frame as standard Base64, written only when the export asks for frames. It is the one
    /// packet key that is not abbreviated: it spells the same name in every JSON format, so a reader (and the README) has one key
    /// for the frame, and its size is negligible next to the Base64 text it introduces.
    /// </summary>
    private static ReadOnlySpan<byte> _KeyFrameBytes => ",\"frame_bytes\":"u8;

    /// <summary>
    /// Packet key <c>buffers</c>: the additional packet buffers as <c>[{"index":n,"bytes":"&lt;Base64&gt;"}]</c>, written only when the
    /// export asks for buffers and the packet has one. Like <c>frame_bytes</c> it is not abbreviated, so every JSON format spells the
    /// key and the record keys the same way and a reader needs one rule.
    /// </summary>
    private static ReadOnlySpan<byte> _KeyBuffers => ",\"buffers\":"u8;

    /// <summary>Packet key <c>CH</c>: opens the array of the top-level fields.</summary>
    private static ReadOnlySpan<byte> _KeyCh => ",\"CH\":["u8;

    /// <summary>Field key <c>FI</c>: the field id; the first key of the field object, so it has no leading comma.</summary>
    private static ReadOnlySpan<byte> _FieldKeyFi => "\"FI\":"u8;

    /// <summary>Field key <c>NA</c>: the field name, written on the first occurrence of the field id only.</summary>
    private static ReadOnlySpan<byte> _FieldKeyNa => ",\"NA\":"u8;

    /// <summary>Field key <c>UI</c>: the display name, written on the first occurrence of the field id only.</summary>
    private static ReadOnlySpan<byte> _FieldKeyUi => ",\"UI\":"u8;

    /// <summary>Field key <c>TY</c>: the numeric field type, written on the first occurrence of the field id only.</summary>
    private static ReadOnlySpan<byte> _FieldKeyTy => ",\"TY\":"u8;

    /// <summary>Field key <c>VA</c>: the value, omitted when it is the same as the previous occurrence's.</summary>
    private static ReadOnlySpan<byte> _FieldKeyVa => ",\"VA\":"u8;

    /// <summary>Field key <c>CR</c>: the custom representation of the value, omitted when it is the same as the previous one.</summary>
    private static ReadOnlySpan<byte> _FieldKeyCr => ",\"CR\":"u8;

    /// <summary>Field key <c>CT</c>: the custom text of the field, omitted when it is the same as the previous one.</summary>
    private static ReadOnlySpan<byte> _FieldKeyCt => ",\"CT\":"u8;

    /// <summary>Field key <c>SF</c>: the same-as-previous flags of the field.</summary>
    private static ReadOnlySpan<byte> _FieldKeySf => ",\"SF\":"u8;

    /// <summary>Field key <c>CH</c>: opens the array of the child fields.</summary>
    private static ReadOnlySpan<byte> _FieldKeyCh => ",\"CH\":["u8;

    #endregion

    #region Packet writing

    /// <summary>
    /// Writes a single packet in compact JSON format.
    /// Uses same-as-previous optimization and field-info deduplication via the state object.
    /// </summary>
    /// <param name="packet">The packet to serialize.</param>
    /// <param name="buffer">Target output buffer.</param>
    /// <param name="state">Mutable exporter state for deduplication tracking.</param>
    /// <param name="includeInfo">
    /// Whether the <c>IN</c> key (the packet summary text) and its same-as-previous flag are written; when <see langword="false"/> the
    /// info is neither read nor compared.
    /// </param>
    /// <param name="includeCustomText">Whether the <c>CT</c> key is written; when <see langword="false"/> no text is read or compared.</param>
    /// <param name="includeCustomRepresentation">
    /// Whether the <c>CR</c> key is written; when <see langword="false"/> no representation is read or compared.
    /// </param>
    /// <param name="includeFrameBytes">Whether the <c>frame_bytes</c> key (the frame as standard Base64) is written.</param>
    /// <param name="includeBuffers">
    /// Whether the <c>buffers</c> key (the additional packet buffers as Base64 records) is written; the key is omitted for a packet
    /// without additional buffers.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The packet has no field tree (a skip packet), or a buffer inside its buffer count is missing (a broken invariant).
    /// </exception>
    /// <remarks>
    /// The state is updated while writing, before the caller knows whether the buffer reaches the output. The caller resets the
    /// state (<see cref="JsonExporterState.Reset"/>) when the packet does not reach the output, so no following same-as-previous
    /// reference can point at a packet that is not in the file.
    /// </remarks>
    /// <seealso cref="JsonExporterState.Reset"/>
    internal static void WritePacket(
        Packet packet,
        ref PooledBuffer buffer,
        JsonExporterState state,
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

        // ID — always present
        buffer.Write(_KeyId);
        JsonHelpers.WriteI64(ref buffer, packet.Id.Value);

        // TS — always present
        buffer.Write(_KeyTs);
        JsonHelpers.WriteI64(ref buffer, packet.Timestamp.AsNanos);

        // IN — info string with same-as-previous optimization. An empty info is omitted. The previous info is updated for
        // every packet, including one with empty info, so it always equals the info of the preceding packet and a reader
        // can mirror it by treating a missing IN (without the same-info flag) as an empty string.
        // With the info switched off (Info = false) nothing of it is read, written, or remembered: no reader sees an IN key, so
        // the previous info stays as it is.
        uint packetSameFlags = 0;
        if (includeInfo)
        {
            string info = packet.Info;
            if (info.Length > 0)
            {
                if (state.PreviousPacketInfo is not null
                    && string.Equals(state.PreviousPacketInfo, info, StringComparison.Ordinal))
                {
                    packetSameFlags |= SameFlags.PacketSameInfo;
                }
                else
                {
                    buffer.Write(_KeyIn);
                    JsonHelpers.WriteJsonString(ref buffer, info);
                }
            }

            state.PreviousPacketInfo = info;
        }

        // SF — emit packet same-as-previous flags if any
        if (packetSameFlags != 0)
        {
            buffer.Write(_KeySf);
            JsonHelpers.WriteU64(ref buffer, packetSameFlags);
        }

        // frame_bytes — opt-in frame copy, always written in full (no same-as-previous: consecutive frames are practically never
        // equal, and a comparison would cost a pass over every frame). An empty frame is "".
        if (includeFrameBytes)
        {
            buffer.Write(_KeyFrameBytes);
            JsonHelpers.WriteBase64String(ref buffer, packet.Frame.Data.Span);
        }

        // buffers — opt-in additional buffers, always written in full (no same-as-previous, for the reason given for frame_bytes);
        // omitted for a packet without any.
        if (includeBuffers && packet.BufferCount > 1)
        {
            buffer.Write(_KeyBuffers);
            JsonHelpers.WriteBuffers(ref buffer, packet);
        }

        // CH — children (fields); materialize: true so lazy protocol trees are exported.
        Field root = packet.RootField();
        if (root.HasChildren(materialize: true))
        {
            buffer.Write(_KeyCh);
            bool first = true;
            foreach (Field child in root.Children(materialize: true))
            {
                if (!first)
                {
                    buffer.WriteByte((byte)',');
                }
                first = false;
                _WriteFieldCompact(child, ref buffer, state, includeCustomText, includeCustomRepresentation);
            }
            buffer.WriteByte((byte)']');
        }

        buffer.WriteByte((byte)'}');
    }

    #endregion

    #region Field writing

    /// <summary>
    /// The text of a field whose custom text the export does not write: always absent. A static readonly default is the target of
    /// the by-reference text of a switched-off field, so the text switch costs no read of the field's own text slot.
    /// </summary>
    private static readonly LazyString _AbsentText;

    /// <summary>
    /// Writes a single field in compact format with deduplication and same-as-previous, then its children recursively
    /// (recursion depth equals the nesting depth of the field tree).
    /// </summary>
    /// <param name="field">The field to write.</param>
    /// <param name="buffer">Target output buffer.</param>
    /// <param name="state">Mutable exporter state for deduplication tracking.</param>
    /// <param name="includeCustomText">Whether the <c>CT</c> key is written (only when the field has a text).</param>
    /// <param name="includeCustomRepresentation">Whether the <c>CR</c> key is written (only when the value has a representation).</param>
    private static void _WriteFieldCompact(
        Field field, ref PooledBuffer buffer, JsonExporterState state, bool includeCustomText, bool includeCustomRepresentation)
    {
        buffer.WriteByte((byte)'{');

        int fieldIdValue = field.FieldId.Value;

        // FI — always present
        buffer.Write(_FieldKeyFi);
        JsonHelpers.WriteI64(ref buffer, fieldIdValue);

        // NA, UI, TY — only on first occurrence via bitmask. Insert is a test-and-set, so the id is marked as seen even
        // when the field has no FieldInfo (then no NA/UI/TY is ever written for that id).
        bool isFirstOccurrence = state.FieldSeen.Insert(fieldIdValue);
        if (isFirstOccurrence)
        {
            FieldInfo? info = field.FieldInfo;
            if (info is not null)
            {
                buffer.Write(_FieldKeyNa);
                JsonHelpers.WriteJsonString(ref buffer, info.Name);
                buffer.Write(_FieldKeyUi);
                JsonHelpers.WriteJsonString(ref buffer, info.UiName);
                buffer.Write(_FieldKeyTy);
                // Compact format keeps the numeric enum value for minimum payload size.
                JsonHelpers.WriteU64(ref buffer, (ulong)info.FieldType);
            }
        }

        // Same-as-previous on the typed value and the two texts. CompareAndUpdate both compares with and overwrites the stored
        // previous values, so it runs exactly once per field occurrence, for every field (also when nothing will be emitted), to
        // keep "previous" equal to the last occurrence in write order. Comparing a present text builds it (LazyString.Equals
        // compares strings); a text that is not flagged is written anyway, so the build is not extra work.
        // A switched-off text is neither read nor compared: the value loses its representation and the text is the absent default,
        // so the previous-value store keeps absent texts for it and no deferred text of the dissector is ever built.
        FieldValue value = field.Value;
        if (!includeCustomRepresentation)
        {
            value = value.WithCustomRepresentation(default);
        }

        LazyString valueCustomRepresentation = value.CustomRepresentation;
        ref readonly LazyString customText = ref (includeCustomText
            ? ref field.CustomText
            : ref _AbsentText);

        // Perf: building the custom text here, through the reference, replaces the deferred object in the field by its string
        // before the copy is taken: the previous-value store then keeps a plain string across packets instead of a deferred
        // object with its captured state.
        if (!customText.IsNull)
        {
            _ = customText.AsString;
        }

        uint fieldSameFlags = state.PreviousFields.CompareAndUpdate(
            fieldIdValue, value, valueCustomRepresentation, customText);

        // VA — value (only if not same as previous)
        if (value.Type != FieldType.None)
        {
            if ((fieldSameFlags & SameFlags.FieldSameValue) == 0)
            {
                buffer.Write(_FieldKeyVa);
                JsonHelpers.WriteFieldValue(ref buffer, value);
            }
        }

        // CR — custom representation text of the field value (materialize only when emitting)
        if (!valueCustomRepresentation.IsNull && (fieldSameFlags & SameFlags.FieldSameCustomRepresentation) == 0)
        {
            buffer.Write(_FieldKeyCr);
            JsonHelpers.WriteJsonString(ref buffer, valueCustomRepresentation.AsString);
        }

        // CT — custom text
        if (!customText.IsNull && (fieldSameFlags & SameFlags.FieldSameCustomText) == 0)
        {
            buffer.Write(_FieldKeyCt);
            JsonHelpers.WriteJsonString(ref buffer, customText.AsString);
        }

        // SF — field same flags
        if (fieldSameFlags != 0)
        {
            buffer.Write(_FieldKeySf);
            JsonHelpers.WriteU64(ref buffer, fieldSameFlags);
        }

        // CH — children (recursive); materialize: true so nested lazy fields are exported.
        if (field.HasChildren(materialize: true))
        {
            buffer.Write(_FieldKeyCh);
            bool first = true;
            foreach (Field child in field.Children(materialize: true))
            {
                if (!first)
                {
                    buffer.WriteByte((byte)',');
                }
                first = false;
                _WriteFieldCompact(child, ref buffer, state, includeCustomText, includeCustomRepresentation);
            }
            buffer.WriteByte((byte)']');
        }

        buffer.WriteByte((byte)'}');
    }

    #endregion
}

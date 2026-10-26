// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Writes packets in compact JSON format with short single/two-character keys
/// and same-as-previous optimization for minimum output size.
/// <para>
/// Packet keys: <c>ID</c>, <c>TS</c>, <c>IN</c>, <c>SF</c>, <c>CH</c>.
/// Field keys: <c>FI</c>, <c>NA</c>, <c>UI</c>, <c>TY</c>, <c>VA</c>, <c>CR</c>, <c>CT</c>, <c>SF</c>, <c>CH</c>.
/// </para>
/// <para>
/// <b>Size reductions (the reason for this format):</b> short keys; the field name, UI name and type only on the first
/// occurrence of a field id (<c>NA</c>, <c>UI</c>, <c>TY</c>); and a same-as-previous mask <c>SF</c> that replaces a value,
/// custom representation, custom text, or packet info string identical to the previous occurrence. "Previous" is the last
/// occurrence in write order over the whole document (kept in <see cref="JsonExporterState"/>), so the output can only be
/// decoded by replaying it from the start. The packet timestamp <c>TS</c> is nanoseconds since the Unix epoch; such values
/// exceed 2^53, so consumers that parse JSON numbers as IEEE doubles lose precision.
/// </para>
/// <para>
/// <b>Thread safety:</b> Stateless apart from the <see cref="JsonExporterState"/> passed in; not thread-safe for one state.
/// </para>
/// </summary>
internal static class CompactWriter
{
    // Key literals are UTF-8 spans over static data, each with its leading comma (except the first key of an object), so
    // writing a key is a single buffer append with no formatting and no allocation.
    // Packet-level keys
    private static ReadOnlySpan<byte> _KeyId => "\"ID\":"u8;
    private static ReadOnlySpan<byte> _KeyTs => ",\"TS\":"u8;
    private static ReadOnlySpan<byte> _KeyIn => ",\"IN\":"u8;
    private static ReadOnlySpan<byte> _KeySf => ",\"SF\":"u8;
    private static ReadOnlySpan<byte> _KeyCh => ",\"CH\":["u8;

    // Field-level keys
    private static ReadOnlySpan<byte> _FieldKeyFi => "\"FI\":"u8;
    private static ReadOnlySpan<byte> _FieldKeyNa => ",\"NA\":"u8;
    private static ReadOnlySpan<byte> _FieldKeyUi => ",\"UI\":"u8;
    private static ReadOnlySpan<byte> _FieldKeyTy => ",\"TY\":"u8;
    private static ReadOnlySpan<byte> _FieldKeyVa => ",\"VA\":"u8;
    private static ReadOnlySpan<byte> _FieldKeyCr => ",\"CR\":"u8;
    private static ReadOnlySpan<byte> _FieldKeyCt => ",\"CT\":"u8;
    private static ReadOnlySpan<byte> _FieldKeySf => ",\"SF\":"u8;
    private static ReadOnlySpan<byte> _FieldKeyCh => ",\"CH\":["u8;

    /// <summary>
    /// Writes a single packet in compact JSON format.
    /// Uses same-as-previous optimization and field-info deduplication via the state object.
    /// </summary>
    /// <param name="packet">The packet to serialize.</param>
    /// <param name="buffer">Target output buffer.</param>
    /// <param name="state">Mutable exporter state for deduplication tracking.</param>
    /// <exception cref="InvalidOperationException">The packet has no field tree (a skip packet).</exception>
    /// <remarks>
    /// The state is updated while writing, before the caller knows whether the buffer reaches the output. If a later write
    /// fails and the packet is skipped, the state still describes that packet, so following same-as-previous references
    /// can point at a packet that is not in the file.
    /// </remarks>
    internal static void WritePacket(Packet packet, ref PooledBuffer buffer, JsonExporterState state)
    {
        // A skip packet has no parsed field tree to serialize: fail loudly instead of writing an empty object.
        if (!packet.HasFieldTree)
        {
            throw new InvalidOperationException("Skip packets cannot be exported (HasFieldTree is false).");
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
        string info = packet.Info;
        uint packetSameFlags = 0;
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

        // SF — emit packet same-as-previous flags if any
        if (packetSameFlags != 0)
        {
            buffer.Write(_KeySf);
            JsonHelpers.WriteU64(ref buffer, packetSameFlags);
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
                _WriteFieldCompact(child, ref buffer, state);
            }
            buffer.WriteByte((byte)']');
        }

        buffer.WriteByte((byte)'}');
    }

    /// <summary>
    /// Writes a single field in compact format with deduplication and same-as-previous, then its children recursively
    /// (recursion depth equals the nesting depth of the field tree).
    /// </summary>
    private static void _WriteFieldCompact(Field field, ref PooledBuffer buffer, JsonExporterState state)
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

        // Compute same-as-previous with typed FieldValue + LazyString (no AsString until emit). CompareAndUpdate both
        // compares with and overwrites the stored previous values, so it must run exactly once per field occurrence, for
        // every field (also when nothing will be emitted), to keep "previous" equal to the last occurrence in write order.
        // The LazyString values are compared without being materialized; only the text that is actually written is.
        FieldValue value = field.Value;
        LazyString valueCustomRepresentation = value.CustomRepresentation;
        LazyString customText = field.CustomText;

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
                _WriteFieldCompact(child, ref buffer, state);
            }
            buffer.WriteByte((byte)']');
        }

        buffer.WriteByte((byte)'}');
    }
}

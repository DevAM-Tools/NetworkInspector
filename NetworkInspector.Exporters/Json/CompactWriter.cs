// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Json;

/// <summary>
/// Writes packets in compact JSON format with short single/two-character keys
/// and same-as-previous optimization for minimum output size.
/// <para>
/// Packet keys: <c>ID</c>, <c>TS</c>, <c>IN</c>, <c>SF</c>, <c>CH</c>.
/// Field keys: <c>FI</c>, <c>NA</c>, <c>UI</c>, <c>TY</c>, <c>VA</c>, <c>CR</c>, <c>CT</c>, <c>SF</c>, <c>CH</c>.
/// </para>
/// </summary>
internal static class CompactWriter
{
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
    internal static void WritePacket(Packet packet, ref PooledBuffer buffer, JsonExporterState state)
    {
        buffer.WriteByte((byte)'{');

        // ID — always present
        buffer.Write(_KeyId);
        JsonHelpers.WriteI64(ref buffer, packet.Id.Value);

        // TS — always present
        buffer.Write(_KeyTs);
        JsonHelpers.WriteI64(ref buffer, packet.Timestamp.AsNanos);

        // IN — info string with same-as-previous optimization
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

    /// <summary>Writes a single field in compact format with deduplication and same-as-previous.</summary>
    private static void _WriteFieldCompact(Field field, ref PooledBuffer buffer, JsonExporterState state)
    {
        buffer.WriteByte((byte)'{');

        int fieldIdValue = field.FieldId.Value;

        // FI — always present
        buffer.Write(_FieldKeyFi);
        JsonHelpers.WriteI64(ref buffer, fieldIdValue);

        // NA, UI, TY — only on first occurrence via bitmask
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

        // Compute same-as-previous with typed FieldValue + LazyString (no AsString until emit).
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

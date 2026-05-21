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
    private static ReadOnlySpan<byte> KeyId => "\"ID\":"u8;
    private static ReadOnlySpan<byte> KeyTs => ",\"TS\":"u8;
    private static ReadOnlySpan<byte> KeyIn => ",\"IN\":"u8;
    private static ReadOnlySpan<byte> KeySf => ",\"SF\":"u8;
    private static ReadOnlySpan<byte> KeyCh => ",\"CH\":["u8;

    // Field-level keys
    private static ReadOnlySpan<byte> FieldKeyFi => "\"FI\":"u8;
    private static ReadOnlySpan<byte> FieldKeyNa => ",\"NA\":"u8;
    private static ReadOnlySpan<byte> FieldKeyUi => ",\"UI\":"u8;
    private static ReadOnlySpan<byte> FieldKeyTy => ",\"TY\":"u8;
    private static ReadOnlySpan<byte> FieldKeyVa => ",\"VA\":"u8;
    private static ReadOnlySpan<byte> FieldKeyCr => ",\"CR\":"u8;
    private static ReadOnlySpan<byte> FieldKeyCt => ",\"CT\":"u8;
    private static ReadOnlySpan<byte> FieldKeySf => ",\"SF\":"u8;
    private static ReadOnlySpan<byte> FieldKeyCh => ",\"CH\":["u8;

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
        buffer.Write(KeyId);
        JsonHelpers.WriteI64(ref buffer, packet.Id.Value);

        // TS — always present
        buffer.Write(KeyTs);
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
                buffer.Write(KeyIn);
                JsonHelpers.WriteJsonString(ref buffer, info);
            }
        }
        state.PreviousPacketInfo = info;

        // SF — emit packet same-as-previous flags if any
        if (packetSameFlags != 0)
        {
            buffer.Write(KeySf);
            JsonHelpers.WriteU64(ref buffer, packetSameFlags);
        }

        // CH — children (fields)
        Field root = packet.RootField();
        if (root.HasChildren)
        {
            buffer.Write(KeyCh);
            bool first = true;
            foreach (Field child in root.Children())
            {
                if (!first)
                {
                    buffer.WriteByte((byte)',');
                }
                first = false;
                WriteFieldCompact(child, ref buffer, state);
            }
            buffer.WriteByte((byte)']');
        }

        buffer.WriteByte((byte)'}');
    }

    /// <summary>Writes a single field in compact format with deduplication and same-as-previous.</summary>
    private static void WriteFieldCompact(Field field, ref PooledBuffer buffer, JsonExporterState state)
    {
        buffer.WriteByte((byte)'{');

        int fieldIdValue = field.FieldId.Value;

        // FI — always present
        buffer.Write(FieldKeyFi);
        JsonHelpers.WriteI64(ref buffer, fieldIdValue);

        // NA, UI, TY — only on first occurrence via bitmask
        bool isFirstOccurrence = state.FieldSeen.Insert(fieldIdValue);
        if (isFirstOccurrence)
        {
            FieldInfo? info = field.FieldInfo;
            if (info is not null)
            {
                buffer.Write(FieldKeyNa);
                JsonHelpers.WriteJsonString(ref buffer, info.Name);
                buffer.Write(FieldKeyUi);
                JsonHelpers.WriteJsonString(ref buffer, info.UiName);
                buffer.Write(FieldKeyTy);
                JsonHelpers.WriteU64(ref buffer, (ulong)info.FieldType);
            }
        }

        // Compute same-as-previous for field value, value custom text, and custom text
        FieldValue value = field.Value;
        string? valueStr = value.Type != FieldType.None ? FormatFieldValue(value) : null;
        string? valueCustomRepresentation = !value.CustomRepresentation.IsNull ? value.CustomRepresentation.AsString : null;
        LazyString customText = field.CustomText;
        string? customTextStr = !customText.IsNull ? customText.AsString : null;

        uint fieldSameFlags = state.PreviousFields.CompareAndUpdate(
            fieldIdValue, valueStr, valueCustomRepresentation, customTextStr);

        // VA — value (only if not same as previous)
        if (value.Type != FieldType.None)
        {
            if ((fieldSameFlags & SameFlags.FieldSameValue) == 0)
            {
                buffer.Write(FieldKeyVa);
                JsonHelpers.WriteFieldValue(ref buffer, value);
            }
        }

        // CR — custom representation text of the field value
        if (valueCustomRepresentation is not null && (fieldSameFlags & SameFlags.FieldSameCustomRepresentation) == 0)
        {
            buffer.Write(FieldKeyCr);
            JsonHelpers.WriteJsonString(ref buffer, valueCustomRepresentation);
        }

        // CT — custom text
        if (customTextStr is not null && (fieldSameFlags & SameFlags.FieldSameCustomText) == 0)
        {
            buffer.Write(FieldKeyCt);
            JsonHelpers.WriteJsonString(ref buffer, customTextStr);
        }

        // SF — field same flags
        if (fieldSameFlags != 0)
        {
            buffer.Write(FieldKeySf);
            JsonHelpers.WriteU64(ref buffer, fieldSameFlags);
        }

        // CH — children (recursive)
        if (field.HasChildren)
        {
            buffer.Write(FieldKeyCh);
            bool first = true;
            foreach (Field child in field.Children())
            {
                if (!first)
                {
                    buffer.WriteByte((byte)',');
                }
                first = false;
                WriteFieldCompact(child, ref buffer, state);
            }
            buffer.WriteByte((byte)']');
        }

        buffer.WriteByte((byte)'}');
    }

    /// <summary>
    /// Formats a field value to a string representation for same-as-previous comparison.
    /// </summary>
    private static string? FormatFieldValue(FieldValue value) => FieldValueFormatter.Format(value);
}

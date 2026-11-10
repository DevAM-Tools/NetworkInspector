// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// The default display line of a field: the one implementation behind <c>ToString</c> and <c>TryFormat</c> of <see cref="Field"/>
/// and <see cref="MutField"/>, so the two cursors cannot show different lines.
/// <para>
/// The rule is decided with the public parts of a field only, in this order:
/// </para>
/// <list type="number">
/// <item>A custom text is set (<see cref="LazyString.IsNull"/> is false): the custom text alone is the whole line, also an empty
/// one. A dissector that wants the label writes it into the text.</item>
/// <item>The value is of type <see cref="FieldType.None"/> (a container): the label.</item>
/// <item>Otherwise: the label, <c>": "</c>, and the value's default string (see <see cref="FieldValue.ToString()"/>: the custom
/// representation, else the default text of the data).</item>
/// </list>
/// <para>
/// The label is <see cref="Infos.FieldInfo.UiName"/>, which is never empty because registration refuses an empty UI name, so a
/// known field always has a label. A field id that the stack does not know has no <see cref="Infos.FieldInfo"/> and therefore no label: its
/// line is the value part alone (no <c>": "</c> in front), the empty line for a value of type <see cref="FieldType.None"/>; a
/// custom text still wins.
/// </para>
/// <para>
/// <b>Thread-safety:</b> stateless. Reads follow the packet's concurrency contract; a deferred custom text is built once however
/// many threads read it (see <see cref="LazyString"/>).
/// </para>
/// </summary>
/// <remarks>
/// A caller that wants an own line reads the parts instead: <c>FieldInfo.UiName</c>, <c>Value</c> (<c>Data</c>,
/// <c>CustomRepresentation</c>), and <c>CustomText</c>.
/// <para>
/// Security: the line is not escaped. A custom text or a string value can carry any character the capture holds (control
/// characters, terminal escape sequences, formula prefixes). Escaping here would change the line every reader rebuilds, and only
/// the consumer knows the target (terminal, CSV cell, markup), so each consumer escapes for its own target (the Text and CSV
/// exporters do). Rejected: escaping in the line. Residual risk: a caller that writes the line unescaped to a terminal or a
/// spreadsheet.
/// </para>
/// The rule is written out in three places, one per way of writing the line: the string and the two span overloads here (they share
/// the case order and differ only in the target), and the Text exporter, which writes the same parts from staged values with its own
/// escaping and text limit. Rejected: one shared decision type that all of them ask. The rule is three comparisons, and sharing it
/// would link a source file into the Exporters assembly (a coupling the compiler checks, but a heavier build layout than the rule
/// is worth); the tests compare the lines instead, so a drift fails there.
/// <para>
/// Sync: EXPORTER_GUIDE § Rebuilding the display line (a reader of an export applies the same rule), <c>FieldStringRepresentationTests</c>
/// (they pin the cases and the default texts), and <c>TextExporter._WriteLineFromParts</c>, which writes the same cases from staged
/// values (the Text tests compare its lines with <c>Field.ToString()</c> of every field of real packets).
/// </para>
/// </remarks>
/// <seealso cref="Field.ToString()"/>
/// <seealso cref="MutField.ToString()"/>
/// <seealso cref="FieldValue.ToString()"/>
internal static class FieldDisplayLine
{
    #region Line

    /// <summary>Builds the display line of a field as one string.</summary>
    /// <param name="packet">The owning packet; <see langword="null"/> for a default cursor.</param>
    /// <param name="storageIndex">The cursor's storage index.</param>
    /// <param name="fieldId">The cursor's field id.</param>
    /// <returns>
    /// The line. Never throws: an invalid cursor (no packet, or <see cref="FieldBody.NullIndex"/>) gives the empty string; a
    /// non-root cursor of a skip-tree packet, which keeps no value or text, gives the label alone. Allocates the result string
    /// (and one temporary for the value text); use <see cref="TryFormat(Packet?, ushort, FieldId, Span{char}, out int)"/> on hot paths.
    /// </returns>
    /// <remarks>
    /// Reading builds a deferred custom text and caches it in the field's own slot (the parts are read through references, never
    /// through copies).
    /// </remarks>
    internal static string GetLine(Packet? packet, ushort storageIndex, FieldId fieldId)
    {
        // Sync: Field.IsValid and MutField.IsValid (the same condition). A default cursor has nothing to show.
        if (packet is null || storageIndex == FieldBody.NullIndex)
        {
            return string.Empty;
        }

        // A cursor of a skip-tree packet that is not the root keeps no value or text, so the label alone is all it can show; the
        // guard keeps the throwing getters of Value and CustomText and the slot of an in-flight populator out of reach.
        string label = _LabelOf(packet, fieldId);
        if (!_HasBody(packet, storageIndex))
        {
            return label;
        }

        // Case 1: a set custom text is the whole line, also an empty one. Reading it through the reference caches a deferred text in
        // its slot.
        ref readonly FieldBody body = ref packet.GetFieldRef(storageIndex);
        ref readonly FieldValue value = ref body.Value;
        if (!body.CustomText.IsNull)
        {
            return body.CustomText.AsString;
        }

        // Case 2: no value (a container) shows its label alone; an unknown id has no label, so such a line is empty.
        if (value.Type == FieldType.None)
        {
            return label;
        }

        // Case 3: label, separator, and value; without a label the value stands alone (a separator in front of nothing would be
        // noise). Concat costs one temporary for the value text; every consumer that cares about allocations uses TryFormat into
        // its own buffer.
        return label.Length > 0
            ? string.Concat(label, _Separator, value.ToString(null, CultureInfo.InvariantCulture))
            : value.ToString(null, CultureInfo.InvariantCulture);
    }

    /// <summary>Formats the display line of a field into a character span.</summary>
    /// <param name="packet">The owning packet; <see langword="null"/> for a default cursor.</param>
    /// <param name="storageIndex">The cursor's storage index.</param>
    /// <param name="fieldId">The cursor's field id.</param>
    /// <param name="destination">Target span.</param>
    /// <param name="charsWritten">Characters written; 0 when the method returns <see langword="false"/>.</param>
    /// <returns>
    /// <see langword="false"/> when <paramref name="destination"/> is too small (its contents are then unspecified). The cases
    /// that need no space follow <see cref="GetLine"/>: an invalid cursor is the empty line (true, 0 written), a non-root
    /// cursor of a skip-tree packet the label alone.
    /// </returns>
    /// <remarks>Perf: writes straight into <paramref name="destination"/>; no allocation.</remarks>
    internal static bool TryFormat(Packet? packet, ushort storageIndex, FieldId fieldId, Span<char> destination, out int charsWritten)
    {
        if (packet is null || storageIndex == FieldBody.NullIndex)
        {
            charsWritten = 0;
            return true;
        }

        string label = _LabelOf(packet, fieldId);
        if (!_HasBody(packet, storageIndex))
        {
            return _TryCopy(label, destination, out charsWritten);
        }

        // The three cases of GetLine, in the same order: custom text alone, label alone for a container, else label, separator, value.
        ref readonly FieldBody body = ref packet.GetFieldRef(storageIndex);
        ref readonly FieldValue value = ref body.Value;
        if (!body.CustomText.IsNull)
        {
            return _TryCopy(body.CustomText.AsSpan, destination, out charsWritten);
        }

        if (value.Type == FieldType.None)
        {
            return _TryCopy(label, destination, out charsWritten);
        }

        // Label and separator, then the value text into the rest. A registered name plus two characters cannot overflow an int.
        int prefix = 0;
        if (label.Length > 0)
        {
            prefix = label.Length + _Separator.Length;
            if (destination.Length < prefix)
            {
                charsWritten = 0;
                return false;
            }

            label.CopyTo(destination);
            _Separator.CopyTo(destination[label.Length..]);
        }

        if (!value.TryFormat(destination[prefix..], out int valueWritten, default, CultureInfo.InvariantCulture))
        {
            charsWritten = 0;
            return false;
        }

        charsWritten = prefix + valueWritten;
        return true;
    }

    /// <summary>
    /// Formats the display line of a field as UTF-8; the same cases and failure contract as the character overload. The label and
    /// a custom text are transcoded with <see cref="Encoding.TryGetBytes(ReadOnlySpan{char}, Span{byte}, out int)"/> (which
    /// measures first and writes nothing when the text does not fit), the value part is
    /// <see cref="FieldValue.TryFormat(Span{byte}, out int, ReadOnlySpan{char}, IFormatProvider?)"/>.
    /// </summary>
    /// <param name="packet">The owning packet; <see langword="null"/> for a default cursor.</param>
    /// <param name="storageIndex">The cursor's storage index.</param>
    /// <param name="fieldId">The cursor's field id.</param>
    /// <param name="utf8Destination">Target span.</param>
    /// <param name="bytesWritten">Bytes written; 0 when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="false"/> when <paramref name="utf8Destination"/> is too small; its contents are then unspecified.</returns>
    /// <remarks>Perf: writes straight into <paramref name="utf8Destination"/>; no allocation.</remarks>
    internal static bool TryFormat(Packet? packet, ushort storageIndex, FieldId fieldId, Span<byte> utf8Destination, out int bytesWritten)
    {
        if (packet is null || storageIndex == FieldBody.NullIndex)
        {
            bytesWritten = 0;
            return true;
        }

        string label = _LabelOf(packet, fieldId);
        if (!_HasBody(packet, storageIndex))
        {
            return _TryTranscode(label, utf8Destination, out bytesWritten);
        }

        // The three cases of GetLine, in the same order: custom text alone, label alone for a container, else label, separator, value.
        ref readonly FieldBody body = ref packet.GetFieldRef(storageIndex);
        ref readonly FieldValue value = ref body.Value;
        if (!body.CustomText.IsNull)
        {
            return _TryTranscode(body.CustomText.AsSpan, utf8Destination, out bytesWritten);
        }

        if (value.Type == FieldType.None)
        {
            return _TryTranscode(label, utf8Destination, out bytesWritten);
        }

        // Label (a measured transcode: false means too small and writes nothing), then the ASCII separator, then the value text.
        int prefix = 0;
        if (label.Length > 0)
        {
            if (!Encoding.UTF8.TryGetBytes(label, utf8Destination, out int labelBytes))
            {
                bytesWritten = 0;
                return false;
            }

            // Sync: _Separator (the same two characters as UTF-8 bytes); a literal keeps this writer free of a transcode of a constant.
            ReadOnlySpan<byte> separator = ": "u8;
            if (utf8Destination.Length - labelBytes < separator.Length)
            {
                bytesWritten = 0;
                return false;
            }

            separator.CopyTo(utf8Destination[labelBytes..]);
            prefix = labelBytes + separator.Length;
        }

        if (!value.TryFormat(utf8Destination[prefix..], out int valueWritten, default, CultureInfo.InvariantCulture))
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = prefix + valueWritten;
        return true;
    }

    #endregion

    #region Parts

    /// <summary>The text between the label and the value of a line.</summary>
    /// <remarks>
    /// Sync: the UTF-8 literal <c>": "u8</c> in the byte overload of <see cref="TryFormat(Packet?, ushort, FieldId, Span{byte}, out int)"/>
    /// (the same two characters), <c>TextExporter</c>'s separator, and EXPORTER_GUIDE § Rebuilding the display line (the reader's
    /// <c>: </c>).
    /// </remarks>
    private const string _Separator = ": ";

    /// <summary>Whether the cursor addresses a stored body whose value and custom text the line may read.</summary>
    /// <param name="packet">The owning packet.</param>
    /// <param name="storageIndex">The cursor's storage index.</param>
    /// <returns>
    /// <see langword="true"/> for every cursor of a packet with a field tree and for the root (index 0) of a
    /// <see cref="FieldTreeMode.Skip"/> packet. <see langword="false"/> for any other cursor of a skip-tree packet, the index
    /// <see cref="FieldBody.SkipStorageIndex"/> included: such a packet keeps no field slab, and the slot of that index is only the
    /// scratch body of a populator in flight, never a stored field, so the line must not read it.
    /// </returns>
    /// <remarks>
    /// The line never throws, so it cannot use the guard of <c>Field.Value</c> as it is: that guard lets the scratch index pass and
    /// <c>GetFieldRef</c> then throws outside a populator. Sync: the skip-packet guard of <c>Value</c> and <c>CustomText</c> in
    /// <c>Field</c> and <c>MutField</c> (the same rule, without the scratch index).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _HasBody(Packet packet, ushort storageIndex) => packet.HasFieldTree
        || storageIndex == 0;

    /// <summary>The label part of the line.</summary>
    /// <param name="packet">The owning packet; its stack knows the field id.</param>
    /// <param name="fieldId">The cursor's field id.</param>
    /// <returns>
    /// The registered UI name, which is never empty; the empty string (no label) for an id
    /// that the stack does not know, which only a foreign stack or a hand-made id can produce.
    /// </returns>
    private static string _LabelOf(Packet packet, FieldId fieldId) => packet.Stack.GetField(fieldId)?.UiName
        ?? string.Empty;

    /// <summary>Copies <paramref name="text"/> into <paramref name="destination"/> when it fits; writes nothing otherwise.</summary>
    /// <param name="text">The text.</param>
    /// <param name="destination">Target span.</param>
    /// <param name="charsWritten">The text length; 0 when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="false"/> when <paramref name="destination"/> is too small.</returns>
    private static bool _TryCopy(ReadOnlySpan<char> text, Span<char> destination, out int charsWritten)
    {
        if (!text.TryCopyTo(destination))
        {
            charsWritten = 0;
            return false;
        }

        charsWritten = text.Length;
        return true;
    }

    /// <summary>Transcodes <paramref name="text"/> to UTF-8 into <paramref name="destination"/> when it fits; writes nothing otherwise.</summary>
    /// <param name="text">The text.</param>
    /// <param name="destination">Target span.</param>
    /// <param name="bytesWritten">The UTF-8 length; 0 when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="false"/> when <paramref name="destination"/> is too small.</returns>
    private static bool _TryTranscode(ReadOnlySpan<char> text, Span<byte> destination, out int bytesWritten)
    {
        // TryGetBytes measures first, so a destination that is too small stays untouched and reports 0 bytes.
        if (!Encoding.UTF8.TryGetBytes(text, destination, out bytesWritten))
        {
            bytesWritten = 0;
            return false;
        }

        return true;
    }

    #endregion
}

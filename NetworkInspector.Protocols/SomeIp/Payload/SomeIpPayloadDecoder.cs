// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp.Payload;

/// <summary>
/// The state of one payload decode: what is decoded, with which limits, and how far it got. A <c>ref struct</c> because it is
/// created on the stack by the populator, passed by reference through the recursion, and never outlives the decode; it holds the
/// payload memory but allocates nothing.
/// <para>
/// The two limits are trust-boundary limits (see <see cref="SomeIpLimits"/>): the nesting depth and the node count bound the work
/// and the memory of a decode, whatever the description and the payload say. Reaching one is an expected failure, reported as a
/// diagnostic that names the setting and the value, and it stops the decode: after a limit nothing is decoded any more, because
/// the position behind the cut-off value is unreliable.
/// </para>
/// </summary>
internal ref struct SomeIpDecodeState
{
    /// <summary>Creates the state of one decode.</summary>
    /// <param name="model">The compiled model (types and sites).</param>
    /// <param name="ids">The fixed payload fields.</param>
    /// <param name="data">The payload bytes; offsets of the decode are relative to its start.</param>
    /// <param name="transform">The serialization defaults of the member.</param>
    /// <param name="metadata">Whether the metadata nodes (type, length, padding, count) are appended.</param>
    /// <param name="maxDepth">The nesting depth limit.</param>
    /// <param name="maxNodes">The node limit.</param>
    internal SomeIpDecodeState(
        SomeIpCompiledModel model,
        SomeIpPayloadFieldIds ids,
        ReadOnlyMemory<byte> data,
        in SomeIpTransformation transform,
        bool metadata,
        int maxDepth,
        int maxNodes)
    {
        Model = model;
        Ids = ids;
        Data = data;
        Transform = transform;
        Metadata = metadata;
        MaxDepth = maxDepth;
        MaxNodes = maxNodes;
        Depth = 0;
        Nodes = 0;
        Stopped = false;
        DataShort = false;
        TagPending = false;
        Tag = default;
    }

    /// <summary>The compiled model.</summary>
    internal readonly SomeIpCompiledModel Model;

    /// <summary>The fixed payload fields.</summary>
    internal readonly SomeIpPayloadFieldIds Ids;

    /// <summary>The payload bytes.</summary>
    internal readonly ReadOnlyMemory<byte> Data;

    /// <summary>The serialization defaults of the member.</summary>
    internal readonly SomeIpTransformation Transform;

    /// <summary>Whether the metadata nodes are appended.</summary>
    internal readonly bool Metadata;

    /// <summary>The nesting depth limit (setting <c>someip.max_type_depth</c>).</summary>
    internal readonly int MaxDepth;

    /// <summary>The node limit (setting <c>someip.max_payload_nodes</c>).</summary>
    internal readonly int MaxNodes;

    /// <summary>The number of containers that are open right now.</summary>
    internal int Depth;

    /// <summary>The number of value and container nodes appended so far; diagnostics and metadata are not counted.</summary>
    internal int Nodes;

    /// <summary>True once a limit was reached: nothing is decoded any more.</summary>
    internal bool Stopped;

    /// <summary>
    /// True once a diagnostic about missing or unusable bytes was appended. The first shortage is the cause and the later ones are its
    /// consequences (every value behind a cut-off one is missing too, and so is the remainder behind a tag that hides the position of
    /// the next one); one diagnostic per payload says it.
    /// </summary>
    internal bool DataShort;

    /// <summary>True while the tag of the TLV member being decoded still has to be shown (metadata view).</summary>
    internal bool TagPending;

    /// <summary>The tag of the TLV member being decoded; meaningful while <see cref="TagPending"/> is true.</summary>
    internal SomeIpTlvTag Tag;
}

/// <summary>
/// Decodes a payload into typed fields by walking the usage sites of a parameter list: base types, strings, arrays, structures,
/// padding, and the diagnostics for everything that does not fit.
/// <para>
/// <b>Contract:</b> a decode never throws for any payload. Every failure becomes a child field <c>someip.payload.error</c> next to
/// the node it concerns and a defined consumed count (what fits), so the siblings are decoded from a known position or, when the
/// position is unknown, not at all. The bytes behind the last decoded value are shown as <c>someip.payload.unparsed</c>.
/// </para>
/// <para>
/// <b>Why recursion with a depth limit:</b> a description may nest types and the payload may repeat them, so the recursion depth
/// is attacker-influenced; the limit <c>someip.max_type_depth</c> bounds the stack use of one decode. The compiler already bounds
/// the depth of the description itself, the limit here bounds what a decode is willing to follow. Rejected alternative: an explicit
/// stack, which would replace the call stack with a heap allocation per decode to protect against a depth the setting already caps.
/// </para>
/// <para>
/// <b>Why a node budget:</b> an array of one-byte elements turns every payload byte into a field; a 64 MiB message would append
/// 64 million fields. The budget <c>someip.max_payload_nodes</c> bounds the fields of one decode, and the host's own cap per
/// packet is a second line behind it.
/// </para>
/// <para><b>Thread-safety:</b> stateless; all state lives in the <see cref="SomeIpDecodeState"/> of the call.</para>
/// </summary>
internal static class SomeIpPayloadDecoder
{
    #region Constants

    /// <summary>The setting key named by the depth diagnostic.</summary>
    private const string _DepthKey = "someip.max_type_depth";

    /// <summary>The setting key named by the node diagnostic.</summary>
    private const string _NodesKey = "someip.max_payload_nodes";

    #endregion

    #region Public API

    /// <summary>
    /// Decodes a parameter list over the whole payload of <paramref name="state"/> and appends the nodes under
    /// <paramref name="parent"/>, then the remainder that no parameter used.
    /// </summary>
    /// <param name="parent">The payload container that receives the nodes.</param>
    /// <param name="state">The decode state; its data is the payload.</param>
    /// <param name="list">The parameter list of the member direction.</param>
    internal static void DecodeList(in MutField parent, ref SomeIpDecodeState state, in SomeIpParameterList list)
    {
        int end = state.Data.Length;
        int offset = 0;

        // A TLV list finds its members by the data id of their tags, in any order; there is no position to miss, so an absent
        // member is no anomaly. The walk returns how far it got, and the rest is the remainder below.
        if (list.IsTlv)
        {
            offset = _WalkTlv(in parent, ref state, list.ById, 0, end);
        }

        // A sequential list decodes its parameters one after the other; the position after one is the start of the next. A list
        // without parameters decodes nothing and leaves the whole payload as the remainder.
        for (int i = 0; i < list.SitesCount && !state.Stopped && !list.IsTlv; i++)
        {
            int siteIndex = list.SitesStart + i;
            if (offset >= end)
            {
                // The payload ends exactly before a parameter: the sender left it out, or an earlier shortage already said so.
                if (!state.DataShort)
                {
                    state.DataShort = true;
                    SomeIpUsageSite missing = state.Model.Sites.Span[siteIndex];
                    parent.Append(
                        state.Ids.Error,
                        FieldValue.NewString(string.Create(
                            CultureInfo.InvariantCulture,
                            $"The payload ends after {end} bytes, before the parameter '{missing.UiName}'.")));
                }

                break;
            }

            offset += _DecodeSite(in parent, ref state, siteIndex, offset, end, supplied: -1, element: -1);
        }

        // What no parameter used is shown, never dropped: the description may be older than the sender, and the bytes may be the
        // next field of a newer version. After a limit the count is already explained by the limit diagnostic.
        if (offset < end)
        {
            _ = parent.Append(state.Ids.Unparsed, FieldValue.NewBytes(state.Data.Slice(offset, end - offset)));
            if (!state.Stopped && !state.DataShort)
            {
                parent.Append(
                    state.Ids.Error,
                    FieldValue.NewString(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{end - offset} bytes remain after the last parameter.")));
            }
        }
    }

    #endregion

    #region Sites

    /// <summary>
    /// Decodes one usage site at <paramref name="offset"/>.
    /// </summary>
    /// <param name="parent">The node that receives the site's node(s).</param>
    /// <param name="state">The decode state.</param>
    /// <param name="siteIndex">Index of the site in the model.</param>
    /// <param name="offset">Start of the value in the payload.</param>
    /// <param name="end">Exclusive end of the range the value may use: the payload, or the extent of the enclosing container.</param>
    /// <param name="supplied">
    /// The byte length of the value when an enclosing tag fixed it (no inline length field is read then), otherwise -1.
    /// </param>
    /// <param name="element">The index of the value in its array for the display text, otherwise -1.</param>
    /// <returns>The number of bytes the value used, including its padding; never more than <c>end - offset</c>.</returns>
    private static int _DecodeSite(
        in MutField parent,
        ref SomeIpDecodeState state,
        int siteIndex,
        int offset,
        int end,
        int supplied,
        int element)
    {
        if (state.Stopped)
        {
            return 0;
        }

        SomeIpUsageSite site = state.Model.Sites.Span[siteIndex];

        // The limits come first: nothing is appended for a value that would exceed them, and the decode stops, because the
        // position behind a value that was not decoded is unknown.
        if (state.Nodes >= state.MaxNodes)
        {
            _Stop(in parent, ref state, string.Create(
                CultureInfo.InvariantCulture,
                $"The payload holds more than {state.MaxNodes} nodes (setting {_NodesKey}); '{site.UiName}' and everything behind it are not decoded."));
            return 0;
        }

        if (state.Depth >= state.MaxDepth)
        {
            _Stop(in parent, ref state, string.Create(
                CultureInfo.InvariantCulture,
                $"The types nest deeper than {state.MaxDepth} levels (setting {_DepthKey}); '{site.UiName}' and everything behind it are not decoded."));
            return 0;
        }

        // A dangling reference or a type the compiler could not build has no decoder. It consumes nothing, so the siblings behind
        // it are decoded as if it were absent; the marker names the missing type.
        if (site.TypeIndex < 0)
        {
            _Error(in parent, ref state, string.Create(
                CultureInfo.InvariantCulture,
                $"Type not available: '{site.ReferenceName}' (used by '{site.UiName}')."));
            return 0;
        }

        SomeIpTypeInfo type = state.Model.Types.Span[site.TypeIndex];

        // A value of a known extent of zero bytes has no content to read. A string, an array and a structure show as empty
        // containers by their own rules; every other type shows an empty node, so that a sender that sent the member without a
        // value is visible and not mistaken for one that left the member out.
        if (supplied == 0 && type.Category is not (SomeIpTypeCategory.Array or SomeIpTypeCategory.Structure or SomeIpTypeCategory.TlvStructure)
            && !(type.Category == SomeIpTypeCategory.Base && type.Primitive == SomeIpPrimitive.String))
        {
            _DecodeEmpty(in parent, ref state, site, element);
            return 0;
        }

        int consumed;
        switch (type.Category)
        {
            case SomeIpTypeCategory.Base when type.Primitive == SomeIpPrimitive.String:
                consumed = _DecodeString(in parent, ref state, site, type, offset, end, supplied, element);
                break;
            case SomeIpTypeCategory.Base:
                consumed = _DecodeBase(in parent, ref state, site, type, offset, end, element);
                break;
            case SomeIpTypeCategory.Array:
                consumed = _DecodeArray(in parent, ref state, site, type, offset, end, supplied, element);
                break;
            case SomeIpTypeCategory.Structure:
            case SomeIpTypeCategory.TlvStructure:
                consumed = _DecodeStructure(in parent, ref state, site, type, offset, end, supplied, element);
                break;
            case SomeIpTypeCategory.Union:
                consumed = _DecodeUnion(in parent, ref state, site, type, offset, end, supplied, element);
                break;
            case SomeIpTypeCategory.Bitfield:
                consumed = _DecodeBitfield(in parent, ref state, site, type, offset, end, element);
                break;
            default:
                _Error(in parent, ref state, string.Create(
                    CultureInfo.InvariantCulture,
                    $"Type not available: '{site.ReferenceName}' (used by '{site.UiName}')."));
                return 0;
        }

        // Alignment behind the value. A value that carries a tag-fixed length is not aligned: its extent is the tag's.
        return supplied >= 0 || state.Stopped
            ? consumed
            : consumed + _Pad(in parent, ref state, site, type, offset + consumed, end);
    }

    /// <summary>Skips the alignment bytes behind a value and, in the metadata view, shows them.</summary>
    private static int _Pad(in MutField parent, ref SomeIpDecodeState state, SomeIpUsageSite site, SomeIpTypeInfo type, int position, int end)
    {
        // The site wins over the type: the same type can be aligned differently where it is used.
        int pad = site.PadTo != 0
            ? site.PadTo
            : type.PadTo;
        if (pad <= 1)
        {
            return 0;
        }

        // Alignment is relative to the start of the payload. Computed in long so no near-limit position can wrap.
        long aligned = (((long)position + pad - 1) / pad) * pad;
        int skip = (int)(Math.Min(aligned, end) - position);
        if (skip > 0 && state.Metadata)
        {
            _ = parent.Append(state.Ids.Padding, FieldValue.NewBytes(state.Data.Slice(position, skip)));
        }

        return Math.Max(skip, 0);
    }

    #endregion

    #region Base types

    /// <summary>Decodes a fixed-width primitive.</summary>
    private static int _DecodeBase(
        in MutField parent,
        ref SomeIpDecodeState state,
        SomeIpUsageSite site,
        SomeIpTypeInfo type,
        int offset,
        int end,
        int element)
    {
        int width = _Width(type.Primitive);
        int available = end - offset;
        if (available < width)
        {
            // Everything that is left belongs to the cut-off value; nothing can follow it.
            _Short(in parent, ref state, string.Create(CultureInfo.InvariantCulture, $"'{site.UiName}' ({type.Name}) needs {width} bytes but only {available} remain."));
            return Math.Max(available, 0);
        }

        state.Nodes++;
        ReadOnlySpan<byte> bytes = state.Data.Span.Slice(offset, width);
        FieldId fieldId = site.FieldId;
        bool little = type.LittleEndian;
        MutField node;
        switch (type.Primitive)
        {
            case SomeIpPrimitive.UInt8:
            case SomeIpPrimitive.UInt16:
            case SomeIpPrimitive.UInt32:
            case SomeIpPrimitive.UInt64:
            {
                ulong value = _ReadUnsigned(bytes, little);
                string? name = null;
                _ = value <= long.MaxValue && type.Lookup is not null && type.Lookup.TryGetValue((long)value, out name);
                node = element < 0 && name is null
                    ? parent.Append(fieldId, FieldValue.NewU64(value))
                    : parent.AppendWithCustomText(fieldId, FieldValue.NewU64(value), new ElementPrefix(element), new NumberText(name, false, 0, value));
                break;
            }

            case SomeIpPrimitive.Int8:
            case SomeIpPrimitive.Int16:
            case SomeIpPrimitive.Int32:
            case SomeIpPrimitive.Int64:
            {
                long value = _ReadSigned(bytes, little);
                string? name = null;
                _ = type.Lookup is not null && type.Lookup.TryGetValue(value, out name);
                node = element < 0 && name is null
                    ? parent.Append(fieldId, FieldValue.NewI64(value))
                    : parent.AppendWithCustomText(fieldId, FieldValue.NewI64(value), new ElementPrefix(element), new NumberText(name, true, value, 0));
                break;
            }

            case SomeIpPrimitive.Float:
            {
                float value = BitConverter.Int32BitsToSingle(unchecked((int)_ReadUnsigned(bytes, little)));

                // The field holds the value widened to a double, which is exact; the text shows the shortest text of the single so
                // that 0.1 reads as 0.1 and not as 0.10000000149011612.
                node = parent.AppendWithCustomText(fieldId, FieldValue.NewF64(value), new ElementPrefix(element), new SingleText(value));
                break;
            }

            case SomeIpPrimitive.Double:
            {
                double value = BitConverter.Int64BitsToDouble(unchecked((long)_ReadUnsigned(bytes, little)));
                node = element < 0
                    ? parent.Append(fieldId, FieldValue.NewF64(value))
                    : parent.AppendWithCustomText(fieldId, FieldValue.NewF64(value), new ElementPrefix(element), value);
                break;
            }

            case SomeIpPrimitive.Bool:
            {
                // One byte; zero is false and every other value is true, the usual reading of a byte that is not exactly 0 or 1.
                bool value = bytes[0] != 0;
                node = element < 0
                    ? parent.Append(fieldId, FieldValue.NewBool(value))
                    : parent.AppendWithCustomText(fieldId, FieldValue.NewBool(value), new ElementPrefix(element), value);
                break;
            }

            default:
            {
                // The UUID is 16 bytes in network order, shown in its canonical form.
                Uuid value = Uuid.FromBytes(bytes);
                node = element < 0
                    ? parent.Append(fieldId, FieldValue.NewUuid(value))
                    : parent.AppendWithCustomText(fieldId, FieldValue.NewUuid(value), new ElementPrefix(element), value);
                break;
            }
        }

        if (state.Metadata)
        {
            string byteOrder = little && width > 1
                ? " little_endian"
                : "";
            _AppendType(in node, ref state, string.Concat("base ", type.Name, byteOrder));
        }

        return width;
    }

    private static int _Width(SomeIpPrimitive primitive) => primitive switch
    {
        SomeIpPrimitive.UInt8 or SomeIpPrimitive.Int8 or SomeIpPrimitive.Bool => 1,
        SomeIpPrimitive.UInt16 or SomeIpPrimitive.Int16 => 2,
        SomeIpPrimitive.UInt32 or SomeIpPrimitive.Int32 or SomeIpPrimitive.Float => 4,
        SomeIpPrimitive.UInt64 or SomeIpPrimitive.Int64 or SomeIpPrimitive.Double => 8,
        _ => 16,
    };

    /// <summary>Reads up to 8 bytes as an unsigned number in the given byte order.</summary>
    private static ulong _ReadUnsigned(ReadOnlySpan<byte> bytes, bool little)
    {
        // One loop for every width: the most significant byte is first in network order and last in little-endian order.
        ulong value = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            int index = i;
            if (little)
            {
                index = bytes.Length - 1 - i;
            }

            value = (value << 8) | bytes[index];
        }

        return value;
    }

    /// <summary>Reads up to 8 bytes as a two's-complement number in the given byte order.</summary>
    private static long _ReadSigned(ReadOnlySpan<byte> bytes, bool little)
    {
        // Shifting the value to the top of the word and back arithmetically extends the sign from any width.
        int shift = 64 - (8 * bytes.Length);
        return (long)(_ReadUnsigned(bytes, little) << shift) >> shift;
    }

    #endregion

    #region Length fields

    /// <summary>
    /// Reads a length field of 1, 2 or 4 bytes. The field is untrusted: it is returned as a long, so a 4-byte value above
    /// <see cref="int.MaxValue"/> is still a plain comparison against the bytes that remain and can never wrap into a small number.
    /// </summary>
    /// <returns>False when fewer than <paramref name="size"/> bytes remain (a diagnostic was appended).</returns>
    private static bool _TryReadLength(
        in MutField parent,
        ref SomeIpDecodeState state,
        SomeIpUsageSite site,
        int offset,
        int end,
        int size,
        bool little,
        out long length)
    {
        length = 0;
        if (end - offset < size)
        {
            _Short(in parent, ref state, string.Create(
                CultureInfo.InvariantCulture,
                $"The {size}-byte length field of '{site.UiName}' needs {size} bytes but only {Math.Max(end - offset, 0)} remain."));
            return false;
        }

        ReadOnlySpan<byte> field = state.Data.Span.Slice(offset, size);
        length = (long)_ReadUnsigned(field, little);
        return true;
    }

    /// <summary>Shows a length field that was read, in the metadata view.</summary>
    private static void _AppendLength(in MutField node, ref SomeIpDecodeState state, long length)
    {
        if (state.Metadata)
        {
            _ = node.Append(state.Ids.Length, FieldValue.NewU64((ulong)length));
        }
    }

    /// <summary>
    /// Clamps a declared extent to the bytes that remain and says so once. The declaration is the sender's claim; the bytes that
    /// are there are the facts.
    /// </summary>
    private static int _ClampExtent(in MutField parent, ref SomeIpDecodeState state, SomeIpUsageSite site, long declared, int available)
    {
        if (declared <= available)
        {
            return (int)declared;
        }

        _Short(in parent, ref state, string.Create(
            CultureInfo.InvariantCulture,
            $"'{site.UiName}' declares {declared} bytes but only {available} remain."));
        return available;
    }

    #endregion

    #region Strings

    /// <summary>Decodes a string: its extent (fixed, supplied, or a length field), then its text.</summary>
    private static int _DecodeString(
        in MutField parent,
        ref SomeIpDecodeState state,
        SomeIpUsageSite site,
        SomeIpTypeInfo type,
        int offset,
        int end,
        int supplied,
        int element)
    {
        int cursor = offset;
        int extent;
        long lengthRead = -1;
        if (supplied >= 0)
        {
            // An enclosing tag fixed the extent: no inline length field exists, whatever the description says.
            extent = _ClampExtent(in parent, ref state, site, supplied, end - cursor);
        }
        else if (type.FixedLength > 0)
        {
            extent = _ClampExtent(in parent, ref state, site, type.FixedLength, end - cursor);
        }
        else
        {
            // The type's own size wins over the member's: the same string type keeps its length field wherever it is used,
            // while a type without an opinion follows the member.
            int size = type.LengthSizeOverride > 0
                ? type.LengthSizeOverride
                : state.Transform.StringLength;
            if (!_TryReadLength(in parent, ref state, site, cursor, end, size, type.LittleEndian, out lengthRead))
            {
                return Math.Max(end - offset, 0);
            }

            cursor += size;
            extent = _ClampExtent(in parent, ref state, site, lengthRead, end - cursor);
        }

        state.Nodes++;
        ReadOnlyMemory<byte> bytes = state.Data.Slice(cursor, extent);
        string text = SomeIpTextDecoder.Decode(bytes, type.Encoding);
        MutField node = parent.AppendWithCustomText(
            site.FieldId, FieldValue.NewString(text), new ElementPrefix(element), "\"", text, "\" (", extent, " bytes)");
        if (state.Metadata)
        {
            _AppendType(in node, ref state, string.Concat("string ", _EncodingName(type.Encoding)));
            if (lengthRead >= 0)
            {
                _AppendLength(in node, ref state, lengthRead);
            }
        }

        return cursor + extent - offset;
    }

    private static string _EncodingName(SomeIpStringEncoding encoding) => encoding switch
    {
        SomeIpStringEncoding.Ascii => "ascii",
        SomeIpStringEncoding.Utf16BigEndian => "utf-16be",
        SomeIpStringEncoding.Utf16LittleEndian => "utf-16le",
        _ => "utf-8",
    };

    #endregion

    #region Arrays

    /// <summary>Decodes an array: its extent, then its elements until the extent or the count is used up.</summary>
    private static int _DecodeArray(
        in MutField parent,
        ref SomeIpDecodeState state,
        SomeIpUsageSite site,
        SomeIpTypeInfo type,
        int offset,
        int end,
        int supplied,
        int element)
    {
        // The element site is the single child of the array site; a description error that left it out leaves nothing to decode.
        if (site.ChildrenCount != 1)
        {
            _Error(in parent, ref state, string.Create(
                CultureInfo.InvariantCulture,
                $"Type not available: the element type of '{site.UiName}'."));
            return 0;
        }

        int cursor = offset;
        int arrayEnd = end;
        long lengthRead = -1;
        bool fixedCount = type.FixedCount > 0;
        if (supplied >= 0)
        {
            arrayEnd = cursor + _ClampExtent(in parent, ref state, site, supplied, end - cursor);
        }
        else if (!fixedCount)
        {
            int size = type.LengthSizeOverride > 0
                ? type.LengthSizeOverride
                : state.Transform.ArrayLength;
            if (!_TryReadLength(in parent, ref state, site, cursor, end, size, little: false, out lengthRead))
            {
                return Math.Max(end - offset, 0);
            }

            cursor += size;
            arrayEnd = cursor + _ClampExtent(in parent, ref state, site, lengthRead, end - cursor);
        }

        state.Nodes++;

        // A fixed array knows its text up front; a variable one learns its count only after the elements are decoded and gets its
        // final text then. The placeholder is the plain word so a reader that sees the node before the decode ends sees nothing wrong.
        MutField node = fixedCount
            ? parent.AppendWithCustomText(site.FieldId, FieldValue.None, new ElementPrefix(element), "array (fixed ", type.FixedCount, ")")
            : parent.AppendWithCustomText(site.FieldId, FieldValue.None, new ElementPrefix(element), "array");
        int elementSite = site.ChildrenStart;
        if (state.Metadata)
        {
            _AppendType(in node, ref state, _ArrayTypeText(in state, type, elementSite));
            if (lengthRead >= 0)
            {
                _AppendLength(in node, ref state, lengthRead);
            }
        }

        // The elements. A fixed array decodes exactly its count; a variable one decodes until its extent is used. An element that
        // uses no bytes (an empty structure) would repeat forever on a variable array, so it ends the array with a diagnostic.
        state.Depth++;
        int count = 0;
        while (!state.Stopped)
        {
            if (fixedCount)
            {
                if (count >= type.FixedCount)
                {
                    break;
                }

                // The extent ends the array short of the count only when the data is short.
                if (cursor >= arrayEnd)
                {
                    _Short(in node, ref state, string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{site.UiName}' has {type.FixedCount} elements but the data ends after {count}."));
                    break;
                }
            }
            else if (cursor >= arrayEnd)
            {
                break;
            }

            int used = _DecodeSite(in node, ref state, elementSite, cursor, arrayEnd, supplied: -1, element: count);
            cursor += used;
            if (state.Stopped)
            {
                break;
            }

            count++;
            if (used == 0 && !fixedCount)
            {
                _Error(in node, ref state, string.Create(
                    CultureInfo.InvariantCulture,
                    $"The elements of '{site.UiName}' use no bytes; the array ends after element {count}."));
                break;
            }
        }

        state.Depth--;
        _CheckElementBounds(in node, ref state, site, type, count);
        if (!fixedCount && node.WantsCustomText(site.FieldId))
        {
            node.SetCustomText(new LazyString(_ArrayText(element, count)));
        }

        if (state.Metadata)
        {
            _ = node.Append(state.Ids.ArrayCount, FieldValue.NewU64((ulong)count));
        }

        // A length field or a tag fixed the extent, so the array owns all of it even when its elements used less. A fixed array
        // without a length field owns what its elements used. After a limit the position is unknown: only what was decoded counts.
        return state.Stopped || (fixedCount && supplied < 0)
            ? cursor - offset
            : arrayEnd - offset;
    }

    private static string _ArrayText(int element, int count) => element < 0
        ? string.Create(CultureInfo.InvariantCulture, $"array[{count}]")
        : string.Create(CultureInfo.InvariantCulture, $"[{element}] array[{count}]");

    private static string _ArrayTypeText(in SomeIpDecodeState state, SomeIpTypeInfo type, int elementSite)
    {
        SomeIpUsageSite child = state.Model.Sites.Span[elementSite];
        string elementName = child.TypeIndex < 0
            ? child.ReferenceName
            : state.Model.Types.Span[child.TypeIndex].Name;
        return type.FixedCount > 0
            ? string.Create(CultureInfo.InvariantCulture, $"array fixed {type.FixedCount} of {elementName}")
            : string.Concat("array of ", elementName);
    }

    /// <summary>Reports an element count outside the bounds of the description; the elements stay visible.</summary>
    private static void _CheckElementBounds(in MutField node, ref SomeIpDecodeState state, SomeIpUsageSite site, SomeIpTypeInfo type, int count)
    {
        if (type.MinElements >= 0 && count < type.MinElements)
        {
            _Error(in node, ref state, string.Create(
                CultureInfo.InvariantCulture,
                $"'{site.UiName}' has {count} elements; the description requires at least {type.MinElements}."));
        }
        else if (type.MaxElements >= 0 && count > type.MaxElements)
        {
            _Error(in node, ref state, string.Create(
                CultureInfo.InvariantCulture,
                $"'{site.UiName}' has {count} elements; the description allows at most {type.MaxElements}."));
        }
    }

    #endregion

    #region Structures

    /// <summary>Decodes a structure: its extent, then its members in order.</summary>
    private static int _DecodeStructure(
        in MutField parent,
        ref SomeIpDecodeState state,
        SomeIpUsageSite site,
        SomeIpTypeInfo type,
        int offset,
        int end,
        int supplied,
        int element)
    {
        int cursor = offset;
        int structEnd = end;
        long lengthRead = -1;
        bool hasExtent = true;
        if (supplied >= 0)
        {
            structEnd = cursor + _ClampExtent(in parent, ref state, site, supplied, end - cursor);
        }
        else
        {
            // Size 0 means the structure has no length field and its members define its extent.
            int size = type.LengthSizeOverride >= 0
                ? type.LengthSizeOverride
                : state.Transform.StructLength;
            if (size == 0)
            {
                hasExtent = false;
            }
            else
            {
                if (!_TryReadLength(in parent, ref state, site, cursor, end, size, little: false, out lengthRead))
                {
                    return Math.Max(end - offset, 0);
                }

                cursor += size;
                structEnd = cursor + _ClampExtent(in parent, ref state, site, lengthRead, end - cursor);
            }
        }

        state.Nodes++;
        bool tlv = type.Category == SomeIpTypeCategory.TlvStructure;
        string kind = tlv
            ? "tlv struct "
            : "struct ";
        MutField node = parent.AppendWithCustomText(site.FieldId, FieldValue.None, new ElementPrefix(element), kind, type.Name);
        if (state.Metadata)
        {
            string typeKind = tlv
                ? "tlv structure "
                : "structure ";
            _AppendType(in node, ref state, string.Concat(typeKind, type.Name));
            if (lengthRead >= 0)
            {
                _AppendLength(in node, ref state, lengthRead);
            }
        }

        // A structure of a tag-fixed length of zero has no members on the wire; it is an empty node and nothing is missing.
        int membersStart = cursor;
        if (supplied == 0)
        {
            return 0;
        }

        state.Depth++;
        if (tlv)
        {
            // The members of a TLV structure carry their own tags and come in any order; the walk ends at the extent.
            cursor += _WalkTlv(in node, ref state, site.ChildByKey, cursor, structEnd);
        }
        else
        {
            for (int i = 0; i < site.ChildrenCount && !state.Stopped; i++)
            {
                int memberSite = site.ChildrenStart + i;
                if (cursor >= structEnd)
                {
                    // The declared extent (or the data) is used up before the member: it does not fit, and neither do the ones behind it.
                    SomeIpUsageSite member = state.Model.Sites.Span[memberSite];
                    if (hasExtent)
                    {
                        _Error(in node, ref state, string.Create(
                            CultureInfo.InvariantCulture,
                            $"The member '{member.UiName}' of '{site.UiName}' does not fit into the {structEnd - membersStart} bytes the structure declares."));
                    }
                    else
                    {
                        _Short(in node, ref state, string.Create(
                            CultureInfo.InvariantCulture,
                            $"The data ends before the member '{member.UiName}' of '{site.UiName}'."));
                    }

                    break;
                }

                cursor += _DecodeSite(in node, ref state, memberSite, cursor, structEnd, supplied: -1, element: -1);
            }
        }

        state.Depth--;

        // A declared extent belongs to the structure whatever its members used; the next sibling starts behind it.
        return state.Stopped || !hasExtent
            ? cursor - offset
            : structEnd - offset;
    }

    #endregion

    #region Unions

    /// <summary>
    /// Decodes a union: a length field, a type selector, and the alternative the selector names.
    /// <para>
    /// Wire layout: <c>[length][selector][value]</c>; the length counts the selector and the value, so a union with selector 0
    /// (empty) has a length of the selector's size and no value. Rejected alternative: treating the length as the value's length
    /// alone, which would make an empty union the only one whose length is smaller than its selector.
    /// </para>
    /// </summary>
    private static int _DecodeUnion(
        in MutField parent,
        ref SomeIpDecodeState state,
        SomeIpUsageSite site,
        SomeIpTypeInfo type,
        int offset,
        int end,
        int supplied,
        int element)
    {
        int cursor = offset;
        int unionEnd;
        long lengthRead = -1;
        if (supplied >= 0)
        {
            unionEnd = cursor + _ClampExtent(in parent, ref state, site, supplied, end - cursor);
        }
        else
        {
            if (!_TryReadLength(in parent, ref state, site, cursor, end, type.UnionLengthSize, little: false, out lengthRead))
            {
                return Math.Max(end - offset, 0);
            }

            cursor += type.UnionLengthSize;
            unionEnd = cursor + _ClampExtent(in parent, ref state, site, lengthRead, end - cursor);
        }

        state.Nodes++;
        MutField node = parent.AppendWithCustomText(site.FieldId, FieldValue.None, new ElementPrefix(element), "union ", type.Name);
        if (state.Metadata)
        {
            _AppendType(in node, ref state, string.Concat("union ", type.Name));
            if (lengthRead >= 0)
            {
                _AppendLength(in node, ref state, lengthRead);
            }
        }

        // The selector. A union shorter than its selector field cannot name an alternative; the bytes it has are shown.
        int selectorSize = type.UnionTypeSize;
        if (unionEnd - cursor < selectorSize)
        {
            _Error(in node, ref state, string.Create(
                CultureInfo.InvariantCulture,
                $"The union '{site.UiName}' declares {unionEnd - cursor} bytes, fewer than its {selectorSize}-byte type selector."));
            return unionEnd - offset;
        }

        ulong selector = _ReadUnsigned(state.Data.Span.Slice(cursor, selectorSize), little: false);
        cursor += selectorSize;
        if (state.Metadata)
        {
            _ = node.Append(state.Ids.UnionType, FieldValue.NewU64(selector));
        }

        // Selector 0 is the empty union: no alternative is set, and nothing follows the selector.
        if (selector == 0)
        {
            node.SetCustomText(new LazyString(string.Concat("union ", type.Name, " (empty)")));
            return unionEnd - offset;
        }

        state.Depth++;
        if (selector <= uint.MaxValue && site.ChildByKey is { } alternatives && alternatives.TryGetValue((uint)selector, out int alternative))
        {
            // The alternative decodes inside the declared extent, with its own length rules: a string alternative reads its own
            // length field, which is why the union does not supply a length.
            _ = _DecodeSite(in node, ref state, alternative, cursor, unionEnd, supplied: -1, element: -1);
        }
        else
        {
            _ = node.Append(state.Ids.TlvUnknown, FieldValue.NewBytes(state.Data.Slice(cursor, unionEnd - cursor)));
            _Error(in node, ref state, string.Create(
                CultureInfo.InvariantCulture,
                $"The union '{site.UiName}' has the type selector {selector}, which the description does not define."));
        }

        state.Depth--;
        return state.Stopped
            ? cursor - offset
            : unionEnd - offset;
    }

    #endregion

    #region Bitfields

    /// <summary>Decodes a bitfield: the raw word, then one boolean child per named bit.</summary>
    private static int _DecodeBitfield(
        in MutField parent,
        ref SomeIpDecodeState state,
        SomeIpUsageSite site,
        SomeIpTypeInfo type,
        int offset,
        int end,
        int element)
    {
        int width = type.Bits / 8;
        int available = end - offset;
        if (available < width)
        {
            _Short(in parent, ref state, string.Create(
                CultureInfo.InvariantCulture,
                $"'{site.UiName}' ({type.Bits}-bit bitfield) needs {width} bytes but only {Math.Max(available, 0)} remain."));
            return Math.Max(available, 0);
        }

        // The byte order is the one of the type; bit 0 is the least significant bit of the word either way.
        state.Nodes++;
        ulong raw = _ReadUnsigned(state.Data.Span.Slice(offset, width), type.LittleEndian);
        MutField node = element < 0
            ? parent.Append(site.FieldId, FieldValue.NewU64(raw))
            : parent.AppendWithCustomText(site.FieldId, FieldValue.NewU64(raw), new ElementPrefix(element), raw);
        if (state.Metadata)
        {
            _AppendType(in node, ref state, string.Create(CultureInfo.InvariantCulture, $"bitfield {type.Bits} bits"));
        }

        // The named bits. A bit key beyond the width cannot occur (the compiler dropped it); the shift is guarded all the same
        // because the model is data, and a shift by 64 or more would silently repeat bit 0.
        ReadOnlySpan<SomeIpUsageSite> sites = state.Model.Sites.Span;
        for (int i = 0; i < site.ChildrenCount; i++)
        {
            SomeIpUsageSite bit = sites[site.ChildrenStart + i];
            bool set = bit.Key < 64 && ((raw >> (int)bit.Key) & 1) != 0;
            state.Nodes++;
            _ = node.Append(bit.FieldId, FieldValue.NewBool(set));
        }

        return width;
    }

    #endregion

    #region TLV

    /// <summary>
    /// Walks the tagged members of a TLV list or structure until the range is used up, a limit is reached, or a tag makes the rest
    /// unreadable.
    /// </summary>
    /// <param name="parent">The node that receives the member nodes.</param>
    /// <param name="state">The decode state.</param>
    /// <param name="byId">Data id to the index of the member site; <see langword="null"/> when the list has no members.</param>
    /// <param name="start">Offset of the first tag.</param>
    /// <param name="end">Exclusive end of the range.</param>
    /// <returns>The bytes used from <paramref name="start"/>; a value below the range when the walk stopped early.</returns>
    private static int _WalkTlv(
        in MutField parent,
        ref SomeIpDecodeState state,
        FrozenDictionary<uint, int>? byId,
        int start,
        int end)
    {
        int position = start;
        while (!state.Stopped && end - position >= 2)
        {
            // The budget is checked per tag, not only per decoded value: an unknown member costs a node as well, and a payload of
            // nothing but short unknown tags would otherwise be free.
            if (state.Nodes >= state.MaxNodes)
            {
                _Stop(in parent, ref state, string.Create(
                    CultureInfo.InvariantCulture,
                    $"The payload holds more than {state.MaxNodes} nodes (setting {_NodesKey}); the members behind this tag are not decoded."));
                break;
            }

            ReadOnlySpan<byte> data = state.Data.Span;
            SomeIpTlvTag tag = SomeIpTlvTag.Parse(BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position, 2)));
            int afterTag = position + 2;
            int siteIndex = -1;
            bool known = byId is not null && byId.TryGetValue(tag.DataId, out siteIndex);
            SomeIpTypeInfo? memberType = null;
            if (known)
            {
                int typeIndex = state.Model.Sites.Span[siteIndex].TypeIndex;
                if (typeIndex >= 0)
                {
                    memberType = state.Model.Types.Span[typeIndex];
                }
            }

            // The extent of the value. A tag whose extent cannot be known ends the walk at that tag: the bytes from it on are the
            // caller's remainder, because the position of the next tag is unknowable.
            SomeIpTlvExtent extent = SomeIpTlv.TryGetExtent(
                in tag, data[afterTag..end], memberType, in state.Transform, out int lengthFieldSize, out long valueLength);
            if (extent == SomeIpTlvExtent.NoLengthField)
            {
                _Error(in parent, ref state, string.Create(
                    CultureInfo.InvariantCulture,
                    $"Wire type 4 of data id {tag.DataId} needs a member whose type has a length field; the members behind this tag cannot be located."));
                state.DataShort = true;
                return position - start;
            }

            if (extent == SomeIpTlvExtent.LengthFieldCut)
            {
                _Short(in parent, ref state, string.Create(
                    CultureInfo.InvariantCulture,
                    $"The length field of data id {tag.DataId} (wire type {tag.WireType}) needs {lengthFieldSize} bytes but only {end - afterTag} remain."));
                return end - start;
            }

            // The value must fit into what is left; what does not fit is clamped and said once, and the walk ends with it.
            int valueStart = afterTag + lengthFieldSize;
            long room = (long)end - valueStart;
            bool cut = valueLength > room;
            if (cut)
            {
                _Short(in parent, ref state, string.Create(
                    CultureInfo.InvariantCulture,
                    $"The member with data id {tag.DataId} declares {valueLength} bytes but only {room} remain."));
                valueLength = room;
            }

            int valueEnd = valueStart + (int)valueLength;
            if (known)
            {
                // A value under wire type 4 to 7 has a length from the tag; one under 0 to 3 has the size of its wire type.
                if (state.Metadata)
                {
                    state.Tag = tag;
                    state.TagPending = true;
                }

                int supplied = tag.WireType >= 4
                    ? (int)valueLength
                    : -1;
                _ = _DecodeSite(in parent, ref state, siteIndex, valueStart, valueEnd, supplied, element: -1);
                state.TagPending = false;
            }
            else
            {
                // A member the description does not define: its bytes stay visible and the walk goes on, so that a newer sender's
                // members do not hide the members that are known.
                state.Nodes++;
                _ = parent.Append(state.Ids.TlvUnknown, FieldValue.NewBytes(state.Data.Slice(valueStart, valueEnd - valueStart)));
                _Error(in parent, ref state, string.Create(
                    CultureInfo.InvariantCulture,
                    $"The data id {tag.DataId} (wire type {tag.WireType}) is not defined by the description; its {valueEnd - valueStart} bytes are shown unparsed."));
            }

            position = valueEnd;
            if (cut)
            {
                break;
            }
        }

        return position - start;
    }

    /// <summary>Appends the tag of a TLV member with its three parts as children (metadata view).</summary>
    private static void _AppendTag(in MutField node, ref SomeIpDecodeState state)
    {
        SomeIpTlvTag tag = state.Tag;
        state.TagPending = false;
        MutField tagNode = node.Append(state.Ids.TlvTag, FieldValue.NewU64(tag.Raw));
        _ = tagNode.Append(state.Ids.TlvWireType, FieldValue.NewU64(tag.WireType));
        _ = tagNode.Append(state.Ids.TlvDataId, FieldValue.NewU64(tag.DataId));
        _ = tagNode.Append(state.Ids.TlvReserved, FieldValue.NewBool(tag.Reserved));
    }

    /// <summary>Appends the node of a member that was sent with a value of zero bytes.</summary>
    private static void _DecodeEmpty(in MutField parent, ref SomeIpDecodeState state, SomeIpUsageSite site, int element)
    {
        state.Nodes++;
        MutField node = parent.AppendWithCustomText(site.FieldId, FieldValue.None, new ElementPrefix(element), "(empty)");
        if (state.TagPending)
        {
            _AppendTag(in node, ref state);
        }
    }

    #endregion

    #region Diagnostics and metadata

    /// <summary>Appends a diagnostic.</summary>
    private static void _Error(in MutField parent, ref SomeIpDecodeState state, string text) =>
        _ = parent.Append(state.Ids.Error, FieldValue.NewString(text));

    /// <summary>Appends the diagnostic of missing bytes, unless an earlier one already said so.</summary>
    private static void _Short(in MutField parent, ref SomeIpDecodeState state, string text)
    {
        if (state.DataShort)
        {
            return;
        }

        state.DataShort = true;
        _Error(in parent, ref state, text);
    }

    /// <summary>Appends the diagnostic of a reached limit and stops the decode.</summary>
    private static void _Stop(in MutField parent, ref SomeIpDecodeState state, string text)
    {
        state.Stopped = true;
        _Error(in parent, ref state, text);
    }

    /// <summary>Appends the type description of a node (metadata view), and the tag of the TLV member it belongs to.</summary>
    private static void _AppendType(in MutField node, ref SomeIpDecodeState state, string text)
    {
        _ = node.Append(state.Ids.Type, FieldValue.NewString(text));
        if (state.TagPending)
        {
            _AppendTag(in node, ref state);
        }
    }

    #endregion

    #region Display parts

    // Display parts are small value types whose ToString builds the text on first read. The parts API of the field append takes
    // them as they are, so a decode whose texts nobody reads formats nothing.

    /// <summary>The "[i] " prefix of an array element; empty for a value that is no element.</summary>
    private readonly struct ElementPrefix(int index)
    {
        public override string ToString() => index < 0
            ? ""
            : string.Create(CultureInfo.InvariantCulture, $"[{index}] ");
    }

    /// <summary>An integer with the name its lookup table gives it: "Active (2)", or just the number.</summary>
    private readonly struct NumberText(string? name, bool isSigned, long signed, ulong unsigned)
    {
        public override string ToString()
        {
            string value = isSigned
                ? signed.ToString(CultureInfo.InvariantCulture)
                : unsigned.ToString(CultureInfo.InvariantCulture);
            return name is null
                ? value
                : string.Concat(name, " (", value, ")");
        }
    }

    /// <summary>A single-precision value in the shortest text that reads back to the same value.</summary>
    private readonly struct SingleText(float value)
    {
        public override string ToString() => value.ToString("R", CultureInfo.InvariantCulture);
    }

    #endregion
}

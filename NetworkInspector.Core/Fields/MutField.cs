// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Thin-cursor mutable field wrapper for protocol parsers to build the field tree.
/// Occupies exactly 16 bytes (Packet ref 8 B + Index 2 B + FieldId 4 B + 2 B padding).
/// Keeping it at 16 bytes or less lets ABIs that return 16-byte structs in two registers (System V x64, Arm64) return a
/// cursor from every <c>Append</c> / <c>Prepend</c> / <c>InsertAfter</c> call without a hidden return buffer; Windows x64 returns only
/// up to 8 bytes in registers and still uses a return buffer there. The <see cref="FieldId"/> is cached in the cursor so that
/// building a child does not have to look it up again in the packet's field storage.
/// <para>
/// The <see cref="ParseContext"/> is not embedded in the cursor; it is threaded explicitly as an
/// <c>in</c> parameter only on the dispatch methods (<c>TryCallNextProtocol*</c>). The
/// field-building methods (Append/Prepend/InsertAfter variants) do not take a context —
/// presence recording happens eagerly via <see cref="ParseContext"/> in <c>Parse</c>, never
/// during field construction.
/// The same cursor type serves both eager parsing (in <c>Parse</c>, with a context for
/// dispatch) and lazy population (in a <see cref="LazyPopulator"/>, where no context is
/// available, so dispatch and index mutation are structurally impossible).
/// </para>
/// <para>
/// This is a ref struct — cannot be stored in collections or captured by a closure, so a lazy populator receives its
/// parent cursor as a parameter; it cannot capture one.
/// </para>
/// <para>
/// <b>Thread-safety:</b> a cursor is a stack-only value used by the thread that parses the packet (or, after the packet is
/// sealed, by the thread running a lazy populator, which the packet guarantees is exclusive per container). It adds no
/// synchronization of its own; the packet's append and materialization paths provide it.
/// </para>
/// <para>
/// <b>Misuse:</b> <c>default(MutField)</c> has a null packet; <see cref="IsValid"/> is false and most members throw
/// <see cref="NullReferenceException"/> on it. Check <see cref="IsValid"/> when a cursor may come from a <c>default</c>.
/// The formatting members are the exception: they never throw and give the empty line for such a cursor.
/// </para>
/// <para>
/// <b>Display line:</b> <see cref="ToString()"/> and <see cref="TryFormat(Span{char}, out int, ReadOnlySpan{char}, IFormatProvider?)"/>
/// give the same default display line as <see cref="Field"/> for the field as built so far (the custom text alone when set,
/// else the UI name for a field without a value, else UI name, <c>": "</c>, and the value's default string).
/// </para>
/// </summary>
public readonly ref struct MutField : ISpanFormattable, IUtf8SpanFormattable
{
    #region Cursor identity and construction

    /// <summary>The owning packet. Null only for <c>default(MutField)</c>.</summary>
    public readonly Packet Packet { get; }

    /// <summary>
    /// Index of this field in the packet's field storage (a <see cref="ushort"/>, like every tree link in the storage).
    /// <c>0</c> is the synthetic root. <see cref="FieldBody.SkipStorageIndex"/> is returned by appends on a
    /// <see cref="FieldTreeMode.Skip"/> packet, where no tree exists. <see cref="FieldBody.NullIndex"/> means "no field".
    /// </summary>
    internal readonly ushort StorageIndex { get; }

    /// <summary>
    /// Identifier of the field definition this cursor points to. Cached in the cursor — it fits in the 4-byte slot between
    /// <see cref="StorageIndex"/> (2 B) and alignment padding — so a child can be built without a lookup in the field storage.
    /// </summary>
    public readonly FieldId FieldId { get; }

    /// <summary>Creates a mutable field handle with a known field ID (avoids array lookup on the hot path).</summary>
    /// <param name="packet">The owning packet.</param>
    /// <param name="index">Storage index of the field in <paramref name="packet"/>.</param>
    /// <param name="fieldId">The field's id; the caller guarantees that it is the id stored at <paramref name="index"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MutField(Packet packet, ushort index, FieldId fieldId)
    {
        Packet = packet;
        StorageIndex = index;
        FieldId = fieldId;
    }

    #endregion

    #region Reading values, buffers, and tree shape

    /// <summary>
    /// Whether this field reference points to a valid packet and index. False only for <c>default(MutField)</c> or a cursor
    /// whose index is <see cref="FieldBody.NullIndex"/>; the skip-tree index counts as valid.
    /// </summary>
    /// <remarks>Sync: <c>FieldDisplayLine</c> repeats the condition for the cursors it formats (no packet, or the null index).</remarks>
    public readonly bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Packet is not null && StorageIndex != FieldBody.NullIndex;
    }

    /// <summary>False when the owning packet was parsed with <see cref="FieldTreeMode.Skip"/>.</summary>
    public readonly bool HasFieldTree
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Packet.HasFieldTree;
    }

    /// <summary>Gets the field's metadata from the stack registry (a registry lookup by <see cref="FieldId"/>, not a stored reference).</summary>
    public readonly FieldInfo? FieldInfo => Packet.Stack.GetField(FieldId);

    /// <summary>
    /// The field's value, as a reference to the field's own storage (see <see cref="Field.Value"/> for what a reference saves
    /// over a copy).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The packet was parsed with <see cref="FieldTreeMode.Skip"/> and this cursor is neither the root nor the field currently
    /// being populated by a skip-tree lazy populator: a skip parse keeps no stored values to read back.
    /// </exception>
    public readonly ref readonly FieldValue Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // On a skip packet only the root (index 0) and the in-flight scratch slot (SkipStorageIndex) have a body;
            // any other index would not map to stored data, so fail loudly instead of returning an unrelated slot.
            if (!HasFieldTree && StorageIndex != 0 && StorageIndex != FieldBody.SkipStorageIndex)
            {
                ThrowHelpers.ThrowSkipFieldTreeValue();
            }

            return ref Packet.GetFieldRef(StorageIndex).Value;
        }
    }

    /// <summary>
    /// Optional custom display text (check <see cref="LazyString.IsNull"/> for absence), as a reference to the field's own
    /// storage.
    /// </summary>
    /// <remarks>
    /// Reading this property never evaluates a deferred text. The string is built once however it is read (see
    /// <see cref="LazyString"/>); evaluating through the reference (<c>field.CustomText.AsString</c>) additionally replaces the
    /// deferred object in the packet's storage by its string, which a copy (<c>LazyString text = field.CustomText;</c>) does not.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Same condition as <see cref="Value"/>.</exception>
    public readonly ref readonly LazyString CustomText
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // Same skip-packet guard as Value.
            if (!HasFieldTree && StorageIndex != 0 && StorageIndex != FieldBody.SkipStorageIndex)
            {
                ThrowHelpers.ThrowSkipFieldTreeValue();
            }

            return ref Packet.GetFieldRef(StorageIndex).CustomText;
        }
    }

    /// <summary>
    /// Stores owned bytes on the packet for nested parse and effect keys, or names why it could not. This is the only way to
    /// attach a buffer; it is safe for every protocol because it carries no limit that a caller could leave out or loosen.
    /// </summary>
    /// <param name="buffer">
    /// Reassembled, decoded, unmasked, or inflated bytes that outlive this parse (not a pooled or stack buffer); the packet keeps
    /// the memory itself, no copy.
    /// </param>
    /// <param name="bound">The stored slice to hand to the nested parse; empty when the method returns <see langword="false"/>.</param>
    /// <param name="failure">
    /// <see cref="BufferBindFailure.TableFull"/> when the packet holds 255 buffers, <see cref="BufferBindFailure.Sealed"/> after
    /// Seal, else <see cref="BufferBindFailure.None"/>.
    /// </param>
    /// <returns><see langword="true"/> when the buffer was stored; a refused bind changes nothing.</returns>
    /// <remarks>
    /// <para>The packet decides only whether a slot is free (255, the effect-layer key packs the index in bits 31–24). It does not
    /// know the calling protocol and has no byte limit. A protocol that derives bytes from untrusted input, where one packet can
    /// complete many messages, applies its own per-packet total: read <see cref="BoundBytes"/>, refuse when
    /// <c>BoundBytes + buffer.Length</c> would pass the protocol's setting, and name that setting in the error. Bytes that a
    /// bounded reassembly buffer already held (a TCP PDU, an IP datagram) need no such total.</para>
    /// <para>Pass the returned slice, not <paramref name="buffer"/>, to the nested parse; an empty <paramref name="bound"/> means
    /// "do not nest-parse". Thread-safety: one writer until the packet is sealed.</para>
    /// </remarks>
    /// <seealso cref="BoundBytes"/>
    /// <seealso cref="BufferBindFailure"/>
    /// <seealso cref="TryGetEffectLayerKey"/>
    public readonly bool TryBindBuffer(ReadOnlyMemory<byte> buffer, out ReadOnlyMemory<byte> bound, out BufferBindFailure failure)
        => Packet.TryBindBuffer(buffer, out bound, out failure);

    /// <summary>
    /// Number of bytes bound to the packet so far through <see cref="TryBindBuffer"/>, whichever protocol bound them; 0 for a
    /// packet that has bound nothing, and again 0 for every parse of a recycled packet.
    /// </summary>
    /// <remarks>
    /// A protocol compares it with its own per-packet setting before it binds derived bytes. The sum counts the bytes of every
    /// protocol together, so the total a setting bounds is the memory the packet pins when that protocol binds.
    /// </remarks>
    /// <seealso cref="TryBindBuffer"/>
    public readonly ulong BoundBytes => Packet.AdditionalBufferBytes;

    /// <summary>
    /// Packs the location of <paramref name="data"/> inside this packet's buffers into an
    /// effect-store layer key. Returns <see langword="false"/> when <paramref name="data"/> is
    /// not a slice of a packet buffer or the packed index/offset does not fit.
    /// </summary>
    /// <param name="data">A slice of the frame or of a buffer bound with <see cref="TryBindBuffer"/>, typically the <c>data</c> argument of Parse.</param>
    /// <param name="key">The packed layer key (buffer index in bits 31–24, byte offset below) on success; 0 on a miss.</param>
    /// <returns><see langword="true"/> when <paramref name="data"/> is a packable packet slice.</returns>
    /// <seealso cref="TryBindBuffer"/>
    public readonly bool TryGetEffectLayerKey(ReadOnlyMemory<byte> data, out int key)
        => Packet.TryGetEffectLayerKey(data, out key);

    /// <summary>
    /// True only when this cursor is valid and its storage index is 0.
    /// A default cursor is not the root.
    /// </summary>
    public readonly bool IsRoot
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Packet is not null && StorageIndex == 0;
    }

    /// <summary>
    /// Whether this field has child fields.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy children are populated first.
    /// When <see langword="false"/>, an unmaterialized lazy container reports no children.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy children before checking.</param>
    /// <returns>
    /// <see langword="true"/> when at least one child is linked; always <see langword="false"/> on a
    /// <see cref="FieldTreeMode.Skip"/> packet.
    /// </returns>
    /// <remarks>
    /// Does not wait for a lazy field that another thread is materializing (a parse-thread cursor never needs to);
    /// <c>Field.HasChildren</c> does. Cost: one flag test and one slot read, plus a populator run when
    /// <paramref name="materialize"/> is true and the field is lazy.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool HasChildren(bool materialize)
    {
        // A skip packet keeps no tree, so there is never a child to report.
        if (!HasFieldTree)
        {
            return false;
        }

        // Populate a lazy container first when asked; otherwise its link list is still empty and the answer would be a
        // false "no children". The cheap NeedsMaterialization test keeps ordinary fields from entering the materializer.
        if (materialize)
        {
            ref readonly FieldBody body = ref Packet.GetFieldRef(StorageIndex);
            if (body.NeedsMaterialization)
            {
                Packet.MaterializeLazyField(StorageIndex);
            }
        }
        return Packet.GetFieldRef(StorageIndex).FirstChildIndex != FieldBody.NullIndex;
    }

    /// <summary>
    /// Number of direct children.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy children are populated first.
    /// When <see langword="false"/>, an unmaterialized lazy container reports zero children.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy children before counting.</param>
    /// <returns>The number of direct children; 0 on a <see cref="FieldTreeMode.Skip"/> packet. Saturates at <see cref="ushort.MaxValue"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ushort ChildCount(bool materialize)
    {
        // A skip packet keeps no tree.
        if (!HasFieldTree)
        {
            return 0;
        }

        // Same materialize-before-read rule as HasChildren: a lazy container counts zero until it is populated.
        if (materialize)
        {
            ref readonly FieldBody body = ref Packet.GetFieldRef(StorageIndex);
            if (body.NeedsMaterialization)
            {
                Packet.MaterializeLazyField(StorageIndex);
            }
        }
        return Packet.GetFieldRef(StorageIndex).ChildCount;
    }

    /// <summary>
    /// Whether this field is lazy (has deferred children that need materialization).
    /// Internal so the lazy mechanism stays transparent to external consumers.
    /// </summary>
    internal readonly bool NeedsLazyMaterialization
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Packet.GetFieldRef(StorageIndex).NeedsMaterialization;
    }

    #endregion

    #region Default string representation

    // The default display line of the field being built, by the same rule as Field.ToString: FieldDisplayLine builds it for both
    // cursors. Inside a dissector the line reflects the value and the texts set so far: a text that is set later (SetCustomText)
    // is not in a line that was already read, and a deferred text is built by the first read, so a text that the dissector
    // completes later in the parse must not be read before then.

    /// <summary>
    /// Returns the default display line of this field as built so far (see <see cref="Field.ToString()"/> for the rule and for
    /// the line of a field id that the stack does not know).
    /// </summary>
    /// <returns>
    /// The line. Never throws: a default (invalid) cursor gives the empty string; a cursor of a <see cref="FieldTreeMode.Skip"/>
    /// packet other than its root, which keeps no value or text, gives the label alone (also inside a populator). Allocates the
    /// result string; use <see cref="TryFormat(Span{char}, out int, ReadOnlySpan{char}, IFormatProvider?)"/> to avoid it.
    /// </returns>
    /// <remarks>
    /// Reading builds a deferred custom text and freezes it for every copy (see <see cref="LazyString"/>), so a text that the
    /// dissector completes later in the parse must not be read before then, also not by a debugger or a log line. The line is not
    /// escaped (see <see cref="Field.ToString()"/>).
    /// </remarks>
    /// <seealso cref="Field.ToString()"/>
    public readonly override string ToString() => FieldDisplayLine.GetLine(Packet, StorageIndex, FieldId);

    /// <summary>Returns <see cref="ToString()"/>; both arguments are ignored (every field has exactly one default line).</summary>
    /// <param name="format">Ignored.</param>
    /// <param name="formatProvider">Ignored; numbers use the invariant culture.</param>
    /// <returns>The same line as <see cref="ToString()"/>.</returns>
    public readonly string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>Formats the default display line into a character span; reports failure when it does not fit.</summary>
    /// <param name="destination">Target span.</param>
    /// <param name="charsWritten">Characters written; 0 when the method returns <see langword="false"/>.</param>
    /// <param name="format">Ignored.</param>
    /// <param name="provider">Ignored; numbers use the invariant culture.</param>
    /// <returns>
    /// <see langword="false"/> when <paramref name="destination"/> is too small. A default (invalid) cursor gives the empty line
    /// (true, 0 written); a non-root cursor of a skip-tree packet gives the label alone.
    /// </returns>
    /// <remarks>Perf: writes straight into <paramref name="destination"/>; no allocation.</remarks>
    public readonly bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => FieldDisplayLine.TryFormat(Packet, StorageIndex, FieldId, destination, out charsWritten);

    /// <summary>Formats the default display line as UTF-8; same cases and failure contract as the character overload.</summary>
    /// <param name="utf8Destination">Target span.</param>
    /// <param name="bytesWritten">Bytes written; 0 when the method returns <see langword="false"/>.</param>
    /// <param name="format">Ignored.</param>
    /// <param name="provider">Ignored; numbers use the invariant culture.</param>
    /// <returns><see langword="false"/> when <paramref name="utf8Destination"/> is too small.</returns>
    public readonly bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => FieldDisplayLine.TryFormat(Packet, StorageIndex, FieldId, utf8Destination, out bytesWritten);

    #endregion

    #region Changing values and texts

    /// <summary>
    /// Replaces the stored field value in place. This is a plain store into the field body: the value cache and any field
    /// observers recorded or saw the value when the field was appended and are not told about this change. The store is
    /// unsynchronized (a multi-word struct write), so use it only while the packet is still being built by one thread.
    /// </summary>
    /// <param name="value">The new value; replaces the old one, its custom text stays.</param>
    /// <exception cref="InvalidOperationException">
    /// The packet was parsed with <see cref="FieldTreeMode.Skip"/> and this cursor is neither the root nor the field currently
    /// being populated by a skip-tree lazy populator (same condition as <see cref="Value"/>).
    /// </exception>
    /// <remarks>Cost: one slot write, no allocation.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void SetValue(FieldValue value) => Packet.GetFieldRef(StorageIndex).SetValue(value);

    /// <summary>
    /// Sets custom display text. Goes through the packet (not a direct store) so an attached value cache that records
    /// display text sees the change.
    /// </summary>
    /// <param name="text">The deferred text that replaces the current one; <c>default</c> clears it.</param>
    /// <remarks>
    /// <b>Discouraged.</b> Set the text when appending the field (<c>AppendWithCustomText</c> with a state object whose
    /// <c>ToString()</c> you complete before the parse returns). Field observers
    /// receive a field when it is appended and never see a later change, so tree-free exports and the field tree would show
    /// different texts. <b>Refused after seal:</b> throws <see cref="InvalidOperationException"/> once the packet is sealed
    /// (for example inside a lazy populator that runs after the parse), because readers on other threads may read the text.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The packet is sealed.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void SetCustomText(LazyString text) => Packet.SetFieldCustomText(StorageIndex, FieldId, text);

    /// <summary>Clears custom display text. Goes through the packet so an attached value cache is updated as well.</summary>
    /// <remarks>
    /// <b>Discouraged</b> for the same reason as <see cref="SetCustomText"/>: observers never see the change. <b>Refused after
    /// seal.</b> Set the text when appending the field instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The packet is sealed.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void ClearCustomText() => Packet.ClearFieldCustomText(StorageIndex, FieldId);

    /// <summary>Appends to custom display text. Goes through the packet so an attached value cache is updated as well.</summary>
    /// <remarks>
    /// <b>Discouraged</b> for the same reason as <see cref="SetCustomText"/>: observers never see the change. <b>Refused after
    /// seal.</b> Build the whole text and set it when appending the field instead.
    /// </remarks>
    /// <param name="suffix">The deferred text to add after the current text; becomes the text when there is none.</param>
    /// <exception cref="InvalidOperationException">The packet is sealed.</exception>
    public readonly void AppendCustomText(LazyString suffix) => Packet.AppendFieldCustomText(StorageIndex, FieldId, suffix);

    #endregion

    #region Packet Info

    /// <summary>
    /// Sets the one-line summary of the whole packet (the text a packet list shows), replacing the summary set so far.
    /// </summary>
    /// <param name="info">The deferred summary text; evaluated on the first read of <see cref="PacketInfo"/>.</param>
    /// <remarks>
    /// Valid while the packet is parsed. The packet reads the summary once, when the root protocol appends its
    /// <c>packet.info</c> field after the dissectors returned; a later change (for example from a lazy populator) is not seen.
    /// The summary is held beside the field tree, so it works on a <see cref="FieldTreeMode.Skip"/> packet too. Cost: one field write.
    /// </remarks>
    public readonly void SetPacketInfo(LazyString info) => Packet.SetInfo(info);

    /// <summary>Adds text after the summary set so far, so each protocol of the stack can contribute its part in parse order.</summary>
    /// <param name="suffix">The deferred text to add after the current summary; becomes the summary when none is set.</param>
    /// <remarks>
    /// Same timing and tree-mode behavior as <see cref="SetPacketInfo"/>. When a summary is already set, both texts are
    /// evaluated at once and joined into a plain string (see <see cref="LazyString.Append"/>); a deferred text is only kept
    /// deferred while it is the sole part.
    /// </remarks>
    public readonly void AppendToPacketInfo(LazyString suffix) => Packet.AppendToInfo(suffix);

    /// <summary>Adds text before the summary set so far, so an outer protocol can lead with its part.</summary>
    /// <param name="prefix">The deferred text to add before the current summary; becomes the summary when none is set.</param>
    /// <remarks>Same timing, tree-mode behavior, and evaluation rule as <see cref="AppendToPacketInfo"/>.</remarks>
    public readonly void PrependToPacketInfo(LazyString prefix) => Packet.PrependToInfo(prefix);

    /// <summary>The packet summary built so far. Reading evaluates the deferred text and freezes it (see <see cref="LazyString"/>).</summary>
    public readonly string PacketInfo => Packet.Info;

    #endregion

    #region Adding fields

    /// <summary>Appends a field as the last child of this field.</summary>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <returns>
    /// The cursor of the new field. In a parse without a field tree (<see cref="FieldTreeMode.Skip"/>) no field is stored: the
    /// cursor then has <see cref="FieldBody.SkipStorageIndex"/> as its storage index, still serves as the parent of further
    /// appends, and reads of its value throw (see <see cref="Value"/>). Field observers and the value cache still see the field.
    /// </returns>
    /// <exception cref="FieldAppendException">
    /// The packet holds the maximum number of fields: 65 533 including the root; one more slot stays free for the error field
    /// that records this failure.
    /// </exception>
    /// <remarks>
    /// Cost: no allocation; one slot write and the link writes. Parse thread only before the packet is sealed; a lazy populator
    /// appends under the claim the packet gives it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField Append(FieldId fieldId, FieldValue value)
        => new(Packet, Packet.AppendChild(StorageIndex, fieldId, value), fieldId);

    /// <summary>Appends a field as the last child of this field, with custom display text.</summary>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="customText">
    /// The text shown for the field instead of the default line; a default (absent) text means "no custom text".
    /// A deferred text is evaluated on first read, and a text that a dissector completes after the append must not be read before.
    /// </param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <remarks>
    /// The text is attached before the field becomes visible, so field observers and the value cache see it. Build the text only
    /// when <see cref="WantsCustomText"/> is true, or use a typed overload, which does so.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField AppendWithCustomText(FieldId fieldId, FieldValue value, LazyString customText)
        => new(Packet, Packet.AppendChildWithCustomText(StorageIndex, fieldId, value, customText), fieldId);

    /// <summary>Prepends a field as the first child of this field (before all existing children).</summary>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <remarks>Same cost and threading as <see cref="Append(FieldId, FieldValue)"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField Prepend(FieldId fieldId, FieldValue value)
        => new(Packet, Packet.PrependChild(StorageIndex, fieldId, value), fieldId);

    /// <summary>Prepends a field as the first child of this field, with custom display text.</summary>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="customText">The text shown for the field instead of the default line; default means "no custom text".</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <remarks>See <see cref="AppendWithCustomText(FieldId, FieldValue, LazyString)"/> for the text rules.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField PrependWithCustomText(FieldId fieldId, FieldValue value, LazyString customText)
        => new(Packet, Packet.PrependChildWithCustomText(StorageIndex, fieldId, value, customText), fieldId);

    /// <summary>Inserts a field right after this field, as its next sibling.</summary>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">This field is the root (it has no siblings), or the packet holds the maximum number of fields.</exception>
    /// <remarks>Same cost and threading as <see cref="Append(FieldId, FieldValue)"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField InsertAfter(FieldId fieldId, FieldValue value)
        => new(Packet, Packet.InsertAfter(StorageIndex, fieldId, value), fieldId);

    /// <summary>Inserts a field right after this field, as its next sibling, with custom display text.</summary>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="customText">The text shown for the field instead of the default line; default means "no custom text".</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <remarks>See <see cref="AppendWithCustomText(FieldId, FieldValue, LazyString)"/> for the text rules.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField InsertAfterWithCustomText(FieldId fieldId, FieldValue value, LazyString customText)
        => new(Packet, Packet.InsertAfterWithCustomText(StorageIndex, fieldId, value, customText), fieldId);

    #region Typed custom text overloads

    // The typed AppendWithCustomText / PrependWithCustomText / InsertAfterWithCustomText / AppendLazyWithCustomText
    // overloads below all follow one pattern:
    //  1. Ask the packet whether anyone will read custom text for this field (a field tree stores it, an attached value
    //     cache records display text, or an observer of the parse asked for display text through ParseObserver.DisplayText).
    //     If not (a Skip parse without a display-text cache or observer), append WITHOUT text and return;
    //     the arguments are never captured, so nothing is allocated for text nobody reads.
    //  2. Otherwise capture the arguments as a value tuple and defer the formatting with LazyString.FormatLazy, so the text
    //     is built only when it is first read. The formatter is a static lambda (no closure over locals, so no per-call
    //     delegate allocation); the arguments are joined without separators using the invariant culture.
    // The arity ladders (one overload per argument count) exist instead of a params array or a string.Format call because a
    // params array allocates on every call and boxes value-type arguments before formatting is even needed, which would
    // defeat step 1 on the hot path. If a new arity is added, add it to all six ladders (plain and lazy, for append, prepend, and
    // insert-after; the last ones follow the lazy ladder of append) and keep the tuple items in order.

    /// <summary>
    /// Whether custom display text or custom representation should be built for
    /// <paramref name="fieldId"/>. True when a field tree will store it, an attached value cache records display text for
    /// the field, or an observer of the parse asked for display text (<c>ParseObserver.DisplayText</c>).
    /// </summary>
    /// <param name="fieldId">The field whose text the caller is about to build.</param>
    /// <returns>
    /// <see langword="true"/> when somebody reads the text; <see langword="false"/> when building it would be wasted work.
    /// </returns>
    /// <remarks>
    /// Use it to guard an expensive text that is built by hand. The typed overloads of the <c>...WithCustomText</c> members
    /// ask it themselves. Parse thread only; the answer holds for the running parse.
    /// </remarks>
    public readonly bool WantsCustomText(FieldId fieldId) => Packet.WantsCustomText(fieldId);

    /// <summary>
    /// Appends a field as the last child of this field, with a custom display text made of one part. The part is converted with
    /// <c>ToString</c> in the invariant culture, and the text is built lazily on first read.
    /// </summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>; any type.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <remarks>
    /// <b>Shared by every typed overload</b> of <c>AppendWithCustomText</c>, <c>PrependWithCustomText</c>,
    /// <c>InsertAfterWithCustomText</c>, and the <c>Lazy</c> forms of the three, each for 1 to 9 text parts <c>arg0</c> … <c>arg8</c>.
    /// <para>
    /// The overload asks <see cref="WantsCustomText"/> first. When nobody reads the text, the field is appended without text and
    /// the arguments are neither captured nor formatted: the call allocates nothing for them. Otherwise the parts are captured
    /// by value in one tuple and joined without separators when the text is first read (each part through <c>ToString</c> or
    /// the type's invariant-culture formatting); a part that is a mutable reference object is read at that moment, not at the
    /// append. The formatter is a static lambda, so no closure and no per-call delegate exist.
    /// </para>
    /// <para>
    /// Cost when the text is wanted: one small object that holds the tuple (see <see cref="LazyString.FormatLazy{TState}"/>); the string
    /// is allocated on first read. Parse thread only before the packet is sealed. A <c>Lazy</c> form also takes a
    /// <see cref="LazyPopulator"/> as the last parameter (see <see cref="AppendLazy"/>) and returns the cursor of the lazy container.
    /// </para>
    /// <para>
    /// Rejected: a <c>params object[]</c> overload, which allocates the array and boxes every value-type part on every call,
    /// even when nobody reads the text.
    /// </para>
    /// </remarks>
    public readonly MutField AppendWithCustomText<T0>(
        FieldId fieldId, FieldValue value, T0 arg0)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Append(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            arg0,
            static s => string.Create(CultureInfo.InvariantCulture, $"{s}"));
        return AppendWithCustomText(fieldId, value, text);
    }

    /// <summary>Appends a field as the last child of this field, with a custom display text made of 2 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendWithCustomText<T0, T1>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Append(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}"));
        return AppendWithCustomText(fieldId, value, text);
    }

    /// <summary>Appends a field as the last child of this field, with a custom display text made of 3 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendWithCustomText<T0, T1, T2>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Append(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}"));
        return AppendWithCustomText(fieldId, value, text);
    }

    /// <summary>Appends a field as the last child of this field, with a custom display text made of 4 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendWithCustomText<T0, T1, T2, T3>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Append(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}"));
        return AppendWithCustomText(fieldId, value, text);
    }

    /// <summary>Appends a field as the last child of this field, with a custom display text made of 5 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendWithCustomText<T0, T1, T2, T3, T4>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Append(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}"));
        return AppendWithCustomText(fieldId, value, text);
    }

    /// <summary>Appends a field as the last child of this field, with a custom display text made of 6 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendWithCustomText<T0, T1, T2, T3, T4, T5>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Append(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}"));
        return AppendWithCustomText(fieldId, value, text);
    }

    /// <summary>Appends a field as the last child of this field, with a custom display text made of 7 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendWithCustomText<T0, T1, T2, T3, T4, T5, T6>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Append(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}"));
        return AppendWithCustomText(fieldId, value, text);
    }

    /// <summary>Appends a field as the last child of this field, with a custom display text made of 8 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <typeparam name="T7">Type of <paramref name="arg7"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="arg7">Text part 8.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendWithCustomText<T0, T1, T2, T3, T4, T5, T6, T7>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Append(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}{s.Item8}"));
        return AppendWithCustomText(fieldId, value, text);
    }

    /// <summary>Prepends a field as the first child of this field, with a custom display text made of 1 part.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependWithCustomText<T0>(
        FieldId fieldId, FieldValue value, T0 arg0)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Prepend(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            arg0,
            static s => string.Create(CultureInfo.InvariantCulture, $"{s}"));
        return PrependWithCustomText(fieldId, value, text);
    }

    /// <summary>Prepends a field as the first child of this field, with a custom display text made of 2 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependWithCustomText<T0, T1>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Prepend(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}"));
        return PrependWithCustomText(fieldId, value, text);
    }

    /// <summary>Prepends a field as the first child of this field, with a custom display text made of 3 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependWithCustomText<T0, T1, T2>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Prepend(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}"));
        return PrependWithCustomText(fieldId, value, text);
    }

    /// <summary>Prepends a field as the first child of this field, with a custom display text made of 4 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependWithCustomText<T0, T1, T2, T3>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Prepend(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}"));
        return PrependWithCustomText(fieldId, value, text);
    }

    /// <summary>Prepends a field as the first child of this field, with a custom display text made of 5 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependWithCustomText<T0, T1, T2, T3, T4>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Prepend(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}"));
        return PrependWithCustomText(fieldId, value, text);
    }

    /// <summary>Prepends a field as the first child of this field, with a custom display text made of 6 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependWithCustomText<T0, T1, T2, T3, T4, T5>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Prepend(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}"));
        return PrependWithCustomText(fieldId, value, text);
    }

    /// <summary>Prepends a field as the first child of this field, with a custom display text made of 7 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependWithCustomText<T0, T1, T2, T3, T4, T5, T6>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Prepend(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}"));
        return PrependWithCustomText(fieldId, value, text);
    }

    /// <summary>Prepends a field as the first child of this field, with a custom display text made of 8 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <typeparam name="T7">Type of <paramref name="arg7"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="arg7">Text part 8.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependWithCustomText<T0, T1, T2, T3, T4, T5, T6, T7>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Prepend(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}{s.Item8}"));
        return PrependWithCustomText(fieldId, value, text);
    }

    /// <summary>Inserts a field right after this field, as its next sibling, with a custom display text made of 1 part.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterWithCustomText<T0>(
        FieldId fieldId, FieldValue value, T0 arg0)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfter(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            arg0,
            static s => string.Create(CultureInfo.InvariantCulture, $"{s}"));
        return InsertAfterWithCustomText(fieldId, value, text);
    }

    /// <summary>Inserts a field right after this field, as its next sibling, with a custom display text made of 2 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterWithCustomText<T0, T1>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfter(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}"));
        return InsertAfterWithCustomText(fieldId, value, text);
    }

    /// <summary>Inserts a field right after this field, as its next sibling, with a custom display text made of 3 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterWithCustomText<T0, T1, T2>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfter(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}"));
        return InsertAfterWithCustomText(fieldId, value, text);
    }

    /// <summary>Inserts a field right after this field, as its next sibling, with a custom display text made of 4 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterWithCustomText<T0, T1, T2, T3>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfter(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}"));
        return InsertAfterWithCustomText(fieldId, value, text);
    }

    /// <summary>Inserts a field right after this field, as its next sibling, with a custom display text made of 5 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterWithCustomText<T0, T1, T2, T3, T4>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfter(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}"));
        return InsertAfterWithCustomText(fieldId, value, text);
    }

    /// <summary>Inserts a field right after this field, as its next sibling, with a custom display text made of 6 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterWithCustomText<T0, T1, T2, T3, T4, T5>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfter(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}"));
        return InsertAfterWithCustomText(fieldId, value, text);
    }

    /// <summary>Inserts a field right after this field, as its next sibling, with a custom display text made of 7 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterWithCustomText<T0, T1, T2, T3, T4, T5, T6>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfter(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}"));
        return InsertAfterWithCustomText(fieldId, value, text);
    }

    /// <summary>Inserts a field right after this field, as its next sibling, with a custom display text made of 8 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <typeparam name="T7">Type of <paramref name="arg7"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="arg7">Text part 8.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterWithCustomText<T0, T1, T2, T3, T4, T5, T6, T7>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfter(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}{s.Item8}"));
        return InsertAfterWithCustomText(fieldId, value, text);
    }

    /// <summary>Appends a lazy container as the last child of this field (see <see cref="AppendLazy"/>), with a custom display text made of 1 part.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendLazyWithCustomText<T0>(
        FieldId fieldId, FieldValue value, T0 arg0, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return AppendLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            arg0,
            static s => string.Create(CultureInfo.InvariantCulture, $"{s}"));
        return AppendLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Appends a lazy container as the last child of this field (see <see cref="AppendLazy"/>), with a custom display text made of 2 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendLazyWithCustomText<T0, T1>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return AppendLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}"));
        return AppendLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Appends a lazy container as the last child of this field (see <see cref="AppendLazy"/>), with a custom display text made of 3 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendLazyWithCustomText<T0, T1, T2>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return AppendLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}"));
        return AppendLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Appends a lazy container as the last child of this field (see <see cref="AppendLazy"/>), with a custom display text made of 4 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendLazyWithCustomText<T0, T1, T2, T3>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return AppendLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}"));
        return AppendLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Appends a lazy container as the last child of this field (see <see cref="AppendLazy"/>), with a custom display text made of 5 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendLazyWithCustomText<T0, T1, T2, T3, T4>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return AppendLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}"));
        return AppendLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Appends a lazy container as the last child of this field (see <see cref="AppendLazy"/>), with a custom display text made of 6 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendLazyWithCustomText<T0, T1, T2, T3, T4, T5>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return AppendLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}"));
        return AppendLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Appends a lazy container as the last child of this field (see <see cref="AppendLazy"/>), with a custom display text made of 7 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendLazyWithCustomText<T0, T1, T2, T3, T4, T5, T6>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return AppendLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}"));
        return AppendLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Appends a lazy container as the last child of this field (see <see cref="AppendLazy"/>), with a custom display text made of 8 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <typeparam name="T7">Type of <paramref name="arg7"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="arg7">Text part 8.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendLazyWithCustomText<T0, T1, T2, T3, T4, T5, T6, T7>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return AppendLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}{s.Item8}"));
        return AppendLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Appends a lazy container as the last child of this field (see <see cref="AppendLazy"/>), with a custom display text made of 9 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <typeparam name="T7">Type of <paramref name="arg7"/>.</typeparam>
    /// <typeparam name="T8">Type of <paramref name="arg8"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="arg7">Text part 8.</param>
    /// <param name="arg8">Text part 9.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendLazyWithCustomText<T0, T1, T2, T3, T4, T5, T6, T7, T8>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return AppendLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}{s.Item8}{s.Item9}"));
        return AppendLazyWithCustomText(fieldId, value, text, populator);
    }

    // The ladders of all positions have the same shape: plain and lazy, 1 to 9 text parts, for append, prepend, and insert-after.
    // The 9-part plain overloads and the lazy ladders of prepend and insert-after follow here; adding an arity means adding it to
    // all six ladders.

    /// <summary>Appends a field as the last child of this field, with a custom display text made of 9 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <typeparam name="T7">Type of <paramref name="arg7"/>.</typeparam>
    /// <typeparam name="T8">Type of <paramref name="arg8"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="arg7">Text part 8.</param>
    /// <param name="arg8">Text part 9.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField AppendWithCustomText<T0, T1, T2, T3, T4, T5, T6, T7, T8>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Append(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}{s.Item8}{s.Item9}"));
        return AppendWithCustomText(fieldId, value, text);
    }

    /// <summary>Prepends a field as the first child of this field, with a custom display text made of 9 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <typeparam name="T7">Type of <paramref name="arg7"/>.</typeparam>
    /// <typeparam name="T8">Type of <paramref name="arg8"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="arg7">Text part 8.</param>
    /// <param name="arg8">Text part 9.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependWithCustomText<T0, T1, T2, T3, T4, T5, T6, T7, T8>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return Prepend(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}{s.Item8}{s.Item9}"));
        return PrependWithCustomText(fieldId, value, text);
    }

    /// <summary>Inserts a field right after this field, as its next sibling, with a custom display text made of 9 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <typeparam name="T7">Type of <paramref name="arg7"/>.</typeparam>
    /// <typeparam name="T8">Type of <paramref name="arg8"/>.</typeparam>
    /// <param name="fieldId">The registered field; an id of another stack is not detected.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="arg7">Text part 8.</param>
    /// <param name="arg8">Text part 9.</param>
    /// <returns>The cursor of the new field; see <see cref="Append(FieldId, FieldValue)"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterWithCustomText<T0, T1, T2, T3, T4, T5, T6, T7, T8>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfter(fieldId, value);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}{s.Item8}{s.Item9}"));
        return InsertAfterWithCustomText(fieldId, value, text);
    }

    /// <summary>Prepends a lazy container as the first child (see <see cref="PrependLazy"/>), with a custom display text made of 1 part.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependLazyWithCustomText<T0>(
        FieldId fieldId, FieldValue value, T0 arg0, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return PrependLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            arg0,
            static s => string.Create(CultureInfo.InvariantCulture, $"{s}"));
        return PrependLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Prepends a lazy container as the first child (see <see cref="PrependLazy"/>), with a custom display text made of 2 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependLazyWithCustomText<T0, T1>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return PrependLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}"));
        return PrependLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Prepends a lazy container as the first child (see <see cref="PrependLazy"/>), with a custom display text made of 3 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependLazyWithCustomText<T0, T1, T2>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return PrependLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}"));
        return PrependLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Prepends a lazy container as the first child (see <see cref="PrependLazy"/>), with a custom display text made of 4 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependLazyWithCustomText<T0, T1, T2, T3>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return PrependLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}"));
        return PrependLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Prepends a lazy container as the first child (see <see cref="PrependLazy"/>), with a custom display text made of 5 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependLazyWithCustomText<T0, T1, T2, T3, T4>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return PrependLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}"));
        return PrependLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Prepends a lazy container as the first child (see <see cref="PrependLazy"/>), with a custom display text made of 6 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependLazyWithCustomText<T0, T1, T2, T3, T4, T5>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return PrependLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}"));
        return PrependLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Prepends a lazy container as the first child (see <see cref="PrependLazy"/>), with a custom display text made of 7 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependLazyWithCustomText<T0, T1, T2, T3, T4, T5, T6>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return PrependLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}"));
        return PrependLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Prepends a lazy container as the first child (see <see cref="PrependLazy"/>), with a custom display text made of 8 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <typeparam name="T7">Type of <paramref name="arg7"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="arg7">Text part 8.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependLazyWithCustomText<T0, T1, T2, T3, T4, T5, T6, T7>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return PrependLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}{s.Item8}"));
        return PrependLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Prepends a lazy container as the first child (see <see cref="PrependLazy"/>), with a custom display text made of 9 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <typeparam name="T7">Type of <paramref name="arg7"/>.</typeparam>
    /// <typeparam name="T8">Type of <paramref name="arg8"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="arg7">Text part 8.</param>
    /// <param name="arg8">Text part 9.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField PrependLazyWithCustomText<T0, T1, T2, T3, T4, T5, T6, T7, T8>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return PrependLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}{s.Item8}{s.Item9}"));
        return PrependLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Inserts a lazy container right after this field (see <see cref="InsertAfterLazy"/>), with a custom display text made of 1 part.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterLazyWithCustomText<T0>(
        FieldId fieldId, FieldValue value, T0 arg0, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfterLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            arg0,
            static s => string.Create(CultureInfo.InvariantCulture, $"{s}"));
        return InsertAfterLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Inserts a lazy container right after this field (see <see cref="InsertAfterLazy"/>), with a custom display text made of 2 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterLazyWithCustomText<T0, T1>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfterLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}"));
        return InsertAfterLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Inserts a lazy container right after this field (see <see cref="InsertAfterLazy"/>), with a custom display text made of 3 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterLazyWithCustomText<T0, T1, T2>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfterLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}"));
        return InsertAfterLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Inserts a lazy container right after this field (see <see cref="InsertAfterLazy"/>), with a custom display text made of 4 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterLazyWithCustomText<T0, T1, T2, T3>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfterLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}"));
        return InsertAfterLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Inserts a lazy container right after this field (see <see cref="InsertAfterLazy"/>), with a custom display text made of 5 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterLazyWithCustomText<T0, T1, T2, T3, T4>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfterLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}"));
        return InsertAfterLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Inserts a lazy container right after this field (see <see cref="InsertAfterLazy"/>), with a custom display text made of 6 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterLazyWithCustomText<T0, T1, T2, T3, T4, T5>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfterLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}"));
        return InsertAfterLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Inserts a lazy container right after this field (see <see cref="InsertAfterLazy"/>), with a custom display text made of 7 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterLazyWithCustomText<T0, T1, T2, T3, T4, T5, T6>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfterLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}"));
        return InsertAfterLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Inserts a lazy container right after this field (see <see cref="InsertAfterLazy"/>), with a custom display text made of 8 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <typeparam name="T7">Type of <paramref name="arg7"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="arg7">Text part 8.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterLazyWithCustomText<T0, T1, T2, T3, T4, T5, T6, T7>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfterLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}{s.Item8}"));
        return InsertAfterLazyWithCustomText(fieldId, value, text, populator);
    }

    /// <summary>Inserts a lazy container right after this field (see <see cref="InsertAfterLazy"/>), with a custom display text made of 9 parts.</summary>
    /// <typeparam name="T0">Type of <paramref name="arg0"/>.</typeparam>
    /// <typeparam name="T1">Type of <paramref name="arg1"/>.</typeparam>
    /// <typeparam name="T2">Type of <paramref name="arg2"/>.</typeparam>
    /// <typeparam name="T3">Type of <paramref name="arg3"/>.</typeparam>
    /// <typeparam name="T4">Type of <paramref name="arg4"/>.</typeparam>
    /// <typeparam name="T5">Type of <paramref name="arg5"/>.</typeparam>
    /// <typeparam name="T6">Type of <paramref name="arg6"/>.</typeparam>
    /// <typeparam name="T7">Type of <paramref name="arg7"/>.</typeparam>
    /// <typeparam name="T8">Type of <paramref name="arg8"/>.</typeparam>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="arg0">Text part 1.</param>
    /// <param name="arg1">Text part 2.</param>
    /// <param name="arg2">Text part 3.</param>
    /// <param name="arg3">Text part 4.</param>
    /// <param name="arg4">Text part 5.</param>
    /// <param name="arg5">Text part 6.</param>
    /// <param name="arg6">Text part 7.</param>
    /// <param name="arg7">Text part 8.</param>
    /// <param name="arg8">Text part 9.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once.</param>
    /// <returns>The cursor of the new lazy container.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    /// <inheritdoc cref="AppendWithCustomText{T0}(FieldId, FieldValue, T0)" path="/remarks"/>
    public readonly MutField InsertAfterLazyWithCustomText<T0, T1, T2, T3, T4, T5, T6, T7, T8>(
        FieldId fieldId, FieldValue value, T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, LazyPopulator populator)
    {
        if (!Packet.WantsCustomText(fieldId))
        {
            return InsertAfterLazy(fieldId, value, populator);
        }

        LazyString text = LazyString.FormatLazy(
            (arg0, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8),
            static s => string.Create(CultureInfo.InvariantCulture, $"{s.Item1}{s.Item2}{s.Item3}{s.Item4}{s.Item5}{s.Item6}{s.Item7}{s.Item8}{s.Item9}"));
        return InsertAfterLazyWithCustomText(fieldId, value, text, populator);
    }

    #endregion

    #endregion

    #region Lazy containers

    /// <summary>
    /// Appends a lazy container field whose children will be populated on first access.
    /// The <paramref name="populator"/> closure is called at most once: when children are first accessed, or right away
    /// when a field watch (or, on a skip parse, the value cache) needs the children; a container nobody reads is never
    /// populated. See <see cref="LazyPopulator"/> for what a populator may and may not do.
    /// </summary>
    /// <remarks>
    /// Lazy containers exist so that a sub-tree costs nothing for parses that never look inside it. The container field
    /// itself is appended eagerly so its position in the tree is fixed.
    /// </remarks>
    /// <param name="fieldId">The field ID for the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>).</param>
    /// <param name="populator">
    /// A closure that creates child fields on demand. A <see langword="null"/> closure is a dissector bug: it is recorded as a
    /// <c>packet.error</c> field (counted in <see cref="Packet.ErrorCount"/>), nothing is thrown, and the container stays empty.
    /// </param>
    /// <returns>
    /// The cursor of the new container; in a <see cref="FieldTreeMode.Skip"/> parse it has <see cref="FieldBody.SkipStorageIndex"/>
    /// as its storage index (see <see cref="Append(FieldId, FieldValue)"/>).
    /// </returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    public readonly MutField AppendLazy(
        FieldId fieldId, FieldValue value, LazyPopulator populator)
    {
        // Append the container first (it gets its final place in the tree), then register the populator against its index.
        // Registration may materialize immediately when a watch needs the children, so the order matters.
        ushort newIndex = Packet.AppendChild(StorageIndex, fieldId, value);
        Packet.RegisterLazyPopulator(fieldId, newIndex, in value, default, populator);
        // The populator is invoked later via MaterializeLazyField without a ParseContext
        // (ref struct cannot be captured in closures) — intentionally preventing deferred index recording.
        return new MutField(Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Appends a lazy container field as the last child of this field, with custom display text.
    /// </summary>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="customText">The text shown for the container; default means "no custom text".</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once (see <see cref="AppendLazy"/>).</param>
    /// <returns>The cursor of the new lazy container; see <see cref="AppendLazy"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    public readonly MutField AppendLazyWithCustomText(
        FieldId fieldId, FieldValue value, LazyString customText, LazyPopulator populator)
    {
        ushort newIndex = Packet.AppendChildWithCustomText(StorageIndex, fieldId, value, customText);
        Packet.RegisterLazyPopulator(fieldId, newIndex, in value, customText, populator);
        return new MutField(Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Prepends a lazy container field as the first child of this field; its children are populated on first access.
    /// </summary>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once (see <see cref="AppendLazy"/>).</param>
    /// <returns>The cursor of the new lazy container; see <see cref="AppendLazy"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    public readonly MutField PrependLazy(
        FieldId fieldId, FieldValue value, LazyPopulator populator)
    {
        ushort newIndex = Packet.PrependChild(StorageIndex, fieldId, value);
        Packet.RegisterLazyPopulator(fieldId, newIndex, in value, default, populator);
        return new MutField(Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Prepends a lazy container field as the first child of this field, with custom display text.
    /// </summary>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="customText">The text shown for the container; default means "no custom text".</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once (see <see cref="AppendLazy"/>).</param>
    /// <returns>The cursor of the new lazy container; see <see cref="AppendLazy"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields.</exception>
    public readonly MutField PrependLazyWithCustomText(
        FieldId fieldId, FieldValue value, LazyString customText, LazyPopulator populator)
    {
        ushort newIndex = Packet.PrependChildWithCustomText(StorageIndex, fieldId, value, customText);
        Packet.RegisterLazyPopulator(fieldId, newIndex, in value, customText, populator);
        return new MutField(Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Inserts a lazy container field right after this field, as its next sibling; its children are populated on first access.
    /// </summary>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once (see <see cref="AppendLazy"/>).</param>
    /// <returns>The cursor of the new lazy container; see <see cref="AppendLazy"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    public readonly MutField InsertAfterLazy(
        FieldId fieldId, FieldValue value, LazyPopulator populator)
    {
        ushort newIndex = Packet.InsertAfter(StorageIndex, fieldId, value);
        Packet.RegisterLazyPopulator(fieldId, newIndex, in value, default, populator);
        return new MutField(Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Inserts a lazy container field right after this field, as its next sibling, with custom display text.
    /// </summary>
    /// <param name="fieldId">The registered field of the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>); copied into the packet.</param>
    /// <param name="customText">The text shown for the container; default means "no custom text".</param>
    /// <param name="populator">A closure that creates the child fields on demand; called at most once (see <see cref="AppendLazy"/>).</param>
    /// <returns>The cursor of the new lazy container; see <see cref="AppendLazy"/> for the cursor in Skip mode.</returns>
    /// <exception cref="FieldAppendException">This field is the root, or the packet holds the maximum number of fields.</exception>
    public readonly MutField InsertAfterLazyWithCustomText(
        FieldId fieldId, FieldValue value, LazyString customText, LazyPopulator populator)
    {
        ushort newIndex = Packet.InsertAfterWithCustomText(StorageIndex, fieldId, value, customText);
        Packet.RegisterLazyPopulator(fieldId, newIndex, in value, customText, populator);
        return new MutField(Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Materializes this field's lazy children if it is a lazy container that has not been populated yet.
    /// Useful for selective materialization during parsing when a downstream protocol needs to access
    /// specific fields from an upstream protocol (e.g., UDP reading IP addresses for checksum).
    /// Runs the populator synchronously on the calling thread; after Seal the packet makes sure only one thread runs it.
    /// </summary>
    /// <returns><see langword="true"/> if materialization was performed, <see langword="false"/> if already populated or not lazy.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool MaterializeIfLazy() => Packet.MaterializeLazyField(StorageIndex);

    #endregion

    #region Protocol Dispatch

    // Shared shape of the TryCallNextProtocol* methods (they differ only in the key type and table lookup):
    //  1. Resolve the dispatch table; configuration faults become Error results.
    //  2. Look up ALL protocols registered for the key (a table can map one key to several protocols).
    //  3. Fast path, 0 or 1 match: 0 is NotDispatched (a miss, not an error); 1 is a direct CallProtocol on this cursor,
    //     so the common case adds no container field and allocates nothing.
    //  4. Rare path, 2 or more matches: _DispatchMultipleProtocols wraps the alternatives in a packet.choice container.
    //     The display text of the key (key.ToString) is built only on that path, so the fast path never formats the key.
    // The ParseContext is copied once via WithDispatch, only after a match, to record which table, key and caller led to the callee (readable
    // by the callee through its DispatchContext; only Core can write it); it is passed by 'in' everywhere else.

    /// <summary>Error when <see cref="ParseContext.Stack"/> is null (configuration fault, not a key miss).</summary>
    /// <returns>An internal-error result.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ParseResult _MissingStack() =>
        ParseError.InternalError("ParseContext has no Stack");

    /// <summary>Error when the dispatch table id is valid but not registered on this stack.</summary>
    /// <returns>A protocol-table-missing result.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ParseResult _MissingTable() =>
        ParseError.ProtocolTableMissing(null, "Dispatch table is not registered on this stack.");

    /// <summary>
    /// Error when a dispatch method of another key type is called on a table: a configuration fault of the calling protocol, not a
    /// key miss. Names the table, the key type it has, and the key type of the method.
    /// </summary>
    /// <param name="table">The resolved table.</param>
    /// <param name="used">The key type of the dispatch method that was called.</param>
    /// <returns>A protocol-table-missing result that names the table and both key types.</returns>
    /// <remarks>Cold path (the check passes on every correct call): not inlined, and the only place that allocates the text.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParseResult _WrongKeyType(ProtocolTable table, ProtocolTableKeyType used) =>
        ParseError.ProtocolTableMissing(
            null,
            string.Create(CultureInfo.InvariantCulture, $"Dispatch table '{table.Name}' has key type {table.KeyType}, not {used}."));

    /// <summary>Error when the dispatch table id is invalid.</summary>
    /// <returns>A protocol-table-missing result.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ParseResult _InvalidTableId() =>
        ParseError.ProtocolTableMissing(null, "Dispatch table id is invalid.");

    /// <summary>
    /// Resolves the dispatch table of a <c>TryCallNextProtocol*</c> call and checks its key type. Configuration faults (no
    /// stack in the context, invalid or unregistered table id, another key type) are returned as an Error result, never thrown,
    /// so the calling dissector propagates them like any other parse error.
    /// </summary>
    /// <param name="context">The context of the running parse.</param>
    /// <param name="tableId">The table the caller dispatches through.</param>
    /// <param name="keyType">The key type of the calling member.</param>
    /// <param name="stack">The stack of the context on success.</param>
    /// <param name="table">The table on success.</param>
    /// <param name="fault">The Error result to return on failure; default on success.</param>
    /// <returns><see langword="true"/> when the table is usable for <paramref name="keyType"/>.</returns>
    /// <remarks>Perf: four compares on the dispatch path, no allocation; a fault text is built only on the failing branch.</remarks>
    private static bool _TryResolveDispatchTable(
        in ParseContext context,
        ProtocolTableId tableId,
        ProtocolTableKeyType keyType,
        [NotNullWhen(true)] out Stack? stack,
        [NotNullWhen(true)] out ProtocolTable? table,
        out ParseResult fault)
    {
        table = null;
        stack = context.Stack;
        if (stack is null)
        {
            fault = _MissingStack();
            return false;
        }

        if (!tableId.IsValid)
        {
            fault = _InvalidTableId();
            return false;
        }

        table = stack.GetProtocolTable(tableId);
        if (table is null)
        {
            fault = _MissingTable();
            return false;
        }

        // Without this check a call of another key type reads as NotDispatched, because the lookup returns an empty list for
        // another key type.
        if (table.KeyType != keyType)
        {
            fault = _WrongKeyType(table, keyType);
            return false;
        }

        fault = default;
        return true;
    }

    /// <summary>
    /// Dispatches to the next protocol by u64 key lookup.
    /// Returns Ok from the callee, <see cref="ParseResult.NotDispatched"/> when the table exists
    /// and the key has no protocol, or Error when the stack/table is missing, the table has another key type, or the callee errors.
    /// </summary>
    /// <param name="tableId">The u64-keyed dispatch table.</param>
    /// <param name="key">The key to look up.</param>
    /// <param name="data">The bytes the next protocol parses.</param>
    /// <param name="context">The context of the running parse; carries the stack.</param>
    /// <returns>
    /// The callee's result; <see cref="ParseResult.NotDispatched"/> (a miss, not an error) when no protocol is registered for
    /// <paramref name="key"/>; an Error result for a configuration fault. Two or more protocols under one key are parsed as
    /// alternatives inside a <c>packet.choice</c> container.
    /// </returns>
    /// <remarks>Parse-thread only. A miss allocates nothing and copies no context.</remarks>
    public readonly ParseResult TryCallNextProtocolU64(
        ProtocolTableId tableId, ulong key, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (!_TryResolveDispatchTable(in context, tableId, ProtocolTableKeyType.U64, out Stack? stack, out ProtocolTable? table, out ParseResult fault))
        {
            return fault;
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllU64(key);

        // Perf: the miss is the common outcome of a table lookup, so it returns before the context is copied.
        if (protocols.IsEmpty)
        {
            return ParseResult.NotDispatched;
        }

        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForU64(tableId, key, context.SelfProtocolId));
        if (protocols.Length == 1)
        {
            return stack.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        return _DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, key.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Dispatches to the next protocol by string key lookup.
    /// Returns Ok from the callee, <see cref="ParseResult.NotDispatched"/> when the table exists
    /// and the key has no protocol, or Error when the stack/table is missing, the table has another key type, or the callee errors.
    /// </summary>
    /// <param name="tableId">The string-keyed dispatch table.</param>
    /// <param name="key">The key to look up (ordinal comparison).</param>
    /// <param name="data">The bytes the next protocol parses.</param>
    /// <param name="context">The context of the running parse; carries the stack.</param>
    /// <returns>See <see cref="TryCallNextProtocolU64"/>.</returns>
    /// <remarks>Parse-thread only. A miss allocates nothing and copies no context.</remarks>
    public readonly ParseResult TryCallNextProtocolString(
        ProtocolTableId tableId, string key, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (!_TryResolveDispatchTable(in context, tableId, ProtocolTableKeyType.String, out Stack? stack, out ProtocolTable? table, out ParseResult fault))
        {
            return fault;
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllString(key);

        // Perf: miss first (see TryCallNextProtocolU64).
        if (protocols.IsEmpty)
        {
            return ParseResult.NotDispatched;
        }

        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForString(tableId, key, context.SelfProtocolId));
        if (protocols.Length == 1)
        {
            return stack.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        return _DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, key);
    }

    /// <summary>
    /// Dispatches to the next protocol by bytes key lookup.
    /// Returns Ok from the callee, <see cref="ParseResult.NotDispatched"/> when the table exists
    /// and the key has no protocol, or Error when the stack/table is missing, the table has another key type, or the callee errors.
    /// </summary>
    /// <param name="tableId">The bytes-keyed dispatch table.</param>
    /// <param name="key">The key to look up.</param>
    /// <param name="data">The bytes the next protocol parses.</param>
    /// <param name="context">The context of the running parse; carries the stack.</param>
    /// <returns>See <see cref="TryCallNextProtocolU64"/>.</returns>
    /// <remarks>Parse-thread only. A miss copies no context.</remarks>
    public readonly ParseResult TryCallNextProtocolBytes(
        ProtocolTableId tableId, BytesKey key, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (!_TryResolveDispatchTable(in context, tableId, ProtocolTableKeyType.Bytes, out Stack? stack, out ProtocolTable? table, out ParseResult fault))
        {
            return fault;
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllBytes(key);

        // Perf: miss first (see TryCallNextProtocolU64).
        if (protocols.IsEmpty)
        {
            return ParseResult.NotDispatched;
        }

        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForBytes(tableId, key, context.SelfProtocolId));
        if (protocols.Length == 1)
        {
            return stack.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        return _DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, key.ToString());
    }

    /// <summary>
    /// Dispatches to the next protocol by bool key lookup.
    /// Returns Ok from the callee, <see cref="ParseResult.NotDispatched"/> when the table exists
    /// and the key has no protocol, or Error when the stack/table is missing, the table has another key type, or the callee errors.
    /// </summary>
    /// <param name="tableId">The bool-keyed dispatch table.</param>
    /// <param name="key">The key to look up.</param>
    /// <param name="data">The bytes the next protocol parses.</param>
    /// <param name="context">The context of the running parse; carries the stack.</param>
    /// <returns>See <see cref="TryCallNextProtocolU64"/>.</returns>
    /// <remarks>Parse-thread only. A miss allocates nothing and copies no context.</remarks>
    public readonly ParseResult TryCallNextProtocolBool(
        ProtocolTableId tableId, bool key, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (!_TryResolveDispatchTable(in context, tableId, ProtocolTableKeyType.Bool, out Stack? stack, out ProtocolTable? table, out ParseResult fault))
        {
            return fault;
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllBool(key);

        // Perf: miss first (see TryCallNextProtocolU64).
        if (protocols.IsEmpty)
        {
            return ParseResult.NotDispatched;
        }

        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForBool(tableId, key, context.SelfProtocolId));
        if (protocols.Length == 1)
        {
            return stack.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        return _DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, key.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Dispatches to any protocol registered in the table.
    /// Returns Ok from the callee, <see cref="ParseResult.NotDispatched"/> when the table exists
    /// and has zero protocols, or Error when the stack/table is missing, the table has another key type, or the callee errors.
    /// </summary>
    /// <param name="tableId">The dispatch table without keys.</param>
    /// <param name="data">The bytes the next protocol parses.</param>
    /// <param name="context">The context of the running parse; carries the stack.</param>
    /// <returns>See <see cref="TryCallNextProtocolU64"/>; the miss is an empty table.</returns>
    /// <remarks>Parse-thread only. A miss allocates nothing and copies no context.</remarks>
    public readonly ParseResult TryCallNextProtocolAny(
        ProtocolTableId tableId, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (!_TryResolveDispatchTable(in context, tableId, ProtocolTableKeyType.Any, out Stack? stack, out ProtocolTable? table, out ParseResult fault))
        {
            return fault;
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllAny();

        // Perf: miss first (see TryCallNextProtocolU64).
        if (protocols.IsEmpty)
        {
            return ParseResult.NotDispatched;
        }

        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForAny(tableId, context.SelfProtocolId));
        if (protocols.Length == 1)
        {
            return stack.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        return _DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, "*");
    }

    /// <summary>
    /// Directly calls a specific protocol. Returns InternalError when <see cref="ParseContext.Stack"/>
    /// is null; never <see cref="ParseResult.NotDispatched"/>.
    /// Invalid ids return <see cref="ParseError"/> via <see cref="Stack.CallProtocol"/>.
    /// </summary>
    /// <param name="protocolId">The protocol to call.</param>
    /// <param name="data">The bytes the protocol parses.</param>
    /// <param name="context">The context of the running parse; carries the stack.</param>
    /// <returns>The callee's result, or an Error result when the context has no stack or the id is not valid on the stack.</returns>
    /// <remarks>Parse thread only. The callee receives this cursor as its parent field.</remarks>
    public readonly ParseResult CallProtocol(ProtocolId protocolId, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        Stack? stack = context.Stack;
        if (stack is null)
        {
            return _MissingStack();
        }
        return stack.CallProtocol(protocolId, in this, data, in context);
    }

    /// <summary>
    /// Dispatches using heuristic matching.
    /// Returns Ok from the callee, <see cref="ParseResult.NotDispatched"/> when the table exists
    /// and <c>TryMatch</c> finds nothing, or Error when the stack/table is missing or the callee errors.
    /// </summary>
    /// <param name="tableId">The heuristic dispatch table.</param>
    /// <param name="data">The bytes the table inspects and the matching protocol parses.</param>
    /// <param name="context">The context of the running parse; carries the stack.</param>
    /// <returns>
    /// The callee's result; <see cref="ParseResult.NotDispatched"/> (a miss, not an error) when no protocol of the table matches;
    /// an Error result for a configuration fault (no stack, invalid or unregistered table id).
    /// </returns>
    /// <remarks>Parse thread only. At most one protocol is called (the first that matches), so no choice container is added.</remarks>
    public readonly ParseResult TryCallHeuristicProtocol(
        HeuristicProtocolTableId tableId, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        Stack? stack = context.Stack;
        if (stack is null)
        {
            return _MissingStack();
        }

        if (!tableId.IsValid)
        {
            return _InvalidTableId();
        }

        HeuristicProtocolTable? table = stack.GetHeuristicProtocolTable(tableId);
        if (table is null)
        {
            return _MissingTable();
        }

        // A heuristic table inspects the payload itself and yields at most one protocol (the first that matches), so there is
        // no choice container here; no DispatchContext is recorded because there is no table key to report.
        if (!table.TryMatch(data, out ProtocolId match))
        {
            return ParseResult.NotDispatched;
        }

        return stack.CallProtocol(match, in this, data, in context);
    }

    #endregion

    #region Choice container for several matches

    /// <summary>
    /// Dispatches to multiple protocols when more than one protocol matches a table key.
    /// Creates a <c>packet.choice</c> container field, dispatches ALL protocols as children,
    /// and returns the maximum consumed bytes.
    /// <para>Callers must pre-check for 0/1 matches (fast path) before calling this method.</para>
    /// <para>
    /// Every alternative receives the same <c>data</c> and is parsed independently; the result is Ok with the largest
    /// consumed count. If every alternative fails (or none consumed anything) the result is still Ok(0), not an error, because
    /// a failing alternative only means that protocol did not fit. Each alternative's own error is not propagated.
    /// </para>
    /// </summary>
    /// <param name="protocols">The protocols registered under the key; two or more.</param>
    /// <param name="data">The bytes every alternative parses.</param>
    /// <param name="context">The dispatch context; its stack is not null (the caller resolved it).</param>
    /// <param name="tableName">The table name, shown in the display text of the container.</param>
    /// <param name="keyDisplay">The key as text, shown in the display text of the container.</param>
    /// <returns>Ok with the largest consumed byte count of any alternative; Ok(0) when none consumed anything.</returns>
    /// <exception cref="FieldAppendException">The packet holds the maximum number of fields (the container cannot be added).</exception>
    private readonly ParseResult _DispatchMultipleProtocols(
        ReadOnlySpan<ProtocolId> protocols, ReadOnlyMemory<byte> data, in ParseContext context,
        string tableName, string keyDisplay)
    {
        // Multiple matches — create a packet.choice container and dispatch all alternatives.
        // The "Choice: " prefix makes the wrapper visually distinct from regular field entries.
        FieldId choiceFieldId = context.Stack!.PacketChoiceFieldId; // Invariant: every caller resolved the stack in _TryResolveDispatchTable.
        MutField choiceField = Packet.WantsCustomText(choiceFieldId)
            ? AppendWithCustomText(choiceFieldId, FieldValue.None, "Choice: ", tableName, ": ", keyDisplay)
            : Append(choiceFieldId, FieldValue.None);

        int maxConsumed = 0;
        for (int i = 0; i < protocols.Length; i++)
        {
            ParseResult result = context.Stack!.CallProtocol(protocols[i], in choiceField, data, in context); // Invariant: see above.
            if (result.TryGetConsumed(out int consumed))
            {
                maxConsumed = Math.Max(maxConsumed, consumed);
            }
            // Errors from individual alternatives are not fatal — continue with remaining.
            // This is intentional: when multiple protocols match a key, each is tried
            // independently and any combination of success/failure is valid.
        }

        return maxConsumed;
    }

    #endregion

    #region Tree navigation

    /// <summary>Creates a cursor for the field at <paramref name="index"/> of the same packet, reading its field id from the storage.</summary>
    /// <param name="index">Storage index of a published field of the packet.</param>
    /// <returns>The cursor of that field.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly MutField ChildMut(ushort index) => new(Packet, index, Packet.GetFieldRef(index).FieldId);

    /// <summary>Gets the parent field.</summary>
    /// <param name="parent">The parent cursor on success; <c>default</c> otherwise.</param>
    /// <returns>
    /// <see langword="false"/> for the root, for a field without a parent link, and on a <see cref="FieldTreeMode.Skip"/>
    /// packet, where no tree exists.
    /// </returns>
    /// <remarks>Follows an index link only: no materialization, no allocation.</remarks>
    public readonly bool TryGetParent(out MutField parent)
    {
        if (!HasFieldTree)
        {
            parent = default;
            return false;
        }

        ushort parentIdx = Packet.GetFieldRef(StorageIndex).ParentIndex;
        if (parentIdx != FieldBody.NullIndex)
        {
            parent = new MutField(Packet, parentIdx, Packet.GetFieldRef(parentIdx).FieldId);
            return true;
        }
        parent = default;
        return false;
    }

    /// <summary>
    /// Tries to get the first child field. Returns false if there are no children.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy children are populated first.
    /// Parent/sibling navigation does not take this parameter because it only follows index links.
    /// </summary>
    /// <param name="firstChild">The first child when present; <c>default</c> otherwise.</param>
    /// <param name="materialize">Whether to materialize lazy children before reading the child list.</param>
    /// <returns><see langword="true"/> when a first child exists; always <see langword="false"/> on a <see cref="FieldTreeMode.Skip"/> packet.</returns>
    public readonly bool TryGetFirstChild(out MutField firstChild, bool materialize)
    {
        if (!HasFieldTree)
        {
            firstChild = default;
            return false;
        }

        if (materialize)
        {
            ref readonly FieldBody body = ref Packet.GetFieldRef(StorageIndex);
            if (body.NeedsMaterialization)
            {
                Packet.MaterializeLazyField(StorageIndex);
            }
        }
        ushort idx = Packet.GetFieldRef(StorageIndex).FirstChildIndex;
        if (idx != FieldBody.NullIndex)
        {
            firstChild = new MutField(Packet, idx, Packet.GetFieldRef(idx).FieldId);
            return true;
        }
        firstChild = default;
        return false;
    }

    /// <summary>
    /// Tries to get the last child field. Returns false if there are no children.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy children are populated first.
    /// </summary>
    /// <param name="lastChild">The last child when present; <c>default</c> otherwise.</param>
    /// <param name="materialize">Whether to materialize lazy children before reading the child list.</param>
    /// <returns><see langword="true"/> when a last child exists; always <see langword="false"/> on a <see cref="FieldTreeMode.Skip"/> packet.</returns>
    public readonly bool TryGetLastChild(out MutField lastChild, bool materialize)
    {
        if (!HasFieldTree)
        {
            lastChild = default;
            return false;
        }

        if (materialize)
        {
            ref readonly FieldBody body = ref Packet.GetFieldRef(StorageIndex);
            if (body.NeedsMaterialization)
            {
                Packet.MaterializeLazyField(StorageIndex);
            }
        }
        ushort idx = Packet.GetFieldRef(StorageIndex).LastChildIndex;
        if (idx != FieldBody.NullIndex)
        {
            lastChild = new MutField(Packet, idx, Packet.GetFieldRef(idx).FieldId);
            return true;
        }
        lastChild = default;
        return false;
    }

    /// <summary>Gets the next sibling field.</summary>
    /// <param name="next">The next sibling cursor on success; <c>default</c> otherwise.</param>
    /// <returns><see langword="false"/> for the last sibling, for the root, and on a <see cref="FieldTreeMode.Skip"/> packet.</returns>
    /// <remarks>Follows an index link only: no materialization, no allocation.</remarks>
    public readonly bool TryGetNext(out MutField next)
    {
        if (!HasFieldTree)
        {
            next = default;
            return false;
        }

        ushort idx = Packet.GetFieldRef(StorageIndex).NextIndex;
        if (idx != FieldBody.NullIndex)
        {
            next = new MutField(Packet, idx, Packet.GetFieldRef(idx).FieldId);
            return true;
        }
        next = default;
        return false;
    }

    /// <summary>Gets the previous sibling field.</summary>
    /// <param name="prev">The previous sibling cursor on success; <c>default</c> otherwise.</param>
    /// <returns><see langword="false"/> for the first sibling, for the root, and on a <see cref="FieldTreeMode.Skip"/> packet.</returns>
    /// <remarks>Follows an index link only: no materialization, no allocation.</remarks>
    public readonly bool TryGetPrev(out MutField prev)
    {
        if (!HasFieldTree)
        {
            prev = default;
            return false;
        }

        ushort idx = Packet.GetFieldRef(StorageIndex).PrevIndex;
        if (idx != FieldBody.NullIndex)
        {
            prev = new MutField(Packet, idx, Packet.GetFieldRef(idx).FieldId);
            return true;
        }
        prev = default;
        return false;
    }

    #endregion

    #region Iteration, conversion, and equality

    /// <summary>
    /// Iterates direct children as mutable cursors.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy children are materialized first.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy children before iterating.</param>
    /// <returns>An allocation-free enumerable for <c>foreach</c>; empty on a <see cref="FieldTreeMode.Skip"/> packet.</returns>
    public readonly MutFieldChildEnumerable Children(bool materialize) => new(Packet, StorageIndex, materialize);

    /// <summary>
    /// Iterates all descendants as mutable cursors (DFS pre-order).
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy fields are materialized during traversal.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy fields during traversal.</param>
    /// <returns>An allocation-free enumerable for <c>foreach</c>; empty on a <see cref="FieldTreeMode.Skip"/> packet.</returns>
    public readonly MutFieldDescendantEnumerable Descendants(bool materialize) => new(Packet, StorageIndex, materialize);

    /// <summary>
    /// Converts to a read-only <see cref="Field"/> cursor of the same packet, storage index, and field id. Reads no field data,
    /// so it is valid on a tree-free packet too (the resulting cursor then has the same read restrictions as this one).
    /// </summary>
    /// <returns>The read-only cursor; no allocation.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Field AsField() => new(Packet, StorageIndex, FieldId);

    /// <summary>
    /// Value equality by packet identity (reference) and storage index. The cached <see cref="FieldId"/> is not compared.
    /// There is no <see cref="object.Equals(object)"/> override or operator
    /// because a ref struct cannot be boxed.
    /// </summary>
    /// <param name="other">The cursor to compare with.</param>
    /// <returns><see langword="true"/> when both cursors point to the same slot of the same packet.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(MutField other) => ReferenceEquals(Packet, other.Packet) && StorageIndex == other.StorageIndex;

    #endregion
}

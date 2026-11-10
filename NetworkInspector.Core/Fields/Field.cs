// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Lightweight read-only wrapper over a field in the packet tree.
/// <para>
/// Can be stored in collections and passed across method boundaries.
/// A default-constructed <see cref="Field"/> is invalid — check <see cref="IsValid"/>
/// before accessing properties that depend on the underlying packet.
/// </para>
/// <para>
/// <b>Design:</b> a field is addressed by a packet reference plus a 16-bit storage index instead of a heap object per
/// field, so walking or storing fields allocates nothing; the data stays in the packet's slab (see
/// <see cref="FieldBody"/>). The struct is 16 bytes: the packet reference (8), the storage index (2), and the cached
/// field id (4), with padding.
/// </para>
/// <para>
/// <b>Thread-safety:</b> an immutable handle that can be copied freely. Reads follow the packet's concurrency
/// contract: tree links are read with volatile loads and only indices below the packet's published field count are
/// followed (see the navigation methods), so a sealed packet can be walked from several threads.
/// </para>
/// <para>
/// <b>Display line:</b> <see cref="ToString()"/> and <see cref="TryFormat(Span{char}, out int, ReadOnlySpan{char}, IFormatProvider?)"/>
/// give the field's default display line: the custom text alone when one is set, else the UI name for a field without a
/// value, else UI name, <c>": "</c>, and the value's default string (see <see cref="FieldValue.ToString()"/>). Every part stays
/// readable (<see cref="FieldInfo"/>, <see cref="Value"/>, <see cref="CustomText"/>), so a caller can build an own line instead.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Field : IEquatable<Field>, ISpanFormattable, IUtf8SpanFormattable
{
    #region Identity and construction

    /// <summary>The owning packet; <see langword="null"/> for a default (invalid) field.</summary>
    public Packet Packet { get; }

    /// <summary>Index into the packet's flat field list.</summary>
    internal ushort StorageIndex { get; }

    /// <summary>
    /// Cached field identifier. Stored so <see cref="FieldInfo"/> and id comparisons need no slab lookup; the 4-byte id
    /// sits in the padding that follows the 2-byte <see cref="StorageIndex"/>, so caching it adds no size.
    /// </summary>
    public FieldId FieldId { get; }

    /// <summary>Creates a field wrapper for the given packet and index (reads the field id from the slab once).</summary>
    /// <param name="packet">The owning packet.</param>
    /// <param name="index">Storage index of a published field.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Field(Packet packet, ushort index)
    {
        Packet = packet;
        StorageIndex = index;
        FieldId = packet.GetFieldRef(index).FieldId;
    }

    /// <summary>Creates a field wrapper when the caller already knows <paramref name="fieldId"/> (skips the slab read).</summary>
    /// <param name="packet">The owning packet.</param>
    /// <param name="index">Storage index of a published field.</param>
    /// <param name="fieldId">The id stored in that slot; not re-checked.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Field(Packet packet, ushort index, FieldId fieldId)
    {
        Packet = packet;
        StorageIndex = index;
        FieldId = fieldId;
    }

    #endregion

    #region Validity

    /// <summary>Whether this field reference points to a valid packet and index.</summary>
    /// <remarks>Sync: <c>FieldDisplayLine</c> repeats the condition for the cursors it formats (no packet, or the null index).</remarks>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Packet is not null && StorageIndex != FieldBody.NullIndex;
    }

    /// <summary>
    /// False when the owning packet was parsed with <see cref="FieldTreeMode.Skip"/>, and for a default field (no packet): such a
    /// field has no tree to walk, so the navigation members report no relatives and no children instead of dereferencing a null packet.
    /// </summary>
    public bool HasFieldTree
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Packet is not null && Packet.HasFieldTree;
    }

    #endregion

    #region Reading the field

    /// <summary>Gets the field's metadata from the stack registry; <see langword="null"/> when the stack does not know <see cref="FieldId"/>.</summary>
    public FieldInfo? FieldInfo => Packet.Stack.GetField(FieldId);

    /// <summary>
    /// The field's value, as a reference to the field's own storage. The string of a deferred custom representation is built
    /// once however the value is read (see <see cref="LazyString"/>); evaluating it through the reference
    /// (<c>field.Value.CustomRepresentation.AsString</c>) additionally replaces the deferred object in the field by its string.
    /// A copy (<c>FieldValue value = field.Value;</c>) costs 32 bytes and leaves the field holding the deferred object, so keep
    /// the reference (<c>ref readonly FieldValue value = ref field.Value;</c>) when the value is read repeatedly.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The packet was parsed with <see cref="FieldTreeMode.Skip"/> and this is not its root (such packets keep no field
    /// slab); use <see cref="TryGetValue"/> when that is expected.
    /// </exception>
    public ref readonly FieldValue Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // A Skip-mode packet has no field slab: only the synthetic root (index 0) can be read. Anything else would
            // read memory that does not exist, so it is rejected up front instead of inside the hot GetFieldRef.
            // SkipStorageIndex is readable only while a skip-tree lazy populator is in flight
            // (GetFieldRef returns the scratch top, or throws when the stack is empty).
            if (!HasFieldTree && StorageIndex != 0 && StorageIndex != FieldBody.SkipStorageIndex)
            {
                ThrowHelpers.ThrowSkipFieldTreeValue();
            }

            return ref Packet.GetFieldRef(StorageIndex).Value;
        }
    }

    /// <summary>
    /// Tries to read this field's value. Returns <see langword="false"/> for skip-tree children
    /// outside the synthetic root and an in-flight skip-tree populator. Skip packets should only
    /// read the root; use this instead of <see cref="Value"/> when a miss is expected.
    /// </summary>
    /// <param name="value">The field value when the read is allowed.</param>
    /// <returns><see langword="true"/> when a value was stored in <paramref name="value"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out FieldValue value)
    {
        // Same Skip-mode rule as Value, reported as a miss instead of an exception.
        if (!HasFieldTree && StorageIndex != 0 && StorageIndex != FieldBody.SkipStorageIndex)
        {
            value = FieldValue.None;
            return false;
        }

        value = Packet.GetFieldRef(StorageIndex).Value;
        return true;
    }

    /// <summary>
    /// Tries to read this field's custom display text. Returns <see langword="false"/> for
    /// skip-tree children that cannot expose a stored <see cref="FieldBody"/>.
    /// </summary>
    /// <param name="text">
    /// A copy of the custom display text when the read is allowed. The copy is not evaluated; it shares the one build of the
    /// string with every other copy (see <see cref="LazyString"/>). Read <see cref="CustomText"/> to also release the deferred
    /// object in the field's own storage.
    /// </param>
    /// <returns><see langword="true"/> when text was stored in <paramref name="text"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetCustomText(out LazyString text)
    {
        if (!HasFieldTree && StorageIndex != 0 && StorageIndex != FieldBody.SkipStorageIndex)
        {
            text = default;
            return false;
        }

        text = Packet.GetFieldRef(StorageIndex).CustomText;
        return true;
    }

    /// <summary>
    /// Optional custom display text (check <see cref="LazyString.IsNull"/> for absence), as a reference to the field's own
    /// storage. Reading the property never evaluates a deferred text.
    /// <para>
    /// The string of a deferred text is built once however it is read (see <see cref="LazyString"/>), and only for a reader that
    /// needs it: a reader that only checks for absence or hands the text on pays nothing. Evaluating through the reference
    /// (<c>field.CustomText.AsString</c>) additionally replaces the deferred object in the field by its string; a copy
    /// (<c>LazyString text = field.CustomText;</c>) shares the one build but leaves the field holding the deferred object.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The packet was parsed with <see cref="FieldTreeMode.Skip"/> and this is not its root; use
    /// <see cref="TryGetCustomText"/> when that is expected.
    /// </exception>
    public ref readonly LazyString CustomText
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (!HasFieldTree && StorageIndex != 0 && StorageIndex != FieldBody.SkipStorageIndex)
            {
                ThrowHelpers.ThrowSkipFieldTreeValue();
            }

            // GetFieldRef yields a ref to the slab slot (no copy), so the reference points at the storage that caches.
            return ref Packet.GetFieldRef(StorageIndex).CustomText;
        }
    }

    /// <summary>
    /// True only when this cursor is valid and its storage index is 0.
    /// A default cursor is not the root.
    /// </summary>
    public bool IsRoot
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
    /// <see langword="true"/> when the field has at least one child; always <see langword="false"/> when the packet was parsed
    /// with <see cref="FieldTreeMode.Skip"/>.
    /// </returns>
    /// <remarks>
    /// With <paramref name="materialize"/> this waits for a lazy field that another thread is materializing; the
    /// <c>MutField</c> counterpart does not wait (a parse-thread cursor never needs to).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasChildren(bool materialize)
    {
        if (!HasFieldTree)
        {
            return false;
        }

        // Lazy containers (children created on first access) are populated only on request, so a caller that merely
        // counts or checks children does not pay for parsing it never shows.
        if (materialize)
        {
            ref readonly FieldBody body = ref Packet.GetFieldRef(StorageIndex);
            if (body.IsLazyPending)
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
    /// <returns>The number of direct children; 0 when the packet was parsed with <see cref="FieldTreeMode.Skip"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ChildCount(bool materialize)
    {
        if (!HasFieldTree)
        {
            return 0;
        }

        if (materialize)
        {
            ref readonly FieldBody body = ref Packet.GetFieldRef(StorageIndex);
            if (body.IsLazyPending)
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
    internal bool NeedsLazyMaterialization
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Packet.GetFieldRef(StorageIndex).NeedsMaterialization;
    }

    #endregion

    #region Default string representation

    // The default display line (custom text alone; else the UI name for a field without a value; else UI name, ": ", and the
    // value's default string) is built by FieldDisplayLine, which MutField calls too, so the two cursors cannot drift apart.
    // The parts stay readable (FieldInfo, Value, CustomText), so a caller can build an own line instead.

    /// <summary>
    /// Returns the default display line of this field: the custom text alone when one is set, else the UI name for a field without
    /// a value, else the UI name, <c>": "</c>, and the value's default string (see <see cref="FieldValue.ToString()"/>). A field
    /// id that the stack does not know has no UI name: its line is the value part alone.
    /// </summary>
    /// <returns>
    /// The line. Never throws (a debugger, a log line, or an interpolation may call it on any field, and CA1065 forbids exceptions
    /// in <c>ToString</c>): a default (invalid) field gives the empty string; a cursor of a <see cref="FieldTreeMode.Skip"/> packet
    /// other than its root, which keeps no value or text (see <see cref="Value"/>), gives the label alone. Allocates the result
    /// string (and one temporary for the value part); use
    /// <see cref="TryFormat(Span{char}, out int, ReadOnlySpan{char}, IFormatProvider?)"/> on hot paths.
    /// </returns>
    /// <remarks>
    /// The line is not escaped: a custom text or string value holds whatever the capture holds, so a consumer that writes it to a
    /// terminal, a CSV cell, or markup escapes it for that target. Reading builds a deferred custom text (see
    /// <see cref="LazyString"/>) and caches it in the field. Sync: EXPORTER_GUIDE § Rebuilding the display line.
    /// </remarks>
    /// <seealso cref="FieldValue.ToString()"/>
    /// <seealso cref="CustomText"/>
    public override string ToString() => FieldDisplayLine.GetLine(Packet, StorageIndex, FieldId);

    /// <summary>Returns <see cref="ToString()"/>; both arguments are ignored (every field has exactly one default line).</summary>
    /// <param name="format">Ignored.</param>
    /// <param name="formatProvider">Ignored; numbers use the invariant culture.</param>
    /// <returns>The same line as <see cref="ToString()"/>.</returns>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>Formats the default display line into a character span; reports failure when it does not fit.</summary>
    /// <param name="destination">Target span.</param>
    /// <param name="charsWritten">Characters written; 0 when the method returns <see langword="false"/>.</param>
    /// <param name="format">Ignored, as for <see cref="FieldValue"/>: every line has one form.</param>
    /// <param name="provider">Ignored; numbers use the invariant culture.</param>
    /// <returns>
    /// <see langword="false"/> when <paramref name="destination"/> is too small (its contents are then unspecified). The invalid
    /// and skip-tree cases follow <see cref="ToString()"/>: an empty line (true, 0 written), and the label alone.
    /// </returns>
    /// <remarks>Perf: writes straight into <paramref name="destination"/>; no allocation. A deferred custom text is built once and
    /// caches in the field's own slot.</remarks>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => FieldDisplayLine.TryFormat(Packet, StorageIndex, FieldId, destination, out charsWritten);

    /// <summary>Formats the default display line as UTF-8; same cases and failure contract as the character overload.</summary>
    /// <param name="utf8Destination">Target span.</param>
    /// <param name="bytesWritten">Bytes written; 0 when the method returns <see langword="false"/>.</param>
    /// <param name="format">Ignored.</param>
    /// <param name="provider">Ignored; numbers use the invariant culture.</param>
    /// <returns><see langword="false"/> when <paramref name="utf8Destination"/> is too small.</returns>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => FieldDisplayLine.TryFormat(Packet, StorageIndex, FieldId, utf8Destination, out bytesWritten);

    #endregion

    #region Tree Navigation

    /// <summary>
    /// Tries to get the parent field. Returns false if this is a root field (or the packet has no field tree).
    /// The parent was published before its children, so no wait on the published count is needed here.
    /// </summary>
    /// <param name="parent">The parent when present; the default (invalid) field when the method returns <see langword="false"/>.</param>
    /// <returns>
    /// <see langword="true"/> when this field has a parent; <see langword="false"/> for the root and for a packet parsed with
    /// <see cref="FieldTreeMode.Skip"/>.
    /// </returns>
    /// <remarks>
    /// A default field (see <see cref="IsValid"/>) has no packet and no relatives: the method returns <see langword="false"/>.
    /// Cost: no allocation; one slab read.
    /// </remarks>
    public bool TryGetParent(out Field parent)
    {
        if (!HasFieldTree)
        {
            parent = default;
            return false;
        }

        ushort parentIdx = Packet.GetFieldRef(StorageIndex).ParentIndex;
        if (parentIdx != FieldBody.NullIndex)
        {
            parent = new Field(Packet, parentIdx);
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
    /// <param name="firstChild">The first child when present; the default (invalid) field when the method returns <see langword="false"/>.</param>
    /// <param name="materialize">Whether to materialize lazy children before reading the child list.</param>
    /// <returns>
    /// <see langword="true"/> when the field has a child; <see langword="false"/> when it has none (an unmaterialized lazy
    /// container without <paramref name="materialize"/> included) and for a packet parsed with <see cref="FieldTreeMode.Skip"/>.
    /// </returns>
    public bool TryGetFirstChild(out Field firstChild, bool materialize)
    {
        if (!HasFieldTree)
        {
            firstChild = default;
            return false;
        }

        if (materialize)
        {
            ref readonly FieldBody body = ref Packet.GetFieldRef(StorageIndex);
            if (body.IsLazyPending)
            {
                Packet.MaterializeLazyField(StorageIndex);
            }
        }
        // The child link is read with a volatile load; WaitUntilFieldPublished then makes sure the slot it points to is
        // covered by the packet's published field count (a concurrent materializer may have linked it a moment earlier),
        // so a default FieldBody is never observed. The same pattern guards every link read below.
        ushort idx = Packet.WaitUntilFieldPublished(Packet.GetFieldRef(StorageIndex).FirstChildIndex);
        if (idx != FieldBody.NullIndex)
        {
            firstChild = new Field(Packet, idx);
            return true;
        }
        firstChild = default;
        return false;
    }

    /// <summary>
    /// Tries to get the last child field. Returns false if there are no children.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy children are populated first.
    /// </summary>
    /// <param name="lastChild">The last child when present; the default (invalid) field when the method returns <see langword="false"/>.</param>
    /// <param name="materialize">Whether to materialize lazy children before reading the child list.</param>
    /// <returns>
    /// <see langword="true"/> when the field has a child; <see langword="false"/> when it has none (an unmaterialized lazy
    /// container without <paramref name="materialize"/> included) and for a packet parsed with <see cref="FieldTreeMode.Skip"/>.
    /// </returns>
    public bool TryGetLastChild(out Field lastChild, bool materialize)
    {
        if (!HasFieldTree)
        {
            lastChild = default;
            return false;
        }

        if (materialize)
        {
            ref readonly FieldBody body = ref Packet.GetFieldRef(StorageIndex);
            if (body.IsLazyPending)
            {
                Packet.MaterializeLazyField(StorageIndex);
            }
        }
        ushort idx = Packet.WaitUntilFieldPublished(Packet.GetFieldRef(StorageIndex).LastChildIndex);
        if (idx != FieldBody.NullIndex)
        {
            lastChild = new Field(Packet, idx);
            return true;
        }
        lastChild = default;
        return false;
    }

    /// <summary>Tries to get the next sibling field. Returns false if this is the last sibling.</summary>
    /// <param name="next">The next sibling when present; the default (invalid) field when the method returns <see langword="false"/>.</param>
    /// <returns>
    /// <see langword="true"/> when a next sibling exists; <see langword="false"/> for the last sibling and for a packet parsed
    /// with <see cref="FieldTreeMode.Skip"/>.
    /// </returns>
    /// <remarks>
    /// A default field has no relatives: the method returns <see langword="false"/>. A concurrent materializer may have linked the
    /// sibling a moment earlier: the read then waits briefly until the sibling's slot is published. No allocation.
    /// </remarks>
    public bool TryGetNext(out Field next)
    {
        if (!HasFieldTree)
        {
            next = default;
            return false;
        }

        ushort idx = Packet.WaitUntilFieldPublished(Packet.GetFieldRef(StorageIndex).NextIndex);
        if (idx != FieldBody.NullIndex)
        {
            next = new Field(Packet, idx);
            return true;
        }
        next = default;
        return false;
    }

    /// <summary>Tries to get the previous sibling field. Returns false if this is the first sibling.</summary>
    /// <param name="prev">The previous sibling when present; the default (invalid) field when the method returns <see langword="false"/>.</param>
    /// <returns>
    /// <see langword="true"/> when a previous sibling exists; <see langword="false"/> for the first sibling and for a packet
    /// parsed with <see cref="FieldTreeMode.Skip"/>.
    /// </returns>
    /// <remarks>Same preconditions and cost as <see cref="TryGetNext"/>.</remarks>
    public bool TryGetPrev(out Field prev)
    {
        if (!HasFieldTree)
        {
            prev = default;
            return false;
        }

        ushort idx = Packet.WaitUntilFieldPublished(Packet.GetFieldRef(StorageIndex).PrevIndex);
        if (idx != FieldBody.NullIndex)
        {
            prev = new Field(Packet, idx);
            return true;
        }
        prev = default;
        return false;
    }

    #endregion

    #region Iterators

    /// <summary>
    /// Iterates direct children of this field.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy children are materialized first.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy children before iterating.</param>
    /// <returns>An enumerable of the direct children in order; empty for a packet parsed with <see cref="FieldTreeMode.Skip"/>.</returns>
    public FieldChildEnumerable Children(bool materialize) => new(Packet, StorageIndex, materialize);

    /// <summary>
    /// Iterates all descendants in depth-first pre-order.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy fields are materialized during traversal.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy fields during traversal.</param>
    /// <returns>An enumerable of every descendant, this field excluded; empty for a packet parsed with <see cref="FieldTreeMode.Skip"/>.</returns>
    public FieldDescendantEnumerable Descendants(bool materialize) => new(Packet, StorageIndex, materialize);

    #endregion

    #region Equality

    /// <inheritdoc />
    /// <remarks>Two handles are equal when they address the same slot of the same packet instance (reference identity of the packet).</remarks>
    /// <param name="other">The handle to compare with.</param>
    /// <returns><see langword="true"/> when both handles address the same slot of the same packet.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Field other) => ReferenceEquals(Packet, other.Packet) && StorageIndex == other.StorageIndex;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Field other && Equals(other);

    /// <inheritdoc />
    /// <remarks>Uses the packet's identity hash so the hash matches the reference-identity <see cref="Equals(Field)"/>.</remarks>
    public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Packet), StorageIndex);

    /// <summary>Whether two handles address the same slot of the same packet (see <see cref="Equals(Field)"/>).</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true"/> when both handles address the same slot of the same packet.</returns>
    public static bool operator ==(Field left, Field right) => left.Equals(right);

    /// <summary>Whether two handles address different slots or different packets (see <see cref="Equals(Field)"/>).</summary>
    /// <param name="left">The first handle.</param>
    /// <param name="right">The second handle.</param>
    /// <returns><see langword="true"/> when the handles do not address the same slot of the same packet.</returns>
    public static bool operator !=(Field left, Field right) => !left.Equals(right);

    #endregion
}

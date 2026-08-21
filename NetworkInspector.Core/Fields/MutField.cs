// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Thin-cursor mutable field wrapper for protocol parsers to build the field tree.
/// Occupies exactly 16 bytes (Packet ref 8 B + Index 2 B + FieldId 4 B + 2 B padding),
/// enabling register-return on x64 (≤ 16 B) and eliminating the hidden-pointer roundtrip
/// on every <c>Append</c> / <c>Prepend</c> / <c>InsertAfter</c> call.
/// <para>
/// The <see cref="ParseContext"/> is no longer embedded; it is threaded explicitly as an
/// <c>in</c> parameter only on the dispatch methods (<c>TryCallNextProtocol*</c>). The
/// field-building methods (Append/Prepend/InsertAfter variants) do not take a context —
/// presence recording happens eagerly via <see cref="ParseContext"/> in <c>Parse</c>, never
/// during field construction.
/// The same cursor type serves both eager parsing (in <c>Parse</c>, with a context for
/// dispatch) and lazy population (in a <see cref="LazyPopulator"/>, where no context is
/// available, so dispatch and index mutation are structurally impossible).
/// </para>
/// <para>This is a ref struct — cannot be stored in collections.</para>
/// </summary>
public readonly ref struct MutField
{
    /// <summary>The owning packet.</summary>
    public readonly Packet Packet { get; }
    internal readonly ushort StorageIndex { get; }
    /// <summary>Cached field identifier — fits in the 4-byte slot between StorageIndex (2 B) and alignment padding.</summary>
    public readonly FieldId FieldId { get; }

    /// <summary>Creates a mutable field handle with a known field ID (avoids array lookup on the hot path).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MutField(Packet packet, ushort index, FieldId fieldId)
    {
        Packet = packet;
        StorageIndex = index;
        FieldId = fieldId;
    }

    #region Read Accessors

    /// <summary>Whether this field reference points to a valid packet and index.</summary>
    public readonly bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Packet is not null && StorageIndex != FieldBody.NullIndex;
    }

    /// <summary>Gets the field's metadata from the stack registry.</summary>
    public readonly FieldInfo? FieldInfo => Packet.Stack.GetField(FieldId);

    /// <summary>The field's value.</summary>
    public readonly FieldValue Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Packet.GetFieldRef(StorageIndex).Value;
    }

    /// <summary>
    /// Optional custom display text (check <see cref="LazyString.IsNull"/> for absence).
    /// Accesses <see cref="FieldBody.CustomText"/> through a mutable ref for in-place caching.
    /// </summary>
    public readonly LazyString CustomText
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Packet.GetFieldRef(StorageIndex).CustomText;
    }

    /// <summary>Whether this is the root field (index 0).</summary>
    public readonly bool IsRoot
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => StorageIndex == 0;
    }

    /// <summary>
    /// Whether this field has child fields.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy children are populated first.
    /// When <see langword="false"/>, an unmaterialized lazy container reports no children.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy children before checking.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool HasChildren(bool materialize)
    {
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ushort ChildCount(bool materialize)
    {
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

    /// <summary>Whether this field is lazy (has deferred children that need materialization).
    /// Internal so the lazy mechanism stays transparent to external consumers.</summary>
    internal readonly bool NeedsLazyMaterialization
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Packet.GetFieldRef(StorageIndex).NeedsMaterialization;
    }

    #endregion


    #region Value Setters

    /// <summary>Sets the field value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void SetValue(FieldValue value) => Packet.GetFieldRef(StorageIndex).Value = value;

    /// <summary>Sets custom display text.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void SetCustomText(LazyString text) => Packet.GetFieldRef(StorageIndex).SetCustomText(text);

    /// <summary>Clears custom display text.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void ClearCustomText() => Packet.GetFieldRef(StorageIndex).ClearCustomText();

    /// <summary>Appends to custom display text.</summary>
    public readonly void AppendCustomText(LazyString suffix) => Packet.GetFieldRef(StorageIndex).AppendCustomText(suffix);

    #endregion

    #region Packet Info

    /// <summary>Sets the packet info/summary string.</summary>
    public readonly void SetPacketInfo(LazyString info) => Packet.SetInfo(info);

    /// <summary>Appends to the packet info/summary string.</summary>
    public readonly void AppendToPacketInfo(LazyString suffix) => Packet.AppendToInfo(suffix);

    /// <summary>Prepends to the packet info/summary string.</summary>
    public readonly void PrependToPacketInfo(LazyString prefix) => Packet.PrependToInfo(prefix);

    /// <summary>The current packet info/summary string.</summary>
    public readonly string PacketInfo => Packet.Info;

    #endregion

    #region Append/Prepend/Insert

    /// <summary>Appends a child field. Throws <see cref="Errors.FieldAppendException"/> on failure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField Append(FieldId fieldId, FieldValue value)
        => new(Packet, Packet.AppendChild(StorageIndex, fieldId, value), fieldId);

    /// <summary>Appends a child field with custom display text. Throws <see cref="Errors.FieldAppendException"/> on failure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField AppendWithCustomText(FieldId fieldId, FieldValue value, LazyString customText)
        => new(Packet, Packet.AppendChildWithCustomText(StorageIndex, fieldId, value, customText), fieldId);

    /// <summary>Prepends a child field (inserts before all existing children). Throws <see cref="Errors.FieldAppendException"/> on failure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField Prepend(FieldId fieldId, FieldValue value)
        => new(Packet, Packet.PrependChild(StorageIndex, fieldId, value), fieldId);

    /// <summary>Prepends a child field with custom display text. Throws <see cref="Errors.FieldAppendException"/> on failure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField PrependWithCustomText(FieldId fieldId, FieldValue value, LazyString customText)
        => new(Packet, Packet.PrependChildWithCustomText(StorageIndex, fieldId, value, customText), fieldId);

    /// <summary>Inserts a field after the current field (as sibling). Throws <see cref="Errors.FieldAppendException"/> on failure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField InsertAfter(FieldId fieldId, FieldValue value)
        => new(Packet, Packet.InsertAfter(StorageIndex, fieldId, value), fieldId);

    /// <summary>Inserts a field after the current field (as sibling) with custom display text.
    /// Throws <see cref="Errors.FieldAppendException"/> on failure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField InsertAfterWithCustomText(FieldId fieldId, FieldValue value, LazyString customText)
        => new(Packet, Packet.InsertAfterWithCustomText(StorageIndex, fieldId, value, customText), fieldId);

    /// <summary>Creates a MutField for a child at the given storage index (internal implementation detail).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly MutField ChildMut(ushort index) => new(Packet, index, Packet.GetFieldRef(index).FieldId);

    #endregion

    #region Lazy Field Support

    /// <summary>
    /// Appends a lazy container field whose children will be populated on first access.
    /// The <paramref name="populator"/> closure is called exactly once when children are first accessed.
    /// Throws <see cref="Errors.FieldAppendException"/> on failure.
    /// </summary>
    /// <param name="fieldId">The field ID for the container.</param>
    /// <param name="value">The container's value (typically <see cref="FieldValue.None"/>).</param>
    /// <param name="populator">A closure that creates child fields on demand.</param>
    public readonly MutField AppendLazy(
        FieldId fieldId, FieldValue value, LazyPopulator populator)
    {
        ushort newIndex = Packet.AppendChild(StorageIndex, fieldId, value);
        Packet.RegisterLazyPopulator(newIndex, populator);
        // The populator is invoked later via MaterializeLazyField without a ParseContext
        // (ref struct cannot be captured in closures) — intentionally preventing deferred index recording.
        return new MutField(Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Appends a lazy container field with custom display text.
    /// Throws <see cref="Errors.FieldAppendException"/> on failure.
    /// </summary>
    public readonly MutField AppendLazyWithCustomText(
        FieldId fieldId, FieldValue value, LazyString customText, LazyPopulator populator)
    {
        ushort newIndex = Packet.AppendChildWithCustomText(StorageIndex, fieldId, value, customText);
        Packet.RegisterLazyPopulator(newIndex, populator);
        return new MutField(Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Prepends a lazy container field whose children will be populated on first access.
    /// Throws <see cref="Errors.FieldAppendException"/> on failure.
    /// </summary>
    public readonly MutField PrependLazy(
        FieldId fieldId, FieldValue value, LazyPopulator populator)
    {
        ushort newIndex = Packet.PrependChild(StorageIndex, fieldId, value);
        Packet.RegisterLazyPopulator(newIndex, populator);
        return new MutField(Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Prepends a lazy container field with custom display text.
    /// Throws <see cref="Errors.FieldAppendException"/> on failure.
    /// </summary>
    public readonly MutField PrependLazyWithCustomText(
        FieldId fieldId, FieldValue value, LazyString customText, LazyPopulator populator)
    {
        ushort newIndex = Packet.PrependChildWithCustomText(StorageIndex, fieldId, value, customText);
        Packet.RegisterLazyPopulator(newIndex, populator);
        return new MutField(Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Inserts a lazy container field after the current field (as sibling).
    /// Throws <see cref="Errors.FieldAppendException"/> on failure.
    /// </summary>
    public readonly MutField InsertAfterLazy(
        FieldId fieldId, FieldValue value, LazyPopulator populator)
    {
        ushort newIndex = Packet.InsertAfter(StorageIndex, fieldId, value);
        Packet.RegisterLazyPopulator(newIndex, populator);
        return new MutField(Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Inserts a lazy container field after the current field (as sibling) with custom display text.
    /// Throws <see cref="Errors.FieldAppendException"/> on failure.
    /// </summary>
    public readonly MutField InsertAfterLazyWithCustomText(
        FieldId fieldId, FieldValue value, LazyString customText, LazyPopulator populator)
    {
        ushort newIndex = Packet.InsertAfterWithCustomText(StorageIndex, fieldId, value, customText);
        Packet.RegisterLazyPopulator(newIndex, populator);
        return new MutField(Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Materializes this field's lazy children if it is a lazy container that has not been populated yet.
    /// Useful for selective materialization during parsing when a downstream protocol needs to access
    /// specific fields from an upstream protocol (e.g., UDP reading IP addresses for checksum).
    /// Returns true if materialization was performed, false if already populated or not lazy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool MaterializeIfLazy() => Packet.MaterializeLazyField(StorageIndex);

    #endregion

    #region Protocol Dispatch

    /// <summary>Error when <see cref="ParseContext.Stack"/> is null (configuration fault, not a key miss).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ParseResult _MissingStack() =>
        ParseError.InternalError("ParseContext has no Stack");

    /// <summary>Error when the dispatch table id is valid but not registered on this stack.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ParseResult _MissingTable() =>
        ParseError.ProtocolTableMissing(null, "Dispatch table is not registered on this stack.");

    /// <summary>Error when the dispatch table id is invalid.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ParseResult _InvalidTableId() =>
        ParseError.ProtocolTableMissing(null, "Dispatch table id is invalid.");

    /// <summary>
    /// Dispatches to the next protocol by u64 key lookup.
    /// Returns Ok from the callee, <see cref="ParseResult.NotDispatched"/> when the table exists
    /// and the key has no protocol, or Error when the stack/table is missing or the callee errors.
    /// </summary>
    public readonly ParseResult TryCallNextProtocolU64(
        ProtocolTableId tableId, ulong key, ReadOnlyMemory<byte> data, in ParseContext context)
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

        ProtocolTable? table = stack.GetProtocolTable(tableId);
        if (table is null)
        {
            return _MissingTable();
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllU64(key);
        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForU64(tableId, key, context.SelfProtocolId));
        if (protocols.Length <= 1)
        {
            if (protocols.IsEmpty)
            {
                return ParseResult.NotDispatched;
            }

            return stack.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        return _DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, key.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Dispatches to the next protocol by string key lookup.
    /// Returns Ok from the callee, <see cref="ParseResult.NotDispatched"/> when the table exists
    /// and the key has no protocol, or Error when the stack/table is missing or the callee errors.
    /// </summary>
    public readonly ParseResult TryCallNextProtocolString(
        ProtocolTableId tableId, string key, ReadOnlyMemory<byte> data, in ParseContext context)
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

        ProtocolTable? table = stack.GetProtocolTable(tableId);
        if (table is null)
        {
            return _MissingTable();
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllString(key);
        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForString(tableId, key, context.SelfProtocolId));
        if (protocols.Length <= 1)
        {
            if (protocols.IsEmpty)
            {
                return ParseResult.NotDispatched;
            }

            return stack.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        return _DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, key);
    }

    /// <summary>
    /// Dispatches to the next protocol by bytes key lookup.
    /// Returns Ok from the callee, <see cref="ParseResult.NotDispatched"/> when the table exists
    /// and the key has no protocol, or Error when the stack/table is missing or the callee errors.
    /// </summary>
    public readonly ParseResult TryCallNextProtocolBytes(
        ProtocolTableId tableId, BytesKey key, ReadOnlyMemory<byte> data, in ParseContext context)
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

        ProtocolTable? table = stack.GetProtocolTable(tableId);
        if (table is null)
        {
            return _MissingTable();
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllBytes(key);
        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForBytes(tableId, key, context.SelfProtocolId));
        if (protocols.Length <= 1)
        {
            if (protocols.IsEmpty)
            {
                return ParseResult.NotDispatched;
            }

            return stack.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        return _DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, key.ToString() ?? string.Empty);
    }

    /// <summary>
    /// Dispatches to the next protocol by bool key lookup.
    /// Returns Ok from the callee, <see cref="ParseResult.NotDispatched"/> when the table exists
    /// and the key has no protocol, or Error when the stack/table is missing or the callee errors.
    /// </summary>
    public readonly ParseResult TryCallNextProtocolBool(
        ProtocolTableId tableId, bool key, ReadOnlyMemory<byte> data, in ParseContext context)
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

        ProtocolTable? table = stack.GetProtocolTable(tableId);
        if (table is null)
        {
            return _MissingTable();
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllBool(key);
        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForBool(tableId, key, context.SelfProtocolId));
        if (protocols.Length <= 1)
        {
            if (protocols.IsEmpty)
            {
                return ParseResult.NotDispatched;
            }

            return stack.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        return _DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, key.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Dispatches to any protocol registered in the table.
    /// Returns Ok from the callee, <see cref="ParseResult.NotDispatched"/> when the table exists
    /// and has zero protocols, or Error when the stack/table is missing or the callee errors.
    /// </summary>
    public readonly ParseResult TryCallNextProtocolAny(
        ProtocolTableId tableId, ReadOnlyMemory<byte> data, in ParseContext context)
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

        ProtocolTable? table = stack.GetProtocolTable(tableId);
        if (table is null)
        {
            return _MissingTable();
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllAny();
        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForAny(tableId, context.SelfProtocolId));
        if (protocols.Length <= 1)
        {
            if (protocols.IsEmpty)
            {
                return ParseResult.NotDispatched;
            }

            return stack.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        return _DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, "*");
    }

    /// <summary>
    /// Directly calls a specific protocol. Returns InternalError when <see cref="ParseContext.Stack"/>
    /// is null; never <see cref="ParseResult.NotDispatched"/>.
    /// </summary>
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

        ProtocolId? match = table.TryMatch(data);
        if (match is null)
        {
            return ParseResult.NotDispatched;
        }

        return stack.CallProtocol(match.Value, in this, data, in context);
    }

    #endregion

    #region Internal Dispatch Logic

    /// <summary>
    /// Dispatches to multiple protocols when more than one protocol matches a table key.
    /// Creates a <c>packet.choice</c> container field, dispatches ALL protocols as children,
    /// and returns the maximum consumed bytes.
    /// <para>Callers must pre-check for 0/1 matches (fast path) before calling this method.</para>
    /// </summary>
    private readonly ParseResult _DispatchMultipleProtocols(
        ReadOnlySpan<ProtocolId> protocols, ReadOnlyMemory<byte> data, in ParseContext context,
        string tableName, string keyDisplay)
    {
        // Multiple matches — create a packet.choice container and dispatch all alternatives.
        // The "Choice: " prefix makes the wrapper visually distinct from regular field entries.
        FieldId choiceFieldId = context.Stack!.PacketChoiceFieldId;
        // ZA.Lazy defers string concatenation to evaluation time.
        LazyString choiceLabel = ZA.Lazy("Choice: ", tableName, ": ", keyDisplay);
        MutField choiceField = AppendWithCustomText(choiceFieldId, FieldValue.None, choiceLabel);

        int maxConsumed = 0;
        for (int i = 0; i < protocols.Length; i++)
        {
            ParseResult result = context.Stack!.CallProtocol(protocols[i], in choiceField, data, in context);
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

    #region Tree Navigation

    /// <summary>Tries to get the parent field. Returns false if this is a root field.</summary>
    public readonly bool TryGetParent(out MutField parent)
    {
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
    /// <param name="firstChild">The first child when present.</param>
    /// <param name="materialize">Whether to materialize lazy children before reading the child list.</param>
    public readonly bool TryGetFirstChild(out MutField firstChild, bool materialize)
    {
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
    /// <param name="lastChild">The last child when present.</param>
    /// <param name="materialize">Whether to materialize lazy children before reading the child list.</param>
    public readonly bool TryGetLastChild(out MutField lastChild, bool materialize)
    {
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

    /// <summary>Tries to get the next sibling field. Returns false if this is the last sibling.</summary>
    public readonly bool TryGetNext(out MutField next)
    {
        ushort idx = Packet.GetFieldRef(StorageIndex).NextIndex;
        if (idx != FieldBody.NullIndex)
        {
            next = new MutField(Packet, idx, Packet.GetFieldRef(idx).FieldId);
            return true;
        }
        next = default;
        return false;
    }

    /// <summary>Tries to get the previous sibling field. Returns false if this is the first sibling.</summary>
    public readonly bool TryGetPrev(out MutField prev)
    {
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

    #region Iterators

    /// <summary>
    /// Iterates direct children as mutable cursors.
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy children are materialized first.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy children before iterating.</param>
    public readonly MutFieldChildEnumerable Children(bool materialize) => new(Packet, StorageIndex, materialize);

    /// <summary>
    /// Iterates all descendants as mutable cursors (DFS pre-order).
    /// When <paramref name="materialize"/> is <see langword="true"/>, lazy fields are materialized during traversal.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy fields during traversal.</param>
    public readonly MutFieldDescendantEnumerable Descendants(bool materialize) => new(Packet, StorageIndex, materialize);

    #endregion

    #region Conversion

    /// <summary>Converts to a read-only <see cref="Field"/> snapshot of the same storage index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Field AsField() => new(Packet, StorageIndex);

    #endregion

    #region Equality

    /// <summary>Value equality by packet identity and storage index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(MutField other) => ReferenceEquals(Packet, other.Packet) && StorageIndex == other.StorageIndex;

    #endregion
}

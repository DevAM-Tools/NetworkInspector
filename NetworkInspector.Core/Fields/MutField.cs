// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Thin-cursor mutable field wrapper for protocol parsers to build the field tree.
/// Occupies exactly 16 bytes (Packet ref 8 B + Index 2 B + FieldId 4 B + 2 B padding),
/// enabling register-return on x64 (≤ 16 B) and eliminating the hidden-pointer roundtrip
/// on every <c>Append</c> / <c>Prepend</c> / <c>InsertAfter</c> call.
/// <para>
/// The <see cref="ParseContext"/> is no longer embedded; it is threaded explicitly as an
/// <c>in</c> parameter on every method that needs it (Append variants, dispatch methods).
/// Lazy field populators (which cannot capture a ref struct) simply pass
/// <see langword="default"/> for the context — this is correct and intentional, as deferred
/// index recording is not allowed inside lazy populators.
/// </para>
/// <para>This is a ref struct — cannot be stored in collections.</para>
/// </summary>
public readonly ref struct MutField
{
    private readonly Packet _Packet;
    private readonly ushort _Index;
    /// <summary>Cached field identifier — fits in the 4-byte slot between _Index (2 B) and alignment padding.</summary>
    private readonly FieldId _FieldId;

    /// <summary>Creates a mutable field handle with a known field ID (avoids array lookup on the hot path).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MutField(Packet packet, ushort index, FieldId fieldId)
    {
        _Packet = packet;
        _Index = index;
        _FieldId = fieldId;
    }

    #region Read Accessors

    /// <summary>The storage index within the packet's field list (internal implementation detail).</summary>
    internal readonly ushort StorageIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Index;
    }

    /// <summary>The owning packet.</summary>
    public readonly Packet Packet
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Packet;
    }

    /// <summary>The field's registered identifier (cached — avoids array indirection).</summary>
    public readonly FieldId FieldId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _FieldId;
    }

    /// <summary>Gets the field's metadata from the stack registry.</summary>
    public readonly FieldInfo? FieldInfo => _Packet.Stack.GetField(FieldId);

    /// <summary>The field's value.</summary>
    public readonly FieldValue Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Packet.GetFieldRef(_Index).Value;
    }


    /// <summary>
    /// Optional custom display text (check <see cref="LazyString.IsNull"/> for absence).
    /// Accesses <see cref="FieldBody.CustomText"/> through a mutable ref for in-place caching.
    /// </summary>
    public readonly LazyString CustomText
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Packet.GetFieldRef(_Index).CustomText;
    }

    #endregion


    #region Value Setters

    /// <summary>Sets the field value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void SetValue(FieldValue value) => _Packet.GetFieldRef(_Index).Value = value;

    /// <summary>Sets custom display text.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void SetCustomText(LazyString text) => _Packet.GetFieldRef(_Index).SetCustomText(text);

    /// <summary>Clears custom display text.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void ClearCustomText() => _Packet.GetFieldRef(_Index).ClearCustomText();

    /// <summary>Appends to custom display text.</summary>
    public readonly void AppendCustomText(LazyString suffix) => _Packet.GetFieldRef(_Index).AppendCustomText(suffix);

    #endregion

    #region Packet Info

    /// <summary>Sets the packet info/summary string.</summary>
    public readonly void SetPacketInfo(LazyString info) => _Packet.SetInfo(info);

    /// <summary>Appends to the packet info/summary string.</summary>
    public readonly void AppendToPacketInfo(LazyString suffix) => _Packet.AppendToInfo(suffix);

    /// <summary>Prepends to the packet info/summary string.</summary>
    public readonly void PrependToPacketInfo(LazyString prefix) => _Packet.PrependToInfo(prefix);

    /// <summary>The current packet info/summary string.</summary>
    public readonly string PacketInfo => _Packet.Info;

    #endregion

    #region Append/Prepend/Insert

    /// <summary>Appends a child field. Throws <see cref="Errors.FieldAppendException"/> on failure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField Append(FieldId fieldId, FieldValue value, in ParseContext context)
    {
        context.TryRecordValue(fieldId, value.Data);
        return new(_Packet, _Packet.AppendChild(_Index, fieldId, value), fieldId);
    }

    /// <summary>Appends a child field with custom display text. Throws <see cref="Errors.FieldAppendException"/> on failure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField AppendWithCustomText(FieldId fieldId, FieldValue value, LazyString customText, in ParseContext context)
    {
        context.TryRecordValue(fieldId, value.Data);
        return new(_Packet, _Packet.AppendChildWithCustomText(_Index, fieldId, value, customText), fieldId);
    }

    /// <summary>Prepends a child field (inserts before all existing children). Throws <see cref="Errors.FieldAppendException"/> on failure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField Prepend(FieldId fieldId, FieldValue value, in ParseContext context)
    {
        context.TryRecordValue(fieldId, value.Data);
        return new(_Packet, _Packet.PrependChild(_Index, fieldId, value), fieldId);
    }

    /// <summary>Prepends a child field with custom display text. Throws <see cref="Errors.FieldAppendException"/> on failure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField PrependWithCustomText(FieldId fieldId, FieldValue value, LazyString customText, in ParseContext context)
    {
        context.TryRecordValue(fieldId, value.Data);
        return new(_Packet, _Packet.PrependChildWithCustomText(_Index, fieldId, value, customText), fieldId);
    }

    /// <summary>Inserts a field after the current field (as sibling). Throws <see cref="Errors.FieldAppendException"/> on failure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField InsertAfter(FieldId fieldId, FieldValue value, in ParseContext context)
    {
        context.TryRecordValue(fieldId, value.Data);
        return new(_Packet, _Packet.InsertAfter(_Index, fieldId, value), fieldId);
    }

    /// <summary>Inserts a field after the current field (as sibling) with custom display text.
    /// Throws <see cref="Errors.FieldAppendException"/> on failure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly MutField InsertAfterWithCustomText(FieldId fieldId, FieldValue value, LazyString customText, in ParseContext context)
    {
        context.TryRecordValue(fieldId, value.Data);
        return new(_Packet, _Packet.InsertAfterWithCustomText(_Index, fieldId, value, customText), fieldId);
    }

    /// <summary>Creates a MutField for a child at the given storage index (internal implementation detail).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly MutField ChildMut(ushort index) => new(_Packet, index, _Packet.GetFieldRef(index).FieldId);

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
        ushort newIndex = _Packet.AppendChild(_Index, fieldId, value);
        _Packet.RegisterLazyPopulator(newIndex, populator);
        // The populator is invoked later via MaterializeLazyField without a ParseContext
        // (ref struct cannot be captured in closures) — intentionally preventing deferred index recording.
        return new MutField(_Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Appends a lazy container field with custom display text.
    /// Throws <see cref="Errors.FieldAppendException"/> on failure.
    /// </summary>
    public readonly MutField AppendLazyWithCustomText(
        FieldId fieldId, FieldValue value, LazyString customText, LazyPopulator populator)
    {
        ushort newIndex = _Packet.AppendChildWithCustomText(_Index, fieldId, value, customText);
        _Packet.RegisterLazyPopulator(newIndex, populator);
        return new MutField(_Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Prepends a lazy container field whose children will be populated on first access.
    /// Throws <see cref="Errors.FieldAppendException"/> on failure.
    /// </summary>
    public readonly MutField PrependLazy(
        FieldId fieldId, FieldValue value, LazyPopulator populator)
    {
        ushort newIndex = _Packet.PrependChild(_Index, fieldId, value);
        _Packet.RegisterLazyPopulator(newIndex, populator);
        return new MutField(_Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Prepends a lazy container field with custom display text.
    /// Throws <see cref="Errors.FieldAppendException"/> on failure.
    /// </summary>
    public readonly MutField PrependLazyWithCustomText(
        FieldId fieldId, FieldValue value, LazyString customText, LazyPopulator populator)
    {
        ushort newIndex = _Packet.PrependChildWithCustomText(_Index, fieldId, value, customText);
        _Packet.RegisterLazyPopulator(newIndex, populator);
        return new MutField(_Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Inserts a lazy container field after the current field (as sibling).
    /// Throws <see cref="Errors.FieldAppendException"/> on failure.
    /// </summary>
    public readonly MutField InsertAfterLazy(
        FieldId fieldId, FieldValue value, LazyPopulator populator)
    {
        ushort newIndex = _Packet.InsertAfter(_Index, fieldId, value);
        _Packet.RegisterLazyPopulator(newIndex, populator);
        return new MutField(_Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Inserts a lazy container field after the current field (as sibling) with custom display text.
    /// Throws <see cref="Errors.FieldAppendException"/> on failure.
    /// </summary>
    public readonly MutField InsertAfterLazyWithCustomText(
        FieldId fieldId, FieldValue value, LazyString customText, LazyPopulator populator)
    {
        ushort newIndex = _Packet.InsertAfterWithCustomText(_Index, fieldId, value, customText);
        _Packet.RegisterLazyPopulator(newIndex, populator);
        return new MutField(_Packet, newIndex, fieldId);
    }

    /// <summary>
    /// Materializes this field's lazy children if it is a lazy container that has not been populated yet.
    /// Useful for selective materialization during parsing when a downstream protocol needs to access
    /// specific fields from an upstream protocol (e.g., UDP reading IP addresses for checksum).
    /// Returns true if materialization was performed, false if already populated or not lazy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool MaterializeIfLazy() => _Packet.MaterializeLazyField(_Index);

    #endregion

    #region Protocol Dispatch

    /// <summary>Dispatches to next protocol by u64 key lookup.</summary>
    public readonly ParseResult TryCallNextProtocolU64(
        ProtocolTableId tableId, ulong key, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        Stack? stack = context.Stack;
        ProtocolTable? table = stack?.GetProtocolTable(tableId);
        if (table is null)
        {
            return 0;
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllU64(key);
        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForU64(tableId, key, context.SelfProtocolId));

        // Fast path: 0 or 1 match — no string allocation needed
        if (protocols.Length <= 1)
        {
            return protocols.IsEmpty ? 0 : stack!.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        // Slow path: multiple matches — only now allocate the key display string
        return DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, key.ToString());
    }

    /// <summary>Dispatches to next protocol by string key lookup.</summary>
    public readonly ParseResult TryCallNextProtocolString(
        ProtocolTableId tableId, string key, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        Stack? stack = context.Stack;
        ProtocolTable? table = stack?.GetProtocolTable(tableId);
        if (table is null)
        {
            return 0;
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllString(key);
        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForString(tableId, key, context.SelfProtocolId));

        // Fast path: 0 or 1 match — direct call without multi-match overhead
        if (protocols.Length <= 1)
        {
            return protocols.IsEmpty ? 0 : stack!.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        return DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, key);
    }

    /// <summary>Dispatches to next protocol by bytes key lookup.</summary>
    public readonly ParseResult TryCallNextProtocolBytes(
        ProtocolTableId tableId, BytesKey key, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        Stack? stack = context.Stack;
        ProtocolTable? table = stack?.GetProtocolTable(tableId);
        if (table is null)
        {
            return 0;
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllBytes(key);
        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForBytes(tableId, key, context.SelfProtocolId));

        // Fast path: 0 or 1 match — no string allocation needed
        if (protocols.Length <= 1)
        {
            return protocols.IsEmpty ? 0 : stack!.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        // Slow path: multiple matches — only now allocate the key display string
        return DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, key.ToString() ?? string.Empty);
    }

    /// <summary>Dispatches to next protocol by bool key lookup.</summary>
    public readonly ParseResult TryCallNextProtocolBool(
        ProtocolTableId tableId, bool key, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        Stack? stack = context.Stack;
        ProtocolTable? table = stack?.GetProtocolTable(tableId);
        if (table is null)
        {
            return 0;
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllBool(key);
        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForBool(tableId, key, context.SelfProtocolId));

        // Fast path: 0 or 1 match — no string allocation needed
        if (protocols.Length <= 1)
        {
            return protocols.IsEmpty ? 0 : stack!.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        // Slow path: multiple matches — bool.ToString() returns cached string (no allocation)
        return DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, key.ToString());
    }

    /// <summary>Dispatches to any protocol registered in the table.</summary>
    public readonly ParseResult TryCallNextProtocolAny(
        ProtocolTableId tableId, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        Stack? stack = context.Stack;
        ProtocolTable? table = stack?.GetProtocolTable(tableId);
        if (table is null)
        {
            return 0;
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllAny();
        ParseContext dispatchedContext = context.WithDispatch(DispatchContext.ForAny(tableId, context.SelfProtocolId));

        // Fast path: 0 or 1 match — direct call
        if (protocols.Length <= 1)
        {
            return protocols.IsEmpty ? 0 : stack!.CallProtocol(protocols[0], in this, data, in dispatchedContext);
        }

        return DispatchMultipleProtocols(protocols, data, in dispatchedContext, table.Name, "*");
    }

    /// <summary>Directly calls a specific protocol.</summary>
    public readonly ParseResult CallProtocol(ProtocolId protocolId, ReadOnlyMemory<byte> data, in ParseContext context)
        => context.Stack!.CallProtocol(protocolId, in this, data, in context);

    /// <summary>Dispatches using heuristic matching.</summary>
    public readonly ParseResult TryCallHeuristicProtocol(
        HeuristicProtocolTableId tableId, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        Stack? stack = context.Stack;
        HeuristicProtocolTable? table = stack?.GetHeuristicProtocolTable(tableId);
        if (table is null)
        {
            return 0;
        }

        ProtocolId? match = table.TryMatch(data);
        if (match is null)
        {
            return 0;
        }

        return stack!.CallProtocol(match.Value, in this, data, in context);
    }

    #endregion

    #region Internal Dispatch Logic

    /// <summary>
    /// Dispatches to multiple protocols when more than one protocol matches a table key.
    /// Creates a <c>packet.choice</c> container field, dispatches ALL protocols as children,
    /// and returns the maximum consumed bytes.
    /// <para>Callers must pre-check for 0/1 matches (fast path) before calling this method.</para>
    /// </summary>
    private readonly ParseResult DispatchMultipleProtocols(
        ReadOnlySpan<ProtocolId> protocols, ReadOnlyMemory<byte> data, in ParseContext context,
        string tableName, string keyDisplay)
    {
        // Multiple matches — create a packet.choice container and dispatch all alternatives.
        // The "Choice: " prefix makes the wrapper visually distinct from regular field entries.
        FieldId choiceFieldId = context.Stack!.PacketChoiceFieldId;
        // ZA.Lazy defers string concatenation to evaluation time.
        LazyString choiceLabel = ZA.Lazy("Choice: ", tableName, ": ", keyDisplay);
        MutField choiceField = AppendWithCustomText(choiceFieldId, FieldValue.None, choiceLabel, in context);

        int maxConsumed = 0;
        for (int i = 0; i < protocols.Length; i++)
        {
            ParseResult result = context.Stack!.CallProtocol(protocols[i], in choiceField, data, in context);
            if (result.TryGetValue(out int consumed))
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

    #region Read-only Navigation

    /// <summary>Converts to a read-only <see cref="Field"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Field AsField() => new(_Packet, _Index);

    /// <summary>
    /// Iterates direct children (read-only).
    /// When <paramref name="materialize"/> is true (default), lazy children are materialized first.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy children before iterating.</param>
    public readonly FieldChildEnumerable Children(bool materialize = true) => new(_Packet, _Index, materialize);

    /// <summary>
    /// Iterates all descendants (read-only, DFS pre-order).
    /// When <paramref name="materialize"/> is true (default), lazy fields are materialized during traversal.
    /// </summary>
    /// <param name="materialize">Whether to materialize lazy fields during traversal.</param>
    public readonly FieldDescendantEnumerable Descendants(bool materialize = true) => new(_Packet, _Index, materialize);
    #endregion
}

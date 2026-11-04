// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Which produced fields an observer receives.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="None"/> is the default. It accepts nothing and does not expand a deferred group.
/// <see cref="All"/> is explicit and expands every deferred group.
/// Build a watch once and reuse it. <see cref="Only(IStack, FieldId)"/> does not run per frame.
/// </para>
/// <para>
/// <see cref="SpanLimit"/> is 16. It is a constant on this type, not a setting. A source change
/// is what moves it. One id is one integer. Two through 16 ids are copied once and scanned.
/// More than 16 ids build a bitset whose word count is <c>(stack.FieldCount + 63) / 64</c>, so
/// the allocation is bounded by the stack the caller already built, not by a raw field id.
/// </para>
/// <para>
/// <b>Why three shapes:</b> <see cref="Accepts"/> runs on the parse path for every produced field, so the common cases avoid any
/// lookup structure. One id is a single integer compare. A handful of ids is a linear scan over a small contiguous array (no hashing,
/// no extra indirection). Only a large set pays for a bitset, which makes the test one shift and one mask. The value cache
/// uses the same "scan up to 16, otherwise bitset" cut-off for its recorded field ids. Watches are built once and reused
/// because the factories allocate (the id copy and the bitset); testing a field against a watch allocates nothing.
/// </para>
/// <para>
/// <b>Deferred groups:</b> a watch also decides whether a deferred (lazily populated) container is expanded during the parse
/// (internal <c>Expands</c>). Expanding costs parse time, so only a watch that can actually receive a child of the container
/// triggers it; <see cref="None"/> never does and <see cref="All"/> always does.
/// </para>
/// <para>
/// <b>Trust boundary:</b> field ids are validated against the issuing stack when the watch is built (callers pass ids from
/// their own code or from user input such as a filter expression). <see cref="Accepts"/> still bounds-checks the bitset word,
/// because a watch tested with an id from a stack with more fields must answer "no" instead of reading out of range. A watch
/// is only meaningful for the stack it was built from; the same ids on another stack name other fields.
/// </para>
/// <para><b>Thread-safety:</b> immutable after construction. Safe to share across threads.</para>
/// </remarks>
public readonly struct FieldWatch : IEquatable<FieldWatch>
{
    #region Constants

    /// <summary>
    /// Ids at or below this count are scanned as a span. Above it, membership is a bitset.
    /// Constant, not a setting.
    /// </summary>
    public const int SpanLimit = 16;

    /// <summary>Representation of a watch. Chosen once by the factory so <see cref="Accepts"/> only switches on it.</summary>
    private enum WatchKind : byte
    {
        None = 0,
        All = 1,
        One = 2,
        Span = 3,
        Bitset = 4,
    }

    #endregion

    #region Fields

    // Only the members that belong to _Kind are populated; the default struct is the None watch (kind 0, all null).
    // _One: the id of a One watch. _Ids: the id copy for Span and Bitset (Bitset keeps it for Equals and Expands).
    // _Bits: the membership bitset of a Bitset watch, one bit per field id. The arrays are never mutated after
    // construction, which is what makes sharing a watch across threads safe.
    private readonly WatchKind _Kind;
    private readonly int _One;
    private readonly FieldId[]? _Ids;
    private readonly ulong[]? _Bits;

    // The ids the caller asked for, before ForExtraction widened them to the owning protocols. Used only to describe a parse
    // plan ("2 fields"); null for None, All, and watches made by Only. Never mutated after construction, so sharing a watch
    // across threads stays safe. Not part of equality: the accepted ids decide, this list is descriptive.
    private readonly FieldId[]? _Requested;

    #endregion

    #region Construction

    private FieldWatch(WatchKind kind, int one, FieldId[]? ids, ulong[]? bits, FieldId[]? requested = null)
    {
        _Kind = kind;
        _One = one;
        _Ids = ids;
        _Bits = bits;
        _Requested = requested;
    }

    /// <summary>Accepts nothing. Does not expand a deferred group. This is <c>default(FieldWatch)</c>.</summary>
    public static FieldWatch None => default;

    /// <summary>Accepts every produced field and expands every deferred group. Expanding every group is the expensive choice.</summary>
    public static FieldWatch All { get; } = new(WatchKind.All, 0, null, null);

    /// <summary>Accepts one field the stack assigned.</summary>
    /// <param name="stack">Stack that assigned <paramref name="fieldId"/>. Null is rejected.</param>
    /// <param name="fieldId">A field id in <c>0 .. stack.FieldCount - 1</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stack"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldId"/> is invalid or was not assigned by <paramref name="stack"/>.</exception>
    public static FieldWatch Only(IStack stack, FieldId fieldId)
    {
        ArgumentNullException.ThrowIfNull(stack);
        _Reject(stack, fieldId);
        return new FieldWatch(WatchKind.One, fieldId.Value, null, null);
    }

    /// <summary>Accepts each listed field. An empty list is rejected.</summary>
    /// <param name="stack">Stack that assigned every id. Null is rejected.</param>
    /// <param name="fieldIds">One or more field ids. Copied once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stack"/> is null.</exception>
    /// <exception cref="ArgumentException">The list is empty, or any id is invalid or was not assigned by <paramref name="stack"/>.</exception>
    public static FieldWatch Only(IStack stack, params ReadOnlySpan<FieldId> fieldIds)
    {
        ArgumentNullException.ThrowIfNull(stack);

        // An empty list is rejected rather than treated as None: an empty watch would silently deliver nothing, which
        // hides a caller bug. Callers who want "nothing" use FieldWatch.None.
        if (fieldIds.Length == 0)
        {
            throw new ArgumentException("A field watch needs at least one field id.", nameof(fieldIds));
        }

        // One id takes the allocation-free One shape.
        if (fieldIds.Length == 1)
        {
            return Only(stack, fieldIds[0]);
        }

        // Validate every id against the stack while copying. The input is a span the caller may reuse or mutate after
        // this call, so the watch keeps its own copy; the watch must never alias caller memory.
        FieldId[] copy = new FieldId[fieldIds.Length];
        for (int i = 0; i < fieldIds.Length; i++)
        {
            _Reject(stack, fieldIds[i]);
            copy[i] = fieldIds[i];
        }

        // Small set: scan the copy. Duplicate ids are harmless (they only repeat a comparison).
        if (fieldIds.Length <= SpanLimit)
        {
            return new FieldWatch(WatchKind.Span, 0, copy, null);
        }

        // Large set: one bit per field id of the stack, rounded up to whole 64-bit words. The size depends on
        // stack.FieldCount (a trusted, already-built stack), never on the largest requested id, so a hostile id cannot
        // force a huge allocation: ids at or above FieldCount were rejected above.
        int words = (stack.FieldCount + 63) / 64;
        ulong[] bits = new ulong[words];
        for (int i = 0; i < copy.Length; i++)
        {
            int value = copy[i].Value;
            bits[value >> 6] |= 1UL << (value & 63);
        }

        return new FieldWatch(WatchKind.Bitset, 0, copy, bits);
    }

    /// <summary>
    /// Accepts the listed fields plus every field of their owning protocols, so every deferred container that can hold a listed
    /// field expands in a parse without field tree.
    /// <para>
    /// <b>Why:</b> <see cref="Only(IStack, ReadOnlySpan{FieldId})"/> expands a deferred container only when the container or a
    /// field of its index group is watched. A listed field inside a container outside that group would then be silently missing
    /// from an export or a filter verdict (a field-tree lookup that misses materializes every pending container of the field's
    /// protocol; without a tree nothing does that, so the watch must). Accepting all fields of the owning protocols makes the
    /// parse expand exactly those protocols' containers. Cost: more expansion and more observer calls than
    /// <see cref="Only(IStack, ReadOnlySpan{FieldId})"/> (the observer ignores the extra ids), paid once per owning protocol and
    /// never for unrelated protocols. Use this for extraction (exports, filters); use <c>Only</c> when the caller handles misses.
    /// </para>
    /// <para>Built once: allocates the id lists and, above <see cref="SpanLimit"/> ids, a bitset.</para>
    /// </summary>
    /// <param name="stack">Stack that assigned every id.</param>
    /// <param name="fieldIds">One or more field ids of <paramref name="stack"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stack"/> is null.</exception>
    /// <exception cref="ArgumentException">The list is empty, or an id was not assigned by <paramref name="stack"/>.</exception>
    public static FieldWatch ForExtraction(IStack stack, params ReadOnlySpan<FieldId> fieldIds)
    {
        ArgumentNullException.ThrowIfNull(stack);
        if (fieldIds.Length == 0)
        {
            throw new ArgumentException("A field watch needs at least one field id.", nameof(fieldIds));
        }

        // Owning protocols of the requested fields. Every id is validated against the stack first, because user input
        // (field names of an export) may be behind the ids and an id outside the stack must not index the field table.
        ReadOnlySpan<FieldInfo> fields = stack.Fields.Span;
        HashSet<ProtocolId> owners = [];
        FieldId[] requested = new FieldId[fieldIds.Length];
        for (int i = 0; i < fieldIds.Length; i++)
        {
            _Reject(stack, fieldIds[i]);
            requested[i] = fieldIds[i];
            _ = owners.Add(fields[fieldIds[i].Value].ProtocolId);
        }

        // Every field those protocols registered, containers included.
        List<FieldId> ids = new(fieldIds.Length);
        for (int raw = 0; raw < fields.Length; raw++)
        {
            if (owners.Contains(fields[raw].ProtocolId))
            {
                ids.Add(fields[raw].Id);
            }
        }

        // The widened list goes through Only for the shape choice (One, Span, Bitset); the requested list is then attached.
        FieldWatch widened = Only(stack, [.. ids]);
        return new FieldWatch(widened._Kind, widened._One, widened._Ids, widened._Bits, requested);
    }

    #endregion

    #region Properties

    /// <summary>Whether this watch accepts nothing.</summary>
    public bool IsNone => _Kind == WatchKind.None;

    /// <summary>Whether this watch accepts every produced field.</summary>
    public bool IsAll => _Kind == WatchKind.All;

    /// <summary>
    /// Adds the ids the caller asked for (before <see cref="ForExtraction"/> widened them) to <paramref name="into"/>, so a parse
    /// plan can count distinct fields over several watches ("2 fields"); it does not change what the watch accepts. Adds nothing
    /// for <see cref="None"/> and <see cref="All"/>; <c>ParsePlan.TryCreate</c> checks <see cref="IsAll"/> first.
    /// </summary>
    /// <param name="into">The set that collects the distinct field ids of a parse plan.</param>
    internal void CollectRequested(HashSet<int> into)
    {
        switch (_Kind)
        {
            case WatchKind.One:
                _ = into.Add(_One);
                break;
            case WatchKind.Span:
            case WatchKind.Bitset:
                foreach (FieldId id in _Requested ?? _Ids!) // Invariant: Span and Bitset keep their id copy.
                {
                    _ = into.Add(id.Value);
                }

                break;
            default:
                break;
        }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Whether a produced field with <paramref name="fieldId"/> is delivered. Never throws and never allocates; an id outside
    /// the watched set (including an invalid id) simply answers <see langword="false"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Accepts(FieldId fieldId)
    {
        // Parse-path check, called once per produced field per observer: inlined, switch on the pre-chosen shape,
        // no virtual call, no delegate, no allocation.
        int value = fieldId.Value;
        switch (_Kind)
        {
            case WatchKind.All:
                return true;
            case WatchKind.One:
                return value == _One;
            case WatchKind.Span:
                FieldId[] ids = _Ids!; // Invariant: the factory sets _Ids for kind Span (at most SpanLimit entries).
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i].Value == value)
                    {
                        return true;
                    }
                }

                return false;
            case WatchKind.Bitset:
                ulong[] bits = _Bits!; // Invariant: the factory sets _Bits for kind Bitset.
                int word = value >> 6;

                // Single unsigned compare rejects both a negative word (FieldId.Invalid is -1, and -1 >> 6 stays negative)
                // and an id beyond the stack's field count at the time of building (more words than were allocated).
                if ((uint)word >= (uint)bits.Length)
                {
                    return false;
                }

                // Bit position within the word is the low six bits of the id (64 bits per word).
                return (bits[word] & (1UL << (value & 63))) != 0;
            default:
                return false;
        }
    }

    /// <summary>
    /// Two watches are equal when they have the same shape and the same ids in the same order. A watch built from
    /// the ids (a, b) therefore differs from one built from (b, a) although they accept the same fields. The bitset is
    /// derived from the ids and is not compared.
    /// </summary>
    public bool Equals(FieldWatch other)
    {
        if (_Kind != other._Kind || _One != other._One)
        {
            return false;
        }

        FieldId[]? ids = _Ids;
        FieldId[]? otherIds = other._Ids;
        if (ids is null || otherIds is null)
        {
            return ids is null && otherIds is null;
        }

        if (ids.Length != otherIds.Length)
        {
            return false;
        }

        for (int i = 0; i < ids.Length; i++)
        {
            if (ids[i].Value != otherIds[i].Value)
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is FieldWatch other && Equals(other);

    /// <summary>Hash code consistent with <see cref="Equals(FieldWatch)"/>: shape, single id, and the ordered id list.</summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add((int)_Kind);
        hash.Add(_One);
        FieldId[]? ids = _Ids;
        if (ids is not null)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                hash.Add(ids[i].Value);
            }
        }

        return hash.ToHashCode();
    }

    /// <summary>Value equality.</summary>
    public static bool operator ==(FieldWatch left, FieldWatch right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(FieldWatch left, FieldWatch right) => !left.Equals(right);

    #endregion

    #region Helpers

    /// <summary>
    /// Whether a deferred container should expand because this watch names it or a field in its index group.
    /// </summary>
    /// <remarks>
    /// Called by the packet when a deferred container is registered during a parse, once per container per observer.
    /// Expanding runs the container's populator immediately so a watched child is reported during this parse instead of on a
    /// later walk (and, on a skip parse where no tree is kept, so the child is not lost). A watched field expands the container
    /// only when both belong to the same index group: for example naming <c>tcp.seq</c> expands the TCP group's container,
    /// while naming <c>tcp.seq_raw</c>, which is not in that index group, does not.
    /// This keeps the populator cost away from parses whose watch does not name the container or a member of its group.
    /// </remarks>
    /// <param name="stack">The stack that owns the field ids; used for the index-group lookup.</param>
    /// <param name="containerFieldId">Field id of the deferred container being registered.</param>
    internal bool Expands(Stack stack, FieldId containerFieldId)
    {
        // The two constant shapes decide without touching the stack.
        if (_Kind == WatchKind.All)
        {
            return true;
        }

        if (_Kind == WatchKind.None)
        {
            return false;
        }

        // The container itself is watched.
        if (Accepts(containerFieldId))
        {
            return true;
        }

        // Otherwise expand only if a watched field shares the container's index group. A container without a group
        // cannot hold a watched child that is discoverable this way.
        IndexGroupId group = stack.GetFieldIndexGroup(containerFieldId);
        if (!group.IsValid)
        {
            return false;
        }

        if (_Kind == WatchKind.One)
        {
            return stack.GetFieldIndexGroup(new FieldId(_One)) == group;
        }

        FieldId[] ids = _Ids!; // Invariant: the only kinds left here are Span and Bitset, and both keep their id copy.
        for (int i = 0; i < ids.Length; i++)
        {
            if (stack.GetFieldIndexGroup(ids[i]) == group)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="fieldId"/> is a valid id that <paramref name="stack"/> assigned.</summary>
    /// <exception cref="ArgumentException">The id is the invalid sentinel or is not below <c>stack.FieldCount</c>.</exception>
    private static void _Reject(IStack stack, FieldId fieldId)
    {
        // Ids are issued densely from 0, so "assigned by this stack" is exactly "valid and below FieldCount". This also
        // guarantees the bitset index computed later stays inside the stack-sized allocation.
        if (!fieldId.IsValid || fieldId.Value >= stack.FieldCount)
        {
            throw new ArgumentException(
                "Field id is not assigned by this stack.",
                nameof(fieldId));
        }
    }

    #endregion
}

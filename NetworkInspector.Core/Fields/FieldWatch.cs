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

    private readonly WatchKind _Kind;
    private readonly int _One;
    private readonly FieldId[]? _Ids;
    private readonly ulong[]? _Bits;

    #endregion

    #region Construction

    private FieldWatch(WatchKind kind, int one, FieldId[]? ids, ulong[]? bits)
    {
        _Kind = kind;
        _One = one;
        _Ids = ids;
        _Bits = bits;
    }

    /// <summary>Accepts nothing. Does not expand a deferred group.</summary>
    public static FieldWatch None => default;

    /// <summary>Accepts every produced field and expands every deferred group.</summary>
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
        if (fieldIds.Length == 0)
        {
            throw new ArgumentException("A field watch needs at least one field id.", nameof(fieldIds));
        }

        if (fieldIds.Length == 1)
        {
            return Only(stack, fieldIds[0]);
        }

        FieldId[] copy = new FieldId[fieldIds.Length];
        for (int i = 0; i < fieldIds.Length; i++)
        {
            _Reject(stack, fieldIds[i]);
            copy[i] = fieldIds[i];
        }

        if (fieldIds.Length <= SpanLimit)
        {
            return new FieldWatch(WatchKind.Span, 0, copy, null);
        }

        int words = (stack.FieldCount + 63) / 64;
        ulong[] bits = new ulong[words];
        for (int i = 0; i < copy.Length; i++)
        {
            int value = copy[i].Value;
            bits[value >> 6] |= 1UL << (value & 63);
        }

        return new FieldWatch(WatchKind.Bitset, 0, copy, bits);
    }

    #endregion

    #region Properties

    /// <summary>Whether this watch accepts nothing.</summary>
    public bool IsNone => _Kind == WatchKind.None;

    /// <summary>Whether this watch accepts every produced field.</summary>
    public bool IsAll => _Kind == WatchKind.All;

    #endregion

    #region Public API

    /// <summary>Whether a produced field with <paramref name="fieldId"/> is delivered.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Accepts(FieldId fieldId)
    {
        int value = fieldId.Value;
        switch (_Kind)
        {
            case WatchKind.All:
                return true;
            case WatchKind.One:
                return value == _One;
            case WatchKind.Span:
                FieldId[] ids = _Ids!;
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i].Value == value)
                    {
                        return true;
                    }
                }

                return false;
            case WatchKind.Bitset:
                ulong[] bits = _Bits!;
                int word = value >> 6;
                if ((uint)word >= (uint)bits.Length)
                {
                    return false;
                }

                return (bits[word] & (1UL << (value & 63))) != 0;
            default:
                return false;
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
    internal bool Expands(Stack stack, FieldId containerFieldId)
    {
        if (_Kind == WatchKind.All)
        {
            return true;
        }

        if (_Kind == WatchKind.None)
        {
            return false;
        }

        if (Accepts(containerFieldId))
        {
            return true;
        }

        IndexGroupId group = stack.GetFieldIndexGroup(containerFieldId);
        if (!group.IsValid)
        {
            return false;
        }

        if (_Kind == WatchKind.One)
        {
            return stack.GetFieldIndexGroup(new FieldId(_One)) == group;
        }

        FieldId[] ids = _Ids!;
        for (int i = 0; i < ids.Length; i++)
        {
            if (stack.GetFieldIndexGroup(ids[i]) == group)
            {
                return true;
            }
        }

        return false;
    }

    private static void _Reject(IStack stack, FieldId fieldId)
    {
        if (!fieldId.IsValid || fieldId.Value >= stack.FieldCount)
        {
            throw new ArgumentException(
                "Field id is not assigned by this stack.",
                nameof(fieldId));
        }
    }

    #endregion
}

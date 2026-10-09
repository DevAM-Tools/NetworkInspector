// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter;

/// <summary>
/// A tree-free filter. It latches each value test while the field is produced and answers after the parse.
/// </summary>
/// <remarks>
/// <para>
/// This is not a <see cref="Filter"/>. Resetting or matching one does not change the other.
/// <see cref="IsMatch"/> is the boolean tree for non-flank observers. When <see cref="HasFlank"/> is
/// true, reading <see cref="IsMatch"/> throws — call <see cref="TryReadMatch"/> instead. Read
/// <see cref="TryReadMatch"/> also for a stored regex failure, before the next parse: the next
/// <see cref="BeginPacket"/> clears the bits and the failure, and keeps the flank sample.
/// <see cref="ResetState"/> also clears the flank sample.
/// </para>
/// <para>
/// <see cref="AlwaysMatch"/> is immutable and safe to share. Every other instance is single-threaded.
/// Do not pass one mutable instance into two overlapping parses.
/// </para>
/// </remarks>
public sealed class FilterObserver : IFieldObserver
{

    #region Fields

    private readonly int[][] _LeafIndexes;
    private readonly Leaf[] _Leaves;
    private readonly BitNode[] _Nodes;
    private readonly int _Root;
    private readonly FlankRuntime? _Flank;
    private readonly int _WhenRoot;
    private readonly bool[] _FlankByFieldId;
    private readonly int[] _SlotByFieldId;

    private FilterError? _Failure;
    private bool _SampleTaken;
    private bool _FlankCommitted;
    private bool _FlankResult;
    private bool _IgnoreFurtherLeafTests;
    private FieldValueData _Sample;
    private PacketId _PacketId;
    private Timestamp _Timestamp;

    #endregion

    #region Construction

    private FilterObserver()
    {
        Expression = string.Empty;
        IsAlwaysMatch = true;
        Watch = FieldWatch.None;
        _LeafIndexes = [];
        _Leaves = [];
        _Nodes = [];
        _FlankByFieldId = [];
        _SlotByFieldId = [];
        _WhenRoot = -1;
    }

    internal FilterObserver(
        string expression,
        IStack stack,
        FieldWatch watch,
        int[][] leafIndexes,
        Leaf[] leaves,
        BitNode[] nodes,
        int root,
        FlankRuntime? flank,
        int whenRoot,
        bool[] flankByFieldId,
        int[] slotByFieldId)
    {
        Expression = expression;
        Stack = stack;
        Watch = watch;
        _LeafIndexes = leafIndexes;
        _Leaves = leaves;
        _Nodes = nodes;
        _Root = root;
        _Flank = flank;
        _WhenRoot = whenRoot;
        _FlankByFieldId = flankByFieldId;
        _SlotByFieldId = slotByFieldId;
        HasFlank = flank is not null;
    }

    /// <summary>The observer for an empty expression. It matches every frame and watches nothing.</summary>
    public static FilterObserver AlwaysMatch { get; } = new();

    #endregion

    #region Properties

    /// <summary>The expression this observer was compiled from. Empty for <see cref="AlwaysMatch"/>.</summary>
    public string Expression { get; }

    /// <summary>Whether this is the shared always-match observer.</summary>
    public bool IsAlwaysMatch { get; }

    /// <summary>The stack the expression was bound to. Null for <see cref="AlwaysMatch"/>.</summary>
    public IStack? Stack { get; }

    /// <summary>Fields the parse must deliver. <see cref="FieldWatch.None"/> for <see cref="AlwaysMatch"/>.</summary>
    public FieldWatch Watch { get; }

    /// <summary>Whether the verdict is a flank transition. Use <see cref="TryReadMatch"/>, not <see cref="IsMatch"/>.</summary>
    public bool HasFlank { get; }

    /// <summary>
    /// Boolean tree after the parse. Not defined for flank observers — call <see cref="TryReadMatch"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">When <see cref="HasFlank"/> is true.</exception>
    public bool IsMatch
    {
        get
        {
            if (HasFlank)
            {
                throw new InvalidOperationException(
                    "FilterObserver.IsMatch is not defined for flank filters. Call TryReadMatch.");
            }

            return _Failure is null && (IsAlwaysMatch || _Eval(_Root));
        }
    }

    #endregion

    #region Public API

    /// <summary>Clears the per-frame bits, a stored regex failure, and the flank sample.</summary>
    public void ResetState()
    {
        if (IsAlwaysMatch)
        {
            return;
        }

        _ClearFrame();
        _Flank?.Reset();
    }

    /// <inheritdoc />
    public void BeginPacket(PacketId id, Timestamp timestamp)
    {
        if (IsAlwaysMatch)
        {
            return;
        }

        // A frame the caller did not read still has to update flank state. Commit it
        // before the per-frame bits and the sample slot are cleared.
        _CommitFlank();
        _PacketId = id;
        _Timestamp = timestamp;
        _ClearFrame();
    }

    /// <inheritdoc />
    public void OnField(in FieldVisit visit)
    {
        if (_Failure is not null || IsAlwaysMatch)
        {
            return;
        }

        // Flank samples must still be captured even when the boolean tree is already decided.
        _CaptureFlank(visit.FieldId, visit.Value.Data);

        if (_IgnoreFurtherLeafTests)
        {
            return;
        }

        int slot = _Slot(visit.FieldId);
        if (slot < 0)
        {
            return;
        }

        int[] indexes = _LeafIndexes[slot];
        FieldValueData data = visit.Value.Data;
        bool anyNewHit = false;
        for (int i = 0; i < indexes.Length; i++)
        {
            ref Leaf leaf = ref _Leaves[indexes[i]];
            if (leaf.Hit)
            {
                continue;
            }

            if (_Test(ref leaf, data))
            {
                leaf.Hit = true;
                anyNewHit = true;
            }

            if (_Failure is not null)
            {
                return;
            }
        }

        // Hits only go false→true in a frame. Once the root is permanently decided, further
        // leaf tests cannot change the boolean verdict. Flank observers keep testing: the
        // when-gate shares the leaf table and must still latch before commit.
        if (anyNewHit && !HasFlank && _IsPermanentlyDecided(_Root))
        {
            _IgnoreFurtherLeafTests = true;
        }
    }

    /// <summary>
    /// Reads the flank verdict or a stored regex failure.
    /// For a port filter this agrees with <see cref="IsMatch"/>.
    /// Call it before the next parse.
    /// </summary>
    public bool TryReadMatch(out bool matched, out FilterError? failure)
    {
        if (IsAlwaysMatch)
        {
            matched = true;
            failure = null;
            return true;
        }

        if (_Failure is FilterError stored)
        {
            matched = false;
            failure = stored;
            return false;
        }

        if (HasFlank)
        {
            _CommitFlank();
            matched = _FlankResult;
            failure = null;
            return true;
        }

        matched = _Eval(_Root);
        failure = null;
        return true;
    }

    #endregion

    #region Helpers

    private void _ClearFrame()
    {
        _Failure = null;
        _SampleTaken = false;
        _FlankCommitted = false;
        _IgnoreFurtherLeafTests = false;
        _Sample = default;
        for (int i = 0; i < _Leaves.Length; i++)
        {
            _Leaves[i].Hit = false;
        }
    }

    private void _CaptureFlank(FieldId fieldId, in FieldValueData data)
    {
        if (_Flank is null || _SampleTaken)
        {
            return;
        }

        int raw = fieldId.Value;
        if ((uint)raw >= (uint)_FlankByFieldId.Length || !_FlankByFieldId[raw])
        {
            return;
        }

        if (!_Flank.Accessor.TryTransform(data, out FieldValueData sample))
        {
            return;
        }

        _Sample = sample;
        _SampleTaken = true;
    }

    private void _CommitFlank()
    {
        if (_FlankCommitted || _Flank is null)
        {
            return;
        }

        _FlankCommitted = true;
        if (!_SampleTaken)
        {
            _FlankResult = false;
            return;
        }

        if (_WhenRoot >= 0 && !_Eval(_WhenRoot))
        {
            _FlankResult = false;
            return;
        }

        _FlankResult = _Flank.Advance(_Sample, _Timestamp.AsNanos, _PacketId.Value);
    }

    private bool _Test(ref Leaf leaf, in FieldValueData data)
    {
        FieldValueData input = data;
        if (leaf.Accessor is not null && !leaf.Accessor.TryTransform(data, out input))
        {
            return false;
        }

        try
        {
            switch (leaf.Kind)
            {
                case LeafKind.Compare:
                case LeafKind.Transform:
                    return FilterCompare.Apply(input, leaf.Op, leaf.Literal);
                case LeafKind.Set:
                    SetPredicate set = new(leaf.Set!);
                    return set.Test(input);
                case LeafKind.Range:
                    RangePredicate range = new(leaf.Literal, leaf.LiteralHigh);
                    return range.Test(input);
                case LeafKind.Presence:
                    return true;
                case LeafKind.Contains:
                    ContainsPredicate contains = new(leaf.Needle!);
                    return contains.Test(input);
                case LeafKind.Matches:
                    MatchesPredicate matches = new(leaf.Regex!);
                    return matches.Test(input);
                default:
                    return false;
            }
        }
        catch (RegexMatchTimeoutException ex)
        {
            _Failure = FilterError.Runtime(
                string.Create(CultureInfo.InvariantCulture, $"Regular expression timed out: {ex.Message}"));
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int _Slot(FieldId fieldId)
    {
        int raw = fieldId.Value;
        return (uint)raw < (uint)_SlotByFieldId.Length
            ? _SlotByFieldId[raw]
            : -1;
    }

    private bool _Eval(int index)
    {
        ref BitNode node = ref _Nodes[index];
        return node.Op switch
        {
            BitOp.Leaf => _Leaves[node.LeafIndex].Hit,
            BitOp.Not => !_Eval(node.Left),
            BitOp.And => _Eval(node.Left) && _Eval(node.Right),
            BitOp.Or => _Eval(node.Left) || _Eval(node.Right),
            _ => node.LeafIndex != 0,
        };
    }

    /// <summary>
    /// Whether further leaf hits cannot change the root verdict.
    /// Hits only rise during a frame, so a permanently true OR/AND root stays true, and a
    /// permanently false AND (for example via <c>!hit</c>) stays false.
    /// </summary>
    private bool _IsPermanentlyDecided(int index) =>
        _IsPermanentlyTrue(index) || _IsPermanentlyFalse(index);

    private bool _IsPermanentlyTrue(int index)
    {
        ref BitNode node = ref _Nodes[index];
        return node.Op switch
        {
            BitOp.Leaf => _Leaves[node.LeafIndex].Hit,
            BitOp.Not => _IsPermanentlyFalse(node.Left),
            BitOp.And => _IsPermanentlyTrue(node.Left) && _IsPermanentlyTrue(node.Right),
            BitOp.Or => _IsPermanentlyTrue(node.Left) || _IsPermanentlyTrue(node.Right),
            BitOp.Const => node.LeafIndex != 0,
            _ => false,
        };
    }

    private bool _IsPermanentlyFalse(int index)
    {
        ref BitNode node = ref _Nodes[index];
        return node.Op switch
        {
            // A miss is not permanent — a later occurrence can still hit.
            BitOp.Leaf => false,
            BitOp.Not => _IsPermanentlyTrue(node.Left),
            BitOp.And => _IsPermanentlyFalse(node.Left) || _IsPermanentlyFalse(node.Right),
            BitOp.Or => _IsPermanentlyFalse(node.Left) && _IsPermanentlyFalse(node.Right),
            BitOp.Const => node.LeafIndex == 0,
            _ => false,
        };
    }

    #endregion
}

/// <summary>What a latched leaf tests.</summary>
internal enum LeafKind : byte
{
    /// <summary>A comparison against a literal.</summary>
    Compare = 0,

    /// <summary>Membership in a set.</summary>
    Set = 1,

    /// <summary>An inclusive range.</summary>
    Range = 2,

    /// <summary>The field was produced.</summary>
    Presence = 3,

    /// <summary>An ordinal substring.</summary>
    Contains = 4,

    /// <summary>A regular expression.</summary>
    Matches = 5,

    /// <summary>Slice or length, then a comparison.</summary>
    Transform = 6,
}

/// <summary>One value test. <see cref="Hit"/> is set during the parse and cleared per frame.</summary>
internal struct Leaf
{
    /// <summary>Whether any occurrence passed.</summary>
    public bool Hit;

    /// <summary>Which test to run.</summary>
    public LeafKind Kind;

    /// <summary>Comparison operator for <see cref="LeafKind.Compare"/> and <see cref="LeafKind.Transform"/>.</summary>
    public CompareOp Op;

    /// <summary>Literal, or the low end of a range.</summary>
    public FieldValueData Literal;

    /// <summary>High end of a range.</summary>
    public FieldValueData LiteralHigh;

    /// <summary>Set members. Not copied per field.</summary>
    public FieldValueData[]? Set;

    /// <summary>Substring needle.</summary>
    public string? Needle;

    /// <summary>Compiled pattern. The timeout is the one from compile options.</summary>
    public Regex? Regex;

    /// <summary>Slice or length transform. The slice buffer is only valid until the test returns.</summary>
    public ValueAccessor? Accessor;
}

/// <summary>Boolean node over latched leaves.</summary>
internal enum BitOp : byte
{
    /// <summary>A leaf hit.</summary>
    Leaf = 0,

    /// <summary>Negation.</summary>
    Not = 1,

    /// <summary>
    /// Conjunction. Evaluated after the parse for the final verdict; during the parse the
    /// observer may stop further leaf tests once the root is permanently decided.
    /// </summary>
    And = 2,

    /// <summary>Disjunction. Evaluated after the parse; permanent true can stop further leaf tests.</summary>
    Or = 3,

    /// <summary>A constant. <see cref="BitNode.LeafIndex"/> is 1 for true.</summary>
    Const = 4,
}

/// <summary>One boolean node. Indexes are into the node or leaf array.</summary>
internal struct BitNode
{
    /// <summary>The operator.</summary>
    public BitOp Op;

    /// <summary>Left child, or the operand of a negation.</summary>
    public int Left;

    /// <summary>Right child.</summary>
    public int Right;

    /// <summary>Leaf index, or 1 when <see cref="Op"/> is <see cref="BitOp.Const"/> and the constant is true.</summary>
    public int LeafIndex;
}

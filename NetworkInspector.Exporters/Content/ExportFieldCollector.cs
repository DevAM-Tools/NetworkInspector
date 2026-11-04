// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Content;

/// <summary>
/// The observer of a field exporter: applies the selection and the occurrence policy to every arriving field and writes the
/// result straight into the exporter's <see cref="IFieldStage"/>. Fed by a tree-free parse (<see cref="IFieldObserver"/>) or by a
/// packet that already carries a tree (<see cref="FeedTree"/>: one lookup per selected field, or a walk for all fields); both
/// feeds share every rule, which is what keeps the values identical across feeds and formats.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hot path:</b> <see cref="OnField"/> runs once per accepted field on the parse thread. It does one array lookup for the
/// policy, one for the occurrence counter, and one call into the stage; it allocates nothing once the arrays have grown to the
/// stack's field count.
/// </para>
/// <para>
/// <b>Occurrence counters without clearing:</b> each counter is stamped with the sequence number of the packet it counts for.
/// A counter whose stamp differs from the current sequence counts as 0, so <see cref="BeginPacket"/> increments one integer
/// instead of clearing an array as large as the stack's field table; the stamps are cleared once when the sequence wraps. The
/// sequence is local (not the packet id) because a replay parses the same id again and must start from zero.
/// </para>
/// <para><b>Thread safety:</b> not thread-safe; one observer instance must not serve two overlapping parses (the
/// <see cref="IFieldObserver"/> contract).</para>
/// </remarks>
/// <seealso cref="IFieldStage"/>
/// <seealso cref="ExportTreeWalker"/>
internal sealed class ExportFieldCollector : IFieldObserver
{
    #region Configuration and state

    /// <summary>Whether every value field is exported (the all-fields shape) instead of a selection.</summary>
    private readonly bool _AllFields;

    /// <summary>The content that decides the shape, the selection, and which texts are kept.</summary>
    private readonly ExportContent _Content;

    /// <summary>The selection of the selected-fields shape; null in the all-fields shape.</summary>
    private readonly FieldSelection? _Selection;

    /// <summary>The stage the next packet's fields go to. Set by the exporter only between packets.</summary>
    internal IFieldStage Stage { get; set; }

    /// <summary>Whether the export keeps custom texts; a feed reads a field's custom text only then (reading can complete a deferred text).</summary>
    internal bool WantsCustomText => _Content.CustomText;

    /// <summary>Creates the collector of one exporter.</summary>
    /// <param name="content">Validated content; its shape must be <see cref="ExportShape.SelectedFields"/> or <see cref="ExportShape.AllFields"/>.</param>
    /// <param name="stage">The stage the fields go to; replaced with <see cref="Stage"/> when the exporter switches batches.</param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> or <paramref name="stage"/> is null.</exception>
    /// <exception cref="ArgumentException">The content's shape is <see cref="ExportShape.Hierarchy"/>.</exception>
    internal ExportFieldCollector(ExportContent content, IFieldStage stage)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(stage);
        if (content.Shape == ExportShape.Hierarchy)
        {
            throw new ArgumentException("The hierarchy shape is written from the field tree, not through the collector.", nameof(content));
        }

        _Content = content;
        _Selection = content.Fields;
        _AllFields = content.Shape == ExportShape.AllFields;
        Stage = stage;

        // Selected fields: the policy table is built once from the selection (its stack is known). AllFields: the table
        // starts small and grows with the first id beyond it; every value field gets Occurrences.All.
        int size = _Selection is null
            ? 64
            : _Selection.Stack.FieldCount;
        _PolicyById = new byte[size];
        _Counts = new int[size];
        _Stamps = new int[size];
        if (_Selection is not null)
        {
            foreach (SelectedField field in _Selection.Fields)
            {
                _PolicyById[field.Id.Value] = (byte)((int)field.Occurrences + 1);
            }
        }
    }

    /// <summary>
    /// The parse demand of this collector: a tree-free parse (<see cref="ParseDepth.Packet"/>) whose watch is the selection's
    /// extraction watch (or <see cref="FieldWatch.All"/>), and whose display-text watch is the selection's display watch
    /// (exactly the selected ids; <see cref="FieldWatch.All"/> for all fields) when the content writes custom texts or
    /// representations, otherwise <see cref="FieldWatch.None"/> (the dissectors then skip building texts).
    /// </summary>
    /// <returns>The demand for a <see cref="ParsePlan"/>.</returns>
    internal ParseDemand CreateDemand()
    {
        FieldWatch watch = FieldWatch.All;
        FieldWatch displayText = FieldWatch.None;
        bool wantsText = _Content.CustomText || _Content.CustomRepresentation;
        if (_Selection is not null)
        {
            watch = _Selection.Watch;
            if (wantsText)
            {
                displayText = _Selection.DisplayWatch;
            }
        }
        else if (wantsText)
        {
            displayText = FieldWatch.All;
        }

        return ParseDemand.Fields(new ParseObserver { Observer = this, Watch = watch, DisplayText = displayText });
    }

    #endregion

    #region Parse feed

    /// <inheritdoc/>
    public void BeginPacket(PacketId id, Timestamp timestamp)
    {
        // One increment invalidates every counter of the previous packet (see remarks). Invariant: a stamp equals _Sequence only
        // for a field counted in this packet. At the wrap (once per 2^32 packets) the stamps are cleared and the sequence
        // restarts at 1, because a field last seen at the wrapped value would otherwise read its stale count.
        _Sequence = unchecked(_Sequence + 1);
        if (_Sequence == 0)
        {
            Array.Clear(_Stamps);
            _Sequence = 1;
        }

        Stage.BeginPacket(id.Value, timestamp.AsNanos);
    }

    /// <inheritdoc/>
    public void OnField(in FieldVisit visit)
    {
        ref readonly FieldValue value = ref visit.Value;
        Accept(visit.FieldId.Value, in value, visit.CustomText);
    }

    /// <inheritdoc/>
    public void EndPacket(PacketId id, bool complete) => Stage.EndPacket(complete);

    #endregion

    #region Tree feed

    /// <summary>
    /// Feeds a packet that carries a field tree (the exporter got a packet it did not observe): walks the tree in pre-order
    /// through <see cref="ExportTreeWalker"/> and applies the same rules as a parse feed.
    /// </summary>
    /// <param name="packet">A packet with <see cref="Packet.HasFieldTree"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="packet"/> is null.</exception>
    internal void FeedTree(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        BeginPacket(packet.Id, packet.Timestamp);
        if (_Selection is null)
        {
            ExportTreeWalker.WalkValues(packet, this);
        }
        else
        {
            _FeedSelectedFields(packet);
        }

        EndPacket(packet.Id, complete: true);
    }

    /// <summary>
    /// Selected fields of a packet that carries a tree: one lookup per selected field instead of a walk over the whole tree.
    /// <para>
    /// <b>Why:</b> a full walk with <c>materialize: true</c> expands every lazy container of the packet (all protocols, all
    /// payload items) to find two or three fields; measured, a walk cost about five times a per-field lookup and about 15 times
    /// its allocations. The lookup expands only the containers that can hold a selected field. The values and the occurrence
    /// rules are the same: each hit goes through
    /// <see cref="Accept"/>.
    /// </para>
    /// <para>
    /// <b>Order:</b> hits arrive grouped by selected field (selection order) and in tree order within one field, not in
    /// pre-order across fields. Formats that write per field (wide rows, field tables) do not see the difference; the long
    /// layouts sort per packet or document the order.
    /// </para>
    /// </summary>
    /// <param name="packet">A packet with a field tree.</param>
    private void _FeedSelectedFields(Packet packet)
    {
        // Indexed loop on purpose: foreach over the IReadOnlyList boxes its enumerator, one allocation per packet on this path.
        IReadOnlyList<SelectedField> fields = _Selection!.Fields; // Invariant: only called when a selection exists.
        for (int i = 0; i < fields.Count; i++)
        {
            SelectedField selected = fields[i];
            FieldLookupCookie cookie = FieldLookupCookie.Start;
            while (packet.TryGetNextField(selected.Id, ref cookie, out Field field, materialize: true))
            {
                FieldValue value = field.Value;

                // Perf: the text is read only when the content writes it; reading a custom text of a field can complete a
                // deferred text.
                LazyString text = default;
                if (WantsCustomText)
                {
                    text = field.CustomText;
                }

                Accept(selected.Id.Value, in value, text);

                // First needs only the first hit: do not scan the rest of the tree for occurrences that would be dropped.
                if (selected.Occurrences == Occurrences.First)
                {
                    break;
                }
            }
        }
    }

    #endregion

    #region Policy and occurrences

    /// <summary>Policy per field id: 0 = not exported, otherwise <c>(int)Occurrences + 1</c>. Dense by id; grown on demand for AllFields.</summary>
    private byte[] _PolicyById;

    /// <summary>Occurrence counter per field id; valid only when the field's entry in <see cref="_Stamps"/> equals <see cref="_Sequence"/>.</summary>
    private int[] _Counts;

    /// <summary>Per field id, the <see cref="_Sequence"/> of the packet its counter in <see cref="_Counts"/> counts for.</summary>
    private int[] _Stamps;

    /// <summary>Packet sequence number; incremented per packet, never 0 while a packet is parsed (the wrap restarts at 1).</summary>
    private int _Sequence;

    /// <summary>
    /// Moves the packet sequence so that the wrap at 2^32 packets can be reached in a few packets; production code never calls it.
    /// </summary>
    /// <param name="sequence">The sequence after which the next <see cref="BeginPacket"/> increments; -1 makes the next packet wrap.</param>
    internal void RestartSequenceAt(int sequence) => _Sequence = sequence;

    /// <summary>Shared rule of both feeds: filter by policy, count the occurrence, apply First / Last / All.</summary>
    /// <param name="fieldId">Field id of the arriving value.</param>
    /// <param name="value">The value (valid only for the call).</param>
    /// <param name="customText">The custom text set when the field was appended.</param>
    internal void Accept(int fieldId, in FieldValue value, LazyString customText)
    {
        // Value-less nodes (protocols, groups) have no column in the flat shapes; the watch delivers them because the
        // extraction watch accepts the owning protocols' fields.
        FieldValueData data = value.Data;
        if (data.Type == FieldType.None || fieldId < 0)
        {
            return;
        }

        int policy = _PolicyOf(fieldId);
        if (policy == 0)
        {
            return;
        }

        // Count this occurrence; the stamp check replaces clearing (see remarks).
        int occurrence = 0;
        if (_Stamps[fieldId] == _Sequence)
        {
            occurrence = _Counts[fieldId];
        }

        _Stamps[fieldId] = _Sequence;
        _Counts[fieldId] = occurrence + 1;

        LazyString representation = default;
        if (_Content.CustomRepresentation)
        {
            representation = value.CustomRepresentation;
        }

        LazyString text = default;
        if (_Content.CustomText)
        {
            text = customText;
        }

        switch ((Occurrences)(policy - 1))
        {
            case Occurrences.First:
                if (occurrence == 0)
                {
                    Stage.AddValue(fieldId, 0, in data, representation, text);
                }

                break;
            case Occurrences.Last:
                // The last value wins in place: one row per packet, numbered 0, no second pass over the packet.
                if (occurrence == 0)
                {
                    Stage.AddValue(fieldId, 0, in data, representation, text);
                }
                else
                {
                    Stage.ReplaceLastValue(fieldId, in data, representation, text);
                }

                break;
            default:
                Stage.AddValue(fieldId, occurrence, in data, representation, text);
                break;
        }
    }

    /// <summary>Policy byte of <paramref name="fieldId"/>; grows the tables for AllFields (amortized, not per packet).</summary>
    /// <param name="fieldId">A non-negative field id of the parse's stack.</param>
    /// <returns>0 when the field is not exported; otherwise <c>(int)Occurrences + 1</c>.</returns>
    private int _PolicyOf(int fieldId)
    {
        if ((uint)fieldId < (uint)_PolicyById.Length)
        {
            int policy = _PolicyById[fieldId];
            if (policy != 0 || !_AllFields)
            {
                return policy;
            }
        }
        else if (!_AllFields)
        {
            return 0;
        }
        else
        {
            _Grow(fieldId);
        }

        _PolicyById[fieldId] = (byte)((int)Occurrences.All + 1);
        return _PolicyById[fieldId];
    }

    /// <summary>Grows the policy, counter, and stamp tables so <paramref name="fieldId"/> is a valid index (at least doubling).</summary>
    /// <param name="fieldId">The id that did not fit.</param>
    /// <exception cref="OverflowException">The new size exceeds <see cref="int.MaxValue"/>.</exception>
    private void _Grow(int fieldId)
    {
        int size = Math.Max(checked(fieldId + 1), checked(_PolicyById.Length * 2));
        Array.Resize(ref _PolicyById, size);
        Array.Resize(ref _Counts, size);
        Array.Resize(ref _Stamps, size);
    }

    #endregion
}

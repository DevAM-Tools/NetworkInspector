// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// The SOME/IP-TP part of the parser: a message whose type has the TP flag is cut into segments, each behind a 4-byte TP word
/// (a 28-bit offset in 16-byte units, three reserved bits, the more-segments flag). The segment that completes the message, or
/// that fixes its length while a hole remains, hands the payload on as a buffer the packet binds; every other segment shows its TP
/// word and nothing of its own bytes.
/// <para>
/// <b>Backward references only.</b> A packet is never changed after its first parse, so a segment cannot point forward to the
/// segment that completes the message. The completing segment lists the segments that contributed (all of them were parsed
/// before it); an earlier segment shows only what was known when it arrived, and a re-parse of it at any later time shows
/// the same. This keeps the index exact: what the index says about a packet during ingest is what it says afterwards.
/// </para>
/// <para>
/// Field tree under the message container:
/// </para>
/// <code>
/// someip.tp: SOME/IP-TP, Offset: 0 bytes, More Segments
/// ├── someip.tp.offset: 0 bytes, someip.tp.more, someip.tp.reserved
/// ├── someip.tp.segments                          [delivering segment only; lazy: one someip.tp.segment (packet id) per segment]
/// ├── someip.tp.segment_count, someip.tp.reassembled_length, someip.tp.partial   [delivering segment only]
/// ├── someip.tp.region: [2048-3071] 1024 bytes    [partial prefix: a covered range behind the first gap]
/// └── someip.tp.error.dropped | evicted | reset | gap | overlap | malformed | size_limit
/// </code>
/// </summary>
public sealed partial class SomeIpProtocol
{
    #region Constants

    /// <summary>
    /// The text of the parse error for a reassembled message that cannot be handed on because the packet's table of bound buffers
    /// is full. Limit: 255 additional buffers per packet (a constant of the packet's effect keys, not configurable); secure by
    /// default. Exceeding it is an expected failure that is named, not a silent drop.
    /// </summary>
    private const string _BoundBufferLimitMessage =
        "Reassembled SOME/IP-TP message cannot be parsed further: a packet can hold at most 255 additional buffers";

    #endregion

    #region Fields

    [NoneField("someip.tp", "SOME/IP Transport Protocol", IndexGroup = "someip.tp")]
    private FieldId _TpContainerFieldId;

    [U64Field("someip.tp.offset", "Offset", IndexGroup = "someip.tp")]
    private FieldId _TpOffsetFieldId;

    [BoolField("someip.tp.more", "More Segments", IndexGroup = "someip.tp")]
    private FieldId _TpMoreFieldId;

    [U64Field("someip.tp.reserved", "Reserved", IndexGroup = "someip.tp")]
    private FieldId _TpReservedFieldId;

    // The segment list container is a U64 field: its value is the layer key of the message, from which the lazy populator finds
    // the published effect. A key is cheaper and safer than holding the contributor array in the tree.
    [U64Field("someip.tp.segments", "Segments", IndexGroup = "someip.tp.segments")]
    private FieldId _TpSegmentsFieldId;

    [U64Field("someip.tp.segment", "Segment Packet", IndexGroup = "someip.tp.segments")]
    private FieldId _TpSegmentFieldId;

    [U64Field("someip.tp.segment_count", "Segment Count", IndexGroup = "someip.tp.stats")]
    private FieldId _TpSegmentCountFieldId;

    [U64Field("someip.tp.reassembled_length", "Reassembled Length", IndexGroup = "someip.tp.stats")]
    private FieldId _TpReassembledLengthFieldId;

    [BoolField("someip.tp.partial", "Partial", IndexGroup = "someip.tp.stats")]
    private FieldId _TpPartialFieldId;

    [BytesField("someip.tp.region", "Region Behind The Gap", IndexGroup = "someip.tp.region")]
    private FieldId _TpRegionFieldId;

    [StringField("someip.tp.error.dropped", "TP Result Unavailable", IndexGroup = "someip.tp.error.dropped")]
    private FieldId _TpErrorDroppedFieldId;

    [StringField("someip.tp.error.evicted", "TP Session Evicted", IndexGroup = "someip.tp.error.evicted")]
    private FieldId _TpErrorEvictedFieldId;

    [StringField("someip.tp.error.reset", "TP Session Restarted", IndexGroup = "someip.tp.error.reset")]
    private FieldId _TpErrorResetFieldId;

    [StringField("someip.tp.error.gap", "TP Gap", IndexGroup = "someip.tp.error.gap")]
    private FieldId _TpErrorGapFieldId;

    [StringField("someip.tp.error.overlap", "TP Overlap Conflict", IndexGroup = "someip.tp.error.overlap")]
    private FieldId _TpErrorOverlapFieldId;

    [StringField("someip.tp.error.malformed", "TP Segment Malformed", IndexGroup = "someip.tp.error.malformed")]
    private FieldId _TpErrorMalformedFieldId;

    [StringField("someip.tp.error.size_limit", "TP Size Limit", IndexGroup = "someip.tp.error.sizeLimit")]
    private FieldId _TpErrorSizeLimitFieldId;

    /// <summary>
    /// The segment sessions. First-parse state: only the ordered first parse calls it (the ingest gate guarantees it), a re-parse
    /// reads the recorded outcome. No lock, because no second thread ever touches it.
    /// </summary>
    private readonly SomeIpTpSessions _TpSessions;

    /// <summary>The lazy populator of the segment list; one delegate per protocol instance, created with it.</summary>
    private readonly LazyPopulator _TpSegmentsPopulator;

    #endregion

    #region TP branch

    /// <summary>
    /// Handles one segment: appends the TP word, feeds the segment to the reassembler on the first parse (or reads the recorded
    /// outcome on a replay), shows the diagnostics of the outcome, and, for the segment that delivers a payload, the segment list,
    /// the statistics and the regions behind a gap, and returns the payload to hand on.
    /// </summary>
    /// <param name="container">The message container.</param>
    /// <param name="afterHeader">The message payload: the TP word and the segment's bytes behind it (at least four bytes).</param>
    /// <param name="header">The header of the message.</param>
    /// <param name="context">Parse context, for index recording.</param>
    /// <param name="messageKey">The layer key of the message slice, or -1 when it cannot be keyed.</param>
    /// <param name="isReplay">Whether this is a re-parse.</param>
    /// <param name="pending">The unit's acceptance decision that is still to be recorded.</param>
    /// <param name="delivered">The payload to hand on (empty when this segment delivers nothing).</param>
    /// <param name="partial">True when <paramref name="delivered"/> is the gap-free prefix of an incomplete message.</param>
    /// <param name="timing">The method-timing outcome of this segment (present for the segment that completes a response).</param>
    /// <returns>
    /// No error, or the parse error that names the limit that stopped the bind of the reassembled message: the full table of
    /// bound buffers, or <c>someip.tp_max_derived_bytes_per_packet</c> in total.
    /// </returns>
    private ParseResult _AppendTp(
        in MutField container,
        ReadOnlyMemory<byte> afterHeader,
        in SomeIpHeader header,
        in ParseContext context,
        int messageKey,
        bool isReplay,
        ref AcceptanceRecord pending,
        out ReadOnlyMemory<byte> delivered,
        out bool partial,
        out TimingFact timing)
    {
        delivered = default;
        partial = false;
        timing = default;

        // The TP word is decided from this segment's bytes alone, so it is identical on every parse. The presence is recorded
        // first, as for every field group.
        _ = SomeIpTpHeader.TryParse(afterHeader.Span, out SomeIpTpHeader tp);
        context.RecordGroupPresence(_SomeipTpGroupId);
        string segmentSuffix = tp.MoreSegments
            ? ", More Segments"
            : ", Last Segment";
        MutField tpField = container.AppendWithCustomText(
            _TpContainerFieldId,
            FieldValue.None,
            "SOME/IP-TP, Offset: ",
            tp.ByteOffset,
            " bytes",
            segmentSuffix);
        tpField.AppendWithCustomText(_TpOffsetFieldId, FieldValue.NewU64(tp.ByteOffset), tp.ByteOffset, " bytes");
        tpField.Append(_TpMoreFieldId, FieldValue.NewBool(tp.MoreSegments));
        tpField.Append(_TpReservedFieldId, FieldValue.NewU64(tp.Reserved));

        // The outcome. The first parse feeds the reassembler and records the outcome before the gate raises the watermark; every
        // later parse reads that record. A segment that cannot be keyed takes the stateless path (the TP word and a named
        // diagnostic), never a mutation: without a key a replay could not find the record, and the two parses would differ.
        PacketId packetId = container.Packet.Id;
        FragmentOutcome outcome;
        if (messageKey < 0)
        {
            outcome = FragmentOutcome.NotRecorded;
        }
        else if (isReplay)
        {
            // A replay reads the outcome and, for the segment that completed a response, the pair that the first parse stored with it.
            if (_TryGetEffect(packetId, messageKey, out SomeIpMessageEffect recorded))
            {
                outcome = recorded.Tp;
                if (_ShowsTimingOnReplay(in header, in outcome))
                {
                    timing = TimingFact.FromEffect(in recorded);
                }
            }
            else
            {
                outcome = FragmentOutcome.NotRecorded;
            }
        }
        else
        {
            SomeIpTpKey key = new(header.ServiceId, header.MethodId, header.ClientId, header.SessionId, header.BaseType, header.InterfaceVersion);
            outcome = _TpSessions.Add(in key, tp.ByteOffset, afterHeader.Span[SomeIpTpHeader.Size..], tp.MoreSegments, packetId.Value);

            // The analysis runs in this first parse only, and its result is recorded with the segment outcome: a key holds one record.
            timing = _TimeSegment(in header, container.Packet, tp.ByteOffset, in outcome);
            _RecordMessageEffect(
                packetId,
                messageKey,
                ref pending,
                new SomeIpMessageEffect(false, false, outcome, timing.RequestPacket, timing.DelayNanos, timing.Forgotten));
        }

        _AppendTpDiagnostics(in tpField, in outcome, in context);
        if (!outcome.DeliversPayload)
        {
            return 0;
        }

        // A delivering segment. Every segment it lists was parsed before it (or is itself), so the list is final now. It is lazy
        // because a 10 MiB message has thousands of segments; its value is the message key, and the populator reads the contributor
        // array from the published effect (lock-free, any thread).
        context.RecordGroupPresence(_SomeipTpSegmentsGroupId);
        context.RecordGroupPresence(_SomeipTpStatsGroupId);
        tpField.AppendLazyWithCustomText(_TpSegmentsFieldId, FieldValue.NewU64((ulong)messageKey), new LazyString("Segments"), _TpSegmentsPopulator);
        tpField.Append(_TpSegmentCountFieldId, FieldValue.NewU64((ulong)outcome.FragmentCount));
        tpField.Append(_TpReassembledLengthFieldId, FieldValue.NewU64((ulong)outcome.DatagramLength));
        tpField.Append(_TpPartialFieldId, FieldValue.NewBool(outcome.Kind == FragmentOutcomeKind.PartialPrefix));

        // The reassembled payload is not a slice of the frame, so the packet binds it as an additional buffer: nested parsers need a
        // buffer the packet knows to compute their layer keys. A full table is a named parse error, never a silent drop. The same
        // array is bound on a replay, in the same order, which gives the same buffer index and therefore the same keys.
        //
        // Security: the bound messages stay referenced until the packet is recycled and one datagram can complete several of them,
        // so the bytes bound to the packet, with this message, are counted against someip.tp_max_derived_bytes_per_packet
        // (value, range, posture, and who may change it: SomeIpLimits.TpMaxDerivedBytesPerPacket). DerivedBytesLimit.TryBind
        // checks the total before it binds, so a refused message leaves the packet unchanged; the TP fields of the segment are
        // already shown and stay. The check depends only on the bound bytes and their order, so a replay reaches the same decision
        // as the first parse. Residual risk: the message that fails was already reassembled by the session (at most the message
        // size limit).
        bool isBound = DerivedBytesLimit.TryBind(
            in container, outcome.Payload!, DerivedBytesTotal, out ReadOnlyMemory<byte> bound, out DerivedBytesFailure failure);
        if (!isBound)
        {
            return ParseError.Custom(
                ProtocolName,
                failure == DerivedBytesFailure.TotalExceeded
                    ? _DerivedBytesLimitText()
                    : _BoundBufferLimitMessage);
        }

        _AppendTpRegions(in tpField, in outcome, bound, in context);

        // What is handed on is the decodable part: the whole message when it is complete, the bytes that arrived without a gap
        // when the last segment fixed the length while a hole remains.
        partial = outcome.Kind == FragmentOutcomeKind.PartialPrefix;
        delivered = bound[..outcome.PrefixLength];
        return 0;
    }

    /// <summary>
    /// The text of the parse error for a packet whose reassembled messages would pass
    /// <c>someip.tp_max_derived_bytes_per_packet</c> in total: it names the setting, the limit, and that the limit is the total
    /// per packet. Sync: <c>DerivedBytesLimit.Text</c> (the wording shared with HTTP and WebSocket).
    /// </summary>
    /// <returns>The error text; allocates one string, on the error path only.</returns>
    private string _DerivedBytesLimitText() => DerivedBytesLimit.Text(
        "Reassembled SOME/IP-TP messages",
        DerivedBytesTotal,
        SomeIpLimits.TpMaxDerivedBytesPerPacket.Key);

    /// <summary>
    /// The total of reassembled bytes per packet as the unsigned value the packet counts against: the setting, or 0 when a caller
    /// that built <see cref="SomeIpSettings"/> by hand gave a negative number. Internal so that the tests can pin the rule.
    /// </summary>
    /// <remarks>
    /// Security: the settings layer keeps the value inside 2 KiB to 16 GiB, but <see cref="SomeIpSettings"/> is a public record that a
    /// plugin or test can construct with any <see cref="long"/>. A bare cast would turn a negative number into a total near
    /// 2^64, which silently removes the limit. Saturating at 0 fails closed: every message is then refused with the named error,
    /// so a wrong value is seen at once instead of weakening the protection. Rejected alternative: a throwing check on every
    /// segment (an exception on a hot parse path for a configuration mistake). Residual risk: none.
    /// </remarks>
    internal ulong DerivedBytesTotal => ulong.CreateSaturating(Settings.TpMaxDerivedBytesPerPacket);

    /// <summary>
    /// Shows what the outcome says went wrong or notable, one named field per cause, with the limit and its value where a limit is
    /// involved. The groups are recorded with the fields, so the index equals the tree.
    /// </summary>
    private void _AppendTpDiagnostics(in MutField tpField, in FragmentOutcome outcome, in ParseContext context)
    {
        if (outcome.Kind == FragmentOutcomeKind.NotRecorded)
        {
            _AppendTpDiagnostic(
                in tpField, _TpErrorDroppedFieldId, _SomeipTpErrorDroppedGroupId, in context,
                "No reassembly result is available for this segment: its position in the packet cannot be keyed, or the first parse recorded none.");
            return;
        }

        if ((outcome.Flags & FragmentOutcomeFlags.Restarted) != 0)
        {
            _AppendTpDiagnostic(
                in tpField, _TpErrorResetFieldId, _SomeipTpErrorResetGroupId, in context,
                "A segment at offset 0 restarted the transfer; the earlier segments of the same message were dropped.");
        }

        if ((outcome.Flags & FragmentOutcomeFlags.OverlapConflict) != 0)
        {
            _AppendTpDiagnostic(
                in tpField, _TpErrorOverlapFieldId, _SomeipTpErrorOverlapGroupId, in context,
                "This segment overlaps bytes that arrived earlier with different content; the earlier bytes are kept.");
        }

        if ((outcome.Flags & FragmentOutcomeFlags.EvictedForCount) != 0)
        {
            _AppendTpDiagnostic(
                in tpField, _TpErrorEvictedFieldId, _SomeipTpErrorEvictedGroupId, in context,
                SomeIpLimits.TpMaxSessions.HitText("Another session was evicted to make room for this one", (ulong)Settings.TpMaxSessions));
        }

        if ((outcome.Flags & FragmentOutcomeFlags.EvictedForBudget) != 0)
        {
            _AppendTpDiagnostic(
                in tpField, _TpErrorEvictedFieldId, _SomeipTpErrorEvictedGroupId, in context,
                SomeIpLimits.TpMaxBufferedBytes.HitText("Another session was evicted to stay within the buffered bytes", (ulong)Settings.TpMaxBufferedBytes));
        }

        if (outcome.Kind == FragmentOutcomeKind.Discarded)
        {
            _AppendTpDiscard(in tpField, outcome.DiscardReason, in context);
        }

        if (outcome.Kind == FragmentOutcomeKind.PartialPrefix && outcome.Gaps is { Length: > 0 } gaps)
        {
            _AppendTpDiagnostic(in tpField, _TpErrorGapFieldId, _SomeipTpErrorGapGroupId, in context, _GapText(gaps));
        }
    }

    private void _AppendTpDiscard(in MutField tpField, FragmentDiscardReason reason, in ParseContext context)
    {
        switch (reason)
        {
            case FragmentDiscardReason.Alignment:
                _AppendTpDiagnostic(
                    in tpField, _TpErrorMalformedFieldId, _SomeipTpErrorMalformedGroupId, in context,
                    "A segment that is not the last must carry a non-zero multiple of 16 bytes (the offset counts 16-byte units).");
                break;

            case FragmentDiscardReason.ExtentLimit:
                _AppendTpDiagnostic(
                    in tpField, _TpErrorSizeLimitFieldId, _SomeipTpErrorSizeLimitGroupId, in context,
                    SomeIpLimits.TpMaxMessageSize.HitText("The segment reaches beyond the largest message that is reassembled", (ulong)Settings.TpMaxMessageSize));
                break;

            case FragmentDiscardReason.BufferLimit:
                _AppendTpDiagnostic(
                    in tpField, _TpErrorEvictedFieldId, _SomeipTpErrorEvictedGroupId, in context,
                    SomeIpLimits.TpMaxBufferedBytes.HitText("This session alone needs more than the buffered bytes allow and was dropped", (ulong)Settings.TpMaxBufferedBytes));
                break;

            case FragmentDiscardReason.BeyondEnd:
                _AppendTpDiagnostic(
                    in tpField, _TpErrorMalformedFieldId, _SomeipTpErrorMalformedGroupId, in context,
                    "The segment starts at or beyond the end of the message that the last segment fixed.");
                break;

            case FragmentDiscardReason.ConflictingEnd:
                _AppendTpDiagnostic(
                    in tpField, _TpErrorMalformedFieldId, _SomeipTpErrorMalformedGroupId, in context,
                    "Two last segments disagree about the length of the message; the session was dropped.");
                break;

            default:
                _AppendTpDiagnostic(
                    in tpField, _TpErrorMalformedFieldId, _SomeipTpErrorMalformedGroupId, in context,
                    "The segment was refused by the reassembler.");
                break;
        }
    }

    private static string _GapText(FragmentRange[] gaps)
    {
        StringBuilder text = new("Missing bytes: ");
        for (int i = 0; i < gaps.Length; i++)
        {
            if (i > 0)
            {
                _ = text.Append(", ");
            }

            _ = text.Append(gaps[i].Start.ToString(CultureInfo.InvariantCulture))
                .Append('-')
                .Append((gaps[i].Start + gaps[i].Length - 1).ToString(CultureInfo.InvariantCulture));
        }

        return text.Append(". The decodable prefix is shown; a late segment completes the message.").ToString();
    }

    private static void _AppendTpDiagnostic(in MutField tpField, FieldId field, IndexGroupId group, in ParseContext context, string text)
    {
        context.RecordGroupPresence(group);
        tpField.Append(field, FieldValue.NewString(text));
    }

    /// <summary>
    /// A message with the TP flag whose payload cannot hold the TP header: the bytes that are there are shown raw (nothing is
    /// decoded from them) and the malformed error names the shortfall. The caller has recorded both groups.
    /// </summary>
    /// <param name="container">The message container.</param>
    /// <param name="payload">The bytes behind the SOME/IP header; empty for none.</param>
    private void _AppendTpTooShort(in MutField container, ReadOnlyMemory<byte> payload)
    {
        if (!payload.IsEmpty)
        {
            container.Append(_PayloadFieldId, FieldValue.NewBytes(payload));
        }

        container.Append(
            _TpErrorMalformedFieldId,
            FieldValue.NewString(string.Create(
                CultureInfo.InvariantCulture,
                $"The TP header needs {SomeIpTpHeader.Size} bytes but only {payload.Length} follow the SOME/IP header.")));
    }

    /// <summary>
    /// For a partial prefix: one raw field per covered range behind the first hole, so the bytes that did arrive are not lost. They
    /// follow the prefix in the reassembled payload, range after range.
    /// </summary>
    private void _AppendTpRegions(in MutField tpField, in FragmentOutcome outcome, ReadOnlyMemory<byte> bound, in ParseContext context)
    {
        if (outcome.CoveredAfterGap is not { Length: > 0 } regions)
        {
            return;
        }

        context.RecordGroupPresence(_SomeipTpRegionGroupId);
        int position = outcome.PrefixLength;
        foreach (FragmentRange region in regions)
        {
            tpField.AppendWithCustomText(
                _TpRegionFieldId,
                FieldValue.NewBytes(bound.Slice(position, region.Length)),
                "[",
                region.Start,
                "-",
                region.Start + region.Length - 1,
                "] ",
                region.Length,
                " bytes");
            position += region.Length;
        }
    }

    #endregion

    #region Population

    /// <summary>
    /// Lists the segments of a delivering segment's message in offset order. Reads only the published effect of this message, never
    /// the reassembler: the effect was recorded by the first parse before the gate raised the watermark, so any thread may expand
    /// the list at any time and see the list of the first parse.
    /// </summary>
    private ParseResult _PopulateTpSegments(in MutField segments)
    {
        if (!segments.Value.Data.TryGetAsU64(out ulong key)
            || !_TryGetEffect(segments.Packet.Id, (int)key, out SomeIpMessageEffect effect)
            || effect.Tp.Contributors is not { } contributors)
        {
            // Cannot happen for a delivering segment; an empty list is the safe answer when it does.
            return 0;
        }

        foreach (int packetId in contributors)
        {
            segments.Append(_TpSegmentFieldId, FieldValue.NewU64((ulong)packetId));
        }

        return 0;
    }

    #endregion
}

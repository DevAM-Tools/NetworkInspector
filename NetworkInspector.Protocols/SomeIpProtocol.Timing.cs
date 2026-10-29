// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Method timing: with <c>someip.method_timing</c> on, a response is paired with the request it answers and shows the request's
/// packet and the delay.
/// <para>
/// <b>Why only the response shows timing:</b> a request is final before its response exists. If the request carried a "response in"
/// field, parsing the response would have to change the tree of a packet that was parsed earlier, and a re-parse of the request
/// would differ from its first parse depending on whether the response had been seen yet. Everything the analysis knows is
/// therefore stored on the later packet.
/// </para>
/// <para>
/// <b>How a pair is kept:</b> the first parse of a response takes the latest pending request of its key from the tracker and stores
/// the pair (request packet, delay, whether the request was forgotten by the pending limit) in the response's effect record. A
/// re-parse and the lazy container read only that record, never the tracker.
/// </para>
/// <para>
/// <b>Request registration:</b> a plain request registers when it is parsed; a segmented request registers with its segment at
/// offset 0 (the packet and time of the first segment, which is when the request was sent). A response pairs when it is complete: a
/// plain response when it is parsed, a segmented one on the segment that completes the message. Only messages with the plain types
/// request (0x00) and response (0x80) take part: an error message answers a request without consuming it, a notification and a
/// request without return have no response, and an acknowledgement is part of the segment protocol and not a message of its own.
/// </para>
/// </summary>
public sealed partial class SomeIpProtocol
{
    #region Fields

    [U64Field("someip.timing", "Method Timing", IndexGroup = "someip.timing")]
    private FieldId _TimingFieldId;

    [U64Field("someip.timing.request_in", "Request Packet", IndexGroup = "someip.timing")]
    private FieldId _TimingRequestInFieldId;

    [F64Field("someip.timing.delay", "Delay (seconds)", IndexGroup = "someip.timing")]
    private FieldId _TimingDelayFieldId;

    [BoolField("someip.timing.request_missing", "Request Missing", IndexGroup = "someip.timing")]
    private FieldId _TimingRequestMissingFieldId;

    [StringField("someip.timing.error", "Timing Anomaly", IndexGroup = "someip.timing")]
    private FieldId _TimingErrorFieldId;

    #endregion

    #region State

    /// <summary>
    /// The pending requests; <see langword="null"/> unless <c>someip.method_timing</c> is on, so a stack that does not use the
    /// analysis holds no table. Touched only by the ordered first parse (see <see cref="SomeIpTimingTracker"/>).
    /// </summary>
    private readonly SomeIpTimingTracker? _TimingTracker;

    /// <summary>The lazy populator of the timing container; one delegate per protocol instance.</summary>
    private readonly LazyPopulator _TimingPopulator;

    #endregion

    #region Types

    /// <summary>
    /// The timing outcome of one message.
    /// </summary>
    /// <param name="Present">True when the message is a response that took part in the analysis (the container is shown).</param>
    /// <param name="RequestPacket">The packet of the paired request, or -1 when none was pending.</param>
    /// <param name="DelayNanos">Response time minus request time in nanoseconds; valid when a request was paired.</param>
    /// <param name="Forgotten">The request of this response was forgotten because the pending table was full.</param>
    private readonly record struct TimingFact(bool Present, int RequestPacket, long DelayNanos, bool Forgotten)
    {
        /// <summary>The fact that a message reads back from its recorded effect.</summary>
        internal static TimingFact FromEffect(in SomeIpMessageEffect effect) =>
            new(true, effect.TimingRequestPacket, effect.TimingDelayNanos, effect.TimingPendingEvicted);
    }

    #endregion

    #region First parse

    /// <summary>
    /// Whether the message takes part in the analysis as a request (<paramref name="request"/> true) or a response, by its exact
    /// message type with the TP flag cleared. The ACK flag is kept in the comparison on purpose: an acknowledgement is not a request.
    /// </summary>
    private static bool _IsTimedType(in SomeIpHeader header, out bool request)
    {
        byte plain = (byte)(header.MessageType & ~SomeIpHeader.TpFlag);
        request = plain == 0x00;
        return plain is 0x00 or 0x80;
    }

    private SomeIpTimingKey _TimingKeyOf(in SomeIpHeader header, Packet packet) => new(
        header.ServiceId,
        header.MethodId,
        header.ClientId,
        header.SessionId,
        header.InterfaceVersion,
        Settings.MethodTimingIgnoreInterface
            ? 0
            : packet.Frame.InterfaceId.Value);

    /// <summary>
    /// The analysis for a message that is not segmented: a request registers, a response pairs and records its pair. A replay reads
    /// the recorded pair and touches nothing else.
    /// </summary>
    /// <param name="parentField">The parent of the message container (for the packet and the effect key).</param>
    /// <param name="messageData">The message bytes, which key its effect.</param>
    /// <param name="header">The header of the message.</param>
    /// <param name="isReplay">Whether this is a re-parse.</param>
    /// <param name="pending">The unit's acceptance decision that a message effect at the unit key takes over.</param>
    /// <param name="messageKey">The effect key of the message, or -1 when it cannot be keyed.</param>
    private TimingFact _TimePlainMessage(
        in MutField parentField,
        ReadOnlyMemory<byte> messageData,
        in SomeIpHeader header,
        bool isReplay,
        ref AcceptanceRecord pending,
        out int messageKey)
    {
        messageKey = -1;
        if (_TimingTracker is null || !_IsTimedType(in header, out bool request))
        {
            return default;
        }

        Packet packet = parentField.Packet;
        if (request)
        {
            // Only the first parse registers: a re-parse of this request must not add it a second time.
            if (!isReplay)
            {
                _TimingTracker.AddRequest(_TimingKeyOf(in header, packet), packet.Id.Value, packet.Timestamp.AsNanos);
            }

            return default;
        }

        // A response needs a key for its record. Without one (the slice is not part of the packet's buffers) neither the first parse
        // nor a replay shows timing, and the request stays pending: the two parses stay identical.
        if (!parentField.TryGetEffectLayerKey(messageData, out messageKey))
        {
            messageKey = -1;
            return default;
        }

        if (isReplay)
        {
            return _TryGetEffect(packet.Id, messageKey, out SomeIpMessageEffect recorded)
                ? TimingFact.FromEffect(in recorded)
                : default;
        }

        TimingFact fact = _PairResponse(in header, packet);
        _RecordMessageEffect(
            packet.Id,
            messageKey,
            ref pending,
            new SomeIpMessageEffect(false, false, FragmentOutcome.NotRecorded, fact.RequestPacket, fact.DelayNanos, fact.Forgotten));
        return fact;
    }

    /// <summary>
    /// The analysis for a segment, called by the first parse of the segment before its effect is recorded: a request segment at
    /// offset 0 registers, the segment that completes a response pairs. The pair goes into the effect that the caller records.
    /// </summary>
    /// <param name="header">The header of the segment.</param>
    /// <param name="packet">The packet of the segment.</param>
    /// <param name="byteOffset">The byte offset of the segment in its message.</param>
    /// <param name="outcome">What the reassembler said about this segment.</param>
    private TimingFact _TimeSegment(in SomeIpHeader header, Packet packet, uint byteOffset, in FragmentOutcome outcome)
    {
        if (_TimingTracker is null || !_IsTimedType(in header, out bool request))
        {
            return default;
        }

        if (request)
        {
            if (byteOffset == 0)
            {
                _TimingTracker.AddRequest(_TimingKeyOf(in header, packet), packet.Id.Value, packet.Timestamp.AsNanos);
            }

            return default;
        }

        // Only the segment that completes the message pairs; the gap-free prefix of an incomplete message does not (the response is
        // not there yet), and the completing segment of that same message does, later.
        return outcome.Kind == FragmentOutcomeKind.Complete
            ? _PairResponse(in header, packet)
            : default;
    }

    private TimingFact _PairResponse(in SomeIpHeader header, Packet packet)
    {
        bool paired = _TimingTracker!.TryPairResponse(
            _TimingKeyOf(in header, packet), packet.Timestamp.AsNanos, out int requestPacket, out long delayNanos, out bool forgotten);
        if (!paired)
        {
            return new TimingFact(true, -1, 0, forgotten);
        }

        return new TimingFact(true, requestPacket, delayNanos, forgotten);
    }

    /// <summary>
    /// Whether a replayed segment shows timing: the same predicate the first parse used, evaluated on the recorded outcome.
    /// </summary>
    private bool _ShowsTimingOnReplay(in SomeIpHeader header, in FragmentOutcome outcome) =>
        _TimingTracker is not null
        && _IsTimedType(in header, out bool request)
        && !request
        && outcome.Kind == FragmentOutcomeKind.Complete;

    #endregion

    #region Tree

    /// <summary>
    /// Appends the lazy timing container to a response. Its value is the effect key of the message; the populator reads the pair
    /// from the published effect, lock-free and on any thread. The group is recorded with the container, so it is exact.
    /// </summary>
    private void _AppendTiming(in MutField container, int messageKey, in ParseContext context)
    {
        context.RecordGroupPresence(_SomeipTimingGroupId);
        _ = container.AppendLazyWithCustomText(
            _TimingFieldId, FieldValue.NewU64((ulong)messageKey), new LazyString("Method Timing"), _TimingPopulator);
    }

    /// <summary>Builds the children of a timing container from the pair that the first parse recorded.</summary>
    private ParseResult _PopulateTiming(in MutField timing)
    {
        if (!timing.Value.Data.TryGetAsU64(out ulong key) || !_TryGetEffect(timing.Packet.Id, (int)key, out SomeIpMessageEffect effect))
        {
            // Cannot happen for a container the parse appended; an empty container is the safe answer when it does.
            return 0;
        }

        if (effect.TimingRequestPacket >= 0)
        {
            _ = timing.Append(_TimingRequestInFieldId, FieldValue.NewU64((ulong)effect.TimingRequestPacket));
            _ = timing.AppendWithCustomText(
                _TimingDelayFieldId, FieldValue.NewF64(effect.TimingDelayNanos / 1_000_000_000.0), effect.TimingDelayNanos / 1_000_000_000.0, " s");
            return 0;
        }

        _ = timing.Append(_TimingRequestMissingFieldId, FieldValue.NewBool(true));
        if (effect.TimingPendingEvicted)
        {
            _ = timing.Append(
                _TimingErrorFieldId,
                FieldValue.NewString(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The request of this response was forgotten because more than {Settings.TimingMaxPending} requests were pending (setting someip.timing_max_pending).")));
        }

        return 0;
    }

    #endregion
}

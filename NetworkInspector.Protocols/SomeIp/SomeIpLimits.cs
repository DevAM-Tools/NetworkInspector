// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>The unit a limit is measured in; it decides how a limit hit is worded.</summary>
internal enum SomeIpLimitUnit : byte
{
    /// <summary>A plain count (messages, sessions, nodes).</summary>
    Count,

    /// <summary>A number of bytes.</summary>
    Bytes,
}

/// <summary>
/// The seven user-facing texts of a limit, in plain English for the end user of the setting (the same seven the limits
/// concept defines): what it limits, why it exists, what it protects, what a smaller value and a larger value do (with an
/// example), and what switching it off would do.
/// </summary>
/// <param name="Title">Short name of the setting; no trailing period.</param>
/// <param name="Description">What the limit bounds and when it matters.</param>
/// <param name="Reason">The threat the limit stops.</param>
/// <param name="Benefit">What the user gets from the limit.</param>
/// <param name="SmallerValues">What happens with a smaller value, with an example.</param>
/// <param name="LargerValues">What happens with a larger value, with an example.</param>
/// <param name="OffEffect">What switching the limit off would do.</param>
internal sealed record SomeIpLimitTexts(
    string Title,
    string Description,
    string Reason,
    string Benefit,
    string SmallerValues,
    string LargerValues,
    string OffEffect);

/// <summary>
/// One trust-boundary limit of the SOME/IP parser: its setting key, unit, default and allowed range, and the seven user
/// texts. The registration turns each definition into a <c>U64</c> setting with minimum and maximum and a description that
/// concatenates the texts; the parser reads the effective value from the setting.
/// <para>
/// This local type carries exactly the fields of the host's planned limit definition, so replacing it later is mechanical:
/// the texts and ranges carry over unchanged and only the registration call changes. Until that concept exists no limit can
/// be switched off, and the <see cref="SomeIpLimitTexts.OffEffect"/> text says so.
/// </para>
/// <para><b>Thread-safety:</b> immutable.</para>
/// </summary>
internal sealed class SomeIpLimitDefinition
{
    /// <summary>Creates a definition; a violated argument rule is a programming error and throws.</summary>
    /// <param name="key">The setting key, starting with <c>someip.</c>.</param>
    /// <param name="unit">The unit of the value.</param>
    /// <param name="defaultValue">The secure default.</param>
    /// <param name="minimum">The smallest allowed value.</param>
    /// <param name="maximum">The largest allowed value.</param>
    /// <param name="texts">The seven user texts.</param>
    /// <exception cref="ArgumentException">The key is empty or lacks the prefix, or <c>minimum &lt;= defaultValue &lt;= maximum</c> does not hold.</exception>
    public SomeIpLimitDefinition(string key, SomeIpLimitUnit unit, ulong defaultValue, ulong minimum, ulong maximum, SomeIpLimitTexts texts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(texts);
        if (!key.StartsWith("someip.", StringComparison.Ordinal))
        {
            throw new ArgumentException("A SOME/IP limit key starts with 'someip.'.", nameof(key));
        }

        if (minimum > defaultValue || defaultValue > maximum)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Limit '{key}': minimum <= default <= maximum must hold."), nameof(minimum));
        }

        Key = key;
        Unit = unit;
        Default = defaultValue;
        Minimum = minimum;
        Maximum = maximum;
        Texts = texts;
    }

    /// <summary>The setting key.</summary>
    public string Key { get; }

    /// <summary>The unit of the value.</summary>
    public SomeIpLimitUnit Unit { get; }

    /// <summary>The secure default.</summary>
    public ulong Default { get; }

    /// <summary>The smallest allowed value.</summary>
    public ulong Minimum { get; }

    /// <summary>The largest allowed value.</summary>
    public ulong Maximum { get; }

    /// <summary>The seven user texts.</summary>
    public SomeIpLimitTexts Texts { get; }

    /// <summary>The seven texts joined into the description of the <c>U64</c> setting.</summary>
    public string SettingDescription =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Texts.Description} Reason: {Texts.Reason} Benefit: {Texts.Benefit} Smaller values: {Texts.SmallerValues} Larger values: {Texts.LargerValues} Off: {Texts.OffEffect}");

    /// <summary>The sentence of a limit hit: what happened, the effective limit, and the setting that changes it.</summary>
    /// <param name="eventText">What happened, without a trailing period.</param>
    /// <param name="effective">The effective limit value.</param>
    public string HitText(string eventText, ulong effective)
    {
        string unit = Unit == SomeIpLimitUnit.Bytes
            ? " bytes"
            : "";
        return string.Create(CultureInfo.InvariantCulture, $"{eventText}. Limit: {effective}{unit} (setting {Key}).");
    }

    /// <summary>
    /// A load warning for a limit hit. Names the setting, so the user knows which value to raise; a limit hit is an expected
    /// failure and is reported, never dropped silently.
    /// </summary>
    /// <param name="measured">The measured value that exceeded the limit.</param>
    /// <param name="effective">The effective limit value.</param>
    public SettingsLoadWarning HitWarning(ulong measured, ulong effective)
    {
        string subject = Unit == SomeIpLimitUnit.Bytes
            ? "bytes"
            : "entries";
        string text = HitText(string.Create(CultureInfo.InvariantCulture, $"The SOME/IP configuration has {measured} {subject}, more than allowed"), effective);
        return new SettingsLoadWarning(SettingsLoadWarningKind.OutOfRange, "someip", Key, text);
    }
}

/// <summary>
/// The catalog of SOME/IP limits. Each definition records, at its declaration, the §4.2 facts: value or range, threat,
/// constant or configurable (where, who), and posture with the reason. Every limit here is a setting (every front end can
/// change it), secure by default: the data it bounds comes from captures and files that are untrusted.
/// <para>
/// Not limits, deliberately: the SOME/IP service-discovery option count (271) is a fact of the entry format (an 8-bit option
/// index plus a 4-bit count) and is reported, not dropped; the entries and options of one discovery message are bounded by the
/// payload size already limited by the TP and TCP limits; the learned-port set is a fact of the port space (65,536 bits).
/// </para>
/// <para><b>Thread-safety:</b> immutable static data.</para>
/// </summary>
internal static class SomeIpLimits
{
    private const ulong _KiB = 1024UL;
    private const ulong _MiB = 1024UL * 1024UL;
    private const ulong _GiB = 1024UL * 1024UL * 1024UL;

    private const string _OffEffect =
        "Not available yet: the limits concept adds an 'off' switch; until then the smallest allowed value is the strictest setting.";

    /// <summary>
    /// Messages decoded from one unit. Threat: a datagram of tiny messages makes one packet cost thousands of containers.
    /// Setting, every front end. Secure by default: real units carry a handful of messages; hostile ones are untrusted
    /// capture data. The default 100 has no recorded derivation beyond "far above any real bundle".
    /// </summary>
    public static SomeIpLimitDefinition MaxMessagesPerUnit { get; } = new(
        "someip.max_messages_per_unit",
        SomeIpLimitUnit.Count,
        100,
        1,
        10_000,
        new SomeIpLimitTexts(
            "Messages per SOME/IP unit",
            "The most SOME/IP messages decoded from one UDP datagram or one reassembled TCP message unit.",
            "A unit is untrusted capture data; a datagram made of tiny messages could make a single packet cost thousands of containers.",
            "A hostile or damaged packet cannot flood the field tree; the remaining bytes stay visible as unparsed data with a diagnostic.",
            "Fewer messages are decoded per unit; for example 10 shows only the first ten messages of a bundled datagram.",
            "More messages per unit are decoded; for example 1000 decodes bundles of up to 1000 messages, but one packet can then hold thousands of fields.",
            _OffEffect));

    /// <summary>
    /// Nesting depth of data types while decoding a payload. Threat: a deep (or self-referencing) type graph exhausts the
    /// stack. Setting, every front end. Secure by default: real graphs nest below 10; the description warns that high values
    /// can end the process.
    /// </summary>
    public static SomeIpLimitDefinition MaxTypeDepth { get; } = new(
        "someip.max_type_depth",
        SomeIpLimitUnit.Count,
        64,
        1,
        1_024,
        new SomeIpLimitTexts(
            "Payload type nesting depth",
            "How deeply structures, arrays, unions and aliases may nest while one payload is decoded.",
            "A deeply nested type graph in the configuration would exhaust the call stack of the thread that expands the payload.",
            "The deepest level is cut off with a named diagnostic instead of ending the process.",
            "Shallower nesting is decoded; for example 1 decodes only the top level of each parameter.",
            "Deeper nesting is decoded; for example 1024 follows very deep types, but a value near the maximum can end the process by stack exhaustion.",
            _OffEffect));

    /// <summary>
    /// Decoded nodes per message payload. Threat: one hostile array fills the packet's field tree (the host caps a packet at
    /// 65,533 fields) and starves every other protocol. Setting, every front end. Secure by default: leaves room for the
    /// other protocols of the packet; large arrays are rare and the user can raise it.
    /// </summary>
    public static SomeIpLimitDefinition MaxPayloadNodes { get; } = new(
        "someip.max_payload_nodes",
        SomeIpLimitUnit.Count,
        16_384,
        16,
        60_000,
        new SomeIpLimitTexts(
            "Decoded nodes per payload",
            "The most fields and array elements decoded from one SOME/IP payload.",
            "One hostile array could fill the whole field tree of a packet (the host stops a packet at 65,533 fields).",
            "The decoding stops with a named diagnostic, the rest of the payload is shown as unparsed bytes, and other protocols of the packet keep their room.",
            "Fewer nodes are decoded; for example 1000 stops large arrays after 1000 elements.",
            "More nodes are decoded; for example 60000 shows very large arrays completely but leaves almost no room for other fields of the packet.",
            _OffEffect));

    /// <summary>
    /// Open SOME/IP-TP sessions. Threat: sessions that never complete hold fragment copies forever. Setting, every front end.
    /// Secure by default: the least recently used session is evicted with a diagnostic.
    /// </summary>
    public static SomeIpLimitDefinition TpMaxSessions { get; } = new(
        "someip.tp_max_sessions",
        SomeIpLimitUnit.Count,
        1_024,
        1,
        1_000_000,
        new SomeIpLimitTexts(
            "SOME/IP-TP open sessions",
            "The most segmented messages that are being reassembled at the same time.",
            "Segmented messages whose last segment never arrives would hold their segments in memory for the whole capture.",
            "Memory stays bounded; the session that was used least recently is dropped with a named diagnostic.",
            "Fewer parallel transfers can be followed; for example 2 follows two transfers at once and drops the older one when a third starts.",
            "More parallel transfers can be followed; for example 100000 follows very many senders but lets abandoned transfers occupy memory longer.",
            _OffEffect));

    /// <summary>
    /// Size of one SOME/IP-TP message, measured as the extent (offset plus length) of a segment, so a single segment at a
    /// huge offset cannot request a huge buffer. Threat: memory by one claimed size. Setting, every front end. Secure by
    /// default: raised explicitly for huge messages.
    /// </summary>
    public static SomeIpLimitDefinition TpMaxMessageSize { get; } = new(
        "someip.tp_max_message_size",
        SomeIpLimitUnit.Bytes,
        10 * _MiB,
        _KiB,
        _GiB,
        new SomeIpLimitTexts(
            "SOME/IP-TP message size",
            "The largest segmented message that is reassembled, measured as the end of its furthest segment.",
            "A single forged segment could claim a position near 4 GiB and make the reassembly request that much memory.",
            "A segment beyond the limit is refused on that segment with a named diagnostic and no memory is requested for it.",
            "Smaller messages only; for example 64 KiB refuses every transfer larger than 64 KiB.",
            "Larger messages are reassembled; for example 1 GiB allows huge transfers but one session can then hold that much memory.",
            _OffEffect));

    /// <summary>
    /// Bytes held by all open SOME/IP-TP sessions together. Threat: sessions that each respect the per-message limit multiply
    /// (1,024 sessions of 10 MiB are 10 GiB). Setting, every front end. Secure by default: the least recently used sessions
    /// are evicted until the new segment fits; a capture with many parallel huge transfers raises it explicitly.
    /// </summary>
    public static SomeIpLimitDefinition TpMaxBufferedBytes { get; } = new(
        "someip.tp_max_buffered_bytes",
        SomeIpLimitUnit.Bytes,
        64 * _MiB,
        _MiB,
        16 * _GiB,
        new SomeIpLimitTexts(
            "SOME/IP-TP buffered bytes",
            "The total memory that all open segmented messages may hold together.",
            "The session count and the message size limit each look small, but they multiply: many parallel large transfers would exhaust memory while every other limit holds.",
            "Total memory stays bounded; the least recently used sessions are dropped with a named diagnostic until the new segment fits.",
            "Less memory is used; for example 8 MiB follows only a few small transfers at once.",
            "More memory may be used; for example 4 GiB follows many large transfers at once but the capture can then hold that much memory.",
            _OffEffect));

    /// <summary>
    /// Bytes bound to one packet in total when a reassembled SOME/IP-TP message is bound, all messages of the packet together and
    /// the bytes of other protocols included (the packet keeps one sum); the message size limit bounds one message only. Threat: the bound payloads stay referenced until the packet is recycled, and one
    /// datagram can hold many segments that each complete a message (up to the messages-per-unit limit, 100 by default), so the
    /// sum is bounded by nothing else per packet (100 messages of 10 MiB are 1 GiB). Setting, every front end, validated by the
    /// settings layer (2 KiB to 16 GiB; the value is rejected outside the range and the default stays). Posture: secure by
    /// default but relaxed on purpose (owner decision): the default equals the buffered-bytes default, because a packet can only
    /// complete messages whose segments the open sessions already hold (plus the bytes of the datagram itself), and those are
    /// bounded by that setting; so the default refuses nothing that the reassembler with its default limits could deliver, and
    /// the limit takes effect when an operator raises the buffered bytes or lowers this value. Exceeding it is an expected
    /// failure: the message that would pass the total is not bound or parsed (its TP fields stay), and the packet reports a
    /// parse error that names this setting, like the 255-buffer limit.
    /// Rejected: reusing the buffered-bytes setting (it bounds memory held across packets, a different quantity), no total.
    /// Residual risk: the message that fails the check was already reassembled (at most the message size limit, held by the
    /// session until it is delivered or evicted); a value near the maximum lets one packet pin that much memory.
    /// </summary>
    public static SomeIpLimitDefinition TpMaxDerivedBytesPerPacket { get; } = new(
        "someip.tp_max_derived_bytes_per_packet",
        SomeIpLimitUnit.Bytes,
        64 * _MiB,
        2 * _KiB,
        16 * _GiB,
        new SomeIpLimitTexts(
            "SOME/IP-TP reassembled bytes per packet",
            "The most bytes bound to one packet when a reassembled segmented message is bound, all bytes of the packet together (the bytes of other protocols count too).",
            "A packet that completes many large segmented messages keeps all of them in memory until it is recycled; "
            + "the message size limit alone does not bound that sum.",
            "A packet cannot pin more than this many reassembled bytes; the message that would pass it is not parsed further "
            + "and the packet reports an error that names this setting.",
            "Fewer bytes per packet; for example 1 MiB refuses a message that completes beyond 1 MiB together with the earlier messages of its packet.",
            "More bytes per packet; for example 4 GiB lets one packet that completes many huge messages keep all of them, using that much memory.",
            _OffEffect));

    /// <summary>
    /// Size of one SOME/IP message in a TCP stream. Threat: a hostile 32-bit length makes the stream buffer wait for and grow
    /// towards a size that never arrives. Setting, every front end. Secure by default: above real messages; the stream buffer
    /// takes the same value.
    /// </summary>
    public static SomeIpLimitDefinition TcpMaxMessageSize { get; } = new(
        "someip.tcp_max_message_size",
        SomeIpLimitUnit.Bytes,
        16 * _MiB,
        _KiB,
        _GiB,
        new SomeIpLimitTexts(
            "SOME/IP TCP message size",
            "The largest SOME/IP message that is framed out of a TCP stream.",
            "A forged length field of up to 4 GiB would make the stream buffer wait for, and reserve, a message that never arrives.",
            "A larger announcement is reported as a stream error instead of buffering; the stream resynchronizes if resynchronization is on.",
            "Smaller messages only; for example 64 KiB treats every larger announced message as an error.",
            "Larger messages are framed; for example 1 GiB allows huge messages but the stream buffer may then grow to that size.",
            _OffEffect));

    /// <summary>
    /// Services in a configuration file. Threat: registration time and memory grow with the file, and files come from
    /// generators and imported profiles. Setting, every front end. Secure by default; a hit refuses the whole file.
    /// </summary>
    public static SomeIpLimitDefinition ConfigMaxServices { get; } = new(
        "someip.config_max_services",
        SomeIpLimitUnit.Count,
        10_000,
        1,
        1_000_000,
        new SomeIpLimitTexts(
            "Services per configuration file",
            "The most service definitions accepted from one SOME/IP configuration file.",
            "Registration time and memory grow with the size of the file, and files may come from generators or from other people.",
            "A huge or hostile file is refused with a message that names this setting instead of stalling the start.",
            "Smaller files only; for example 100 refuses a file with 101 services.",
            "Larger files are accepted; for example 1000000 accepts huge fleets but starting the stack takes longer and needs more memory.",
            _OffEffect));

    /// <summary>
    /// Type definitions in a configuration file (counted over all services). Threat and posture as for the service count.
    /// </summary>
    public static SomeIpLimitDefinition ConfigMaxTypes { get; } = new(
        "someip.config_max_types",
        SomeIpLimitUnit.Count,
        1_000_000,
        1,
        100_000_000,
        new SomeIpLimitTexts(
            "Data types per configuration file",
            "The most data type definitions accepted from one SOME/IP configuration file, counted over all services.",
            "Registration time and memory grow with the number of types, and files may come from generators or from other people.",
            "A huge or hostile file is refused with a message that names this setting instead of stalling the start.",
            "Smaller files only; for example 1000 refuses a file with 1001 type definitions.",
            "Larger files are accepted; for example 100000000 accepts extreme files but starting the stack takes longer and needs more memory.",
            _OffEffect));

    /// <summary>
    /// Unanswered requests kept for method timing. Threat: requests that never get a response grow the table forever.
    /// Setting, every front end. Secure by default: far above any real round-trip window; the oldest request is forgotten
    /// with a diagnostic.
    /// </summary>
    public static SomeIpLimitDefinition TimingMaxPending { get; } = new(
        "someip.timing_max_pending",
        SomeIpLimitUnit.Count,
        65_536,
        1,
        10_000_000,
        new SomeIpLimitTexts(
            "Pending requests for method timing",
            "The most unanswered requests that method timing remembers while it waits for responses.",
            "Requests that never get a response would otherwise be kept for the whole capture.",
            "Memory stays bounded; the oldest unanswered request is forgotten and a response to it shows a named diagnostic.",
            "Fewer requests are remembered; for example 100 pairs only requests answered within the last hundred.",
            "More requests are remembered; for example 10000000 pairs very late responses but unanswered requests occupy memory longer.",
            _OffEffect));

    /// <summary>Every limit, in declaration order; the registration iterates it to create the settings.</summary>
    public static IReadOnlyList<SomeIpLimitDefinition> All { get; } =
    [
        MaxMessagesPerUnit,
        MaxTypeDepth,
        MaxPayloadNodes,
        TpMaxSessions,
        TpMaxMessageSize,
        TpMaxBufferedBytes,
        TpMaxDerivedBytesPerPacket,
        TcpMaxMessageSize,
        ConfigMaxServices,
        ConfigMaxTypes,
        TimingMaxPending,
    ];
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Limits;

/// <summary>
/// The trust-boundary limits of <c>NetworkInspector.Core</c>: stream and datagram reassembly, the depth of lazy field
/// expansion, the rows of a value cache, and the interfaces of a frame registry. Immutable static data; thread-safe.
/// </summary>
/// <remarks>
/// Every limit is a <see cref="LimitDefinition"/>. <see cref="StackBuilder"/> registers each as a setting (group
/// <c>reassembly</c>, <c>packet</c>, <c>valuecache</c>, or <c>frames</c>), so a user changes it with the settings file,
/// <c>ni settings set</c>, or <c>--set</c>; the built <see cref="Stack"/> hands the live holders to the owners as
/// <see cref="CoreLimitSet"/> (<see cref="Stack.Limits"/>). Every default is one fixed number sized for the reference machine of
/// 8 logical processors and 16 GB of memory; none is read from the machine. Every limit allows <c>off</c> (the user decides).
/// Each hit is reported with the name of the limit and never truncated silently.
/// <para>
/// Constants that stay constants because they are facts of a format or a storage type: the 65 534 fields of one packet (the
/// 16-bit storage index of <see cref="Packet"/>) and the 255 additional buffers of one packet (the 8-bit buffer index of an effect
/// key).
/// </para>
/// <para>Architecture: docs/architecture.md § Core. Sync: <c>custom_instructions.md</c> § Trust-boundary limits.</para>
/// </remarks>
/// <seealso cref="CoreLimitSet"/>
/// <seealso cref="CoreLimitPolicy"/>
public static class CoreLimits
{
    #region Stream reassembly

    /// <summary>The most bytes one direction of a stream buffers while it waits for a complete message.</summary>
    /// <remarks>
    /// Threat: a stream that never completes a message (hostile or damaged) holds its bytes in memory for the life of the
    /// connection. Configurable: by every front end. Posture: secure by default, because the bytes come from capture files and
    /// live traffic. Value: 1 MiB, 16 times the largest message of the protocols that keep the default (64 KiB); range 4 KiB
    /// to 1 GiB; <c>off</c> is allowed. Applies to every stream configuration that does not register a size of its own. A hit
    /// drops the buffered bytes of the message in progress and is reported, never truncated silently.
    /// </remarks>
    public static LimitDefinition MaxStreamBufferSize { get; } = new(
        "reassembly.maxStreamBufferSize",
        LimitUnits.Bytes,
        defaultValue: 1L << 20,
        minimum: 4L << 10,
        maximum: 1L << 30,
        new LimitTexts(
            Title: "Largest stream buffer",
            Description: "The most bytes one direction of a connection buffers while a message is incomplete.",
            Reason: "A stream that never completes a message would otherwise hold its bytes in memory without end.",
            Benefit: "A hostile or damaged stream is reported and costs a bounded amount of memory.",
            SmallerValues: "Larger messages are dropped: 64 KiB drops a message of 100 KiB.",
            LargerValues: "Larger messages are reassembled: 64 MiB keeps up to 64 MiB per direction of every open connection.",
            OffEffect: "A stream buffers without bound and a hostile capture can use all memory."));

    /// <summary>The largest message that a stream reassembles.</summary>
    /// <remarks>
    /// Threat: a boundary detector, driven by untrusted stream bytes, announces a huge message and the stream buffers toward it.
    /// Configurable: by every front end. Posture: secure by default. Value: 64 KiB, the largest DNS message over TCP (65 535
    /// bytes plus the 2-byte length prefix, RFC 1035 § 4.2.2); range 1 KiB to 1 GiB; <c>off</c> is allowed. Applies to every
    /// stream configuration that does not register a size of its own (protocols with larger units register theirs). A hit moves
    /// the stream to its error state and is reported.
    /// </remarks>
    public static LimitDefinition MaxStreamPduSize { get; } = new(
        "reassembly.maxStreamPduSize",
        LimitUnits.Bytes,
        defaultValue: 64L << 10,
        minimum: 1L << 10,
        maximum: 1L << 30,
        new LimitTexts(
            Title: "Largest stream message",
            Description: "The largest single message that a stream reassembles from its segments.",
            Reason: "A hostile length announcement would otherwise make the stream buffer toward a message that never completes.",
            Benefit: "An oversized announcement is reported at once and the stream recovers at the next message.",
            SmallerValues: "Larger messages are refused: 16 KiB refuses a DNS answer of 20 KiB.",
            LargerValues: "Larger messages are reassembled: 16 MiB accepts large HTTP bodies but buffers them in memory.",
            OffEffect: "Any announced message size is accepted and the buffer limit is the only bound."));

    /// <summary>The most times one stream direction rebuilds its buffer because a segment starts before the first byte seen.</summary>
    /// <remarks>
    /// Threat: each rebuild copies the whole buffer, and the sender chooses how often a segment arrives before the origin, so
    /// descending segments cost quadratic work. Configurable: by every front end. Posture: secure by default. Value: 64, more
    /// than 10 times the 0 to 3 rebuilds of an ordinary capture; range 1 to 65,536; <c>off</c> is allowed. A hit refuses the
    /// segment, names the limit, and moves the stream to its error state.
    /// </remarks>
    public static LimitDefinition MaxStreamRebases { get; } = new(
        "reassembly.maxStreamRebases",
        LimitUnits.Count,
        defaultValue: 64,
        minimum: 1,
        maximum: 65_536,
        new LimitTexts(
            Title: "Most stream rebuilds",
            Description: "How many times one direction of a stream may rebuild its buffer for a segment that starts before the first byte seen.",
            Reason: "Each rebuild copies the whole buffer, so a crafted order of segments would otherwise keep the process busy for hours.",
            Benefit: "A crafted capture is refused with the name of this limit instead of stalling the parse.",
            SmallerValues: "Streams that start with heavily reordered segments are refused earlier: 4 refuses a start with 5 early segments.",
            LargerValues: "Streams with extreme reordering are accepted: 1024 allows 1024 copies of the buffer, up to 16 MiB each.",
            OffEffect: "Any number of rebuilds is allowed and a crafted capture can stall the parse thread."));

    #endregion

    #region Datagram reassembly

    /// <summary>The most datagrams that are open at the same time in one fragment reassembler.</summary>
    /// <remarks>
    /// Threat: datagrams whose remaining fragments never arrive hold pooled copies of their fragments for ever. Configurable: by
    /// every front end. Posture: secure by default. Value: 1,024, more than 10 times the datagrams in flight on a busy link in
    /// one reassembly timeout; range 1 to 1,048,576; <c>off</c> is allowed. At the limit the oldest datagram is evicted and
    /// counted.
    /// </remarks>
    public static LimitDefinition MaxPendingDatagrams { get; } = new(
        "reassembly.maxPendingDatagrams",
        LimitUnits.Count,
        defaultValue: 1_024,
        minimum: 1,
        maximum: 1_048_576,
        new LimitTexts(
            Title: "Most open datagrams",
            Description: "The most fragmented datagrams that a reassembler keeps open while it waits for their remaining fragments.",
            Reason: "Datagrams whose fragments never complete would otherwise hold memory for ever.",
            Benefit: "A fragment flood evicts the oldest datagrams and is counted instead of growing without bound.",
            SmallerValues: "Datagrams are evicted earlier: 16 evicts a datagram when 17 are open.",
            LargerValues: "More datagrams stay open: 65536 keeps 65536 datagrams and needs memory for their fragments.",
            OffEffect: "Any number of datagrams stays open and a fragment flood can use all memory."));

    /// <summary>The most stored fragment bytes over all open datagrams of one reassembler together.</summary>
    /// <remarks>
    /// Threat: the datagram count and the length of one datagram each look small, but they multiply (1,024 datagrams of 64 KiB
    /// are 64 MiB; overlapping fragments are counted twice under one overlap policy). Configurable: by every front end.
    /// Posture: secure by default. Value: 64 MiB, the product of the 1,024 open datagrams and the 65,535 bytes of an IP
    /// datagram; range 1 MiB to 1 TiB; <c>off</c> is allowed. At the limit the oldest datagram is evicted and counted; a
    /// fragment that cannot fit is refused and named.
    /// </remarks>
    public static LimitDefinition MaxFragmentStorage { get; } = new(
        "reassembly.maxFragmentStorage",
        LimitUnits.Bytes,
        defaultValue: 64L << 20,
        minimum: 1L << 20,
        maximum: 1L << 40,
        new LimitTexts(
            Title: "Most fragment bytes",
            Description: "The most bytes of stored fragments over all open datagrams of one reassembler together.",
            Reason: "Open datagrams of many fragments could together hold far more memory than any one of them suggests.",
            Benefit: "The memory of reassembly has a bound that does not depend on the capture.",
            SmallerValues: "Datagrams are evicted earlier: 8 MiB evicts the oldest datagram once 8 MiB of fragments are stored.",
            LargerValues: "More fragments are kept: 1 GiB lets 1 GiB of fragments wait for their datagrams.",
            OffEffect: "Stored fragments are not bounded by bytes and a hostile capture can use all memory."));

    /// <summary>The most fragments that one open datagram stores.</summary>
    /// <remarks>
    /// Threat: every insert scans and shifts the sorted fragment list, so a datagram of very many tiny fragments makes each
    /// fragment cost more. Configurable: by every front end. Posture: secure by default. Value: 8,192, the most fragments of
    /// an IP datagram (65,536 bytes in units of 8); range 1 to 1,000,000; <c>off</c> is allowed. A hit refuses the fragment and
    /// names the limit.
    /// </remarks>
    public static LimitDefinition MaxFragmentsPerDatagram { get; } = new(
        "reassembly.maxFragmentsPerDatagram",
        LimitUnits.Count,
        defaultValue: 8_192,
        minimum: 1,
        maximum: 1_000_000,
        new LimitTexts(
            Title: "Most fragments per datagram",
            Description: "The most fragments that one open datagram stores.",
            Reason: "A datagram of very many tiny fragments makes every further fragment more expensive to place.",
            Benefit: "The cost of placing a fragment stays bounded.",
            SmallerValues: "Datagrams of many fragments are refused: 64 refuses a datagram of 65 fragments.",
            LargerValues: "More fragments are stored: 100000 allows 100000 fragments per datagram but each insert then shifts a longer list.",
            OffEffect: "A datagram may hold any number of fragments and a hostile sender can make every insert slow."));

    #endregion

    #region Packet, value cache, and frame registry

    /// <summary>The deepest nesting of lazy fields that one request expands.</summary>
    /// <remarks>
    /// Threat: lazy populators that register lazy fields in turn recurse; an unbounded depth exhausts the stack. Configurable:
    /// by every front end. Posture: secure by default. Value: 128, more than 10 times the nesting of any real protocol stack;
    /// range 4 to 1,024; <c>off</c> is allowed. A hit stops the expansion, records a packet error that names the limit, and the
    /// packet stays readable.
    /// </remarks>
    public static LimitDefinition MaxLazyDepth { get; } = new(
        "packet.maxLazyDepth",
        LimitUnits.Count,
        defaultValue: 128,
        minimum: 4,
        maximum: 1_024,
        new LimitTexts(
            Title: "Deepest lazy nesting",
            Description: "The deepest nesting of lazily expanded fields that one read of a packet follows.",
            Reason: "Lazy fields that contain lazy fields recurse, and unbounded recursion would end the process by exhausting the stack.",
            Benefit: "A crafted packet gets an error field that names this limit instead of ending the process.",
            SmallerValues: "Deeper nesting is cut: 8 stops the expansion at the ninth level.",
            LargerValues: "Deeper nesting is followed: 1000 follows 1000 levels but needs more stack.",
            OffEffect: "Any nesting is followed and a crafted packet can end the process by exhausting the stack."));

    /// <summary>The most recorded field occurrences that one value cache keeps.</summary>
    /// <remarks>
    /// Threat: a cache with <c>RecordAllFields</c> on an untrusted capture grows with every field of every packet. Configurable:
    /// by every front end. Posture: secure by default. Value: 100,000,000 occurrences, which keeps a 16 GB machine below half of
    /// its memory at about 40 bytes per occurrence; range 1 to 2^40; <c>off</c> is allowed. At the limit the cache stops
    /// recording, keeps what it holds, and sets <see cref="ValueCache.IsRecordingIncomplete"/> for good; the hit text is
    /// <see cref="ValueCache.RecordingLimitMessage"/>.
    /// </remarks>
    public static LimitDefinition MaxValueCacheRows { get; } = new(
        "valuecache.maxRows",
        LimitUnits.Count,
        defaultValue: 100_000_000,
        minimum: 1,
        maximum: 1L << 40,
        new LimitTexts(
            Title: "Most value cache rows",
            Description: "The most recorded field occurrences that one value cache keeps; later occurrences are not recorded.",
            Reason: "A cache that records every field of every packet of an untrusted capture would otherwise use all memory.",
            Benefit: "The cache stops at a known size, says that it is incomplete, and the rest of the application keeps running.",
            SmallerValues: "The cache is full earlier: 1000000 stops recording after one million occurrences.",
            LargerValues: "The cache holds more: 1000000000 keeps a billion occurrences and needs tens of gigabytes.",
            OffEffect: "The cache grows with the capture and a large capture can use all memory."));

    /// <summary>The most interfaces that one frame interface registry registers.</summary>
    /// <remarks>
    /// Threat: the interfaces come from capture files; a file of nothing but interface blocks registers as many as it can hold,
    /// and a registry that outlives its sessions keeps every one reachable. Configurable: by every front end. Posture: secure by
    /// default. Value: 1,048,576, sixteen full pcapng sections of 65,536 interfaces; range 1 to the largest id; <c>off</c> is
    /// allowed (the id range still bounds it). A hit refuses the registration with an exception that names the limit.
    /// </remarks>
    public static LimitDefinition MaxInterfaces { get; } = new(
        "frames.maxInterfaces",
        LimitUnits.Count,
        defaultValue: 1_048_576,
        minimum: 1,
        maximum: Array.MaxLength - 1,
        new LimitTexts(
            Title: "Most capture interfaces",
            Description: "The most capture interfaces that one registry registers while captures are read.",
            Reason: "A capture file that consists of interface descriptions would otherwise register without end.",
            Benefit: "A crafted file is refused with the name of this limit instead of filling the registry.",
            SmallerValues: "Captures with many interfaces are refused: 16 refuses the seventeenth interface.",
            LargerValues: "More interfaces are accepted: 100000000 accepts a hundred million interfaces and needs memory for each.",
            OffEffect: "Interfaces are bounded only by the id range and a crafted file can use all memory."));

    #endregion

    #region Catalog

    /// <summary>Every limit of Core, for <see cref="LimitCatalog.Check"/> and for a front end that lists them.</summary>
    public static IReadOnlyList<LimitDefinition> All { get; } =
    [
        MaxStreamBufferSize,
        MaxStreamPduSize,
        MaxStreamRebases,
        MaxPendingDatagrams,
        MaxFragmentStorage,
        MaxFragmentsPerDatagram,
        MaxLazyDepth,
        MaxValueCacheRows,
        MaxInterfaces,
    ];

    #endregion
}

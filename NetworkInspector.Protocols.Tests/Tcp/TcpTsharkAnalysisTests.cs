// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Cross-validates the TCP sequence analysis (anomaly flags, relative numbers, window values, round trips, conversation
/// completeness) against an independent dissector, packet by packet, on whole conversations.
/// </summary>
/// <remarks>
/// <para>
/// Why a golden comparison: the unit tests of the analysis were written by the same author as the analysis, so they pin
/// what the code does, not what the rules say. A conversation that both implementations dissect is the only check that
/// catches a rule that is wrong in the same way in code and test. Each scenario below builds one conversation with the
/// frame builder, dissects it with Network Inspector (frame <c>i</c> at its own capture time) and with tshark (same
/// capture times in a temporary pcap), and compares every listed field of every packet.
/// </para>
/// <para>
/// How fields are compared: anomaly flags are compared by presence (they carry no value), counters and numbers by
/// value, durations with a tolerance of one microsecond (the dissectors print different numbers of digits). A mismatch
/// is reported with the scenario, the frame number and both values, all mismatches at once.
/// </para>
/// <para>
/// Known, documented differences are not hidden: <c>tcp.reassembled_in</c> and the final (two-pass) completeness value
/// need a later packet and are not reproduced, so they are not compared. tshark is required (no skip): the test fails
/// when it is missing, unless the developer escape hatch of the verifier is set.
/// </para>
/// <para>Thread safety: every test owns its stack and its frames.</para>
/// </remarks>
internal sealed class TcpTsharkAnalysisTests
{
    #region Conversation description

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);

    // All capture times are shifted by 100 s. A SYN stamped exactly at the epoch is read as "no SYN seen yet" by the
    // reference dissector (it uses a zero time as its unset marker), which switches the initial round trip off.
    private const long _BaseMicros = 100_000_000;

    // Ports without a registered application protocol on either side, so only the TCP layer is under test.
    private const ushort _ClientPort = 40001;
    private const ushort _ServerPort = 40002;

    /// <summary>One segment of a scenario.</summary>
    /// <param name="Ms">Capture time in milliseconds.</param>
    /// <param name="FromClient">Direction of the segment.</param>
    /// <param name="Seq">Wire sequence number.</param>
    /// <param name="Ack">Wire acknowledgement number.</param>
    /// <param name="Flags">TCP flags byte.</param>
    /// <param name="Length">Payload length (zero bytes).</param>
    /// <param name="Window">Advertised window.</param>
    /// <param name="Scale">Window scale shift to put into the options of a SYN, or null for no option.</param>
    private readonly record struct Seg(
        long Ms, bool FromClient, uint Seq, uint Ack, byte Flags, int Length = 0, ushort Window = 65535, byte? Scale = null);

    private static byte[] _Build(Seg segment)
    {
        EthernetLayer eth = segment.FromClient
            ? new(_DstMac, _SrcMac)
            : new(_SrcMac, _DstMac);
        IPv4Layer ip = segment.FromClient
            ? new(_ClientIp, _ServerIp)
            : new(_ServerIp, _ClientIp);
        ushort src = segment.FromClient
            ? _ClientPort
            : _ServerPort;
        ushort dst = segment.FromClient
            ? _ServerPort
            : _ClientPort;
        byte[] payload = new byte[segment.Length];

        if (segment.Scale is byte shift)
        {
            TcpOptionsBuilder options = default;
            options.WindowScale(shift);
            TcpLayerWithOptions withOptions = new(
                src, dst, options.Build(), seqNum: segment.Seq, ackNum: segment.Ack, flags: segment.Flags, windowSize: segment.Window);
            return FrameStack.Start(eth).Then(ip).Then(withOptions).CreateWithFixedValues().EmitFrame(payload);
        }

        TcpLayer tcp = new(src, dst, seqNum: segment.Seq, ackNum: segment.Ack, flags: segment.Flags, windowSize: segment.Window);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(payload);
    }

    // Handshake with a 10 ms round trip in each half: SYN at t, SYN-ACK at t+10, ACK at t+20 (initial round trip 20 ms).
    private static List<Seg> _Handshake(long t, uint clientIsn = 1000, uint serverIsn = 2000, byte? clientScale = null, byte? serverScale = null) =>
    [
        new(t, true, clientIsn, 0, TcpFlags.Syn, Scale: clientScale),
        new(t + 10, false, serverIsn, clientIsn + 1, TcpFlags.SynAck, Scale: serverScale),
        new(t + 20, true, clientIsn + 1, serverIsn + 1, TcpFlags.Ack),
    ];

    #endregion

    #region Compared fields

    // Fields without a value: compared by presence.
    private static readonly string[] _PresenceFields =
    [
        "tcp.analysis.flags", "tcp.analysis.retransmission", "tcp.analysis.fast_retransmission",
        "tcp.analysis.spurious_retransmission", "tcp.analysis.out_of_order", "tcp.analysis.lost_segment",
        "tcp.analysis.keep_alive", "tcp.analysis.keep_alive_ack", "tcp.analysis.duplicate_ack",
        "tcp.analysis.zero_window", "tcp.analysis.zero_window_probe", "tcp.analysis.zero_window_probe_ack",
        "tcp.analysis.window_update", "tcp.analysis.window_full", "tcp.analysis.reused_ports",
        "tcp.analysis.ack_lost_segment", "tcp.analysis.partial_ack", "tcp.analysis.ambiguous_ack",
    ];

    // Whole numbers: compared by value (frame numbers count from 1 on both sides).
    private static readonly string[] _NumberFields =
    [
        "tcp.stream", "tcp.seq", "tcp.ack", "tcp.nxtseq", "tcp.len", "tcp.window_size", "tcp.window_size_scalefactor", "tcp.completeness",
        "tcp.analysis.duplicate_ack_num", "tcp.analysis.duplicate_ack_frame", "tcp.analysis.acks_frame",
        "tcp.analysis.rto_frame", "tcp.analysis.bytes_in_flight",
    ];

    // Durations in seconds: compared with a tolerance of one microsecond.
    private static readonly string[] _SecondsFields =
    [
        "tcp.analysis.initial_rtt", "tcp.analysis.ack_rtt", "tcp.analysis.rto",
    ];

    #endregion

    #region Comparison

    /// <summary>
    /// Dissects the scenario with both implementations and asserts that every compared field matches on every packet.
    /// </summary>
    private static async Task _AssertMatchesTshark(string scenario, List<Seg> segments, string[]? extraNumberFields = null)
    {
        TsharkVerifier.RequireAvailable();

        List<byte[]> frames = [.. segments.Select(_Build)];
        List<long> micros = [.. segments.Select(s => _BaseMicros + (s.Ms * 1000))];
        string[] numberFields = extraNumberFields is null
            ? _NumberFields
            : [.. _NumberFields, .. extraNumberFields];

        // Debugging aid: with NI_TSHARK_DUMP_DIR set, the reference dissector's full TCP tree of the conversation is written to
        // <dir>/<scenario>.txt, so a mismatch can be read next to the rule that produced it.
        string? dumpDirectory = Environment.GetEnvironmentVariable("NI_TSHARK_DUMP_DIR");
        if (!string.IsNullOrEmpty(dumpDirectory))
        {
            string tree = TsharkVerifier.RunOnTimedFrames(frames, micros, "-V -O tcp");
            await File.WriteAllTextAsync(Path.Combine(dumpDirectory, $"{scenario}.txt"), tree).ConfigureAwait(false);
        }

        // tshark: one invocation for the whole conversation. JSON keeps the presence of valueless fields, which the tab
        // separated output cannot show. A field is present in a packet exactly when its key exists in the layers object.
        string fieldArguments = string.Join(" ", _PresenceFields.Concat(numberFields).Concat(_SecondsFields).Select(f => $"-e {f}"));
        string json = TsharkVerifier.RunOnTimedFrames(frames, micros, $"-T json {fieldArguments}");
        List<Dictionary<string, string>> tshark = _ParseTsharkJson(json);
        await Assert.That(tshark.Count).IsEqualTo(frames.Count).Because($"{scenario}: tshark packet count");

        // Network Inspector: the same frames at the same times, in order, on one stack (the analysis is stateful).
        using Stack stack = ProtocolTestHelper.BuildStack();
        List<string> mismatches = [];
        for (int i = 0; i < frames.Count; i++)
        {
            Packet packet = ProtocolTestHelper.ParseFrame(stack, frames[i], i, Timestamp.FromNanos(micros[i] * 1000));
            _CompareFrame(scenario, i, stack, packet, tshark[i], numberFields, mismatches);
        }

        await Assert.That(mismatches).IsEmpty().Because(Environment.NewLine + string.Join(Environment.NewLine, mismatches));
    }

    private static void _CompareFrame(
        string scenario,
        int index,
        Stack stack,
        Packet packet,
        Dictionary<string, string> tshark,
        string[] numberFields,
        List<string> mismatches)
    {
        foreach (string field in _PresenceFields)
        {
            bool ours = _Ni(stack, packet, field) is not null;
            bool theirs = tshark.ContainsKey(field);
            if (ours != theirs)
            {
                mismatches.Add($"{scenario} frame {index + 1}: {field} ni={(ours ? "present" : "absent")} tshark={(theirs ? "present" : "absent")}");
            }
        }

        foreach (string field in numberFields)
        {
            string? ours = _Ni(stack, packet, field);
            tshark.TryGetValue(field, out string? theirs);
            if (!_SameNumber(ours, theirs))
            {
                mismatches.Add($"{scenario} frame {index + 1}: {field} ni={ours ?? "absent"} tshark={theirs ?? "absent"}");
            }
        }

        foreach (string field in _SecondsFields)
        {
            string? ours = _Ni(stack, packet, field);
            tshark.TryGetValue(field, out string? theirs);
            if (!_SameSeconds(ours, theirs))
            {
                mismatches.Add($"{scenario} frame {index + 1}: {field} ni={ours ?? "absent"} tshark={theirs ?? "absent"}");
            }
        }
    }

    private static string? _Ni(Stack stack, Packet packet, string field)
    {
        FieldId? id = stack.GetFieldId(field);
        if (id is null || !packet.TryGetFieldValue(id.Value, out FieldValue value, materialize: true))
        {
            return null;
        }

        return value.ToString();
    }

    private static bool _SameNumber(string? ours, string? theirs)
    {
        if (ours is null || theirs is null)
        {
            return ours is null && theirs is null;
        }

        return long.TryParse(ours, NumberStyles.Integer, CultureInfo.InvariantCulture, out long a)
            && long.TryParse(theirs, NumberStyles.Integer, CultureInfo.InvariantCulture, out long b)
            && a == b;
    }

    private static bool _SameSeconds(string? ours, string? theirs)
    {
        if (ours is null || theirs is null)
        {
            return ours is null && theirs is null;
        }

        return double.TryParse(ours, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
            && double.TryParse(theirs, NumberStyles.Float, CultureInfo.InvariantCulture, out double b)
            && Math.Abs(a - b) < 1e-6;
    }

    /// <summary>Reads the per-packet field map out of tshark's JSON output (first occurrence of a repeated field).</summary>
    private static List<Dictionary<string, string>> _ParseTsharkJson(string json)
    {
        List<Dictionary<string, string>> packets = [];
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonElement packet in document.RootElement.EnumerateArray())
        {
            Dictionary<string, string> fields = new(StringComparer.Ordinal);
            JsonElement layers = packet.GetProperty("_source").GetProperty("layers");
            foreach (JsonProperty property in layers.EnumerateObject())
            {
                JsonElement value = property.Value;
                if (value.ValueKind == JsonValueKind.Array)
                {
                    fields[property.Name] = value.GetArrayLength() > 0
                        ? value[0].GetString() ?? string.Empty
                        : string.Empty;
                }
                else
                {
                    fields[property.Name] = value.GetString() ?? string.Empty;
                }
            }

            packets.Add(fields);
        }

        return packets;
    }

    #endregion

    #region Scenarios

    /// <summary>A, C, B arrive in that order: C is a lost segment (gap), the late B is out-of-order, ACKs refer to the data.</summary>
    [Test]
    public async Task ReorderedSegmentsMatchTshark()
    {
        List<Seg> s = _Handshake(0);
        s.Add(new(22, true, 1001, 2001, TcpFlags.PshAck, 100));
        s.Add(new(23, true, 1201, 2001, TcpFlags.PshAck, 100));
        s.Add(new(24, true, 1101, 2001, TcpFlags.PshAck, 100));
        s.Add(new(30, false, 2001, 1301, TcpFlags.Ack));
        await _AssertMatchesTshark("reorder", s).ConfigureAwait(false);
    }

    /// <summary>The same data again 200 ms later is a retransmission with its timeout; one fully acknowledged is spurious.</summary>
    [Test]
    public async Task RetransmissionAndSpuriousRetransmissionMatchTshark()
    {
        List<Seg> s = _Handshake(0);
        s.Add(new(30, true, 1001, 2001, TcpFlags.PshAck, 100));
        s.Add(new(250, true, 1001, 2001, TcpFlags.PshAck, 100));
        s.Add(new(260, false, 2001, 1101, TcpFlags.Ack));
        s.Add(new(500, true, 1001, 2001, TcpFlags.PshAck, 100));
        await _AssertMatchesTshark("retransmission", s).ConfigureAwait(false);
    }

    /// <summary>A hole, three duplicate ACKs, then the retransmission of the hole within 20 ms: a fast retransmission.</summary>
    [Test]
    public async Task DuplicateAcksAndFastRetransmissionMatchTshark()
    {
        List<Seg> s = _Handshake(0);
        s.Add(new(22, true, 1001, 2001, TcpFlags.PshAck, 100));
        s.Add(new(23, false, 2001, 1101, TcpFlags.Ack));
        s.Add(new(24, true, 1201, 2001, TcpFlags.PshAck, 100));
        s.Add(new(25, false, 2001, 1101, TcpFlags.Ack));
        s.Add(new(26, true, 1301, 2001, TcpFlags.PshAck, 100));
        s.Add(new(27, false, 2001, 1101, TcpFlags.Ack));
        s.Add(new(28, true, 1401, 2001, TcpFlags.PshAck, 100));
        s.Add(new(29, false, 2001, 1101, TcpFlags.Ack));
        s.Add(new(30, true, 1101, 2001, TcpFlags.PshAck, 100));
        s.Add(new(31, false, 2001, 1501, TcpFlags.Ack));
        await _AssertMatchesTshark("dupack", s).ConfigureAwait(false);
    }

    /// <summary>A zero window, probes, probe ACKs and the re-opening update.</summary>
    [Test]
    public async Task ZeroWindowProbeSequenceMatchesTshark()
    {
        List<Seg> s = _Handshake(0);
        s.Add(new(22, true, 1001, 2001, TcpFlags.PshAck, 100));
        s.Add(new(23, false, 2001, 1101, TcpFlags.Ack, Window: 0));
        s.Add(new(30, true, 1101, 2001, TcpFlags.Ack, 1));
        s.Add(new(31, false, 2001, 1101, TcpFlags.Ack, Window: 0));
        s.Add(new(40, true, 1101, 2001, TcpFlags.Ack, 1));
        s.Add(new(41, false, 2001, 1101, TcpFlags.Ack, Window: 0));
        s.Add(new(50, false, 2001, 1102, TcpFlags.Ack, Window: 65535));
        s.Add(new(51, true, 1102, 2001, TcpFlags.PshAck, 100));
        await _AssertMatchesTshark("zerowindow", s).ConfigureAwait(false);
    }

    /// <summary>A connection that is closed, then opened again on the same address and port pair.</summary>
    [Test]
    public async Task PortReuseStartsANewStreamLikeTshark()
    {
        List<Seg> s = _Handshake(0);
        s.Add(new(30, true, 1001, 2001, TcpFlags.PshAck, 100));
        s.Add(new(31, false, 2001, 1101, TcpFlags.Ack));
        s.Add(new(40, true, 1101, 2001, TcpFlags.FinAck));
        s.Add(new(41, false, 2001, 1102, TcpFlags.FinAck));
        s.Add(new(42, true, 1102, 2002, TcpFlags.Ack));
        s.AddRange(_Handshake(100, clientIsn: 5000, serverIsn: 6000));
        s.Add(new(130, true, 5001, 6001, TcpFlags.PshAck, 50));
        s.Add(new(131, false, 6001, 5051, TcpFlags.Ack));
        await _AssertMatchesTshark("reuse", s).ConfigureAwait(false);
    }

    /// <summary>A retransmitted SYN with the same sequence number before any close stays in the old stream.</summary>
    [Test]
    public async Task RetransmittedSynStaysInTheStreamLikeTshark()
    {
        List<Seg> s =
        [
            new(0, true, 1000, 0, TcpFlags.Syn),
            new(3000, true, 1000, 0, TcpFlags.Syn),
            new(3010, false, 2000, 1001, TcpFlags.SynAck),
            new(3020, true, 1001, 2001, TcpFlags.Ack),
        ];
        await _AssertMatchesTshark("synretransmit", s).ConfigureAwait(false);
    }

    /// <summary>A keep-alive probe one byte before the next sequence number and its acknowledgement.</summary>
    [Test]
    public async Task KeepAliveAndKeepAliveAckMatchTshark()
    {
        List<Seg> s = _Handshake(0);
        s.Add(new(22, true, 1001, 2001, TcpFlags.PshAck, 100));
        s.Add(new(23, false, 2001, 1101, TcpFlags.Ack));
        s.Add(new(1000, true, 1100, 2001, TcpFlags.Ack, 1));
        s.Add(new(1001, false, 2001, 1101, TcpFlags.Ack));
        await _AssertMatchesTshark("keepalive", s).ConfigureAwait(false);
    }

    /// <summary>A capture that starts in the middle of a connection: no SYN, so unknown scale, acknowledged unseen data.</summary>
    [Test]
    public async Task MidStreamCaptureMatchesTshark()
    {
        List<Seg> s =
        [
            new(0, false, 5000, 1000, TcpFlags.PshAck, 100),
            new(1, true, 1000, 5100, TcpFlags.Ack),
            new(2, false, 5100, 1000, TcpFlags.PshAck, 100),
            new(3, true, 1000, 5200, TcpFlags.Ack),
        ];
        await _AssertMatchesTshark("midstream", s).ConfigureAwait(false);
    }

    /// <summary>Scaled windows in both directions and a segment that reaches the right edge of the receiver's window.</summary>
    [Test]
    public async Task ScaledWindowAndWindowFullMatchTshark()
    {
        List<Seg> s = _Handshake(0, clientScale: 2, serverScale: 2);

        // The server advertises 100 (unscaled) = 400 bytes. 100 + 100 + 100 + 100 bytes fill the window exactly.
        s.Add(new(22, false, 2001, 1001, TcpFlags.Ack, Window: 100));
        s.Add(new(23, true, 1001, 2001, TcpFlags.PshAck, 100));
        s.Add(new(24, true, 1101, 2001, TcpFlags.PshAck, 100));
        s.Add(new(25, true, 1201, 2001, TcpFlags.PshAck, 100));
        s.Add(new(26, true, 1301, 2001, TcpFlags.PshAck, 100));
        s.Add(new(30, false, 2001, 1401, TcpFlags.Ack, Window: 100));
        await _AssertMatchesTshark("windowfull", s).ConfigureAwait(false);
    }

    /// <summary>Scaling is only used when both sides offer it: the server's SYN-ACK without the option switches it off.</summary>
    [Test]
    public async Task OneSidedWindowScaleIsNotUsedLikeTshark()
    {
        List<Seg> s = _Handshake(0, clientScale: 7, serverScale: null);
        s.Add(new(22, true, 1001, 2001, TcpFlags.PshAck, 10, Window: 100));
        s.Add(new(23, false, 2001, 1011, TcpFlags.Ack, Window: 100));
        await _AssertMatchesTshark("onesidedscale", s).ConfigureAwait(false);
    }

    /// <summary>The close sequence with a retransmitted FIN: completeness bits and sequence numbers.</summary>
    [Test]
    public async Task CloseSequenceWithRetransmittedFinMatchesTshark()
    {
        List<Seg> s = _Handshake(0);
        s.Add(new(30, true, 1001, 2001, TcpFlags.PshAck, 100));
        s.Add(new(31, false, 2001, 1101, TcpFlags.Ack));
        s.Add(new(40, true, 1101, 2001, TcpFlags.FinAck));
        s.Add(new(250, true, 1101, 2001, TcpFlags.FinAck));
        s.Add(new(251, false, 2001, 1102, TcpFlags.Ack));
        s.Add(new(260, false, 2001, 1102, TcpFlags.FinAck));
        s.Add(new(261, true, 1102, 2002, TcpFlags.Ack));
        await _AssertMatchesTshark("close", s).ConfigureAwait(false);
    }

    /// <summary>A sequence number space that wraps through zero: no anomaly may be reported across the wrap.</summary>
    [Test]
    public async Task SequenceNumberWrapMatchesTshark()
    {
        List<Seg> s = _Handshake(0, clientIsn: 0xFFFFFF00, serverIsn: 7);
        s.Add(new(22, true, 0xFFFFFF01, 8, TcpFlags.PshAck, 100));
        s.Add(new(23, true, 0xFFFFFF65, 8, TcpFlags.PshAck, 100));
        s.Add(new(24, true, 0xFFFFFFC9, 8, TcpFlags.PshAck, 100));
        s.Add(new(25, true, 0x0000002D, 8, TcpFlags.PshAck, 100));
        s.Add(new(30, false, 8, 0x00000091, TcpFlags.Ack));
        await _AssertMatchesTshark("wrap", s).ConfigureAwait(false);
    }

    #endregion
}

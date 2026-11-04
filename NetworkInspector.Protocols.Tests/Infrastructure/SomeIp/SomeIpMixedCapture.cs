// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure.SomeIp;

/// <summary>
/// A synthetic capture of 2,000 frames from a fixed seed that exercises every feature of the SOME/IP parser at once: service
/// discovery with port learning, segmented messages in order, reordered, restarted, overlapping, filled late and evicted, request and
/// response pairs for timing, a unit that only the content-based detection accepts, TCP streams with a lost position, messages inside
/// reassembled IP datagrams, payloads of every category, and a protected member with a verified checksum.
/// <para>
/// Every frame carries a fixed capture time, and the generator draws from one seeded random source, so the capture is the same on
/// every run and on every machine. The settings that the capture needs are part of it (<see cref="Settings"/>), so a test builds the
/// stack from the capture and nothing else.
/// </para>
/// <para><b>Thread-safety:</b> the capture is built once and then read-only.</para>
/// </summary>
internal sealed class SomeIpMixedCapture : SomeIpPayloadKit
{
    /// <summary>The number of frames of the capture.</summary>
    internal const int FrameCount = 2000;

    private const long _Millisecond = 1_000_000;

    /// <summary>The size of the SOME/IP header in bytes (the test project sees two types of that name, so the constant is spelled out).</summary>
    private const int _HeaderSize = 16;

    private readonly Random _Random = new(20260101);
    private readonly List<byte[]> _Frames = [];
    private ushort _Session = 1;

    private SomeIpMixedCapture()
    {
    }

    /// <summary>The service description of the capture.</summary>
    internal static string Config { get; } = _BuildConfig();

    /// <summary>The settings the capture needs, as name and value.</summary>
    internal static (string Name, SettingValue Value)[] Settings { get; } =
    [
        ("someip.learn_sd_ports", SettingValue.Bool(true)),
        ("someip.method_timing", SettingValue.Bool(true)),
        ("someip.verify_e2e_crc", SettingValue.Bool(true)),
        ("someip.show_type_metadata", SettingValue.Bool(true)),
        ("someip.heuristic", SettingValue.Bool(true)),
        ("someip.tp_max_sessions", SettingValue.U64(4)),
        ("someip.max_messages_per_unit", SettingValue.U64(200)),
    ];

    /// <summary>The frames in capture order.</summary>
    internal IReadOnlyList<byte[]> Frames => _Frames;

    /// <summary>The capture time of frame <paramref name="index"/> in nanoseconds.</summary>
    /// <param name="index">The frame index.</param>
    internal static long TimeOf(int index) => (index + 1) * _Millisecond;

    /// <summary>Builds the capture.</summary>
    internal static SomeIpMixedCapture Build()
    {
        SomeIpMixedCapture capture = new();
        capture._Generate();
        return capture;
    }

    #region Description

    private static string _BuildConfig() => Json(
        Join(
            Method(1, "seq", Sites(Site("a", "uint8"), Site("b", "i16le"), Site("c", "state_t"), Site("d", "words_t"), Site("e", "rec"), Site("f", "string")), Sites(Site("r", "uint8"))),
            Method(2, "tlv", "[]", "{\"1\":{\"name\":\"t1\",\"type_reference\":\"uint8\"},\"2\":{\"name\":\"t2\",\"type_reference\":\"string\"},\"3\":{\"name\":\"t3\",\"type_reference\":\"uint16\"}}"),
            Method(3, "uni", "[]", Sites(Site("u", "union_t"), Site("flags", "flags_t"))),
            Method(5, "prot", Sites(Site("a", "uint8"), Site("b", "uint16")), "[]", ",\"e2e_profile\":\"04\""),
            "{\"kind\":\"event\",\"id\":4,\"name\":\"Ev\",\"type_reference\":\"flags_t\"}"),
        Join(
            "{\"category\":\"base_type\",\"name\":\"i16le\",\"type_reference\":\"int16\",\"byte_order\":\"little_endian\"}",
            "{\"category\":\"base_type\",\"name\":\"state_t\",\"type_reference\":\"uint8\",\"lookup_values\":{\"1\":\"Idle\",\"2\":\"Active\"}}",
            "{\"category\":\"array\",\"name\":\"words_t\",\"type_reference\":\"uint16\"}",
            "{\"category\":\"structure\",\"name\":\"rec\",\"members\":[{\"name\":\"x\",\"type_reference\":\"uint8\"},{\"name\":\"y\",\"type_reference\":\"uint16\"}]}",
            "{\"category\":\"bitfield\",\"name\":\"flags_t\",\"bits\":8,\"members\":{\"0\":{\"name\":\"ready\"},\"3\":{\"name\":\"fault\"}}}",
            "{\"category\":\"union\",\"name\":\"union_t\",\"members\":{\"1\":{\"name\":\"ua\",\"type_reference\":\"uint16\"},\"2\":{\"name\":\"ub\",\"type_reference\":\"uint8\"}}}"));

    #endregion

    #region Generation

    private void _Generate()
    {
        // Each round appends one scenario; a scenario is a small, self-contained exchange. The mix is drawn from the seeded source so
        // that scenarios interleave in the same way on every run, and the loop ends exactly at the frame count.
        while (_Frames.Count < FrameCount)
        {
            switch (_Random.Next(10))
            {
                case 0:
                    _Discovery();
                    break;
                case 1:
                    _LearnedData();
                    break;
                case 2:
                case 3:
                case 4:
                    _RequestResponse();
                    break;
                case 5:
                    _Segments();
                    break;
                case 6:
                    _Heuristic();
                    break;
                case 7:
                    _TcpStream();
                    break;
                case 8:
                    _Protected();
                    break;
                default:
                    _Fragmented();
                    break;
            }
        }

        _Frames.RemoveRange(FrameCount, _Frames.Count - FrameCount);
    }

    private ushort _NextSession()
    {
        // Session ids run from 1 to 65535 and wrap; 0 is not used.
        ushort session = _Session;
        _Session = session == ushort.MaxValue
            ? (ushort)1
            : (ushort)(session + 1);
        return session;
    }

    private void _Add(byte[] frame) => _Frames.Add(frame);

    private static byte[] _Udp(byte[] message, ushort source = 40000, ushort destination = 30490) =>
        FrameBuilderTestExtensions.WrapRawUdp(message, source, destination);

    private void _Discovery()
    {
        // An offer that announces a UDP endpoint: the learned port decodes the data frames that follow, and not the ones before.
        _Add(new SomeIpSdBuilder()
            .Offer(0x4001, 0x0001, 1, 0, 3)
            .Ipv4Option([10, 0, 0, 5], 0x11, 30512)
            .BuildFrame());
        _Add(new SomeIpSdBuilder()
            .Subscribe(0x4001, 0x0001, 1, 5, 0x0010)
            .ConfigurationOption("a=b")
            .BuildFrame());
    }

    private void _LearnedData() =>
        // With the content-based detection on, a plausible header is decoded on any port, so the learned-port marker never shows in this
        // capture; the ports are learned all the same, and the unit still exercises the decision that a replay has to repeat.
        _Add(_Udp(SomeIpBytes.Message(ServiceId, 4, [(byte)_Random.Next(256)], 1, _NextSession(), 0x02), 40000, 30512));

    private void _Heuristic() =>
        _Add(_Udp(SomeIpBytes.Message(0x1234, (ushort)_Random.Next(1, 100), _RandomBytes(_Random.Next(0, 12)), 1, _NextSession()), 41000, 41001));

    private byte[] _RandomBytes(int length)
    {
        byte[] bytes = new byte[length];
        _Random.NextBytes(bytes);
        return bytes;
    }

    private void _RequestResponse()
    {
        ushort session = _NextSession();
        switch (_Random.Next(3))
        {
            case 0:
            {
                // Sequential request with every simple category, answered with one byte.
                byte[] text = Encoding.UTF8.GetBytes("v" + _Random.Next(1000));
                byte[] request = new SomeIpBytes()
                    .U8((byte)_Random.Next(256)).U16Le((ushort)_Random.Next(65536)).U8((byte)_Random.Next(1, 4))
                    .U32(4).U16((ushort)_Random.Next(65536)).U16((ushort)_Random.Next(65536))
                    .U32(3).U8((byte)_Random.Next(256)).U16((ushort)_Random.Next(65536))
                    .U32((uint)text.Length).Bytes(text)
                    .ToArray();
                // One request in four is lost, so its response says that the request is missing.
                if (_Random.Next(4) != 0)
                {
                    _Add(_Udp(SomeIpBytes.Message(ServiceId, 1, request, 7, session)));
                }

                _Add(_Udp(SomeIpBytes.Message(ServiceId, 1, [(byte)_Random.Next(256)], 7, session, 0x80)));
                break;
            }

            case 1:
            {
                // A TLV response in a random member order, some members absent.
                SomeIpBytes tlv = new();
                if (_Random.Next(2) == 0)
                {
                    tlv.U16(0x0001).U8((byte)_Random.Next(256));
                }

                if (_Random.Next(2) == 0)
                {
                    tlv.U16(0x1003).U16((ushort)_Random.Next(65536));
                }

                tlv.U16(0x5002).U8(2).Utf8("ok");
                _Add(_Udp(SomeIpBytes.Message(ServiceId, 2, [], 7, session)));
                _Add(_Udp(SomeIpBytes.Message(ServiceId, 2, tlv.ToArray(), 7, session, 0x80)));
                break;
            }

            default:
            {
                // A union (known or unknown selector) and a bitfield.
                uint selector = (uint)_Random.Next(0, 4);
                byte[] value = [(byte)_Random.Next(256)];
                if (selector == 1)
                {
                    value = [value[0], 0x01];
                }

                byte[] union = new SomeIpBytes().U32(4 + (uint)value.Length).U32(selector).Bytes(value).U8((byte)_Random.Next(256)).ToArray();
                _Add(_Udp(SomeIpBytes.Message(ServiceId, 3, [], 7, session)));
                _Add(_Udp(SomeIpBytes.Message(ServiceId, 3, union, 7, session, 0x80)));
                break;
            }
        }
    }

    private void _Protected()
    {
        // A protected request with a correct checksum, or with one flipped byte.
        byte[] inner = [(byte)_Random.Next(256), 0x01, 0x02];
        byte[] head = new SomeIpBytes().U16(15).U16((ushort)_Random.Next(65536)).U32(9).ToArray();
        ushort session = _NextSession();
        byte[] message = SomeIpBytes.Message(ServiceId, 5, new SomeIpBytes().Bytes(head).U32(0).Bytes(inner).ToArray(), 7, session);
        ulong region = BinaryPrimitives.ReadUInt64BigEndian(message.AsSpan(8, 8));
        uint crc = SomeIpE2e.ComputeProfile04(region, message.AsSpan(_HeaderSize));
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(_HeaderSize + 8, 4), crc);
        if (_Random.Next(3) == 0)
        {
            message[^1] ^= 0xFF;
        }

        _Add(_Udp(message));
    }

    private void _Segments()
    {
        ushort session = _NextSession();
        byte[] a = _RandomBytes(16);
        byte[] b = _RandomBytes(16);
        byte[] last = _RandomBytes(8);
        byte[] Segment(uint offset, byte[] data, bool more) =>
            SomeIpFrames.TpFrame(offset, data, more, ServiceId, 1, 9, session, 0x20);
        switch (_Random.Next(6))
        {
            case 0:
                // In order.
                _Add(Segment(0, a, more: true));
                _Add(Segment(16, b, more: true));
                _Add(Segment(32, last, more: false));
                break;
            case 1:
                // Reordered: the last segment first, then the middle, then the start.
                _Add(Segment(32, last, more: false));
                _Add(Segment(16, b, more: true));
                _Add(Segment(0, a, more: true));
                break;
            case 2:
                // A restart: the second segment at offset 0 abandons the first attempt.
                _Add(Segment(0, a, more: true));
                _Add(Segment(0, b, more: true));
                _Add(Segment(16, a, more: false));
                break;
            case 3:
                // An overlap: the second segment covers bytes that the first already holds, with different content; the first arrival wins.
                _Add(Segment(0, _RandomBytes(32), more: true));
                _Add(Segment(16, b, more: true));
                _Add(Segment(32, last, more: false));
                break;
            case 4:
                // A late gap fill: the decodable prefix is shown first, the whole message when the hole closes.
                _Add(Segment(0, a, more: true));
                _Add(Segment(32, last, more: false));
                _Add(Segment(16, b, more: true));
                break;
            default:
                // Sessions that never complete, so the session limit (4) evicts the least recently used.
                for (int i = 0; i < 6; i++)
                {
                    _Add(SomeIpFrames.TpFrame(0, a, more: true, service: ServiceId, method: 1, client: 9, session: _NextSession(), messageType: 0x20));
                }

                break;
        }
    }

    private void _TcpStream()
    {
        // A stream on the well-known port: two messages split over several segments, then bytes that are no header (the position is
        // lost and found again), then one more message.
        TcpTestConversation conversation = new(30490, clientPort: (ushort)_Random.Next(50000, 60000));
        conversation.Handshake();
        byte[] first = SomeIpBytes.Message(ServiceId, 1, _RandomBytes(_Random.Next(0, 40)), 7, _NextSession());
        byte[] second = SomeIpBytes.Message(ServiceId, 2, [], 7, _NextSession());
        conversation.ClientSend(SomeIpBytes.Concat(first, second), mss: _Random.Next(10, 50));
        conversation.ClientSend(_RandomBytes(_Random.Next(1, 20)));
        conversation.ClientSend(SomeIpBytes.Message(ServiceId, 3, [], 7, _NextSession()));
        foreach (byte[] frame in conversation.Frames)
        {
            _Add(frame);
        }
    }

    private void _Fragmented()
    {
        // A message inside a reassembled IPv4 datagram: its effect keys carry a buffer index.
        byte[] message = SomeIpBytes.Message(ServiceId, 1, _RandomBytes(_Random.Next(2000, 4000)), 7, _NextSession());
        foreach (byte[] fragment in SomeIpFrames.FragmentedUdp(message, 40000, 30490))
        {
            _Add(fragment);
        }
    }

    #endregion
}

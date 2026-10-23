// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// The sample protocol on a real standard stack: port dispatch on UDP 40000, the NIS1 heuristic on TCP, the post-parser, and
/// short or malformed payloads.
/// </summary>
internal sealed class SampleProtocolTests
{
    #region Constants

    private const ushort _SamplePort = 40000;

    private const ushort _TcpPort = 41234;

    #endregion

    #region Helpers

    // The 14-byte sample payload, cut out of a generated UDP frame (payload starts after Ethernet, IPv4, and UDP headers).
    private static byte[] _Payload(ulong counter = 1, byte gear = 2, byte speed = 3) =>
        SampleFrameWriter.Write(counter, gear, speed, _SamplePort)[42..];

    private static bool _Has(Stack stack, Packet packet, string fieldName) =>
        stack.GetFieldId(fieldName) is FieldId id && packet.TryGetFieldValue(id, out _, materialize: true);

    #endregion

    #region Port dispatch

    [Test]
    public async Task UdpPortDecodesCounterGearAndSpeed()
    {
        using Stack stack = SampleStack.Build();
        Frame frame = SampleStack.MakeFrame(stack, SampleFrameWriter.Write(counter: 7, gear: 3, speed: 120, udpPort: _SamplePort));

        Packet packet = SampleStack.Parse(stack, frame);

        await Assert.That(frame.Length).IsEqualTo(56);
        await Assert.That(_Has(stack, packet, "sample")).IsTrue();
        await Assert.That(SampleStack.U64(stack, packet, "sample.counter")).IsEqualTo(7UL);
        await Assert.That(SampleStack.U64(stack, packet, "sample.gear")).IsEqualTo(3UL);
        await Assert.That(SampleStack.U64(stack, packet, "sample.speed")).IsEqualTo(120UL);
        await Assert.That(packet.Info).Contains("Sample counter=7 gear=3 speed=120");
    }

    [Test]
    public async Task ThePacketInfoIsExactlyTheDeferredText()
    {
        using Stack stack = SampleStack.Build();
        Frame frame = SampleStack.MakeFrame(stack, SampleFrameWriter.Write(counter: 7, gear: 3, speed: 120, udpPort: _SamplePort));

        Packet packet = SampleStack.Parse(stack, frame);

        await Assert.That(packet.Info).IsEqualTo("Sample counter=7 gear=3 speed=120");
    }

    [Test]
    public async Task UdpContainerPrecedesSampleContainerInTheTree()
    {
        using Stack stack = SampleStack.Build();
        Packet packet = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, SampleFrameWriter.Write(7, 3, 120, _SamplePort)));
        FieldId udp = Expect.NotNull(stack.GetFieldId("udp"));
        FieldId sample = Expect.NotNull(stack.GetFieldId("sample"));

        List<FieldId> order = [];
        foreach (Field field in packet.IterFieldsFlat(materialize: true))
        {
            order.Add(field.FieldId);
        }

        await Assert.That(order.IndexOf(udp)).IsGreaterThanOrEqualTo(0);
        await Assert.That(order.IndexOf(sample)).IsGreaterThan(order.IndexOf(udp));
    }

    [Test]
    public async Task SkipModeReportsSpeedToTheObserver()
    {
        using Stack stack = SampleStack.Build();
        Frame frame = SampleStack.MakeFrame(stack, SampleFrameWriter.Write(7, 3, 120, _SamplePort));
        FieldId speed = Expect.NotNull(stack.GetFieldId("sample.speed"));
        RecordingFieldObserver observer = new();
        ParseObserver[] observers = [new() { Observer = observer, Watch = FieldWatch.Only(stack, speed) }];
        ParseOptions options = new(FieldTreeMode.Skip, observers: observers);

        bool parsed = Packet.TryParse(new PacketId(0), stack, frame, in options, out Packet? packet, out ParseFailure failure);

        await Assert.That(parsed).IsTrue();
        await Assert.That(packet).IsNotNull();
        await Assert.That(observer.Ids.Count).IsEqualTo(1);
        await Assert.That(observer.Ids[0]).IsEqualTo(speed);
        await Assert.That(observer.Values[0]).IsEqualTo(120UL);
    }

    [Test]
    public async Task UdpOnAnotherPortStaysUndecoded()
    {
        using Stack stack = SampleStack.Build();

        Packet packet = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, SampleFrameWriter.Write(7, 3, 120, 41234)));

        await Assert.That(_Has(stack, packet, "sample")).IsFalse();
    }

    #endregion

    #region TCP heuristic

    [Test]
    public async Task MagicOnAnyTcpPortDecodesThroughTheHeuristic()
    {
        using Stack stack = SampleStack.Build();
        byte[] payload = _Payload(counter: 11, gear: 4, speed: 99);

        // Both segments belong to one connection (one source port): TCP caches a heuristic match per connection.
        Packet first = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, RawFrames.Tcp(payload, 50001, _TcpPort, 1000), id: 0));
        Packet second = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, RawFrames.Tcp(payload, 50001, _TcpPort, 1014), id: 1));

        await Assert.That(SampleStack.U64(stack, first, "sample.speed")).IsEqualTo(99UL);
        await Assert.That(SampleStack.U64(stack, first, "sample.counter")).IsEqualTo(11UL);
        await Assert.That(_Has(stack, second, "sample")).IsTrue();
    }

    [Test]
    public async Task ShortPayloadWithMagicIsNotMatched()
    {
        using Stack stack = SampleStack.Build();
        byte[] payload = _Payload()[..13];

        Packet packet = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, RawFrames.Tcp(payload, 50002, _TcpPort, 1)));

        await Assert.That(_Has(stack, packet, "sample")).IsFalse();
        await Assert.That(SampleStack.Text(stack, packet, "packet.error")).IsNull();
    }

    [Test]
    public async Task OtherMagicIsNotMatched()
    {
        using Stack stack = SampleStack.Build();
        byte[] payload = _Payload();
        payload[3] = (byte)'2';

        Packet packet = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, RawFrames.Tcp(payload, 50003, _TcpPort, 1)));

        await Assert.That(_Has(stack, packet, "sample")).IsFalse();
    }

    [Test]
    public async Task HttpRequestOnTheSamePortStillDecodesAsHttp()
    {
        using Stack stack = SampleStack.Build();
        byte[] request = "GET / HTTP/1.1\r\nHost: example.invalid\r\n\r\n"u8.ToArray();

        Packet packet = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, RawFrames.Tcp(request, 50004, _TcpPort, 1)));

        await Assert.That(_Has(stack, packet, "http.request.method")).IsTrue();
        await Assert.That(_Has(stack, packet, "sample")).IsFalse();
    }

    [Test]
    public async Task UdpHasNoHeuristicTable()
    {
        using Stack stack = SampleStack.Build();

        // The same payload over UDP on a port without a registration stays undecoded UDP payload.
        Packet packet = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, RawFrames.Udp(_Payload(), 50005, _TcpPort)));

        await Assert.That(_Has(stack, packet, "sample")).IsFalse();
        await Assert.That(_Has(stack, packet, "udp")).IsTrue();
    }

    #endregion

    #region Post-parser

    [Test]
    public async Task UdpFrameGetsMagicOffset42()
    {
        using Stack stack = SampleStack.Build();

        Packet packet = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, SampleFrameWriter.Write(1, 1, 1, _SamplePort)));

        await Assert.That(SampleStack.U64(stack, packet, "sample.post.magic_offset")).IsEqualTo(42UL);
    }

    [Test]
    public async Task TcpFrameGetsMagicOffset54()
    {
        using Stack stack = SampleStack.Build();

        Packet packet = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, SampleFrameWriter.WriteTcp(1, 1, 1, _TcpPort, 1)));

        await Assert.That(SampleStack.U64(stack, packet, "sample.post.magic_offset")).IsEqualTo(54UL);
    }

    [Test]
    public async Task FrameWithoutTheMagicHasNoMarker()
    {
        using Stack stack = SampleStack.Build();
        byte[] dnsLike = RawFrames.Udp("\0\u0001\u0001\0\0\u0001\0\0\0\0\0\0"u8, 50006, 53);

        Packet packet = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, dnsLike));

        await Assert.That(SampleStack.U64(stack, packet, "sample.post.magic_offset")).IsNull();
    }

    [Test]
    public async Task MagicAtAnOddOffsetInsideRandomPayloadReportsThatOffset()
    {
        using Stack stack = SampleStack.Build();
        byte[] payload = new byte[40];
        "NIS1"u8.CopyTo(payload.AsSpan(13));
        byte[] frame = RawFrames.Udp(payload, 50007, 6000);

        Packet packet = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, frame));

        await Assert.That(SampleStack.U64(stack, packet, "sample.post.magic_offset")).IsEqualTo(42UL + 13);
    }

    #endregion

    #region Malformed payloads

    [Test]
    public async Task ShortPayloadOnTheRegisteredPortIsAParseErrorNotACrash()
    {
        using Stack stack = SampleStack.Build();
        byte[] payload = _Payload()[..13];

        Packet packet = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, RawFrames.Udp(payload, 50008, _SamplePort)));
        string? error = SampleStack.Text(stack, packet, "packet.error");

        await Assert.That(error).IsNotNull();
        await Assert.That(error).Contains("Insufficient data: expected 14 bytes, got 13");
        await Assert.That(SampleStack.U64(stack, packet, "sample.counter")).IsNull();
    }

    [Test]
    public async Task WrongMagicOnTheRegisteredPortNamesTheMagic()
    {
        using Stack stack = SampleStack.Build();
        byte[] payload = _Payload();
        "XXXX"u8.CopyTo(payload);

        Packet packet = SampleStack.Parse(stack, SampleStack.MakeFrame(stack, RawFrames.Udp(payload, 50009, _SamplePort)));
        string? error = SampleStack.Text(stack, packet, "packet.error");

        await Assert.That(error).IsNotNull();
        await Assert.That(error).Contains("Magic");
    }

    #endregion
}

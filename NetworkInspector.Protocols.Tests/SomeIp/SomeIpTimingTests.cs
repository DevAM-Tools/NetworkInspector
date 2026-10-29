// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using PacketIndex = NetworkInspector.Core.Index.PacketIndex;

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// Method timing: a response is paired with the latest pending request of its key and shows the request's packet and the delay; a
/// request shows nothing; a re-parse reproduces the first parse; the pending limit forgets the oldest request and says so. Every
/// packet carries an explicit capture time, because a delay is the difference of two of them.
/// </summary>
internal sealed class SomeIpTimingTests : SomeIpPayloadKit
{
    private const long _Second = 1_000_000_000;

    private static readonly string _Config = Json(Method(1, "Echo", Sites(Site("a", "uint8")), Sites(Site("r", "uint8"))));

    private static SomeIpTestStack _Stack(params (string Name, SettingValue Value)[] settings) =>
        SomeIpTestStack.Create(_Config, [("someip.method_timing", SettingValue.Bool(true)), .. settings]);

    /// <summary>A UDP frame with one message of the test service; client and session identify the request.</summary>
    private static byte[] _Message(byte messageType, ushort session, ushort client = 2, ushort method = 1, byte[]? payload = null) =>
        SomeIpFrames.Udp(SomeIpBytes.Message(ServiceId, method, payload ?? [1], client, session, messageType));

    private static long _At(double seconds) => (long)(seconds * _Second);

    /// <summary>A 16-byte segment of a segmented message of the test service (client 2, session 3).</summary>
    private static byte[] _Segment(uint offset, bool more, byte messageType) =>
        SomeIpFrames.TpFrame(offset, new byte[16], more, ServiceId, 1, 2, 3, messageType);

    #region Opt-in

    [Test]
    public async Task WithTimingOffNoContainerAndNoGroupAppearAndWithItOnlyTheResponseShowsOne()
    {
        byte[] request = _Message(0x00, 3);
        byte[] response = _Message(0x80, 3);
        using SomeIpTestStack off = SomeIpTestStack.Create(_Config);
        using SomeIpTestStack on = _Stack();

        Packet offRequest = off.ParseIndexed(request, out _);
        Packet offResponse = off.ParseIndexed(response, out PacketIndex offIndex2);
        Packet onRequest = on.ParseAt(request, _At(1.0));
        Packet onResponse = on.ParseAt(response, _At(1.5));

        await Assert.That(off.Count(offRequest, "someip.timing")).IsEqualTo(0);
        await Assert.That(off.Count(offResponse, "someip.timing")).IsEqualTo(0);
        await Assert.That(off.HasGroup(offIndex2, offResponse, "someip.timing")).IsFalse();
        await Assert.That(on.Count(onRequest, "someip.timing")).IsEqualTo(0);
        await Assert.That(on.Count(onResponse, "someip.timing")).IsEqualTo(1);

        // Nothing but the timing subtree differs between the two responses.
        List<string> without = _Names(off, offResponse);
        List<string> with = _Names(on, onResponse);
        await Assert.That(with.Where(static n => !n.StartsWith("someip.timing", StringComparison.Ordinal)).ToList()).IsEquivalentTo(without);
    }

    private static List<string> _Names(SomeIpTestStack s, Packet packet)
    {
        List<string> names = [];
        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            string? name = s.Stack.GetField(field.FieldId)?.Name;
            if (name is not null && name.StartsWith("someip", StringComparison.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    #endregion

    #region Pairing

    [Test]
    public async Task AResponseShowsItsRequestPacketAndTheDelayAndALoneResponseSaysTheRequestIsMissing()
    {
        using SomeIpTestStack s = _Stack();
        byte[] requestFrame = _Message(0x00, 3);

        Packet request = s.ParseAt(requestFrame, _At(1.0));
        Packet response = s.ParseAt(_Message(0x80, 3), _At(1.0025));
        Packet lone = s.ParseAt(_Message(0x80, 99), _At(1.5));

        await Assert.That(s.U64s(response, "someip.timing.request_in")).IsEquivalentTo(One(0UL));
        List<double> delay = F64s(s, response, "someip.timing.delay");
        await Assert.That(delay.Count).IsEqualTo(1);
        await Assert.That(delay[0]).IsEqualTo(0.0025).Within(1e-9);
        await Assert.That(s.Count(request, "someip.timing")).IsEqualTo(0);
        await Assert.That(Bools(s, lone, "someip.timing.request_missing")).IsEquivalentTo(One(true));
        await Assert.That(s.Count(lone, "someip.timing.delay")).IsEqualTo(0);
        await Assert.That(s.Count(lone, "someip.timing.error")).IsEqualTo(0);

        // The request was final before its response existed: parsing the response changed nothing in the request's tree.
        Packet again = s.ReparseAt(requestFrame, 0, _At(1.0));
        await PacketFieldComparer.AssertFieldIdentical(s.Stack, request, again);
    }

    [Test]
    public async Task TheResponseTakesTheLatestPendingRequestAndTheOlderOneStaysForTheNextResponse()
    {
        using SomeIpTestStack s = _Stack();

        _ = s.ParseAt(_Message(0x00, 3), _At(1.0));
        Packet second = s.ParseAt(_Message(0x00, 3), _At(1.2));
        Packet firstResponse = s.ParseAt(_Message(0x80, 3), _At(1.3));
        Packet secondResponse = s.ParseAt(_Message(0x80, 3), _At(1.4));

        await Assert.That(s.U64s(firstResponse, "someip.timing.request_in")).IsEquivalentTo(One((ulong)second.Id.Value));
        await Assert.That(F64s(s, firstResponse, "someip.timing.delay")[0]).IsEqualTo(0.1).Within(1e-9);
        await Assert.That(s.U64s(secondResponse, "someip.timing.request_in")).IsEquivalentTo(One(0UL));
        await Assert.That(F64s(s, secondResponse, "someip.timing.delay")[0]).IsEqualTo(0.4).Within(1e-9);
    }

    [Test]
    public async Task OnlyTheSameServiceMethodClientSessionAndInterfacePair()
    {
        using SomeIpTestStack s = _Stack();
        FrameInterfaceId first = s.RegisterInterface("eth0");
        FrameInterfaceId second = s.RegisterInterface("eth1");

        _ = s.ParseAt(_Message(0x00, 3, client: 2), _At(1.0), first);
        Packet sessionMismatch = s.ParseAt(_Message(0x80, 4, client: 2), _At(1.1), first);
        Packet clientMismatch = s.ParseAt(_Message(0x80, 3, client: 5), _At(1.1), first);
        Packet interfaceMismatch = s.ParseAt(_Message(0x80, 3, client: 2), _At(1.1), second);
        Packet match = s.ParseAt(_Message(0x80, 3, client: 2), _At(1.2), first);

        await Assert.That(Bools(s, sessionMismatch, "someip.timing.request_missing")).IsEquivalentTo(One(true));
        await Assert.That(Bools(s, clientMismatch, "someip.timing.request_missing")).IsEquivalentTo(One(true));
        await Assert.That(Bools(s, interfaceMismatch, "someip.timing.request_missing")).IsEquivalentTo(One(true));
        await Assert.That(s.U64s(match, "someip.timing.request_in")).IsEquivalentTo(One(0UL));
    }

    [Test]
    public async Task PairingAcrossInterfacesIsOptIn()
    {
        using SomeIpTestStack s = _Stack(("someip.method_timing_ignore_interface", SettingValue.Bool(true)));
        FrameInterfaceId first = s.RegisterInterface("eth0");
        FrameInterfaceId second = s.RegisterInterface("eth1");

        _ = s.ParseAt(_Message(0x00, 3), _At(1.0), first);
        Packet response = s.ParseAt(_Message(0x80, 3), _At(1.1), second);

        await Assert.That(s.U64s(response, "someip.timing.request_in")).IsEquivalentTo(One(0UL));
    }

    [Test]
    public async Task NotificationsRequestsWithoutReturnAndErrorsTakeNoPartAndAnErrorDoesNotConsumeTheRequest()
    {
        using SomeIpTestStack s = _Stack();

        _ = s.ParseAt(_Message(0x00, 3), _At(1.0));
        Packet notification = s.ParseAt(_Message(0x02, 3), _At(1.1));
        Packet noReturn = s.ParseAt(_Message(0x01, 3), _At(1.2));
        Packet error = s.ParseAt(_Message(0x81, 3), _At(1.3));
        Packet response = s.ParseAt(_Message(0x80, 3), _At(1.4));

        await Assert.That(s.Count(notification, "someip.timing")).IsEqualTo(0);
        await Assert.That(s.Count(noReturn, "someip.timing")).IsEqualTo(0);
        await Assert.That(s.Count(error, "someip.timing")).IsEqualTo(0);
        await Assert.That(s.U64s(response, "someip.timing.request_in")).IsEquivalentTo(One(0UL));
        await Assert.That(F64s(s, response, "someip.timing.delay")[0]).IsEqualTo(0.4).Within(1e-9);
    }

    [Test]
    public async Task ASegmentedResponseIsPairedOnTheSegmentThatCompletesItWithTheFirstRequestSegment()
    {
        using SomeIpTestStack s = _Stack();

        Packet request1 = s.ParseAt(_Segment(0, more: true, 0x20), _At(1.0));
        _ = s.ParseAt(_Segment(16, more: false, 0x20), _At(1.1));
        Packet response1 = s.ParseAt(_Segment(0, more: true, 0xA0), _At(1.5));
        Packet response2 = s.ParseAt(_Segment(16, more: true, 0xA0), _At(1.6));
        Packet response3 = s.ParseAt(_Segment(32, more: false, 0xA0), _At(1.7));

        await Assert.That(s.Count(request1, "someip.timing")).IsEqualTo(0);
        await Assert.That(s.Count(response1, "someip.timing")).IsEqualTo(0);
        await Assert.That(s.Count(response2, "someip.timing")).IsEqualTo(0);
        await Assert.That(s.U64s(response3, "someip.timing.request_in")).IsEquivalentTo(One((ulong)request1.Id.Value));
        await Assert.That(F64s(s, response3, "someip.timing.delay")[0]).IsEqualTo(0.7).Within(1e-9);
    }

    #endregion

    #region Limit

    [Test]
    public async Task AtThePendingLimitTheOldestRequestIsForgottenAndItsResponseNamesTheSetting()
    {
        using SomeIpTestStack s = _Stack(("someip.timing_max_pending", SettingValue.U64(2)));

        _ = s.ParseAt(_Message(0x00, 1), _At(1.0));
        _ = s.ParseAt(_Message(0x00, 2), _At(1.1));
        _ = s.ParseAt(_Message(0x00, 3), _At(1.2));
        Packet forgotten = s.ParseAt(_Message(0x80, 1), _At(1.3));
        Packet third = s.ParseAt(_Message(0x80, 3), _At(1.4));

        await Assert.That(Bools(s, forgotten, "someip.timing.request_missing")).IsEquivalentTo(One(true));
        List<string> errors = s.Strings(forgotten, "someip.timing.error");
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("someip.timing_max_pending");
        await Assert.That(s.U64s(third, "someip.timing.request_in")).IsEquivalentTo(One(2UL));
    }

    [Test]
    public async Task TheTrackerKeepsAtMostTheLimitPendingAndRemembersAtMostTheLimitForgottenKeys()
    {
        SomeIpTimingTracker tracker = new(3);
        for (ushort session = 0; session < 20; session++)
        {
            tracker.AddRequest(new SomeIpTimingKey(1, 1, 1, session, 1, 0), session, session);
        }

        await Assert.That(tracker.PendingCount).IsEqualTo(3);
        await Assert.That(tracker.ForgottenCount).IsEqualTo(3);

        // The newest three are pending; a forgotten key is remembered only while its memory lasts.
        SomeIpTimingKey newest = new(1, 1, 1, 19, 1, 0);
        await Assert.That(tracker.TryPairResponse(in newest, 100, out int packet, out long delay, out bool forgotten)).IsTrue();
        await Assert.That(packet).IsEqualTo(19);
        await Assert.That(delay).IsEqualTo(81L);
        await Assert.That(forgotten).IsFalse();
        SomeIpTimingKey recentlyForgotten = new(1, 1, 1, 16, 1, 0);
        await Assert.That(tracker.TryPairResponse(in recentlyForgotten, 100, out _, out _, out bool remembered)).IsFalse();
        await Assert.That(remembered).IsTrue();
        SomeIpTimingKey longForgotten = new(1, 1, 1, 0, 1, 0);
        await Assert.That(tracker.TryPairResponse(in longForgotten, 100, out _, out _, out bool dropped)).IsFalse();
        await Assert.That(dropped).IsFalse();
    }

    [Test]
    public async Task ExtremeTimestampsSaturateTheDelayInsteadOfWrapping()
    {
        SomeIpTimingTracker tracker = new(4);
        SomeIpTimingKey key = new(1, 1, 1, 1, 1, 0);
        tracker.AddRequest(in key, 1, long.MinValue + 1);

        await Assert.That(tracker.TryPairResponse(in key, long.MaxValue - 1, out _, out long delay, out _)).IsTrue();
        await Assert.That(delay).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task AModelCannotBeRegisteredOnASecondStack()
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(Json(Method(1, "M", Sites(Site("a", "uint8")), "[]"))));
        _ = SomeIpRegistration.TryLoadConfig(stream, out SomeIpConfig? config, out _);
        SomeIpCompiledModel model = SomeIpConfigCompiler.Compile(config!, new List<SettingsLoadWarning>());

        void RegisterOnNewBuilder()
        {
#pragma warning disable CA2000 // The builder is dropped with its stack in this test.
            StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
#pragma warning restore CA2000
            SomeIpProtocol protocol = new(model, SomeIpSettings.Default);
            ProtocolId id = builder.RegisterProtocol(protocol);
            protocol.RegisterFields(builder, id);
        }

        RegisterOnNewBuilder();
        await Assert.That(RegisterOnNewBuilder).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task AResponseStampedBeforeItsRequestHasADelayOfZeroAndALimitBelowOneIsRefused()
    {
        SomeIpTimingTracker tracker = new(4);
        SomeIpTimingKey key = new(1, 1, 1, 1, 1, 0);
        tracker.AddRequest(in key, 5, 1_000);

        await Assert.That(tracker.TryPairResponse(in key, 500, out int packet, out long delay, out _)).IsTrue();
        await Assert.That(packet).IsEqualTo(5);
        await Assert.That(delay).IsEqualTo(0L);
        await Assert.That(() => new SomeIpTimingTracker(0)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Replay and isolation

    [Test]
    public async Task AReparseOfARequestAndOfAResponseGivesTheTreeOfTheFirstParse()
    {
        using SomeIpTestStack s = _Stack();
        byte[] requestFrame = _Message(0x00, 3);
        byte[] responseFrame = _Message(0x80, 3);
        Packet request = s.ParseAt(requestFrame, _At(1.0));
        Packet response = s.ParseAt(responseFrame, _At(1.2));
        _ = s.ParseAt(_Message(0x00, 3), _At(2.0));
        _ = s.ParseAt(_Message(0x80, 3), _At(2.1));

        Packet requestAgain = s.ReparseAt(requestFrame, 0, _At(1.0));
        Packet responseAgain = s.ReparseAt(responseFrame, 1, _At(1.2));

        await PacketFieldComparer.AssertFieldIdentical(s.Stack, request, requestAgain);
        await PacketFieldComparer.AssertFieldIdentical(s.Stack, response, responseAgain);
        await Assert.That(s.U64s(responseAgain, "someip.timing.request_in")).IsEquivalentTo(One(0UL));
    }

    [Test]
    public async Task AUnitOfFifteenHundredResponsesInsideAReassembledDatagramReplaysEveryPair()
    {
        // Both units are cut into IPv4 fragments, so every message lives in a buffer that the packet binds (buffer index 1) and has
        // its own effect key; a key that overflowed or aliased would give two responses the same pair or lose one.
        using SomeIpTestStack s = _Stack(("someip.max_messages_per_unit", SettingValue.U64(2000)));
        const int count = 1500;
        byte[][] requests = [.. Enumerable.Range(1, count).Select(static i => SomeIpBytes.Message(ServiceId, 1, [1], 2, (ushort)i, 0x00))];
        byte[][] responses = [.. Enumerable.Range(1, count).Select(static i => SomeIpBytes.Message(ServiceId, 1, [1], 2, (ushort)i, 0x80))];
        List<byte[]> frames = [.. SomeIpFrames.FragmentedUdp(SomeIpBytes.Concat(requests), 40000, 30490)];
        int requestFragments = frames.Count;
        frames.AddRange(SomeIpFrames.FragmentedUdp(SomeIpBytes.Concat(responses), 40000, 30490));

        Packet[] first = new Packet[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            first[i] = s.ParseAt(frames[i], _At(1.0 + (i * 0.001)));
        }

        Packet responseUnit = first[^1];
        List<ulong> pairs = s.U64s(responseUnit, "someip.timing.request_in");
        await Assert.That(s.Count(responseUnit, "someip.timing")).IsEqualTo(count);
        await Assert.That(pairs.Count).IsEqualTo(count);
        await Assert.That(pairs.All(p => p == (ulong)(requestFragments - 1))).IsTrue();
        for (int i = 0; i < frames.Count; i++)
        {
            await PacketFieldComparer.AssertFieldIdentical(s.Stack, first[i], s.ReparseAt(frames[i], i, _At(1.0 + (i * 0.001))));
        }
    }

    [Test]
    public async Task AnotherStackStartsWithNoPendingRequests()
    {
        using SomeIpTestStack first = _Stack();
        using SomeIpTestStack second = _Stack();

        _ = first.ParseAt(_Message(0x00, 3), _At(1.0));
        Packet response = second.ParseAt(_Message(0x80, 3), _At(1.1));

        await Assert.That(Bools(second, response, "someip.timing.request_missing")).IsEquivalentTo(One(true));
    }

    [Test]
    public async Task TheTimingGroupIsRecordedForResponsesOnlyAndTheIndexStaysExact()
    {
        using SomeIpTestStack s = _Stack();
        _ = s.ParseAt(_Message(0x00, 3), _At(1.0));

        Packet response = s.ParseIndexed(_Message(0x80, 3), out PacketIndex index);
        Packet request = s.ParseIndexed(_Message(0x00, 4), out PacketIndex requestIndex);

        await Assert.That(s.HasGroup(index, response, "someip.timing")).IsTrue();
        await Assert.That(s.HasGroup(requestIndex, request, "someip.timing")).IsFalse();
        await s.AssertIndexExact(index, response);
    }

    #endregion
}

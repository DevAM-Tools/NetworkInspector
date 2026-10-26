// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the HTTP/2 client connection preface at the start of a segment: it is shown as the magic field and the
/// frames after it are decoded as frames, not mistaken for a frame header.
/// </summary>
internal sealed class Http2PrefaceTests
{
    private static readonly byte[] _Preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

    private static byte[] _SegmentWithPrefaceAnd(byte[] frames) => Http2PayloadBuilder.WrapTcp([.. _Preface, .. frames]);

    private static byte[] _SettingsFrame() =>
        Http2PayloadBuilder.BuildFrame(Http2PayloadBuilder.FrameType.Settings, 0, 0, Http2PayloadBuilder.BuildSettingsBody((3, 100)));

    [Test]
    public async Task PrefaceFollowedBySettingsDecodesTheSettingsFrame()
    {
        byte[] frame = _SegmentWithPrefaceAnd(_SettingsFrame());

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBytesField(stack, packet, "http2.magic", _Preface).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.frame.type", 4).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.frame.length", 6).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.settings.id", 3).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.settings.value", 100).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task PrefaceFollowedBySettingsIsIndexedExactly()
    {
        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(_SegmentWithPrefaceAnd(_SettingsFrame())).ConfigureAwait(false);
    }

    [Test]
    public async Task PrefaceAloneIsShownAsMagicWithoutFrames()
    {
        byte[] frame = Http2PayloadBuilder.WrapTcp(_Preface);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBytesField(stack, packet, "http2.magic", _Preface).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "http2.frame").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task PrefaceFollowedByTruncatedFrameHeaderShowsOnlyTheMagic()
    {
        byte[] frame = Http2PayloadBuilder.WrapTcp([.. _Preface, 0x00, 0x00, 0x06, 0x04, 0x00]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "http2.magic").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "http2.frame").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task SegmentWithoutPrefaceHasNoMagic()
    {
        byte[] frame = Http2PayloadBuilder.WrapTcp(_SettingsFrame());

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "http2.magic").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http2.frame.type", 4).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task SegmentShorterThanAFrameHeaderIsAnError()
    {
        // Eight bytes cannot hold a frame header, and are not the preface either.
        byte[] frame = Http2PayloadBuilder.WrapTcp([0, 0, 0, 4, 0, 0, 0, 0]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "http2.frame").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task HeuristicParserDescriptionNamesOnlyThePreface()
    {
        Http2HeuristicParser parser = new(ProtocolId.Invalid);

        await Assert.That(parser.Description).IsNotNull();
        await Assert.That(parser.Description!.Contains("frame header", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }
}

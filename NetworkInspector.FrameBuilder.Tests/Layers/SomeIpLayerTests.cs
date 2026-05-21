// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests.Layers;

/// <summary>
/// Tests for <see cref="SomeIpLayer"/> — Length fixup, field layout,
/// and round-trip verification.
/// </summary>
internal sealed class SomeIpLayerTests
{
    private static readonly MacAddress _Dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _Src = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly EthernetLayer _Eth = new(_Dst, _Src);
    private static readonly IPv4Address _SrcIp = new(0xC0A80001);
    private static readonly IPv4Address _DstIp = new(0xC0A80002);
    private static readonly IPv4Layer _Ip4 = new(_SrcIp, _DstIp);
    private static readonly UdpLayer _Udp = new(30490, 30490, FB.Auto<ushort>.Explicit(0)); // typical SOME/IP ports

    /// <summary>SOME/IP byte offset in a (Eth + IPv4 + UDP + SOME/IP) frame.</summary>
    private const int SomeIpOffsetInFrame = 14 + IPv4Header.Size + UdpHeader.Size;

    /// <summary>Builds one Eth + IPv4 + UDP + <typeparamref name="TApp"/> frame via the fluent API.</summary>
    private static byte[] BuildFrame<TApp>(in TApp app, ReadOnlySpan<byte> payload)
        where TApp : struct, IStatelessLayer, IPayloadLayer, IPseudoHeaderIndependent
    {
        FB.CreatedStack<
            FB.StatelessStack<TApp,
                FB.StatelessStack<FB.UdpLayer,
                    FB.StatelessStack<FB.IPv4Layer,
                        FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>>,
            FB.NoTrailer, FB.NoInterceptor> stack = FB.FrameStack
                .Start(_Eth)
                .Then(_Ip4)
                .Then(_Udp)
                .Then(in app)
                .CreateWithFixedValues();

        byte[] buf = new byte[2048];
        FB.FrameSequence<
            FB.StatelessStack<TApp,
                FB.StatelessStack<FB.UdpLayer,
                    FB.StatelessStack<FB.IPv4Layer,
                        FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>>,
            FB.NoTrailer, FB.NoInterceptor> seq = stack.Build(payload);
        seq.MoveNext(buf, out int written);
        byte[] frame = new byte[written];
        buf.AsSpan(0, written).CopyTo(frame);
        return frame;
    }

    #region Header size

    [Test]
    public async Task SomeIpLayer_HeaderSize_Is16()
    {
        SomeIpLayer someIp = new(serviceId: 0x1234, methodId: 0x0001);
        await Assert.That(someIp.HeaderSize).IsEqualTo(SomeIpHeader.Size); // 16
    }

    [Test]
    public async Task SomeIpTpLayer_HeaderSize_Is20()
    {
        SomeIpTpLayer tp = new(serviceId: 0x1234, methodId: 0x0001);
        await Assert.That(tp.HeaderSize).IsEqualTo(20); // 16 + 4
    }

    #endregion

    #region Field layout

    [Test]
    public async Task SomeIpLayer_ServiceId_WrittenAtOffset0()
    {
        SomeIpLayer someIp = new(serviceId: 0xABCD, methodId: 0x0001);
        byte[] frame = BuildFrame(in someIp, [1, 2, 3, 4]);

        ushort serviceId = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(SomeIpOffsetInFrame, 2));
        await Assert.That(serviceId).IsEqualTo((ushort)0xABCD);
    }

    [Test]
    public async Task SomeIpLayer_MethodId_WrittenAtOffset2()
    {
        SomeIpLayer someIp = new(serviceId: 0x0001, methodId: 0x8001);
        byte[] frame = BuildFrame(in someIp, [1, 2, 3, 4]);

        ushort methodId = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(SomeIpOffsetInFrame + 2, 2));
        await Assert.That(methodId).IsEqualTo((ushort)0x8001);
    }

    [Test]
    public async Task SomeIpLayer_MessageType_WrittenAtOffset14()
    {
        SomeIpLayer someIp = new(serviceId: 0x1234, methodId: 0x0001,
            messageType: SomeIpMessageType.Response);
        byte[] frame = BuildFrame(in someIp, [1, 2, 3, 4]);

        byte msgType = frame[SomeIpOffsetInFrame + 14];
        await Assert.That(msgType).IsEqualTo(SomeIpMessageType.Response);
    }

    #endregion

    #region Length fixup

    [Test]
    public async Task SomeIpLayer_Length_IsCorrect_NoPayload()
    {
        // Length = headerSize - 8 = 16 - 8 = 8 (no payload).
        SomeIpLayer someIp = new(serviceId: 0x1234, methodId: 0x0001);
        byte[] frame = BuildFrame(in someIp, ReadOnlySpan<byte>.Empty);

        uint length = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(SomeIpOffsetInFrame + 4, 4));
        await Assert.That(length).IsEqualTo(8u);
    }

    [Test]
    public async Task SomeIpLayer_Length_IsCorrect_WithPayload()
    {
        SomeIpLayer someIp = new(serviceId: 0x1234, methodId: 0x0001);
        byte[] payload = new byte[100];
        byte[] frame = BuildFrame(in someIp, payload);

        uint length = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(SomeIpOffsetInFrame + 4, 4));
        // Length = 16 - 8 + 100 = 108.
        await Assert.That(length).IsEqualTo(108u);
    }

    #endregion
}

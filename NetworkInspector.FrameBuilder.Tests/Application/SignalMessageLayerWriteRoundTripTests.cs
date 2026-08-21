// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests.Application;

/// <summary>
/// Bitfield writeback correctness for structured <see cref="SignalMessageLayer"/> instances.
/// </summary>
internal sealed class SignalMessageLayerWriteRoundTripTests
{
    private static SignalMessageLayout _DualUint16LittleEndianFixture =>
        new()
        {
            PduId = 0x901,
            Name = "smoke",
            UiName = "Smoke",
            ByteLength = 4,
            Signals = ImmutableArray.Create(
                new SignalSpec
                {
                    Name = "A",
                    UiName = "A",
                    StartBit = 0,
                    BitLength = 16,
                    Endian = SignalEndian.Little,
                    Factor = 0.5,
                    Offset = 50.0,
                    Unit = string.Empty,
                },
                new SignalSpec
                {
                    Name = "B",
                    UiName = "B",
                    StartBit = 16,
                    BitLength = 16,
                    Endian = SignalEndian.Little,
                    Factor = 1.0,
                    Offset = 0.0,
                    Unit = string.Empty,
                }),
            DispatchBindings = [],
            Mux = null,
            MuxGroups = [],
        };

    /// <summary>
    /// Encoder stores physical 88 for signal A ⇒ raw (88-50)/0.5 = 76 ⇒ LE bytes 4C00.
    /// Signal B physical 513 ⇒ raw LE 0102.
    /// </summary>
    [Test]
    public async Task WriteHeader_EncodesScaledLittleEndianFields()
    {
        SignalMessageLayout layout = _DualUint16LittleEndianFixture;

        SignalMessageValueSet vals =
            SignalMessageValueSet.For(layout)
                .Set("A", 88.0)
                .Set("B", 513.0);

        SignalMessageLayer layer = new(layout, vals);
        byte[] buffer = new byte[layout.ByteLength];
        layer.WriteHeader(buffer.AsSpan());

        await Assert.That(buffer[0]).IsEqualTo((byte)0x4C); // raw 76
        await Assert.That(buffer[1]).IsEqualTo((byte)0x00);
        await Assert.That(buffer[2]).IsEqualTo((byte)0x01);
        await Assert.That(buffer[3]).IsEqualTo((byte)0x02);
    }

    /// <summary>
    /// FlexRay + Signal Message composition: the 4 signal bytes must appear at frame offset 7
    /// (immediately after the 7-byte FlexRay header).
    /// Signal A physical 88 ⇒ raw 76 = 0x4C; LE bytes 4C 00.
    /// Signal B physical 513 ⇒ raw 513 = 0x0201; LE bytes 01 02.
    /// </summary>
    [Test]
    public async Task FlexRay_SignalMessage_SignalBytesAtCorrectOffset()
    {
        SignalMessageLayout layout = _DualUint16LittleEndianFixture;

        SignalMessageValueSet vals =
            SignalMessageValueSet.For(layout)
                .Set("A", 88.0)
                .Set("B", 513.0);

        SignalMessageLayer spdu = new(layout, vals);

        // Encode signal bytes first, then pass as FlexRay payload (IRootLayer cannot .Then(payloadLayer)).
        byte[] signalBytes = new byte[layout.ByteLength];
        spdu.WriteHeader(signalBytes.AsSpan());

        // frameId=42, cycleCount=0, 4-byte payload → total frame = 7 + 4 = 11 bytes.
        FB.FlexRayLayer flexray = new(frameId: 42, cycleCount: 0, payload: signalBytes.AsMemory());
        FB.CreatedStack<FB.StatelessStack<FB.FlexRayLayer, FB.StackEnd>, FB.NoTrailer, FB.NoInterceptor> stack
            = FB.FrameStack.Start(flexray).CreateWithFixedValues();

        byte[] frame = new byte[11];
        FB.FrameSequence<FB.StatelessStack<FB.FlexRayLayer, FB.StackEnd>, FB.NoTrailer, FB.NoInterceptor> seq
            = stack.Build(ReadOnlySpan<byte>.Empty);
        seq.MoveNext(frame, out int written);

        await Assert.That(written).IsEqualTo(11);

        // Signal bytes are at offset 7 (after the 7-byte FlexRay header).
        await Assert.That(frame[7]).IsEqualTo((byte)0x4C);
        await Assert.That(frame[8]).IsEqualTo((byte)0x00);
        await Assert.That(frame[9]).IsEqualTo((byte)0x01);
        await Assert.That(frame[10]).IsEqualTo((byte)0x02);
    }

    /// <summary>
    /// LIN + Signal Message composition: the 4 signal bytes must appear at frame offset 8
    /// (immediately after the 8-byte LIN header).
    /// Signal A physical 88 ⇒ raw 76 = 0x4C; LE bytes 4C 00.
    /// Signal B physical 513 ⇒ raw 513; LE bytes 01 02.
    /// LIN max payload is 8 bytes; 4 bytes is within spec.
    /// </summary>
    [Test]
    public async Task Lin_SignalMessage_SignalBytesAtCorrectOffset()
    {
        SignalMessageLayout layout = _DualUint16LittleEndianFixture;

        SignalMessageValueSet vals =
            SignalMessageValueSet.For(layout)
                .Set("A", 88.0)
                .Set("B", 513.0);

        SignalMessageLayer spdu = new(layout, vals);

        // Encode signal bytes first, then pass as LIN data (IRootLayer cannot .Then(payloadLayer)).
        byte[] signalBytes = new byte[layout.ByteLength];
        spdu.WriteHeader(signalBytes.AsSpan());

        // frameId=0x10, 4-byte data → total frame = 8 + 4 = 12 bytes.
        FB.LinLayer lin = new(frameId: 0x10, data: signalBytes.AsSpan());
        FB.CreatedStack<FB.StatelessStack<FB.LinLayer, FB.StackEnd>, FB.NoTrailer, FB.NoInterceptor> stack
            = FB.FrameStack.Start(lin).CreateWithFixedValues();

        byte[] frame = new byte[12];
        FB.FrameSequence<FB.StatelessStack<FB.LinLayer, FB.StackEnd>, FB.NoTrailer, FB.NoInterceptor> seq
            = stack.Build(ReadOnlySpan<byte>.Empty);
        seq.MoveNext(frame, out int written);

        await Assert.That(written).IsEqualTo(12);

        // Signal bytes are at offset 8 (after the 8-byte LIN header).
        await Assert.That(frame[8]).IsEqualTo((byte)0x4C);
        await Assert.That(frame[9]).IsEqualTo((byte)0x00);
        await Assert.That(frame[10]).IsEqualTo((byte)0x01);
        await Assert.That(frame[11]).IsEqualTo((byte)0x02);
    }
}

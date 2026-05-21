// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Smoke tests for the Stage-0 capability surface introduced ahead of the
/// <see cref="TcpConnection{TC2S,TS2C}"/> work (plan section 3.0.1 / 3.0.2):
/// the <see cref="IStreamCarrier"/> and <see cref="IStreamProducer"/>
/// markers, plus the <see cref="FrameSink"/> delegate contract.
/// </summary>
/// <remarks>
/// The markers themselves carry no behaviour; these tests pin the
/// expected type-system shape (TCP layers carry <see cref="IStreamCarrier"/>,
/// UDP deliberately does not) and exercise the
/// <see cref="IStreamProducer"/> contract end-to-end against a
/// pooled <see cref="ArrayBufferWriter{T}"/>.
/// </remarks>
[NotInParallel(nameof(StreamCapabilitySmokeTests))]
internal sealed class StreamCapabilitySmokeTests
{
    [Test]
    public async Task TcpLayer_IsStreamCarrier()
        => await Assert.That(typeof(IStreamCarrier).IsAssignableFrom(typeof(TcpLayer))).IsTrue();

    [Test]
    public async Task TcpLayerWithOptions_IsStreamCarrier()
        => await Assert.That(typeof(IStreamCarrier).IsAssignableFrom(typeof(TcpLayerWithOptions))).IsTrue();

    [Test]
    public async Task TcpLayerWithAutoSequence_IsStreamCarrier()
        => await Assert.That(typeof(IStreamCarrier).IsAssignableFrom(typeof(TcpLayerWithAutoSequence))).IsTrue();

    [Test]
    public async Task UdpLayer_IsNotStreamCarrier()
        => await Assert.That(typeof(IStreamCarrier).IsAssignableFrom(typeof(UdpLayer))).IsFalse();

    [Test]
    public async Task IStreamProducer_WritesIntoSuppliedBufferWriter()
    {
        EchoProducer producer = new("hello-stream"u8.ToArray());
        ArrayBufferWriter<byte> writer = new();

        ((IStreamProducer)producer).WriteStream(writer);

        await Assert.That(writer.WrittenCount).IsEqualTo("hello-stream"u8.Length);
        await Assert.That(writer.WrittenSpan.SequenceEqual("hello-stream"u8)).IsTrue();
    }

    [Test]
    public async Task FrameSink_DelegateInvokedOncePerFrame()
    {
        List<byte[]> frames = [];
        FrameSink sink = frame => frames.Add(frame.ToArray());

        sink([0x01, 0x02]);
        sink([0xAA, 0xBB, 0xCC]);
        sink([]);

        await Assert.That(frames.Count).IsEqualTo(3);
        await Assert.That(frames[0].AsSpan().SequenceEqual([(byte)0x01, (byte)0x02])).IsTrue();
        await Assert.That(frames[1].AsSpan().SequenceEqual([(byte)0xAA, (byte)0xBB, (byte)0xCC])).IsTrue();
        await Assert.That(frames[2].Length).IsEqualTo(0);
    }

    /// <summary>
    /// Reference <see cref="IStreamProducer"/> implementation used by the
    /// smoke test above; copies a fixed buffer into the supplied writer.
    /// </summary>
    private sealed class EchoProducer(byte[] payload) : IStreamProducer
    {
        public void WriteStream(IBufferWriter<byte> writer)
        {
            Span<byte> dst = writer.GetSpan(payload.Length);
            payload.CopyTo(dst);
            writer.Advance(payload.Length);
        }
    }
}

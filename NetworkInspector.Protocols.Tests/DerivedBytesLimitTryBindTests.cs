// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for <see cref="DerivedBytesLimit.TryBind"/>, the one place where a protocol applies its own per-packet total to the sum of
/// all bytes bound to the packet: the limit is inclusive and overflow-free, a refused buffer changes nothing, the bytes of other
/// binders count, and a full table is told from an exceeded total.
/// A probe protocol on a UDP port runs a script of binds on the packet and records each result.
/// </summary>
internal sealed class DerivedBytesLimitTryBindTests
{
    private const ushort _ProbePort = 15610;

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    private readonly record struct Result(bool Bound, DerivedBytesFailure Failure, ulong BoundBytesAfter);

    /// <summary>Binds <paramref name="preBound"/> one-byte buffers directly, then one buffer per entry of
    /// <paramref name="sizes"/> through the helper.</summary>
    private sealed class ScriptProtocol(ulong limit, int preBoundBytes, int preBoundBuffers, int[] sizes, List<Result> results) : IProtocol
    {
        public string Name => "bind.script";

        public string UiName => "Bind Script";

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            if (preBoundBytes > 0)
            {
                _ = parentField.TryBindBuffer(new byte[preBoundBytes], out _, out _);
            }

            for (int index = 0; index < preBoundBuffers; index++)
            {
                _ = parentField.TryBindBuffer(new byte[1], out _, out _);
            }

            foreach (int size in sizes)
            {
                bool bound = DerivedBytesLimit.TryBind(in parentField, new byte[size], limit, out _, out DerivedBytesFailure failure);
                results.Add(new Result(bound, failure, parentField.BoundBytes));
            }

            return data.Length;
        }
    }

    private static List<Result> _Run(ulong limit, int[] sizes, int preBoundBytes = 0, int preBoundBuffers = 0)
    {
        List<Result> results = [];
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        builder.RegisterProtocol(new ScriptProtocol(limit, preBoundBytes, preBoundBuffers, sizes, results), static (b, id, _) =>
        {
            b.RegisterParserInU64TableByName(UdpProtocol.PortTableName, _ProbePort, id);
        });
        using Stack stack = builder.Build();
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002));
        UdpLayer udp = new(40000, _ProbePort);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(new byte[8]);

        _ = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));
        return results;
    }

    /// <summary>A buffer that brings the sum exactly to the limit is bound; one byte more is refused with the total failure and binds nothing.</summary>
    [Test]
    public async Task TheTotalIsInclusiveAndARefusalChangesNothing()
    {
        List<Result> atLimit = _Run(1000, [1000]);
        List<Result> overLimit = _Run(1000, [1001]);

        await Assert.That(atLimit[0].Bound).IsTrue();
        await Assert.That(atLimit[0].BoundBytesAfter).IsEqualTo(1000UL);
        await Assert.That(overLimit[0].Bound).IsFalse();
        await Assert.That(overLimit[0].Failure).IsEqualTo(DerivedBytesFailure.TotalExceeded);
        await Assert.That(overLimit[0].BoundBytesAfter).IsEqualTo(0UL);
    }

    /// <summary>A refused buffer counts for nothing: after 400 bound and 601 refused, 600 still fits (400 + 600 = 1000).</summary>
    [Test]
    public async Task ASmallerBufferStillFitsAfterARefusal()
    {
        List<Result> results = _Run(1000, [400, 601, 600]);

        await Assert.That(results[0].Bound).IsTrue();
        await Assert.That(results[1].Bound).IsFalse();
        await Assert.That(results[1].BoundBytesAfter).IsEqualTo(400UL);
        await Assert.That(results[2].Bound).IsTrue();
        await Assert.That(results[2].BoundBytesAfter).IsEqualTo(1000UL);
    }

    /// <summary>Limit 0 refuses every non-empty buffer and accepts an empty one; the largest limit never overflows.</summary>
    [Test]
    public async Task TheExtremeLimitsAreHandledWithoutOverflow()
    {
        List<Result> zero = _Run(0, [1, 0]);
        List<Result> largest = _Run(ulong.MaxValue, [10, 10], preBoundBytes: 5);

        await Assert.That(zero[0].Bound).IsFalse();
        await Assert.That(zero[0].Failure).IsEqualTo(DerivedBytesFailure.TotalExceeded);
        await Assert.That(zero[1].Bound).IsTrue();
        await Assert.That(largest[0].Bound).IsTrue();
        await Assert.That(largest[1].Bound).IsTrue();
        await Assert.That(largest[1].BoundBytesAfter).IsEqualTo(25UL);
    }

    /// <summary>Bytes bound by anyone else count against the total: 600 already bound leaves room for 400, not 600.</summary>
    [Test]
    public async Task BytesBoundByAnotherBinderCountAgainstTheTotal()
    {
        List<Result> results = _Run(1000, [600, 400], preBoundBytes: 600);

        await Assert.That(results[0].Bound).IsFalse();
        await Assert.That(results[0].Failure).IsEqualTo(DerivedBytesFailure.TotalExceeded);
        await Assert.That(results[1].Bound).IsTrue();
        await Assert.That(results[1].BoundBytesAfter).IsEqualTo(1000UL);
    }

    /// <summary>With 255 buffers stored the table failure is reported; when the total is exceeded as well, the total is named first.</summary>
    [Test]
    public async Task AFullTableIsToldFromAnExceededTotal()
    {
        List<Result> tableFull = _Run(ulong.MaxValue, [1], preBoundBuffers: 255);
        List<Result> both = _Run(100, [200], preBoundBuffers: 255);

        await Assert.That(tableFull[0].Bound).IsFalse();
        await Assert.That(tableFull[0].Failure).IsEqualTo(DerivedBytesFailure.TableFull);
        await Assert.That(both[0].Failure).IsEqualTo(DerivedBytesFailure.TotalExceeded);
    }
}

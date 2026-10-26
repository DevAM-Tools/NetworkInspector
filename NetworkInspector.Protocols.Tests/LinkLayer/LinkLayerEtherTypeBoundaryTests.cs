// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the boundary between a length and an EtherType in the Linux cooked capture headers: IEEE 802.3 defines a
/// value of 0x0600 (1536) or more as an EtherType, as Ethernet does, so exactly 0x0600 is dispatched.
/// </summary>
internal sealed class LinkLayerEtherTypeBoundaryTests
{
    private const ushort _BoundaryEtherType = 0x0600;

    /// <summary>Counts the payloads it receives.</summary>
    private sealed class CountingProtocol : IProtocol
    {
        public string Name => "ethertype.probe";

        public string UiName => "EtherType Probe";

        public int Calls { get; private set; }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            Calls++;
            return data.Length;
        }
    }

    private static Stack _BuildStack(CountingProtocol probe)
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        builder.RegisterProtocol(probe, static (b, id, _) =>
        {
            b.RegisterParserInU64TableByName(EthernetProtocol.EtherTypeTableName, _BoundaryEtherType, id);
        });
        return builder.Build();
    }

    /// <summary>Linux cooked capture v1: 16-byte header (protocol in the last two bytes) plus 4 payload bytes.</summary>
    private static byte[] _SllFrame(ushort protocol)
    {
        byte[] frame = new byte[16 + 4];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(14), protocol);
        return frame;
    }

    /// <summary>Linux cooked capture v2: 20-byte header (protocol in the first two bytes) plus 4 payload bytes.</summary>
    private static byte[] _Sll2Frame(ushort protocol)
    {
        byte[] frame = new byte[20 + 4];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), protocol);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), 1);
        frame[11] = 6;
        return frame;
    }

    [Test]
    public async Task SllDispatchesAnEtherTypeOfExactly0x0600()
    {
        CountingProtocol probe = new();
        using Stack stack = _BuildStack(probe);

        _ = ProtocolTestHelper.ParseFrame(stack, _SllFrame(_BoundaryEtherType), 0, Timestamp.FromMillis(0), LinkType.LinuxSll);

        await Assert.That(probe.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task SllDoesNotDispatchAValueBelow0x0600()
    {
        CountingProtocol probe = new();
        using Stack stack = _BuildStack(probe);

        _ = ProtocolTestHelper.ParseFrame(stack, _SllFrame(_BoundaryEtherType - 1), 0, Timestamp.FromMillis(0), LinkType.LinuxSll);

        await Assert.That(probe.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Sll2DispatchesAnEtherTypeOfExactly0x0600()
    {
        CountingProtocol probe = new();
        using Stack stack = _BuildStack(probe);

        _ = ProtocolTestHelper.ParseFrame(stack, _Sll2Frame(_BoundaryEtherType), 0, Timestamp.FromMillis(0), LinkType.LinuxSll2);

        await Assert.That(probe.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Sll2DoesNotDispatchAValueBelow0x0600()
    {
        CountingProtocol probe = new();
        using Stack stack = _BuildStack(probe);

        _ = ProtocolTestHelper.ParseFrame(stack, _Sll2Frame(_BoundaryEtherType - 1), 0, Timestamp.FromMillis(0), LinkType.LinuxSll2);

        await Assert.That(probe.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task EthernetDispatchesAnEtherTypeOfExactly0x0600()
    {
        // Control for the boundary the cooked captures now share with Ethernet.
        CountingProtocol probe = new();
        using Stack stack = _BuildStack(probe);
        byte[] frame = new byte[14 + 4];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), _BoundaryEtherType);

        _ = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await Assert.That(probe.Calls).IsEqualTo(1);
    }
}

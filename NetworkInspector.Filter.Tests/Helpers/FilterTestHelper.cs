// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Helpers;

/// <summary>
/// Builds real protocol stacks and synthetic Ethernet/IPv4/UDP frames so filter tests exercise
/// the same field tree the production parsers produce.
/// </summary>
internal static class FilterTestHelper
{
    #region Stack

    /// <summary>Creates a stack with all standard protocols registered. The caller disposes it.</summary>
    public static Stack BuildStack()
    {
#pragma warning disable CA2000 // Ownership of the settings manager transfers to the stack.
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
#pragma warning restore CA2000
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    /// <summary>
    /// Creates a stack that additionally knows a protocol which does <b>not</b> register a field
    /// named after itself. Such a protocol has no container field, which is the case the
    /// evaluator's owner-scan fallback exists for.
    /// </summary>
    public static Stack BuildStackWithContainerlessProtocol()
    {
#pragma warning disable CA2000 // Ownership of the settings manager transfers to the stack.
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
#pragma warning restore CA2000
        ProtocolRegistration.RegisterStandardProtocols(builder);
        ContainerlessProtocol protocol = new();
        _ = builder.RegisterProtocol(
            protocol,
            static (stackBuilder, protocolId, _) =>
                stackBuilder.RegisterField(protocolId, "noctr.value", "Value", FieldType.U64));
        return builder.Build();
    }

    /// <summary>Creates a stack that only knows Ethernet, used to test rebinding failures.</summary>
    public static Stack BuildEthernetOnlyStack()
    {
#pragma warning disable CA2000 // Ownership of the settings manager transfers to the stack.
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
#pragma warning restore CA2000
        _ = builder.RegisterProtocol(new EthernetProtocol());
        return builder.Build();
    }

    /// <summary>Field-to-protocol owner table for a stack, as the evaluator sees it.</summary>
    public static ProtocolId[] FieldOwners(IStack stack) => new SymbolResolver(stack).FieldOwners;

    /// <summary>Resolves a protocol id, failing the test when the stack does not know the name.</summary>
    public static ProtocolId ProtocolIdOf(IStack stack, string name)
    {
        ArgumentNullException.ThrowIfNull(stack);

        ProtocolId? id = stack.GetProtocolId(name);
        if (id is not ProtocolId protocolId)
        {
            throw new InvalidOperationException($"Stack does not know protocol '{name}'.");
        }
        return protocolId;
    }

    /// <summary>Resolves a field id, failing the test when the stack does not know the name.</summary>
    public static FieldId FieldIdOf(IStack stack, string name)
    {
        ArgumentNullException.ThrowIfNull(stack);

        FieldId? id = stack.GetFieldId(name);
        if (id is not FieldId fieldId)
        {
            throw new InvalidOperationException($"Stack does not know field '{name}'.");
        }
        return fieldId;
    }

    #endregion

    #region Frames

    /// <summary>Builds an Ethernet + IPv4 + UDP frame with the given ports, TTL and payload.</summary>
    public static byte[] BuildUdpFrame(
        ushort sourcePort,
        ushort destinationPort,
        byte timeToLive = 64,
        byte[]? payload = null)
    {
        byte[] body = payload ?? [0xDE, 0xAD, 0xBE, 0xEF];
        int udpLength = 8 + body.Length;
        int ipLength = 20 + udpLength;
        byte[] frame = new byte[14 + ipLength];

        // Ethernet II.
        byte[] destinationMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        byte[] sourceMac = [0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB];
        destinationMac.CopyTo(frame, 0);
        sourceMac.CopyTo(frame, 6);
        frame[12] = 0x08;
        frame[13] = 0x00;

        // IPv4.
        int ip = 14;
        frame[ip] = 0x45;
        frame[ip + 1] = 0x00;
        frame[ip + 2] = (byte)(ipLength >> 8);
        frame[ip + 3] = (byte)ipLength;
        frame[ip + 4] = 0x00;
        frame[ip + 5] = 0x01;
        frame[ip + 6] = 0x40;
        frame[ip + 7] = 0x00;
        frame[ip + 8] = timeToLive;
        frame[ip + 9] = 17;
        frame[ip + 10] = 0x00;
        frame[ip + 11] = 0x00;
        frame[ip + 12] = 192;
        frame[ip + 13] = 168;
        frame[ip + 14] = 1;
        frame[ip + 15] = 10;
        frame[ip + 16] = 192;
        frame[ip + 17] = 168;
        frame[ip + 18] = 1;
        frame[ip + 19] = 20;

        // UDP.
        int udp = ip + 20;
        frame[udp] = (byte)(sourcePort >> 8);
        frame[udp + 1] = (byte)sourcePort;
        frame[udp + 2] = (byte)(destinationPort >> 8);
        frame[udp + 3] = (byte)destinationPort;
        frame[udp + 4] = (byte)(udpLength >> 8);
        frame[udp + 5] = (byte)udpLength;
        frame[udp + 6] = 0x00;
        frame[udp + 7] = 0x00;
        body.CopyTo(frame, udp + 8);

        return frame;
    }

    /// <summary>
    /// Builds an Ethernet + IPv4 + UDP frame carrying a minimal DNS query for
    /// <c>example.com</c>, which gives the tests a real string field to work with.
    /// </summary>
    public static byte[] BuildDnsQueryFrame(ushort transactionId = 0x1234, string label = "example")
    {
        ArgumentNullException.ThrowIfNull(label);

        List<byte> query =
        [
            (byte)(transactionId >> 8), (byte)transactionId,
            0x01, 0x00,
            0x00, 0x01,
            0x00, 0x00,
            0x00, 0x00,
            0x00, 0x00,
            (byte)label.Length,
        ];

        foreach (char character in label)
        {
            query.Add((byte)character);
        }

        query.AddRange([0x03, (byte)'c', (byte)'o', (byte)'m', 0x00, 0x00, 0x01, 0x00, 0x01]);

        return BuildUdpFrame(40000, 53, payload: [.. query]);
    }

    /// <summary>
    /// Builds a QinQ frame (Ethernet + outer VLAN + inner VLAN + IPv4 + UDP) so scope tests have
    /// two occurrences of the same protocol at different depths.
    /// </summary>
    public static byte[] BuildDoubleVlanUdpFrame(
        ushort outerVlanId = 100,
        ushort innerVlanId = 200,
        ushort sourcePort = 53,
        ushort destinationPort = 1024)
    {
        byte[] inner = BuildUdpFrame(sourcePort, destinationPort);
        byte[] frame = new byte[inner.Length + 8];

        Array.Copy(inner, 0, frame, 0, 12);
        frame[12] = 0x81;
        frame[13] = 0x00;
        frame[14] = (byte)(outerVlanId >> 8);
        frame[15] = (byte)outerVlanId;
        frame[16] = 0x81;
        frame[17] = 0x00;
        frame[18] = (byte)(innerVlanId >> 8);
        frame[19] = (byte)innerVlanId;
        frame[20] = 0x08;
        frame[21] = 0x00;
        Array.Copy(inner, 14, frame, 22, inner.Length - 14);

        return frame;
    }

    /// <summary>Builds an Ethernet + IPv4 + TCP frame; used to test protocol presence and pruning.</summary>
    public static byte[] BuildTcpFrame(ushort sourcePort, ushort destinationPort)
    {
        int tcpLength = 20;
        int ipLength = 20 + tcpLength;
        byte[] frame = new byte[14 + ipLength];

        byte[] destinationMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        byte[] sourceMac = [0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB];
        destinationMac.CopyTo(frame, 0);
        sourceMac.CopyTo(frame, 6);
        frame[12] = 0x08;
        frame[13] = 0x00;

        int ip = 14;
        frame[ip] = 0x45;
        frame[ip + 2] = (byte)(ipLength >> 8);
        frame[ip + 3] = (byte)ipLength;
        frame[ip + 5] = 0x02;
        frame[ip + 8] = 64;
        frame[ip + 9] = 6;
        frame[ip + 12] = 10;
        frame[ip + 13] = 0;
        frame[ip + 14] = 0;
        frame[ip + 15] = 1;
        frame[ip + 16] = 10;
        frame[ip + 17] = 0;
        frame[ip + 18] = 0;
        frame[ip + 19] = 2;

        int tcp = ip + 20;
        frame[tcp] = (byte)(sourcePort >> 8);
        frame[tcp + 1] = (byte)sourcePort;
        frame[tcp + 2] = (byte)(destinationPort >> 8);
        frame[tcp + 3] = (byte)destinationPort;
        frame[tcp + 12] = 0x50;
        frame[tcp + 13] = 0x02;

        return frame;
    }

    #endregion

    #region Parsing

    /// <summary>Parses one frame on the given stack.</summary>
    public static Packet Parse(Stack stack, byte[] frameData, int packetId = 0, long timestampNanos = 0)
    {
        Frame frame = Frame.Create(
            new FrameId(packetId),
            Timestamp.FromNanos(timestampNanos),
            frameData,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        return Packet.ParseFrame(new PacketId(packetId), stack, frame);
    }

    /// <summary>Parses one frame on the given stack while recording presence into an index.</summary>
    public static Packet ParseIndexed(
        Stack stack,
        PacketIndex index,
        byte[] frameData,
        int packetId = 0,
        long timestampNanos = 0)
    {
        Frame frame = Frame.Create(
            new FrameId(packetId),
            Timestamp.FromNanos(timestampNanos),
            frameData,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        return Packet.ParseFrameIndexed(new PacketId(packetId), stack, frame, index);
    }

    #endregion

    #region Assertions

    /// <summary>Compiles an expression and fails the test when compilation does not succeed.</summary>
    public static Filter CompileOrThrow(string expression, IStack stack)
    {
        FilterResult<Filter> result = Filter.Compile(expression, stack);
        if (!result.TryGetValue(out Filter? filter))
        {
            throw new InvalidOperationException($"Expected '{expression}' to compile but got {result.Error}");
        }
        return filter;
    }

    /// <summary>Evaluates a packet and fails the test when evaluation reports an error.</summary>
    public static bool MatchOrThrow<TIndex>(Filter filter, Packet packet, TIndex? index = default)
        where TIndex : IPacketIndexReader
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (!filter.TryIsMatch(packet, index, out bool matched, out FilterError? failure))
        {
            throw new InvalidOperationException($"Evaluation failed: {failure}");
        }
        return matched;
    }

    /// <summary>Evaluates a packet without a presence index.</summary>
    public static bool MatchOrThrow(Filter filter, Packet packet)
        => MatchOrThrow<IPacketIndexReader>(filter, packet, null);

    #endregion
}

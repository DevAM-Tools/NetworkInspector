// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Multicast Listener Discovery tests: the MLDv1 messages (RFC 2710), the MLDv2 query and report (RFC 3810) and the
/// floating-point decoding of the response code and the query interval code.
/// </summary>
internal sealed class Icmpv6MldTests
{
    /// <summary>The multicast group ff02::1:ff00:1 used by the frames in this fixture.</summary>
    private static readonly byte[] _Group = [0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0xFF, 0, 0, 0x01];

    /// <summary>Source address 2001:db8::1.</summary>
    private static readonly byte[] _SourceA = [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];

    /// <summary>Source address 2001:db8::2.</summary>
    private static readonly byte[] _SourceB = [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02];

    /// <summary>Builds an MLDv1 body: maximum response delay, reserved, multicast address (20 bytes after the ICMPv6 header).</summary>
    private static byte[] _V1Body(ushort delayMs)
    {
        byte[] body = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(0, 2), delayMs);
        _Group.CopyTo(body.AsSpan(4));
        return body;
    }

    /// <summary>Builds an MLDv2 query body: response code, reserved, address, flags, QQIC, source count and sources.</summary>
    private static byte[] _V2QueryBody(ushort responseCode, byte flags, byte qqic, ushort declaredSources, params byte[][] sources)
    {
        byte[] body = new byte[24 + (16 * sources.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(0, 2), responseCode);
        _Group.CopyTo(body.AsSpan(4));
        body[20] = flags;
        body[21] = qqic;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(22, 2), declaredSources);
        for (int i = 0; i < sources.Length; i++)
        {
            sources[i].CopyTo(body.AsSpan(24 + (16 * i)));
        }

        return body;
    }

    /// <summary>Appends one multicast address record (type, auxiliary words, sources, auxiliary data) to <paramref name="records"/>.</summary>
    private static void _AppendRecord(List<byte> records, byte type, byte[][] sources, byte[] auxData)
    {
        records.Add(type);
        records.Add((byte)(auxData.Length / 4));
        records.Add(0);
        records.Add((byte)sources.Length);
        records.AddRange(_Group);
        foreach (byte[] source in sources)
        {
            records.AddRange(source);
        }

        records.AddRange(auxData);
    }

    /// <summary>Builds an MLDv2 report body: reserved, record count, then the given records.</summary>
    private static byte[] _ReportBody(ushort declaredRecords, List<byte> records)
    {
        byte[] body = new byte[4 + records.Count];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2, 2), declaredRecords);
        records.CopyTo(body, 4);
        return body;
    }

    #region MLDv1

    [Test]
    [Arguments((byte)130)]
    [Arguments((byte)131)]
    [Arguments((byte)132)]
    public async Task Mldv1MessagesShowDelayAndAddress(byte type)
    {
        byte[] frame = Icmpv6PacketBuilder.Build(type, 0, _V1Body(10_000));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mld.maximum_response_delay", 10_000).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.mld.multicast_address", "ff02::1:ff00:1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.mld.maximum_response_code").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.data").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task MldMessageShorterThanTheV1LayoutShowsNoMldFields()
    {
        // 4 header bytes + 8 body bytes = 12 bytes, less than the 24 of an MLDv1 message.
        byte[] frame = Icmpv6PacketBuilder.Build(130, 0, new byte[8]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.mld.multicast_address").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.data").ConfigureAwait(false);
        }
    }

    #endregion

    #region MLDv2 query

    [Test]
    public async Task Mldv2QueryDecodesTheFloatingPointResponseCode()
    {
        // 0x8FFF: exponent 0, mantissa 0xFFF -> (0xFFF | 0x1000) << 3 = 65528 ms.
        byte[] frame = Icmpv6PacketBuilder.Build(130, 0, _V2QueryBody(0x8FFF, 0, 0, 0));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mld.maximum_response_code", 0x8FFF).ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.mld.maximum_response_code", "65528 ms").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.mld.maximum_response_delay").ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.mld.multicast_address", "ff02::1:ff00:1").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Mldv2QueryShowsFlagsIntervalAndSources()
    {
        // Flags 0x0A: S set, QRV 2. QQIC 125 is below 128, so it is the interval itself.
        byte[] frame = Icmpv6PacketBuilder.Build(130, 0, _V2QueryBody(1000, 0x0A, 125, 2, _SourceA, _SourceB));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "icmpv6.mld.flag.s", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mld.flag.qrv", 2).ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.mld.flag", "[S, QRV=2]").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mld.qqi", 125).ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.mld.qqi", "125 s").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mld.nb_sources", 2).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.mld.source_address", "2001:db8::1").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Mldv2QueryDeclaringMoreSourcesThanItCarriesKeepsTheDeclaredCount()
    {
        // The query declares 5 sources but carries one: the count stays the wire value so the cut is visible.
        byte[] frame = Icmpv6PacketBuilder.Build(130, 0, _V2QueryBody(1000, 0, 0, 5, _SourceA));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mld.nb_sources", 5).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.mld.source_address", "2001:db8::1").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Mldv2QueryWithoutSourcesHasNoSourceField()
    {
        byte[] frame = Icmpv6PacketBuilder.Build(130, 0, _V2QueryBody(1000, 0, 0, 0));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.mld.source_address").ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments((ushort)0x0000, 0u)]
    [Arguments((ushort)0x7FFF, 32767u)]
    [Arguments((ushort)0x8000, 32768u)]
    [Arguments((ushort)0x8FFF, 65528u)]
    [Arguments((ushort)0xFFFF, 8_387_584u)]
    public async Task MaxResponseCodeDecodingCoversTheBoundaries(ushort code, uint expected) =>
        await Assert.That(Icmpv6MldParser.DecodeMaxResponseCode(code)).IsEqualTo(expected);

    [Test]
    [Arguments((byte)0x00, 0u)]
    [Arguments((byte)0x7F, 127u)]
    [Arguments((byte)0x80, 128u)]
    [Arguments((byte)0xFF, 31744u)]
    public async Task QueryIntervalCodeDecodingCoversTheBoundaries(byte code, uint expected) =>
        await Assert.That(Icmpv6MldParser.DecodeQqic(code)).IsEqualTo(expected);

    #endregion

    #region MLDv2 report

    [Test]
    public async Task Mldv2ReportListsEveryRecord()
    {
        List<byte> records = [];
        _AppendRecord(records, 4, [_SourceA, _SourceB], []);
        _AppendRecord(records, 2, [], [1, 2, 3, 4]);
        byte[] frame = Icmpv6PacketBuilder.Build(143, 0, _ReportBody(2, records));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mldr.nb_mcast_records", 2).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mldr.mar.record_type", 4).ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "icmpv6.mldr.mar.record_type", "Change To Exclude Mode (4)").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mldr.mar.nb_sources", 2).ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.mldr.mar.multicast_address", "ff02::1:ff00:1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertIPv6Field(stack, packet, "icmpv6.mldr.mar.source_address", "2001:db8::1").ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "icmpv6.mldr.mar.auxiliary_data", [1, 2, 3, 4]).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.data").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Mldv2ReportWithoutRecordsShowsOnlyTheCount()
    {
        byte[] frame = Icmpv6PacketBuilder.Build(143, 0, _ReportBody(0, []));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mldr.nb_mcast_records", 0).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.mldr.mar").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Mldv2ReportEndsTheWalkAtARecordThatDoesNotFit()
    {
        // The second record declares 4 auxiliary bytes that are cut off: it is dropped, the first stays.
        List<byte> records = [];
        _AppendRecord(records, 1, [], []);
        _AppendRecord(records, 5, [], [9, 9, 9, 9]);
        records.RemoveRange(records.Count - 4, 4);
        byte[] frame = Icmpv6PacketBuilder.Build(143, 0, _ReportBody(2, records));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mldr.nb_mcast_records", 2).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mldr.mar.record_type", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "icmpv6.mldr.mar.auxiliary_data").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Mldv2ReportDeclaringMoreRecordsThanItCarriesDoesNotLoop()
    {
        // 65535 declared records in a message with one: the walk is bounded by the message, not by the count.
        List<byte> records = [];
        _AppendRecord(records, 1, [], []);
        byte[] frame = Icmpv6PacketBuilder.Build(143, 0, _ReportBody(ushort.MaxValue, records));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mldr.nb_mcast_records", ushort.MaxValue).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "icmpv6.mldr.mar.record_type", 1).ConfigureAwait(false);
        }
    }

    #endregion

    #region Concurrency

    [Test]
    public async Task ConcurrentMaterializationYieldsTheSameFieldsAsASingleThread()
    {
        List<byte> records = [];
        _AppendRecord(records, 4, [_SourceA, _SourceB], []);
        await ConcurrentMaterialization.AssertMatchesSingleThreadedResult(
            Icmpv6PacketBuilder.Build(130, 0, _V1Body(10_000)),
            Icmpv6PacketBuilder.Build(130, 0, _V2QueryBody(0x8FFF, 0x0A, 125, 2, _SourceA, _SourceB)),
            Icmpv6PacketBuilder.Build(143, 0, _ReportBody(1, records))).ConfigureAwait(false);
    }

    #endregion
}

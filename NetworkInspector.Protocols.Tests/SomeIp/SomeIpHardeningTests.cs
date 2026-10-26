// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for SOME/IP and SOME/IP-SD parsing of crafted input: array lengths that do not fit a signed 32-bit integer, and the
/// index groups recorded for Service Discovery messages that lack entries, options or a payload.
/// </summary>
internal sealed class SomeIpHardeningTests
{
    private static readonly EthernetLayer _Eth = new(
        MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
        MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));

    private static readonly IPv4Layer _Ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));

    private static byte[] _SdFrame(ReadOnlySpan<byte> sdPayload)
    {
        SomeIpLayer someIp = new(serviceId: 0xFFFF, methodId: 0x8100, messageType: SomeIpMessageType.Notification);
        UdpLayer udp = new(srcPort: 30490, dstPort: 30490);
        return FrameStack.Start(_Eth).Then(_Ip).Then(udp).Then(someIp).CreateWithFixedValues().EmitFrame(sdPayload);
    }

    /// <summary>SD payload: flags, reserved, entries length, no entries, options length, then 'tail' zero bytes.</summary>
    private static byte[] _SdPayload(uint entriesLength, uint? optionsLength, int tail)
    {
        List<byte> payload = [0xC0, 0x00, 0x00, 0x00];
        payload.AddRange(_BigEndian(entriesLength));
        if (optionsLength is uint options)
        {
            payload.AddRange(_BigEndian(options));
        }

        payload.AddRange(new byte[tail]);
        return payload.ToArray();
    }

    private static byte[] _BigEndian(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    #region SD array lengths

    [Test]
    [Arguments(0x7FFFFFF8u)]
    [Arguments(0x7FFFFFF9u)]
    [Arguments(0x7FFFFFFFu)]
    [Arguments(0x80000000u)]
    [Arguments(0xFFFFFFFFu)]
    public async Task EntriesLengthBeyondThePayloadIsReportedAsAnError(uint entriesLength)
    {
        byte[] frame = _SdFrame(_SdPayload(entriesLength, optionsLength: 0, tail: 16));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "Entries array extends beyond SD payload")
                .ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments(0x7FFFFFFDu)]
    [Arguments(0x7FFFFFFFu)]
    [Arguments(0x80000000u)]
    [Arguments(0xFFFFFFFFu)]
    public async Task OptionsLengthBeyondThePayloadIsReportedAsAnError(uint optionsLength)
    {
        byte[] frame = _SdFrame(_SdPayload(entriesLength: 0, optionsLength, tail: 8));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "Options array extends beyond SD payload")
                .ConfigureAwait(false);
        }
    }

    [Test]
    public async Task OptionsLengthOfExactlyTheRemainingBytesIsNotAnError()
    {
        // Control: a declared options array that ends exactly at the payload end is valid.
        byte[] frame = _SdFrame(_SdPayload(entriesLength: 0, optionsLength: 3, tail: 3));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }
    }

    #endregion

    #region SD index groups

    private static byte[] _Entry() => new byte[16];

    [Test]
    public async Task SdWithoutAnyPayloadRecordsNoSdGroup()
    {
        // SOME/IP length 8 and no payload: there is nothing to decode as Service Discovery.
        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(_SdFrame([])).ConfigureAwait(false);
    }

    [Test]
    public async Task SdEndingAfterTheEntriesLengthRecordsNoOptionsGroup()
    {
        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(_SdFrame(_SdPayload(0, optionsLength: null, tail: 0)))
            .ConfigureAwait(false);
    }

    [Test]
    public async Task SdWithAnEmptyOptionsArrayRecordsNoOptionsGroup()
    {
        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(_SdFrame(_SdPayload(0, optionsLength: 0, tail: 0)))
            .ConfigureAwait(false);
    }

    [Test]
    public async Task SdWithAnEntryAndAnOptionRecordsBothGroups()
    {
        byte[] payload = [.. new byte[] { 0xC0, 0, 0, 0 }, .. _BigEndian(16), .. _Entry(), .. _BigEndian(4), .. new byte[] { 0, 1, 0x01, 0 }];

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(_SdFrame(payload)).ConfigureAwait(false);
    }

    [Test]
    public async Task SdWithAnEntriesArrayBeyondThePayloadRecordsNoEntriesGroup()
    {
        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(_SdFrame(_SdPayload(1000, optionsLength: null, tail: 0)))
            .ConfigureAwait(false);
    }

    [Test]
    public async Task SdWithAnOptionsArrayBeyondThePayloadRecordsNoOptionsGroup()
    {
        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(_SdFrame(_SdPayload(0, optionsLength: 1000, tail: 4)))
            .ConfigureAwait(false);
    }

    #endregion
}

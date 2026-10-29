// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure.SomeIp;

/// <summary>
/// Builds SOME/IP service discovery payloads from entries and options, with the lengths computed from what was added unless a test
/// overrides them to produce a malformed payload.
/// <para>
/// The layout it writes (big-endian): a flags byte, three reserved bytes, the entries length, the entries (16 bytes each), the
/// options length, the options. An option is a 2-byte length (counting the bytes behind the type), the type, a reserved byte and
/// the data. <b>Thread-safety:</b> not thread-safe, one instance per test.
/// </para>
/// </summary>
internal sealed class SomeIpSdBuilder
{
    private readonly List<byte[]> _Entries = [];
    private readonly List<byte[]> _Options = [];

    /// <summary>The flags byte (default 0xC0: reboot and unicast).</summary>
    internal byte Flags { get; set; } = 0xC0;

    /// <summary>Overrides the entries length field; <see langword="null"/> writes the real length.</summary>
    internal uint? EntriesLengthOverride { get; set; }

    /// <summary>Overrides the options length field; <see langword="null"/> writes the real length.</summary>
    internal uint? OptionsLengthOverride { get; set; }

    /// <summary>Leaves out the options length field and the options altogether.</summary>
    internal bool OmitOptions { get; set; }

    /// <summary>Bytes appended after the options (a payload that is longer than it declares).</summary>
    internal byte[] Trailing { get; set; } = [];

    /// <summary>Adds an entry of 16 bytes built from its fields.</summary>
    internal SomeIpSdBuilder Entry(
        byte type,
        ushort service,
        ushort instance,
        byte major,
        uint ttl,
        uint lastWord,
        byte index1 = 0,
        byte index2 = 0,
        byte count1 = 0,
        byte count2 = 0)
    {
        _Entries.Add(new SomeIpBytes()
            .U8(type).U8(index1).U8(index2).U8((byte)((count1 << 4) | (count2 & 0x0F)))
            .U16(service).U16(instance).U8(major)
            .U8((byte)(ttl >> 16)).U8((byte)(ttl >> 8)).U8((byte)ttl)
            .U32(lastWord)
            .ToArray());
        return this;
    }

    /// <summary>Adds an offer (type 0x01) that references the options of its first run.</summary>
    internal SomeIpSdBuilder Offer(ushort service, ushort instance, byte major, uint minor, uint ttl, byte count1 = 1, byte index1 = 0) =>
        Entry(0x01, service, instance, major, ttl, minor, index1: index1, count1: count1);

    /// <summary>Adds a find (type 0x00).</summary>
    internal SomeIpSdBuilder Find(ushort service, ushort instance, byte major, uint minor, uint ttl) =>
        Entry(0x00, service, instance, major, ttl, minor);

    /// <summary>Adds a subscribe (type 0x06) with the eventgroup in the last word.</summary>
    internal SomeIpSdBuilder Subscribe(ushort service, ushort instance, byte major, uint ttl, ushort eventgroup, byte counter = 0, bool initial = false)
    {
        // Byte 13 of the entry: the initial data request in bit 7 and the counter in the low nibble.
        uint flagsAndCounter = counter;
        if (initial)
        {
            flagsAndCounter |= 0x80;
        }

        return Entry(0x06, service, instance, major, ttl, (flagsAndCounter << 16) | eventgroup);
    }

    /// <summary>Adds a subscribe acknowledgement (type 0x07).</summary>
    internal SomeIpSdBuilder SubscribeAck(ushort service, ushort instance, byte major, uint ttl, ushort eventgroup) =>
        Entry(0x07, service, instance, major, ttl, eventgroup);

    /// <summary>Adds bytes that are placed in the entries array as they are (a partial entry, for example).</summary>
    internal SomeIpSdBuilder RawEntries(params byte[] bytes)
    {
        _Entries.Add(bytes);
        return this;
    }

    /// <summary>Adds an endpoint option for IPv4: type 0x04 (endpoint), 0x14 (multicast) or 0x24 (service discovery endpoint).</summary>
    internal SomeIpSdBuilder Ipv4Option(byte[] address, byte protocol, ushort port, byte type = 0x04)
    {
        _Options.Add(new SomeIpBytes().U16(9).U8(type).U8(0).Bytes(address).U8(0).U8(protocol).U16(port).ToArray());
        return this;
    }

    /// <summary>Adds an endpoint option for IPv6: type 0x06, 0x16 or 0x26.</summary>
    internal SomeIpSdBuilder Ipv6Option(byte[] address, byte protocol, ushort port, byte type = 0x06)
    {
        _Options.Add(new SomeIpBytes().U16(21).U8(type).U8(0).Bytes(address).U8(0).U8(protocol).U16(port).ToArray());
        return this;
    }

    /// <summary>Adds a configuration option (type 0x01) with the items as length-prefixed strings and a terminating zero.</summary>
    internal SomeIpSdBuilder ConfigurationOption(params string[] items)
    {
        SomeIpBytes data = new();
        foreach (string item in items)
        {
            _ = data.U8((byte)item.Length).Utf8(item);
        }

        _ = data.U8(0);
        byte[] bytes = data.ToArray();
        _Options.Add(new SomeIpBytes().U16((ushort)(bytes.Length + 1)).U8(0x01).U8(0).Bytes(bytes).ToArray());
        return this;
    }

    /// <summary>Adds a load balancing option (type 0x02).</summary>
    internal SomeIpSdBuilder LoadBalancingOption(ushort priority, ushort weight)
    {
        _Options.Add(new SomeIpBytes().U16(5).U8(0x02).U8(0).U16(priority).U16(weight).ToArray());
        return this;
    }

    /// <summary>Adds an option of any type with the data behind its reserved byte; the length field is computed.</summary>
    internal SomeIpSdBuilder Option(byte type, params byte[] data)
    {
        _Options.Add(new SomeIpBytes().U16((ushort)(data.Length + 1)).U8(type).U8(0).Bytes(data).ToArray());
        return this;
    }

    /// <summary>Adds option bytes as they are (a cut-off option, for example).</summary>
    internal SomeIpSdBuilder RawOptions(params byte[] bytes)
    {
        _Options.Add(bytes);
        return this;
    }

    /// <summary>Builds the payload.</summary>
    internal byte[] BuildPayload()
    {
        byte[] entries = SomeIpBytes.Concat([.. _Entries]);
        byte[] options = SomeIpBytes.Concat([.. _Options]);
        SomeIpBytes payload = new SomeIpBytes()
            .U8(Flags).U8(0).U8(0).U8(0)
            .U32(EntriesLengthOverride ?? (uint)entries.Length)
            .Bytes(entries);
        if (!OmitOptions)
        {
            _ = payload.U32(OptionsLengthOverride ?? (uint)options.Length).Bytes(options);
        }

        return payload.Bytes(Trailing).ToArray();
    }

    /// <summary>Builds the complete SOME/IP message: service 0xFFFF, method 0x8100, a notification, interface version 1.</summary>
    internal byte[] BuildMessage() => SomeIpBytes.Message(0xFFFF, 0x8100, BuildPayload(), clientId: 0, sessionId: 1, messageType: 0x02);

    /// <summary>Builds the Ethernet/IPv4/UDP frame to the well-known port that carries the message.</summary>
    internal byte[] BuildFrame() => FrameBuilderTestExtensions.WrapRawUdp(BuildMessage(), 30490, 30490);
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// Builds packets that carry one value of each address type (IPv4, IPv6, MAC, EUI-64, UUID) with chosen values, on a private stack
/// that holds one test protocol. The standard protocols produce no EUI-64 or UUID field and cannot be given the boundary values
/// (the lowest and the highest address, the values around the sign bit) that the address tests need.
/// </summary>
/// <remarks>
/// <para>
/// Every packet holds the six fields in a fixed order (<see cref="FieldNames"/>), appended eagerly under one layer container, so a
/// tree parse and a tree-free parse deliver the same fields. Unless a packet's <see cref="Values"/> give the first IPv6 field a custom
/// representation or text (<c>IPv6Representation</c>, <c>IPv6Text</c>), the fields carry neither, so the display line of a field is
/// its UI name and the default text of the address.
/// </para>
/// <para>Thread safety: not thread-safe; one test owns one factory.</para>
/// </remarks>
internal sealed class AddressProbePacketFactory : IDisposable
{
    #region Address values

    /// <summary>The address field names in append order; they are registered with the probe protocol.</summary>
    internal static IReadOnlyList<string> FieldNames { get; } = ["adp.ipv4", "adp.ipv6", "adp.ipv6dst", "adp.mac", "adp.eui64", "adp.uuid"];

    /// <summary>The address values of one packet: one of each type and a second IPv6 address (a destination next to the source).</summary>
    /// <param name="IPv4">The IPv4 address.</param>
    /// <param name="IPv6">The IPv6 address.</param>
    /// <param name="Mac">The MAC address.</param>
    /// <param name="Eui64">The EUI-64 identifier.</param>
    /// <param name="Uuid">The UUID.</param>
    /// <param name="IPv6Dst">The second IPv6 address; <c>::</c> when not given.</param>
    /// <param name="IPv6Representation">
    /// The custom representation of the first IPv6 value; <see langword="null"/> sets none, the empty string an empty one.
    /// </param>
    /// <param name="IPv6Text">The custom text of the first IPv6 field; <see langword="null"/> sets none, the empty string an empty one.</param>
    internal readonly record struct Values(
        IPv4Address IPv4,
        IPv6Address IPv6,
        MacAddress Mac,
        Eui64 Eui64,
        Uuid Uuid,
        IPv6Address IPv6Dst = default,
        string? IPv6Representation = null,
        string? IPv6Text = null);

    /// <summary>
    /// A packet with readable, distinct values: <c>192.168.1.1</c>, <c>2001:DB8::1</c> (and <c>2001:DB8::2</c> as the second IPv6
    /// address), <c>00:1A:2B:3C:4D:5E</c>, <c>00:1A:2B:FF:FE:3C:4D:5E</c>, and <c>123E4567-E89B-12D3-A456-426614174000</c>.
    /// </summary>
    internal static Values Readable { get; } = new(
        new IPv4Address(0xC0A80101u),
        new IPv6Address(0x2001_0DB8_0000_0000UL, 1UL),
        new MacAddress(0x001A_2B3C_4D5EUL),
        new Eui64(0x001A_2BFF_FE3C_4D5EUL),
        new Uuid(0x123E_4567_E89B_12D3UL, 0xA456_4266_1417_4000UL),
        new IPv6Address(0x2001_0DB8_0000_0000UL, 2UL));

    /// <summary>The default text of each value of <see cref="Readable"/>, in <see cref="FieldNames"/> order (upper-case hex).</summary>
    internal static IReadOnlyList<string> ReadableTexts { get; } =
    [
        "192.168.1.1",
        "2001:DB8::1",
        "2001:DB8::2",
        "00:1A:2B:3C:4D:5E",
        "00:1A:2B:FF:FE:3C:4D:5E",
        "123E4567-E89B-12D3-A456-426614174000",
    ];

    /// <summary>
    /// Five packets of boundary values in ascending address order, so that row <c>i</c> of every type is its <c>i</c>-th smallest
    /// value: the lowest address, the second lowest, the highest below the sign bit, the lowest with the sign bit set, and the
    /// highest. The tests feed them in another order (<see cref="ShuffledBoundaries"/>) and expect <c>ORDER BY value</c> to give
    /// this one back; a signed column or a byte-order swap breaks it.
    /// </summary>
    /// <returns>A new array of the values in ascending order.</returns>
    internal static Values[] AscendingBoundaries() =>
    [
        new(new IPv4Address(0u), new IPv6Address(0, 0), new MacAddress(0UL), new Eui64(0UL), new Uuid(0, 0)),
        new(new IPv4Address(1u), new IPv6Address(0, 1), new MacAddress(1UL), new Eui64(1UL), new Uuid(0, 1)),
        new(
            new IPv4Address(0x7FFF_FFFFu),
            new IPv6Address(0x7FFF_FFFF_FFFF_FFFFUL, ulong.MaxValue),
            new MacAddress(0x7FFF_FFFF_FFFFUL),
            new Eui64(0x7FFF_FFFF_FFFF_FFFFUL),
            new Uuid(0x7FFF_FFFF_FFFF_FFFFUL, ulong.MaxValue)),
        new(
            new IPv4Address(0x8000_0000u),
            new IPv6Address(0x8000_0000_0000_0000UL, 0),
            new MacAddress(0x8000_0000_0000UL),
            new Eui64(0x8000_0000_0000_0000UL),
            new Uuid(0x8000_0000_0000_0000UL, 0)),
        new(
            new IPv4Address(0xFFFF_FFFFu),
            new IPv6Address(ulong.MaxValue, ulong.MaxValue),
            new MacAddress(0xFFFF_FFFF_FFFFUL),
            new Eui64(ulong.MaxValue),
            new Uuid(ulong.MaxValue, ulong.MaxValue)),
    ];

    /// <summary>The boundary packets of <see cref="AscendingBoundaries"/> in an order that is not the ascending one.</summary>
    /// <returns>A new array of the values in feed order.</returns>
    internal static Values[] ShuffledBoundaries()
    {
        Values[] ascending = AscendingBoundaries();
        return [ascending[3], ascending[0], ascending[4], ascending[2], ascending[1]];
    }

    /// <summary>The decimal text of one integer per packet, the form in which DuckDB.NET returns an unsigned integer column.</summary>
    /// <param name="values">The packets' values.</param>
    /// <param name="select">Picks the integer of a packet.</param>
    /// <returns>One text per packet, in the order of <paramref name="values"/>.</returns>
    internal static List<string> DecimalOf(IEnumerable<Values> values, Func<Values, ulong> select) =>
        [.. values.Select(value => select(value).ToString(CultureInfo.InvariantCulture))];

    #endregion

    #region Factory state and field lookup

    /// <summary>The factory's own stack, disposed with the factory.</summary>
    private readonly Stack _Stack;

    /// <summary>The probe protocol that appends the address fields with the current values.</summary>
    private readonly ProbeProtocol _Protocol;

    /// <summary>The id of the probe protocol in <see cref="_Stack"/>; the first protocol of every parse.</summary>
    private readonly ProtocolId _ProtocolId;

    /// <summary>The id of the next packet (and frame) the factory creates.</summary>
    private int _NextPacketId;

    /// <summary>Creates a factory with its own stack and the probe protocol.</summary>
    internal AddressProbePacketFactory()
    {
        SettingsManager settingsManager = new(); // ownership passes to the stack, which disposes it
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        _Protocol = new();
        _ProtocolId = builder.RegisterProtocol(_Protocol);
        _Protocol.RegisterFields(builder, _ProtocolId);
        _Stack = builder.Build();
    }

    /// <summary>The factory's own stack: a field selection for the factory's packets must be resolved against it.</summary>
    internal Stack Stack => _Stack;

    /// <summary>The id of an address field in the factory's stack.</summary>
    /// <param name="name">The registered name, for example <c>adp.ipv6</c>.</param>
    /// <returns>The field id.</returns>
    /// <exception cref="InvalidOperationException">The name is not registered.</exception>
    internal FieldId IdOf(string name)
    {
        FieldId? id = _Stack.GetFieldId(name);
        if (id is null)
        {
            throw new InvalidOperationException(Invariant($"Unknown address probe field {name}."));
        }

        return id.Value;
    }

    /// <summary>The name of the field table (DuckDB) or the field file stem (Parquet) of an address field: <c>field_{id}</c>.</summary>
    /// <param name="name">The registered name, for example <c>adp.uuid</c>.</param>
    /// <returns>The table name.</returns>
    internal string TableOf(string name) => Invariant($"field_{IdOf(name).Value.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>The content of an export of every address field: no packet info and no frame bytes, so only the field data is written.</summary>
    /// <param name="shape">
    /// The shape; <see cref="ExportShape.AllFields"/> and <see cref="ExportShape.Hierarchy"/> need no selection, and
    /// <see cref="ExportShape.SelectedFields"/> selects the six fields (all occurrences).
    /// </param>
    /// <returns>The content.</returns>
    /// <exception cref="InvalidOperationException">The field selection of the six fields cannot be created.</exception>
    internal ExportContent Content(ExportShape shape)
    {
        FieldSelection? selection = null;
        if (shape == ExportShape.SelectedFields)
        {
            FieldRequest[] requests = [.. FieldNames.Select(static name => new FieldRequest(name))];
            if (!FieldSelection.TryCreate(_Stack, requests, Occurrences.All, out selection, out FieldSelectionError error))
            {
                throw new InvalidOperationException(error.Message);
            }
        }

        return new ExportContent { Shape = shape, Fields = selection, Info = false, FrameBytes = false };
    }

    /// <inheritdoc/>
    public void Dispose() => _Stack.Dispose();

    #endregion

    #region Building packets

    /// <summary>Parses one packet with a field tree that carries <paramref name="values"/>.</summary>
    /// <param name="values">The address values of the packet.</param>
    /// <returns>The parsed tree packet; its ids count up from 0.</returns>
    internal Packet CreateTreePacket(in Values values)
    {
        _Protocol.Current = values;
        ParseOptions options = new(firstProtocol: _ProtocolId);
        return _Parse(in options);
    }

    /// <summary>Parses one packet per entry of <paramref name="values"/> with a field tree.</summary>
    /// <param name="values">The address values of each packet, in packet order.</param>
    /// <returns>The packets.</returns>
    internal List<Packet> CreateTreePackets(IReadOnlyList<Values> values)
    {
        List<Packet> packets = new(values.Count);
        foreach (Values entry in values)
        {
            packets.Add(CreateTreePacket(in entry));
        }

        return packets;
    }

    /// <summary>
    /// Offers a packet with <paramref name="values"/> to an exporter, parsed with the exporter's own parse demand (a tree-free parse,
    /// the pipeline of the exporter guide), so the exporter's observer stages the fields.
    /// </summary>
    /// <typeparam name="TExporter">An exporter that states its parse need.</typeparam>
    /// <param name="exporter">The exporter; <see cref="IPacketListener.OnFinish"/> is not called.</param>
    /// <param name="values">The address values of the packet.</param>
    /// <returns>What <see cref="IPacketListener.OnPacket"/> returned.</returns>
    /// <exception cref="InvalidOperationException">The parse plan of the exporter's demand cannot be created, or the packet does not parse.</exception>
    internal bool FeedTreeFree<TExporter>(TExporter exporter, in Values values)
        where TExporter : IPacketListener, IParseDemandSource
    {
        ParseDemand[] demands = [exporter.ParseDemand];
        if (!ParsePlan.TryCreate(_Stack, demands, out ParsePlan? plan, out ParsePlanError error))
        {
            throw new InvalidOperationException(error.Message);
        }

        _Protocol.Current = values;
        ParseOptions options = plan.Options with { FirstProtocol = _ProtocolId };
        return exporter.OnPacket(_Parse(in options));
    }

    /// <summary>Parses one probe frame with the given options.</summary>
    /// <param name="options">The parse options; the first protocol must be the probe protocol.</param>
    /// <returns>The packet.</returns>
    /// <exception cref="InvalidOperationException">The frame does not parse.</exception>
    private Packet _Parse(in ParseOptions options)
    {
        int id = _NextPacketId++;
        Frame frame = Frame.Create(
            id,
            Timestamp.FromNanos(1_000_000_000L + id),
            new byte[14],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            _Stack.FrameInterfaceRegistry).Value;
        if (!Packet.TryParse(id, _Stack, frame, in options, out Packet? packet, out ParseFailure failure) || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet;
    }

    #endregion

    #region Probe protocol

    /// <summary>The probe protocol: appends the layer container and one field per address (the IPv6 type twice) with the current values.</summary>
    private sealed class ProbeProtocol : IProtocol
    {
        /// <summary>The id of the layer container field.</summary>
        private FieldId _LayerId;

        /// <summary>The id of the IPv4 address field.</summary>
        private FieldId _IPv4Id;

        /// <summary>The id of the first IPv6 address field (the one that may carry a custom representation or text).</summary>
        private FieldId _IPv6Id;

        /// <summary>The id of the second IPv6 address field.</summary>
        private FieldId _IPv6DstId;

        /// <summary>The id of the MAC address field.</summary>
        private FieldId _MacId;

        /// <summary>The id of the EUI-64 field.</summary>
        private FieldId _Eui64Id;

        /// <summary>The id of the UUID field.</summary>
        private FieldId _UuidId;

        /// <summary>The values the next parse appends.</summary>
        internal Values Current { get; set; }

        /// <inheritdoc/>
        public string Name => "adp";

        /// <inheritdoc/>
        public string UiName => "Address Probe";

        /// <summary>Registers the layer container and the six address fields.</summary>
        /// <param name="builder">The builder of the factory's stack.</param>
        /// <param name="protocolId">The id of this protocol.</param>
        internal void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _LayerId = builder.RegisterField(protocolId, "adp", "Address Probe", FieldType.None);
            _IPv4Id = builder.RegisterField(protocolId, "adp.ipv4", "IPv4 Address", FieldType.IPv4Address);
            _IPv6Id = builder.RegisterField(protocolId, "adp.ipv6", "IPv6 Address", FieldType.IPv6Address);
            _IPv6DstId = builder.RegisterField(protocolId, "adp.ipv6dst", "IPv6 Destination", FieldType.IPv6Address);
            _MacId = builder.RegisterField(protocolId, "adp.mac", "MAC Address", FieldType.MacAddress);
            _Eui64Id = builder.RegisterField(protocolId, "adp.eui64", "EUI-64", FieldType.Eui64);
            _UuidId = builder.RegisterField(protocolId, "adp.uuid", "UUID", FieldType.Uuid);
        }

        /// <inheritdoc/>
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            Values values = Current;
            MutField layer = parentField.Append(_LayerId, FieldValue.None);
            _ = layer.Append(_IPv4Id, FieldValue.NewIPv4(values.IPv4));
            if (values.IPv6Representation is null && values.IPv6Text is null)
            {
                _ = layer.Append(_IPv6Id, FieldValue.NewIPv6(values.IPv6));
            }
            else
            {
                // A text or representation that is null is absent (a default LazyString), an empty one is set but empty.
                LazyString representation = default;
                if (values.IPv6Representation is not null)
                {
                    representation = new LazyString(values.IPv6Representation);
                }

                LazyString text = default;
                if (values.IPv6Text is not null)
                {
                    text = new LazyString(values.IPv6Text);
                }

                _ = layer.AppendWithCustomText(_IPv6Id, FieldValue.NewIPv6(values.IPv6, representation), text);
            }

            _ = layer.Append(_IPv6DstId, FieldValue.NewIPv6(values.IPv6Dst));
            _ = layer.Append(_MacId, FieldValue.NewMacAddress(values.Mac));
            _ = layer.Append(_Eui64Id, FieldValue.NewEui64(values.Eui64));
            _ = layer.Append(_UuidId, FieldValue.NewUuid(values.Uuid));
            return data.Length;
        }
    }

    #endregion
}

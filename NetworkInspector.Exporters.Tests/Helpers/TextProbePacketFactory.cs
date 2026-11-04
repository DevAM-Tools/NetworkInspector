// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// Builds packets whose fields cover every text case an export has to tell apart, on a private stack that holds one test
/// protocol: a field without texts, a field with a custom text, a field with only a custom representation, a field with an empty
/// custom text and an empty custom representation, a bytes field without texts, and a field with both texts. The standard
/// protocols cannot produce an empty text or a representation-only field, which the presence tests need.
/// </summary>
/// <remarks>
/// <para>
/// The same packet is available as a tree packet (<see cref="CreateTreePacket"/>) and through a tree-free parse
/// (<see cref="Feed"/> with <c>treeFree</c>), so a test can compare the two feeds of an exporter on exactly the same fields.
/// The fields are appended eagerly (no lazy populator), like the UDP dissector does, so both parses deliver every field.
/// </para>
/// <para>Thread safety: not thread-safe; one test owns one factory.</para>
/// </remarks>
internal sealed class TextProbePacketFactory : IDisposable
{
    #region Probe fields and expected texts

    /// <summary>The texts a probe field carries:<see langword="null"/> is absent, the empty string is set but empty.</summary>
    /// <param name="Representation">The custom representation of the value.</param>
    /// <param name="Text">The custom text of the field.</param>
    internal readonly record struct Texts(string? Representation, string? Text);

    /// <summary>The probe value fields in append order; the names are registered with the probe protocol.</summary>
    internal static IReadOnlyList<string> FieldNames { get; } =
        ["txp.plain", "txp.text", "txp.repr", "txp.empty", "txp.bytes", "txp.both"];

    /// <summary>The texts each probe value field carries (what the protocol sets).</summary>
    internal static IReadOnlyDictionary<string, Texts> Expected { get; } = new Dictionary<string, Texts>
    {
        ["txp.plain"] = new Texts(null, null),
        ["txp.text"] = new Texts(null, "Length: 8 bytes"),
        ["txp.repr"] = new Texts("Echo", null),
        ["txp.empty"] = new Texts(string.Empty, string.Empty),
        ["txp.bytes"] = new Texts(null, null),
        ["txp.both"] = new Texts("nine", "Nine: nine"),
    };

    /// <summary>
    /// The field that occurs twice in the probe packet: the first occurrence has an empty custom text, the second has no text.
    /// It is not one of <see cref="FieldNames"/>; the wide CSV layout joins its two occurrences into one cell.
    /// </summary>
    internal const string DuplicateFieldName = "txp.dup";

    /// <summary>
    /// The probe field that holds bytes and has a child (<c>txp.leaf</c>) and no custom text, like a protocol layer without a
    /// display text. It is not one of <see cref="FieldNames"/>.
    /// </summary>
    internal const string BytesLayerName = "txp.blayer";

    /// <summary>The custom text of the probe layer container (a node without a value).</summary>
    internal const string LayerText = "Probe: a layer";

    /// <summary>
    /// The lines of the three fields with an id that the stack does not know, in append order: a value (the value alone, no label and
    /// no separator), a container (the empty line), and a value with a custom text (the text alone).
    /// </summary>
    internal static IReadOnlyList<string> UnknownIdsLines { get; } = ["7", string.Empty, "Own text"];

    /// <summary>The first id that no probe field uses; the three unknown-id fields use it and the two ids after it.</summary>
    private const int _UnknownIdBase = 900_000;

    /// <summary>The three payload bytes of the bytes field.</summary>
    internal static ReadOnlyMemory<byte> RawBytes { get; } = new byte[] { 0x0A, 0xFF, 0x00 };

    #endregion

    #region Factory state and field lookup

    /// <summary>The factory's own stack, disposed with the factory.</summary>
    private readonly Stack _Stack;

    /// <summary>The probe protocol that appends the probe fields with their texts.</summary>
    private readonly ProbeProtocol _Protocol;

    /// <summary>The id of the probe protocol in <see cref="_Stack"/>; the first protocol of every parse.</summary>
    private readonly ProtocolId _ProtocolId;

    /// <summary>The id of the next packet (and frame) the factory creates.</summary>
    private int _NextPacketId;

    /// <summary>Creates a factory with its own stack and the probe protocol.</summary>
    internal TextProbePacketFactory()
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

    /// <summary>The id of a probe field in the factory's stack.</summary>
    /// <param name="name">The registered name, for example <c>txp.text</c>.</param>
    /// <returns>The field id.</returns>
    /// <exception cref="InvalidOperationException">The name is not registered.</exception>
    internal FieldId IdOf(string name)
    {
        FieldId? id = _Stack.GetFieldId(name);
        if (id is null)
        {
            throw new InvalidOperationException(Invariant($"Unknown probe field {name}."));
        }

        return id.Value;
    }

    /// <inheritdoc/>
    public void Dispose() => _Stack.Dispose();

    #endregion

    #region Building packets

    /// <summary>The selected-fields content for the named probe fields (all occurrences), with the given text switches.</summary>
    /// <param name="shape">The shape; <see cref="ExportShape.SelectedFields"/> selects the fields of <paramref name="names"/>.</param>
    /// <param name="text">Whether custom texts are written.</param>
    /// <param name="representation">Whether custom representations are written.</param>
    /// <param name="names">The selected fields; the six probe value fields when <see langword="null"/>.</param>
    /// <returns>The content: without info and frame bytes, so a CSV export has exactly the field columns.</returns>
    /// <exception cref="InvalidOperationException">The field selection cannot be created.</exception>
    internal ExportContent FlatContent(
        ExportShape shape, bool text = true, bool representation = true, IReadOnlyList<string>? names = null)
    {
        FieldSelection? selection = null;
        if (shape == ExportShape.SelectedFields)
        {
            IReadOnlyList<string> selected = names
                ?? FieldNames;
            FieldRequest[] requests = [.. selected.Select(static name => new FieldRequest(name))];
            if (!FieldSelection.TryCreate(_Stack, requests, Occurrences.All, out selection, out FieldSelectionError error))
            {
                throw new InvalidOperationException(error.Message);
            }
        }

        return new ExportContent
        {
            Shape = shape,
            Fields = selection,
            Info = false,
            FrameBytes = false,
            CustomText = text,
            CustomRepresentation = representation,
        };
    }

    /// <summary>Parses the probe packet with a field tree (the packet carries every field and text).</summary>
    /// <param name="omitTexts">
    /// <see langword="true"/> for a variant of the packet in which <c>txp.text</c>, <c>txp.empty</c>, and <c>txp.both</c> carry
    /// no text and no representation, so a column of several packets has rows with and rows without a text.
    /// </param>
    /// <param name="withUnknownIds">
    /// <see langword="true"/> to append three more fields whose ids the stack does not know (see <see cref="UnknownIdsLines"/>), so a
    /// test can check the lines of fields that have no label.
    /// </param>
    /// <returns>The parsed tree packet.</returns>
    internal Packet CreateTreePacket(bool omitTexts = false, bool withUnknownIds = false)
    {
        _Protocol.OmitTexts = omitTexts;
        _Protocol.AppendUnknownIds = withUnknownIds;
        ParseOptions options = new(firstProtocol: _ProtocolId);
        return _Parse(in options);
    }

    /// <summary>
    /// Offers the probe packet to an exporter: parsed with a field tree, or tree-free with the exporter's own parse demand (the
    /// pipeline of the exporter guide), so the exporter's observer stages the fields.
    /// </summary>
    /// <typeparam name="TExporter">An exporter that states its parse need.</typeparam>
    /// <param name="exporter">The exporter; <see cref="IPacketListener.OnFinish"/> is not called.</param>
    /// <param name="treeFree"><see langword="true"/> to parse with the exporter's demand, <see langword="false"/> for a tree packet.</param>
    /// <exception cref="InvalidOperationException">The exporter refused the packet, or the parse plan could not be created.</exception>
    internal void Feed<TExporter>(TExporter exporter, bool treeFree)
        where TExporter : IPacketListener, IParseDemandSource
    {
        bool accepted;
        if (!treeFree)
        {
            accepted = exporter.OnPacket(CreateTreePacket());
        }
        else
        {
            ParseDemand[] demands = [exporter.ParseDemand];
            if (!ParsePlan.TryCreate(_Stack, demands, out ParsePlan? plan, out ParsePlanError error))
            {
                throw new InvalidOperationException(error.Message);
            }

            // CreateTreePacket(omitTexts: true) leaves the flag set; a tree-free feed always parses the packet with its texts.
            _Protocol.OmitTexts = false;
            _Protocol.AppendUnknownIds = false;
            ParseOptions options = plan.Options with { FirstProtocol = _ProtocolId };
            accepted = exporter.OnPacket(_Parse(in options));
        }

        // A refused probe packet would make every later assertion about its texts vacuous, so it stops the test here.
        if (!accepted)
        {
            throw new InvalidOperationException("The exporter refused the probe packet.");
        }
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

    /// <summary>The probe protocol: appends the layer container and the probe fields with their texts.</summary>
    private sealed class ProbeProtocol : IProtocol
    {
        /// <summary>The id of the layer container field.</summary>
        private FieldId _LayerId;

        /// <summary>The id of the group container field.</summary>
        private FieldId _GroupId;

        /// <summary>The id of the field without texts.</summary>
        private FieldId _PlainId;

        /// <summary>The id of the field with a custom text.</summary>
        private FieldId _TextId;

        /// <summary>The id of the field with only a custom representation.</summary>
        private FieldId _ReprId;

        /// <summary>The id of the field with an empty custom text and an empty custom representation.</summary>
        private FieldId _EmptyId;

        /// <summary>The id of the bytes field without texts.</summary>
        private FieldId _BytesId;

        /// <summary>The id of the field with both a custom text and a custom representation.</summary>
        private FieldId _BothId;

        /// <summary>The id of the field that occurs twice.</summary>
        private FieldId _DupId;

        /// <summary>The id of the bytes field that has a child and no custom text.</summary>
        private FieldId _BytesLayerId;

        /// <summary>The id of the child of the bytes layer.</summary>
        private FieldId _LeafId;

        /// <summary>Whether the next parse leaves the texts and representations out of the three fields that carry them.</summary>
        internal bool OmitTexts { get; set; }

        /// <summary>Whether the next parse appends the three fields of <see cref="UnknownIdsLines"/> after the probe fields.</summary>
        internal bool AppendUnknownIds { get; set; }

        /// <inheritdoc/>
        public string Name => "txp";

        /// <inheritdoc/>
        public string UiName => "Probe";

        /// <summary>Registers the layer container, a group, and the six probe fields.</summary>
        /// <param name="builder">The builder of the factory's stack.</param>
        /// <param name="protocolId">The id of this protocol.</param>
        internal void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _LayerId = builder.RegisterField(protocolId, "txp", "Probe", FieldType.None);
            _GroupId = builder.RegisterField(protocolId, "txp.group", "Group", FieldType.None);
            _PlainId = builder.RegisterField(protocolId, "txp.plain", "Plain Value", FieldType.U64);
            _TextId = builder.RegisterField(protocolId, "txp.text", "Length", FieldType.U64);
            _ReprId = builder.RegisterField(protocolId, "txp.repr", "Echo Field", FieldType.String);
            _EmptyId = builder.RegisterField(protocolId, "txp.empty", "Empty Texts", FieldType.U64);
            _BytesId = builder.RegisterField(protocolId, "txp.bytes", "Raw", FieldType.Bytes);
            _BothId = builder.RegisterField(protocolId, "txp.both", "Both", FieldType.U64);
            _DupId = builder.RegisterField(protocolId, DuplicateFieldName, "Twice", FieldType.U64);
            _BytesLayerId = builder.RegisterField(protocolId, BytesLayerName, "Bytes Layer", FieldType.Bytes);
            _LeafId = builder.RegisterField(protocolId, "txp.leaf", "Leaf", FieldType.U64);
        }

        /// <inheritdoc/>
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            MutField layer = parentField.AppendWithCustomText(_LayerId, FieldValue.None, new LazyString(LayerText));
            _ = layer.Append(_GroupId, FieldValue.None);
            _ = layer.Append(_PlainId, FieldValue.NewU64(5));
            if (OmitTexts)
            {
                _ = layer.Append(_TextId, FieldValue.NewU64(8));
                _ = layer.Append(_ReprId, FieldValue.NewString("e", new LazyString("Echo")));
                _ = layer.Append(_EmptyId, FieldValue.NewU64(7));
                _ = layer.Append(_BytesId, FieldValue.NewBytes(RawBytes));
                _ = layer.Append(_BothId, FieldValue.NewU64(9));
            }
            else
            {
                _ = layer.AppendWithCustomText(_TextId, FieldValue.NewU64(8), new LazyString("Length: 8 bytes"));
                _ = layer.Append(_ReprId, FieldValue.NewString("e", new LazyString("Echo")));
                _ = layer.AppendWithCustomText(_EmptyId, FieldValue.NewU64(7, LazyString.Empty), LazyString.Empty);
                _ = layer.Append(_BytesId, FieldValue.NewBytes(RawBytes));
                _ = layer.AppendWithCustomText(_BothId, FieldValue.NewU64(9, new LazyString("nine")), new LazyString("Nine: nine"));
            }

            _ = layer.AppendWithCustomText(_DupId, FieldValue.NewU64(1), LazyString.Empty);
            _ = layer.Append(_DupId, FieldValue.NewU64(2));

            // A container that holds bytes (like a protocol layer that keeps its raw bytes) and has a child, with no custom text.
            MutField bytesLayer = layer.Append(_BytesLayerId, FieldValue.NewBytes(RawBytes));
            _ = bytesLayer.Append(_LeafId, FieldValue.NewU64(1));

            // Ids the stack does not know have no label: the value alone, nothing, or the custom text.
            if (AppendUnknownIds)
            {
                _ = layer.Append(new FieldId(_UnknownIdBase), FieldValue.NewU64(7));
                _ = layer.Append(new FieldId(_UnknownIdBase + 1), FieldValue.None);
                _ = layer.AppendWithCustomText(new FieldId(_UnknownIdBase + 2), FieldValue.NewU64(7), new LazyString("Own text"));
            }

            return data.Length;
        }
    }

    #endregion
}

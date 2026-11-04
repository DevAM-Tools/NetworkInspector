// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

public sealed partial class SomeIpProtocol
{
    #region Constants

    /// <summary>The marker text of a payload that is left undecoded because decoding is switched off.</summary>
    private const string _DisabledText = "The payload is not decoded because the setting someip.decode_payload is switched off.";

    #endregion

    #region Fields: payload nodes

    // The fixed payload fields are all in the group of the raw payload: a message that holds a payload container may hold any of
    // them, and the lazy subtree cannot say which before it is built. The group is therefore a superset by design; a filter on
    // one of these fields verifies each candidate on the tree. The typed value fields are the fallback of a parameter whose own
    // field could not be registered (see SomeIpPayloadFieldRegistry); they exist so that such a value stays visible.
    [BytesField("someip.payload.unparsed", "Unparsed Payload Bytes", IndexGroup = "someip.payload")]
    private FieldId _PayloadUnparsedFieldId;

    [BytesField("someip.payload.padding", "Padding", IndexGroup = "someip.payload")]
    private FieldId _PayloadPaddingFieldId;

    [StringField("someip.payload.type", "Type", IndexGroup = "someip.payload")]
    private FieldId _PayloadTypeFieldId;

    [U64Field("someip.payload.length", "Length Field", IndexGroup = "someip.payload")]
    private FieldId _PayloadLengthFieldId;

    [U64Field("someip.payload.array.count", "Element Count", IndexGroup = "someip.payload")]
    private FieldId _PayloadArrayCountFieldId;

    [StringField("someip.payload.disabled", "Payload Decoding Switched Off", IndexGroup = "someip.payload")]
    private FieldId _PayloadDisabledFieldId;

    [StringField("someip.payload.error", "Payload Anomaly", IndexGroup = "someip.payload")]
    private FieldId _PayloadErrorFieldId;

    [U64Field("someip.payload.value.u64", "Unsigned Value", IndexGroup = "someip.payload")]
    private FieldId _PayloadValueU64FieldId;

    [I64Field("someip.payload.value.i64", "Signed Value", IndexGroup = "someip.payload")]
    private FieldId _PayloadValueI64FieldId;

    [F64Field("someip.payload.value.f64", "Floating-Point Value", IndexGroup = "someip.payload")]
    private FieldId _PayloadValueF64FieldId;

    [BoolField("someip.payload.value.bool", "Boolean Value", IndexGroup = "someip.payload")]
    private FieldId _PayloadValueBoolFieldId;

    [StringField("someip.payload.value.string", "String Value", IndexGroup = "someip.payload")]
    private FieldId _PayloadValueStringFieldId;

    [UuidField("someip.payload.value.uuid", "UUID Value", IndexGroup = "someip.payload")]
    private FieldId _PayloadValueUuidFieldId;

    [NoneField("someip.payload.value.none", "Value Container", IndexGroup = "someip.payload")]
    private FieldId _PayloadValueNoneFieldId;

    // TLV and union nodes. The tag nodes exist in the metadata view only; the unknown node holds the bytes of a member or an
    // alternative that the description does not define, so that a newer sender's members stay visible.
    [U64Field("someip.payload.tlv.tag", "TLV Tag", IndexGroup = "someip.payload")]
    private FieldId _PayloadTlvTagFieldId;

    [U64Field("someip.payload.tlv.tag.wire_type", "TLV Wire Type", IndexGroup = "someip.payload")]
    private FieldId _PayloadTlvWireTypeFieldId;

    [U64Field("someip.payload.tlv.tag.data_id", "TLV Data ID", IndexGroup = "someip.payload")]
    private FieldId _PayloadTlvDataIdFieldId;

    [BoolField("someip.payload.tlv.tag.reserved", "TLV Reserved Bit", IndexGroup = "someip.payload")]
    private FieldId _PayloadTlvReservedFieldId;

    [BytesField("someip.payload.tlv.unknown", "Undefined Member Bytes", IndexGroup = "someip.payload")]
    private FieldId _PayloadTlvUnknownFieldId;

    [U64Field("someip.payload.union.type", "Union Type Selector", IndexGroup = "someip.payload")]
    private FieldId _PayloadUnionTypeFieldId;

    // End-to-end protection. The container holds the header bytes as its value (so a viewer can highlight them); the group is
    // recorded for every message of a protected member, because the container exists in every case: the decoded header, the
    // "incomplete" marker of a payload shorter than the header, and the marker of an unknown profile.
    [BytesField("someip.e2e", "End-To-End Protection", IndexGroup = "someip.e2e")]
    private FieldId _E2eFieldId;

    [StringField("someip.e2e.profile", "E2E Profile", IndexGroup = "someip.e2e")]
    private FieldId _E2eProfileFieldId;

    [U64Field("someip.e2e.length", "E2E Length", IndexGroup = "someip.e2e")]
    private FieldId _E2eLengthFieldId;

    [U64Field("someip.e2e.counter", "E2E Counter", IndexGroup = "someip.e2e")]
    private FieldId _E2eCounterFieldId;

    [U64Field("someip.e2e.data_id", "E2E Data ID", IndexGroup = "someip.e2e")]
    private FieldId _E2eDataIdFieldId;

    [U64Field("someip.e2e.crc", "E2E CRC", IndexGroup = "someip.e2e")]
    private FieldId _E2eCrcFieldId;

    [U64Field("someip.e2e.crc_calculated", "E2E CRC Calculated", IndexGroup = "someip.e2e")]
    private FieldId _E2eCrcCalculatedFieldId;

    [BoolField("someip.e2e.crc_valid", "E2E CRC Valid", IndexGroup = "someip.e2e")]
    private FieldId _E2eCrcValidFieldId;

    [StringField("someip.e2e.error", "E2E Anomaly", IndexGroup = "someip.e2e")]
    private FieldId _E2eErrorFieldId;

    #endregion

    #region State

    /// <summary>
    /// The fixed payload fields as one object for the decoder. Set once by <see cref="_RegisterPayloadFields"/> on the single-threaded
    /// stack build and read-only afterwards.
    /// </summary>
    private SomeIpPayloadFieldIds? _PayloadIds;

    /// <summary>
    /// The lazy populator of every member direction, indexed by <see cref="SomeIpMemberInfo.PayloadSlot"/> (request) and the slot
    /// after it (response). One delegate per direction is created here, once; a message then picks one by index, so a parse
    /// allocates no delegate per message and a populator needs no way to find its member again.
    /// </summary>
    private LazyPopulator[] _PayloadPopulators = [];

    #endregion

    #region Properties

    /// <summary>
    /// The problems of the payload field registration (a name the host refused), one text each. Read by the registration after
    /// the fields are registered and reported as load warnings; empty when every field was registered under its own name.
    /// </summary>
    internal IReadOnlyList<string> PayloadRegistrationProblems { get; private set; } = [];

    #endregion

    #region Registration

    /// <summary>
    /// Registers the fields of the configured payload types and creates the populator of every member direction. Called by the
    /// hook that runs after the declared fields are registered, because the fallback ids it needs exist only then.
    /// </summary>
    private void _RegisterPayloadFields(IStackBuilder builder, ProtocolId protocolId)
    {
        SomeIpPayloadFieldIds ids = new()
        {
            Unparsed = _PayloadUnparsedFieldId,
            Padding = _PayloadPaddingFieldId,
            Type = _PayloadTypeFieldId,
            Length = _PayloadLengthFieldId,
            ArrayCount = _PayloadArrayCountFieldId,
            Error = _PayloadErrorFieldId,
            ValueU64 = _PayloadValueU64FieldId,
            ValueI64 = _PayloadValueI64FieldId,
            ValueF64 = _PayloadValueF64FieldId,
            ValueBool = _PayloadValueBoolFieldId,
            ValueString = _PayloadValueStringFieldId,
            ValueUuid = _PayloadValueUuidFieldId,
            ValueNone = _PayloadValueNoneFieldId,
            TlvTag = _PayloadTlvTagFieldId,
            TlvWireType = _PayloadTlvWireTypeFieldId,
            TlvDataId = _PayloadTlvDataIdFieldId,
            TlvReserved = _PayloadTlvReservedFieldId,
            TlvUnknown = _PayloadTlvUnknownFieldId,
            UnionType = _PayloadUnionTypeFieldId,
        };

        List<string> problems = [];
        int slots = SomeIpPayloadFieldRegistry.Register(builder, protocolId, Model, ids, problems.Add);

        // One populator per member direction. The closure holds the member, which is how a populator finds the parameter list of
        // its message without a lookup: the message that picked this delegate already resolved the member.
        LazyPopulator[] populators = new LazyPopulator[slots];
        foreach (SomeIpServiceInfo service in Model.Services.Span)
        {
            foreach (SomeIpMemberInfo member in service.Members.Span)
            {
                // A member the registration skipped has no slot and no fields; its messages are treated as not configured.
                if (member.PayloadSlot < 0)
                {
                    continue;
                }

                populators[member.PayloadSlot] = (in MutField container) => _PopulatePayload(in container, member, outDirection: false, 0, verify: false);
                populators[member.PayloadSlot + 1] = (in MutField container) => _PopulatePayload(in container, member, outDirection: true, 0, verify: false);
            }
        }

        _PayloadIds = ids;
        _PayloadPopulators = populators;
        PayloadRegistrationProblems = problems;
    }

    #endregion

    #region Plan

    /// <summary>
    /// Decides whether the payload of a message is decoded and in which direction. The same decision serves the parse pass (which
    /// groups to record) and the message branch (which populator to attach), so the two cannot disagree.
    /// </summary>
    /// <param name="header">The header of the message.</param>
    /// <param name="member">The member the model resolved for the message, or <see langword="null"/> when it defines none.</param>
    /// <param name="outDirection">True for the response list, false for the request list.</param>
    /// <returns>
    /// False when the model does not define the member, or the message type has no payload description: an error message carries
    /// the error's own content, and a type the format does not define is shown raw.
    /// </returns>
    private bool _TryGetPayloadDirection(in SomeIpHeader header, [NotNullWhen(true)] SomeIpMemberInfo? member, out bool outDirection)
    {
        outDirection = false;
        if (member is null || _PayloadIds is null)
        {
            return false;
        }

        // The direction by the base type: a request and a request without return carry the input list; a response and a
        // notification carry the output list. The ACK and TP flags are cleared by BaseType, so a segmented or acknowledged message
        // is decoded like its plain form.
        switch (header.BaseType)
        {
            case 0x00:
            case 0x01:
                return true;
            case 0x02:
            case 0x80:
                outDirection = true;
                return true;
            default:
                return false;
        }
    }

    #endregion

    #region Parse branches

    /// <summary>
    /// Appends the service node and, below it, the method node of a message. Called exactly once per message on every path whose
    /// header resolved a service (also an empty payload, a segment that delivers nothing, a payload another parser took, an error):
    /// the identity comes from the header, not from the decode, so a filter on a method selects every message of it. The method
    /// node holds the payload bytes as its value (a slice, no copy) and, when a populator is given, builds the decoded values on
    /// demand. The display texts are strings of the model, so no text is built per message.
    /// </summary>
    /// <param name="container">The message container.</param>
    /// <param name="names">The names of the message; no service appends nothing, no member appends the service node only.</param>
    /// <param name="payload">The value of the method node: the payload, the delivered bytes, or empty.</param>
    /// <param name="populator">The lazy populator of the decoded values, or <see langword="null"/> for a node without children.</param>
    private static void _AppendIdentity(in MutField container, in MessageNames names, ReadOnlyMemory<byte> payload, LazyPopulator? populator)
    {
        if (names.Service is not { } service)
        {
            return;
        }

        MutField serviceNode = container.AppendWithCustomText(service.FieldId, FieldValue.None, service.UiName);
        if (names.Member is not { } member)
        {
            return;
        }

        if (populator is null)
        {
            _ = serviceNode.AppendWithCustomText(member.FieldId, FieldValue.NewBytes(payload), member.UiName);
            return;
        }

        _ = serviceNode.AppendLazyWithCustomText(member.FieldId, FieldValue.NewBytes(payload), member.UiName, populator);
    }

    /// <summary>
    /// Attaches a payload no other parser consumed: the raw bytes as <c>someip.payload</c> (every such payload, decoded or not, so
    /// the field keeps one meaning), and the identity nodes, whose method node carries the lazy decoded values when the member
    /// defines a parameter list for this message type and decoding is on. The raw bytes carry a marker when decoding is switched
    /// off, so a reader knows the bytes are undecoded by choice.
    /// </summary>
    /// <param name="container">The message container.</param>
    /// <param name="header">The header of the message; for a segmented message the header of the delivering segment.</param>
    /// <param name="names">The names of the message (service and member the model resolved, if any).</param>
    /// <param name="payload">The payload: the message payload, or the bytes a segmented message delivered.</param>
    /// <param name="partial">Whether the payload is the gap-free prefix of an incomplete segmented message.</param>
    /// <param name="context">Parse context, for index recording.</param>
    private void _AppendPayload(
        in MutField container,
        in SomeIpHeader header,
        in MessageNames names,
        ReadOnlyMemory<byte> payload,
        bool partial,
        in ParseContext context)
    {
        LazyPopulator? populator = null;
        bool decodable = _TryGetPayloadDirection(in header, names.Member, out bool outDirection);
        if (decodable && Settings.DecodePayload)
        {
            populator = _SelectPopulator(in header, names.Member!, outDirection, payload, partial, in context); // decodable: the member is set
        }

        _AppendIdentity(in container, in names, payload, populator);

        MutField raw = container.Append(_PayloadFieldId, FieldValue.NewBytes(payload));
        if (decodable && !Settings.DecodePayload)
        {
            // The marker says the bytes are undecoded by choice; no member group is recorded, so a filter on a parameter selects
            // nothing, which is right: no parameter is in the tree.
            _ = raw.Append(_PayloadDisabledFieldId, FieldValue.NewString(_DisabledText));
        }
    }

    /// <summary>Records the groups of a decodable payload and returns the populator that builds its values.</summary>
    private LazyPopulator _SelectPopulator(
        in SomeIpHeader header,
        SomeIpMemberInfo member,
        bool outDirection,
        ReadOnlyMemory<byte> payload,
        bool partial,
        in ParseContext context)
    {
        // The member group is a superset: the message may hold a parameter or may not (truncated, or a TLV member that is absent),
        // and the lazy subtree cannot say before it is built. The filter verifies the candidates on the tree.
        IndexGroupId memberGroup = member.InGroup;
        int directionSlot = 0;
        if (outDirection)
        {
            memberGroup = member.OutGroup;
            directionSlot = 1;
        }

        context.RecordGroupPresence(memberGroup);

        // A protected member always shows the end-to-end container (the header, or the reason there is none), so the group is exact.
        if (member.E2e != SomeIpE2eProfile.None)
        {
            context.RecordGroupPresence(_SomeipE2eGroupId);
        }

        LazyPopulator populator = _PayloadPopulators[member.PayloadSlot + directionSlot];
        if (Settings.VerifyE2eCrc
            && !partial
            && member.E2e is SomeIpE2eProfile.Profile04 or SomeIpE2eProfile.Profile44
            && payload.Length >= SomeIpE2e.Header12)
        {
            // The checksum covers header bytes that the payload container does not hold, and a lazy populator can neither reach its
            // message (a skip-tree parse has no parent) nor be handed an argument. The eight bytes therefore travel in the delegate
            // of this one message. Cost: one closure and one delegate per verified message, paid only for a protected member with
            // verification switched on; rejected alternative: a second effect record per message, which would also cost an
            // allocation and a lookup for every message of the stack. A partial prefix verifies nothing: the sum is over the
            // complete payload.
            ulong region = SomeIpE2e.PackHeaderRegion(in header);
            populator = _CreateVerifyingPopulator(member, outDirection, region);
        }

        return populator;
    }

    /// <summary>
    /// Creates the populator of one verified message. It lives in a method of its own on purpose: a lambda that captures locals
    /// makes the compiler allocate the closure object when the enclosing scope is entered, not when the lambda is created, so a
    /// lambda inside <see cref="_AppendPayload"/> would cost an allocation on every message, protected or not.
    /// </summary>
    private LazyPopulator _CreateVerifyingPopulator(SomeIpMemberInfo member, bool outDirection, ulong headerRegion) =>
        (in MutField c) => _PopulatePayload(in c, member, outDirection, headerRegion, verify: true);

    /// <summary>
    /// A message without payload for a member whose direction declares parameters is incomplete; the sender left them out. The
    /// diagnostic is eager because no container exists to hold it.
    /// </summary>
    private void _AppendMissingPayload(in MutField container, in SomeIpHeader header, SomeIpMemberInfo? member, in ParseContext context)
    {
        if (!Settings.DecodePayload || header.IsTp || !_TryGetPayloadDirection(in header, member, out bool outDirection))
        {
            return;
        }

        SomeIpParameterList list = outDirection
            ? member.Out
            : member.In;
        if (list.SitesCount == 0)
        {
            return;
        }

        context.RecordGroupPresence(_SomeipPayloadGroupId);
        _ = container.Append(
            _PayloadErrorFieldId,
            FieldValue.NewString(string.Create(
                CultureInfo.InvariantCulture,
                $"The message has no payload but '{member.UiName}' declares {list.SitesCount} parameters in this direction.")));
    }

    #endregion

    #region Population

    /// <summary>
    /// Builds the subtree of a payload container: the end-to-end header of a protected member, then the parameter list of the member
    /// direction over the bytes behind it.
    /// </summary>
    /// <param name="container">The payload container; its value is the payload.</param>
    /// <param name="member">The member the message belongs to.</param>
    /// <param name="outDirection">True for the response list.</param>
    /// <param name="headerRegion">The packed SOME/IP header bytes of the checksum; meaningful when <paramref name="verify"/> is true.</param>
    /// <param name="verify">Whether the checksum of this message is verified.</param>
    private ParseResult _PopulatePayload(in MutField container, SomeIpMemberInfo member, bool outDirection, ulong headerRegion, bool verify)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> payload))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        // The protection header first. A payload that cannot carry it (too short, an unknown profile) is shown as it is, and no
        // parameter is decoded from bytes whose meaning is unknown.
        ReadOnlyMemory<byte> inner = payload;
        if (member.E2e != SomeIpE2eProfile.None && !_AppendE2e(in container, member, payload, headerRegion, verify, out inner))
        {
            return 0;
        }

        // Offsets of the decode are relative to the first parameter byte, so alignment counts from the serialization and not from the
        // protection header.
        SomeIpDecodeState state = new(
            Model, _PayloadIds!, inner, member.Transform, Settings.ShowTypeMetadata, Settings.MaxTypeDepth, Settings.MaxPayloadNodes);
        SomeIpParameterList parameters = member.In;
        if (outDirection)
        {
            parameters = member.Out;
        }

        SomeIpPayloadDecoder.DecodeList(in container, ref state, parameters);
        return 0;
    }

    /// <summary>
    /// Appends the end-to-end container of a protected payload: the header fields and, for profiles 04 and 44 with verification on,
    /// the calculated checksum and the verdict.
    /// </summary>
    /// <param name="container">The payload container.</param>
    /// <param name="member">The protected member.</param>
    /// <param name="payload">The whole payload.</param>
    /// <param name="headerRegion">The packed SOME/IP header bytes of the checksum.</param>
    /// <param name="verify">Whether to verify the checksum.</param>
    /// <param name="inner">The bytes behind the header when the header was decoded.</param>
    /// <returns>False when the payload is shown as raw bytes instead (unknown profile, or shorter than the header).</returns>
    private bool _AppendE2e(
        in MutField container,
        SomeIpMemberInfo member,
        ReadOnlyMemory<byte> payload,
        ulong headerRegion,
        bool verify,
        out ReadOnlyMemory<byte> inner)
    {
        inner = payload;
        SomeIpE2eProfile profile = member.E2e;

        // A profile this build does not know consumes no header: a guessed length would misalign every inner byte. The payload is
        // shown as raw bytes, with one diagnostic that names the profile as the description spells it.
        if (profile == SomeIpE2eProfile.Unknown)
        {
            MutField unknown = container.AppendWithCustomText(
                _E2eFieldId, FieldValue.NewBytes(ReadOnlyMemory<byte>.Empty), "(unknown profile ", member.E2eProfileText, ")");
            _ = unknown.Append(
                _E2eErrorFieldId,
                FieldValue.NewString(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The end-to-end profile '{member.E2eProfileText}' is not supported (04, 44 and 08 are); the payload is not decoded.")));
            _ = container.Append(_PayloadUnparsedFieldId, FieldValue.NewBytes(payload));
            return false;
        }

        string name = SomeIpE2e.ProfileName(profile);
        int size = SomeIpE2e.HeaderSize(profile);
        if (payload.Length < size)
        {
            // A cut or malformed message cannot carry the header; reading one would take payload bytes for a counter and a checksum.
            MutField incomplete = container.AppendWithCustomText(
                _E2eFieldId, FieldValue.NewBytes(ReadOnlyMemory<byte>.Empty), "Profile ", name, " (incomplete)");
            _ = incomplete.Append(
                _E2eErrorFieldId,
                FieldValue.NewString(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The end-to-end header of profile {name} has {size} bytes but the payload holds {payload.Length} of {size}.")));
            _ = container.Append(_PayloadUnparsedFieldId, FieldValue.NewBytes(payload));
            return false;
        }

        ReadOnlySpan<byte> header = payload.Span[..size];
        MutField node = container.AppendWithCustomText(_E2eFieldId, FieldValue.NewBytes(payload[..size]), "Profile ", name);
        _ = node.Append(_E2eProfileFieldId, FieldValue.NewString(name));
        uint crc;
        if (profile == SomeIpE2eProfile.Profile08)
        {
            // Profile 08: the checksum comes first and every field has 32 bits.
            crc = BinaryPrimitives.ReadUInt32BigEndian(header);
            _ = node.AppendWithCustomText(_E2eCrcFieldId, FieldValue.NewU64(crc), "0x", new UpperHex8(crc));
            _ = node.Append(_E2eLengthFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt32BigEndian(header[4..])));
            _ = node.Append(_E2eCounterFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt32BigEndian(header[8..])));
            uint dataId = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
            _ = node.AppendWithCustomText(_E2eDataIdFieldId, FieldValue.NewU64(dataId), "0x", new UpperHex8(dataId));
        }
        else
        {
            // Profiles 04 and 44: length and counter have 16 bits, the data id and the checksum 32 bits.
            crc = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
            _ = node.Append(_E2eLengthFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(header)));
            _ = node.Append(_E2eCounterFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(header[2..])));
            uint dataId = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            _ = node.AppendWithCustomText(_E2eDataIdFieldId, FieldValue.NewU64(dataId), "0x", new UpperHex8(dataId));
            _ = node.AppendWithCustomText(_E2eCrcFieldId, FieldValue.NewU64(crc), "0x", new UpperHex8(crc));

            // The verdict, only for the profiles whose construction is pinned, and only when the populator was given the header
            // bytes (verification is on and the payload is complete).
            if (verify)
            {
                uint calculated = SomeIpE2e.ComputeProfile04(headerRegion, payload.Span);
                _ = node.AppendWithCustomText(_E2eCrcCalculatedFieldId, FieldValue.NewU64(calculated), "0x", new UpperHex8(calculated));
                _ = node.Append(_E2eCrcValidFieldId, FieldValue.NewBool(calculated == crc));
            }
        }

        inner = payload[size..];
        return true;
    }

    #endregion
}

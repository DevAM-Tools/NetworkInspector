// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// The service discovery part of the SOME/IP parser. A discovery message (message id 0xFFFF8100) is announced to the index by an
/// eager scan and expanded on demand by a lazy populator, both driven by the one walk of <see cref="SomeIpSdDecoder"/>.
/// <para>
/// Field tree under the discovery container (<c>someip_sd</c>, lazy):
/// </para>
/// <code>
/// someip_sd: SOME/IP-SD (unicast)
/// ├── someip_sd.flags: 0xC0 (.reboot .unicast .initial_events)
/// ├── someip_sd.reserved, someip_sd.entries_length
/// ├── someip_sd.entries: Entries Array (2 entries)
/// │   ├── someip_sd.entry: Offer Service (Service ID 0xF232 (ClimateControl), Instance ID 0x0001, Version 2.0, Options: 0-0)
/// │   │   ├── .type .index1 .index2 .n_opt_1 .n_opt_2 .options_referenced
/// │   │   ├── .serviceid .service.name .instanceid .majorver .ttl
/// │   │   ├── .minorver | .reserved .initial_events .reserved2 .counter .eventgroupid .eventgroup.name
/// │   │   └── someip_sd.error.entry_unknown | offer_without_endpoint | subscribe_after_stop
/// │   └── someip_sd.entries.raw                     [bytes that are not a whole entry]
/// ├── someip_sd.options_length
/// ├── someip_sd.options: Options Array (12 bytes)
/// │   ├── someip_sd.option: IPv4 Endpoint (192.168.1.100:30512 (UDP))
/// │   │   ├── .length .type .reserved .ipv4 | .ipv6 .reserved2 .proto .port
/// │   │   ├── .config (+ .config.item (+ .key .value))* | .lb_priority .lb_weight | .data
/// │   │   └── someip_sd.error.option_unknown | option_wrong_length | l4_unsupported | config_malformed
/// │   └── someip_sd.options.raw                     [bytes that are not a decodable option]
/// └── someip_sd.error.truncated | entries_malformed | entries_empty | options_truncated | options_bytes_left | too_many_options
/// </code>
/// </summary>
public sealed partial class SomeIpProtocol
{
    #region Fields: container and fixed part

    // The group names of service discovery are camel case on purpose: the generator builds the group field names from them
    // (someipSd.error.entriesMalformed becomes _SomeipSdErrorEntriesMalformedGroupId), and an underscore would carry into an
    // identifier. The lazy container holds the payload bytes as its value, so a viewer can highlight them.
    [BytesField("someip_sd", "SOME/IP-SD", IndexGroup = "someipSd")]
    private FieldId _SdContainerFieldId;

    [U64Field("someip_sd.flags", "Flags", IndexGroup = "someipSd")]
    private FieldId _SdFlagsFieldId;

    [BoolField("someip_sd.flags.reboot", "Reboot", IndexGroup = "someipSd")]
    private FieldId _SdFlagsRebootFieldId;

    [BoolField("someip_sd.flags.unicast", "Unicast", IndexGroup = "someipSd")]
    private FieldId _SdFlagsUnicastFieldId;

    [BoolField("someip_sd.flags.initial_events", "Explicit Initial Data Events Request", IndexGroup = "someipSd")]
    private FieldId _SdFlagsInitialEventsFieldId;

    [U64Field("someip_sd.reserved", "Reserved", IndexGroup = "someipSd")]
    private FieldId _SdReservedFieldId;

    [U64Field("someip_sd.entries_length", "Entries Length", IndexGroup = "someipSd")]
    private FieldId _SdEntriesLengthFieldId;

    [U64Field("someip_sd.options_length", "Options Length", IndexGroup = "someipSd")]
    private FieldId _SdOptionsLengthFieldId;

    #endregion

    #region Fields: entries

    [NoneField("someip_sd.entries", "Entries Array", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntriesFieldId;

    [BytesField("someip_sd.entries.raw", "Bytes Behind The Last Entry", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntriesRawFieldId;

    [BytesField("someip_sd.entry", "Entry", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryFieldId;

    [U64Field("someip_sd.entry.type", "Type", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryTypeFieldId;

    [U64Field("someip_sd.entry.index1", "Index 1st Options", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryIndex1FieldId;

    [U64Field("someip_sd.entry.index2", "Index 2nd Options", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryIndex2FieldId;

    [U64Field("someip_sd.entry.n_opt_1", "Number Of Options 1", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryNumOpt1FieldId;

    [U64Field("someip_sd.entry.n_opt_2", "Number Of Options 2", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryNumOpt2FieldId;

    [StringField("someip_sd.entry.options_referenced", "Options Referenced", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryOptionsReferencedFieldId;

    [U64Field("someip_sd.entry.serviceid", "Service ID", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryServiceIdFieldId;

    [StringField("someip_sd.entry.service.name", "Service Name", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryServiceNameFieldId;

    [U64Field("someip_sd.entry.instanceid", "Instance ID", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryInstanceIdFieldId;

    [U64Field("someip_sd.entry.majorver", "Major Version", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryMajorVerFieldId;

    [U64Field("someip_sd.entry.ttl", "TTL", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryTtlFieldId;

    [U64Field("someip_sd.entry.minorver", "Minor Version", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryMinorVerFieldId;

    [U64Field("someip_sd.entry.reserved", "Reserved", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryReservedFieldId;

    [BoolField("someip_sd.entry.initial_events", "Initial Data Requested", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryInitialEventsFieldId;

    [U64Field("someip_sd.entry.reserved2", "Reserved (bits 4-6)", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryReserved2FieldId;

    [U64Field("someip_sd.entry.counter", "Counter", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryCounterFieldId;

    [U64Field("someip_sd.entry.eventgroupid", "Eventgroup ID", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryEventgroupIdFieldId;

    [StringField("someip_sd.entry.eventgroup.name", "Eventgroup Name", IndexGroup = "someipSd.entries")]
    private FieldId _SdEntryEventgroupNameFieldId;

    #endregion

    #region Fields: options

    [NoneField("someip_sd.options", "Options Array", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionsFieldId;

    [BytesField("someip_sd.options.raw", "Bytes That Are Not An Option", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionsRawFieldId;

    [BytesField("someip_sd.option", "Option", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionFieldId;

    [U64Field("someip_sd.option.length", "Length", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionLengthFieldId;

    [U64Field("someip_sd.option.type", "Type", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionTypeFieldId;

    [U64Field("someip_sd.option.reserved", "Reserved", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionReservedFieldId;

    [IPv4Field("someip_sd.option.ipv4", "IPv4 Address", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionIpv4FieldId;

    [IPv6Field("someip_sd.option.ipv6", "IPv6 Address", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionIpv6FieldId;

    [U64Field("someip_sd.option.reserved2", "Reserved (before protocol)", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionReserved2FieldId;

    [U64Field("someip_sd.option.proto", "L4 Protocol", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionProtoFieldId;

    [U64Field("someip_sd.option.port", "Port", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionPortFieldId;

    [StringField("someip_sd.option.config", "Configuration", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionConfigFieldId;

    [NoneField("someip_sd.option.config.item", "Configuration Item", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionConfigItemFieldId;

    [StringField("someip_sd.option.config.item.key", "Key", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionConfigItemKeyFieldId;

    [StringField("someip_sd.option.config.item.value", "Value", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionConfigItemValueFieldId;

    [U64Field("someip_sd.option.lb_priority", "Priority", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionLbPriorityFieldId;

    [U64Field("someip_sd.option.lb_weight", "Weight", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionLbWeightFieldId;

    [BytesField("someip_sd.option.data", "Data", IndexGroup = "someipSd.options")]
    private FieldId _SdOptionDataFieldId;

    #endregion

    #region Fields: diagnostics

    [StringField("someip_sd.error.truncated", "SD Truncated", IndexGroup = "someipSd.error.truncated")]
    private FieldId _SdErrorTruncatedFieldId;

    [StringField("someip_sd.error.entries_malformed", "SD Entries Malformed", IndexGroup = "someipSd.error.entriesMalformed")]
    private FieldId _SdErrorEntriesMalformedFieldId;

    [StringField("someip_sd.error.entries_empty", "SD Entries Empty", IndexGroup = "someipSd.error.entriesEmpty")]
    private FieldId _SdErrorEntriesEmptyFieldId;

    [StringField("someip_sd.error.entry_unknown", "SD Entry Unknown", IndexGroup = "someipSd.error.entryUnknown")]
    private FieldId _SdErrorEntryUnknownFieldId;

    [StringField("someip_sd.error.offer_without_endpoint", "SD Offer Without Endpoint", IndexGroup = "someipSd.error.offerWithoutEndpoint")]
    private FieldId _SdErrorOfferWithoutEndpointFieldId;

    [StringField("someip_sd.error.subscribe_after_stop", "SD Subscribe After Stop", IndexGroup = "someipSd.error.subscribeAfterStop")]
    private FieldId _SdErrorSubscribeAfterStopFieldId;

    [StringField("someip_sd.error.options_truncated", "SD Options Truncated", IndexGroup = "someipSd.error.optionsTruncated")]
    private FieldId _SdErrorOptionsTruncatedFieldId;

    [StringField("someip_sd.error.options_bytes_left", "SD Options Bytes Left", IndexGroup = "someipSd.error.optionsBytesLeft")]
    private FieldId _SdErrorOptionsBytesLeftFieldId;

    [StringField("someip_sd.error.option_unknown", "SD Option Unknown", IndexGroup = "someipSd.error.optionUnknown")]
    private FieldId _SdErrorOptionUnknownFieldId;

    [StringField("someip_sd.error.option_wrong_length", "SD Option Wrong Length", IndexGroup = "someipSd.error.optionWrongLength")]
    private FieldId _SdErrorOptionWrongLengthFieldId;

    [StringField("someip_sd.error.l4_unsupported", "SD L4 Protocol Unsupported", IndexGroup = "someipSd.error.l4Unsupported")]
    private FieldId _SdErrorL4UnsupportedFieldId;

    [StringField("someip_sd.error.config_malformed", "SD Configuration Malformed", IndexGroup = "someipSd.error.configMalformed")]
    private FieldId _SdErrorConfigMalformedFieldId;

    [StringField("someip_sd.error.too_many_options", "SD Too Many Options", IndexGroup = "someipSd.error.tooManyOptions")]
    private FieldId _SdErrorTooManyOptionsFieldId;

    #endregion

    #region State

    /// <summary>The lazy populator of the discovery container; one delegate per protocol instance, created with it.</summary>
    private readonly LazyPopulator _SdPopulator;

    /// <summary>Field id of each diagnostic, indexed by the bit number of its <see cref="SomeIpSdErrors"/> flag (13 entries).</summary>
    private FieldId[] _SdErrorFieldIds = [];

    /// <summary>Index group of each diagnostic, indexed like <see cref="_SdErrorFieldIds"/>.</summary>
    private IndexGroupId[] _SdErrorGroupIds = [];

    /// <summary>
    /// Fills the per-diagnostic tables once the fields and groups exist. Runs during the single-threaded stack build; the tables
    /// are read-only afterwards, so a parse indexes them without a lock. A table indexed by the flag's bit number replaces a
    /// 13-way branch in two places (the index recording and the population), which is why both stay in step with the enum.
    /// </summary>
    partial void _RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        _SdErrorFieldIds =
        [
            _SdErrorTruncatedFieldId, _SdErrorEntriesMalformedFieldId, _SdErrorEntriesEmptyFieldId, _SdErrorEntryUnknownFieldId,
            _SdErrorOfferWithoutEndpointFieldId, _SdErrorSubscribeAfterStopFieldId, _SdErrorOptionsTruncatedFieldId,
            _SdErrorOptionsBytesLeftFieldId, _SdErrorOptionUnknownFieldId, _SdErrorOptionWrongLengthFieldId,
            _SdErrorL4UnsupportedFieldId, _SdErrorConfigMalformedFieldId, _SdErrorTooManyOptionsFieldId,
        ];
        _SdErrorGroupIds =
        [
            _SomeipSdErrorTruncatedGroupId, _SomeipSdErrorEntriesMalformedGroupId, _SomeipSdErrorEntriesEmptyGroupId,
            _SomeipSdErrorEntryUnknownGroupId, _SomeipSdErrorOfferWithoutEndpointGroupId, _SomeipSdErrorSubscribeAfterStopGroupId,
            _SomeipSdErrorOptionsTruncatedGroupId, _SomeipSdErrorOptionsBytesLeftGroupId, _SomeipSdErrorOptionUnknownGroupId,
            _SomeipSdErrorOptionWrongLengthGroupId, _SomeipSdErrorL4UnsupportedGroupId, _SomeipSdErrorConfigMalformedGroupId,
            _SomeipSdErrorTooManyOptionsGroupId,
        ];

        // The payload fields come last: they register fields of their own and need the fallback ids that the declared fields
        // received before this hook ran.
        _RegisterPayloadFields(builder, protocolId);

        // The DTLS protocol for the opt-in detection. It may register after this protocol, so the id arrives through a callback.
        builder.WhenProtocolRegistered("dtls", id => _DtlsProtocolId = id);
    }

    #endregion

    #region Parse branch

    /// <summary>
    /// Handles the payload of a discovery message: scans it, records exactly the groups the lazy subtree will hold, and appends
    /// the lazy container. A payload shorter than the fixed part has no container; the diagnostic goes on the message container
    /// and the caller keeps the bytes as raw payload.
    /// </summary>
    /// <param name="container">The message container.</param>
    /// <param name="sd">The payload behind the SOME/IP header (empty when the message has none).</param>
    /// <param name="context">Parse context, for index recording.</param>
    /// <param name="isReplay">Whether this is a re-parse: the scan then learns no ports.</param>
    /// <param name="summary">The packet-info text of the message when a container was appended.</param>
    /// <returns>True when the discovery container was appended and the payload is accounted for.</returns>
    private bool _AppendSd(in MutField container, ReadOnlyMemory<byte> sd, in ParseContext context, bool isReplay, out string? summary)
    {
        summary = null;

        // A re-parse never learns: it scans without the learned-port table, so the first parse alone changes it.
        SomeIpLearnedPorts? learnedPorts = null;
        if (!isReplay)
        {
            learnedPorts = _LearnedPorts;
        }

        SomeIpSdScan scan = SomeIpSdDecoder.Scan(sd.Span, learnedPorts);
        _RecordSdGroups(in context, in scan);

        if (!scan.HasContainer)
        {
            // Fewer bytes than the fixed part: nothing to expand, but the message is malformed and says so.
            container.Append(_SdErrorFieldIds[0], FieldValue.NewString(_SdErrorText(SomeIpSdErrors.Truncated)));
            return false;
        }

        string containerText = scan.UnicastFlag
            ? "SOME/IP-SD (unicast)"
            : "SOME/IP-SD (multicast)";
        container.AppendLazyWithCustomText(
            _SdContainerFieldId,
            FieldValue.NewBytes(sd),
            new LazyString(containerText),
            _SdPopulator);
        summary = SomeIpTables.SdSummary(scan.EntryKinds, scan.UnicastFlag);
        return true;
    }

    /// <summary>
    /// Records the index groups of a scan: the container, the entries and the options when they are emitted, and one group per
    /// diagnostic. A group is recorded exactly when the lazy subtree holds a field of it; the scan and the population are driven by
    /// the same walk, so the two cannot differ.
    /// </summary>
    private void _RecordSdGroups(in ParseContext context, in SomeIpSdScan scan)
    {
        if (scan.HasContainer)
        {
            context.RecordGroupPresence(_SomeipSdGroupId);
        }

        if (scan.HasEntries)
        {
            context.RecordGroupPresence(_SomeipSdEntriesGroupId);
        }

        if (scan.HasOptions)
        {
            context.RecordGroupPresence(_SomeipSdOptionsGroupId);
        }

        uint errors = (uint)scan.Errors;
        while (errors != 0)
        {
            int bit = BitOperations.TrailingZeroCount(errors);
            context.RecordGroupPresence(_SdErrorGroupIds[bit]);
            errors &= errors - 1;
        }
    }

    /// <summary>The sentence of a diagnostic. Constants: a diagnostic names the rule, the bytes are in the tree next to it.</summary>
    private static string _SdErrorText(SomeIpSdErrors error) => error switch
    {
        SomeIpSdErrors.Truncated => "The service discovery payload ends before the bytes that it declares.",
        SomeIpSdErrors.EntriesMalformed => "The entries array is not a whole number of 16-byte entries.",
        SomeIpSdErrors.EntriesEmpty => "The entries array has length zero.",
        SomeIpSdErrors.EntryUnknown => "The entry type is not one of find, offer, subscribe, subscribe acknowledgement.",
        SomeIpSdErrors.OfferWithoutEndpoint => "The offer references no option, so it announces no endpoint.",
        SomeIpSdErrors.SubscribeAfterStop => "This subscribe directly follows the stop-subscribe of the same service, instance and eventgroup.",
        SomeIpSdErrors.OptionsTruncated => "The options array or an option ends beyond the payload.",
        SomeIpSdErrors.OptionsBytesLeft => "Fewer than three bytes remain after the last option, too few for the next option.",
        SomeIpSdErrors.OptionUnknown => "The option type is not defined.",
        SomeIpSdErrors.OptionWrongLength => "The option has a size that its type does not allow.",
        SomeIpSdErrors.L4Unsupported => "A multicast or service discovery endpoint option names TCP, which cannot be multicast.",
        SomeIpSdErrors.ConfigMalformed => "A configuration item is longer than the bytes left in the option.",
        SomeIpSdErrors.TooManyOptions => "More than 271 options: an entry cannot reference the rest, which is shown as raw bytes.",
        _ => "Service discovery anomaly.",
    };

    #endregion

    #region Population

    /// <summary>Builds the subtree of the discovery container from its stored payload bytes. Reads nothing else but the immutable model.</summary>
    private ParseResult _PopulateSd(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> sd))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        SdPopulateVisitor visitor = new(this, in container, sd);
        SomeIpSdDecoder.Walk(sd.Span, ref visitor);
        return 0;
    }

    /// <summary>
    /// Appends the events of the walk to the tree. It lives inside the protocol because it reads its field ids; it holds the
    /// containers it appends to, which is why it is a <c>ref struct</c> (a <see cref="MutField"/> cannot be stored otherwise).
    /// </summary>
    private ref struct SdPopulateVisitor : ISomeIpSdVisitor
    {
        private readonly SomeIpProtocol _Owner;
        private readonly MutField _Sd;
        private readonly ReadOnlyMemory<byte> _Bytes;
        private MutField _Entries;
        private MutField _Options;

        internal SdPopulateVisitor(SomeIpProtocol owner, in MutField sd, ReadOnlyMemory<byte> bytes)
        {
            _Owner = owner;
            _Sd = sd;
            _Bytes = bytes;
            _Entries = default;
            _Options = default;
        }

        public void Container(byte flags, uint reserved, uint entriesLength)
        {
            // The flags byte: bit 7 reboot, bit 6 unicast, bit 5 explicit initial data request; the other five bits are reserved
            // and shown only in the number.
            MutField flagsField = _Sd.AppendWithCustomText(_Owner._SdFlagsFieldId, FieldValue.NewU64(flags), new LazyString(DisplayTables.FormatHexU8(flags)));
            flagsField.Append(_Owner._SdFlagsRebootFieldId, FieldValue.NewBool((flags & SomeIpSdDecoder.RebootFlagMask) != 0));
            flagsField.Append(_Owner._SdFlagsUnicastFieldId, FieldValue.NewBool((flags & SomeIpSdDecoder.UnicastFlagMask) != 0));
            flagsField.Append(_Owner._SdFlagsInitialEventsFieldId, FieldValue.NewBool((flags & SomeIpSdDecoder.InitialEventsFlagMask) != 0));
            _Sd.Append(_Owner._SdReservedFieldId, FieldValue.NewU64(reserved));
            _Sd.Append(_Owner._SdEntriesLengthFieldId, FieldValue.NewU64(entriesLength));
        }

        public void ArrayError(SomeIpSdErrors error) => _AppendError(in _Sd, error);

        public void EntriesBegin(int wholeEntries, int rawBytes) =>
            _Entries = _Sd.AppendWithCustomText(_Owner._SdEntriesFieldId, FieldValue.None, "Entries Array (", wholeEntries, " entries)");

        public void Entry(int index, int offset, ReadOnlySpan<byte> entry, SomeIpSdErrors errors)
        {
            byte type = entry[0];
            uint ttl = SomeIpSdDecoder.TimeToLive(entry);
            bool ttlZero = ttl == 0;
            ushort service = BinaryPrimitives.ReadUInt16BigEndian(entry[4..]);
            ushort instance = BinaryPrimitives.ReadUInt16BigEndian(entry[6..]);
            byte major = entry[8];
            int firstIndex = entry[1];
            int secondIndex = entry[2];
            int firstCount = entry[3] >> 4;
            int secondCount = entry[3] & 0x0F;

            // Names: the model knows the service by id and interface version; a version it does not define still gives the name.
            SomeIpCompiledModel model = _Owner.Model;
            bool hasModel = !model.Services.IsEmpty;
            string? serviceName = null;
            FrozenDictionary<ushort, string>? eventgroupNames = null;
            if (model.TryGetService(service, major, out int serviceIndex))
            {
                SomeIpServiceInfo info = model.Services.Span[serviceIndex];
                serviceName = info.UiName;
                eventgroupNames = info.EventGroupNames;
            }
            else if (model.TryGetServiceName(service, out string? anyVersion))
            {
                serviceName = anyVersion;
            }

            string suffixWord = serviceName ?? "";
            if (service == SomeIpTables.SdAnyServiceId)
            {
                suffixWord = "ANY";
            }
            else if (service == SomeIpTables.SdOtherServiceId)
            {
                suffixWord = "other service";
            }

            bool special = service is SomeIpTables.SdAnyServiceId or SomeIpTables.SdOtherServiceId;
            NameSuffix serviceSuffix = special
                ? new NameSuffix(suffixWord, unknown: false)
                : new NameSuffix(serviceName, unknown: hasModel && serviceName is null);

            string references = _OptionRuns(firstIndex, firstCount, secondIndex, secondCount);
            bool eventgroupEntry = SomeIpSdDecoder.IsEventgroupEntry(type);
            ushort eventgroup = BinaryPrimitives.ReadUInt16BigEndian(entry[14..]);
            string? eventgroupName = null;
            eventgroupNames?.TryGetValue(eventgroup, out eventgroupName);

            // The summary of the entry: what a reader needs without expanding it. Built here because the population is the lazy
            // path (an entry is expanded on demand), and it is stored as the display text of the container.
            string typeName = SomeIpTables.SdEntryTypeName(type, ttlZero);
            string summary = eventgroupEntry
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{typeName} (Service ID {new HexId(service)}{serviceSuffix}, Instance ID {new HexId(instance)}, Eventgroup ID {new HexId(eventgroup)}{_EventgroupSuffix(eventgroup, eventgroupName)}, Options: {references})")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{typeName} (Service ID {new HexId(service)}{serviceSuffix}, Instance ID {new HexId(instance)}, Version {major}.{BinaryPrimitives.ReadUInt32BigEndian(entry[12..])}, Options: {references})");

            MutField field = _Entries.AppendWithCustomText(
                _Owner._SdEntryFieldId, FieldValue.NewBytes(_Bytes.Slice(offset, entry.Length)), new LazyString(summary));
            field.AppendWithCustomText(_Owner._SdEntryTypeFieldId, FieldValue.NewU64(type), new LazyString(SomeIpTables.SdEntryType(type, ttlZero)));
            field.Append(_Owner._SdEntryIndex1FieldId, FieldValue.NewU64((ulong)firstIndex));
            field.Append(_Owner._SdEntryIndex2FieldId, FieldValue.NewU64((ulong)secondIndex));
            field.Append(_Owner._SdEntryNumOpt1FieldId, FieldValue.NewU64((ulong)firstCount));
            field.Append(_Owner._SdEntryNumOpt2FieldId, FieldValue.NewU64((ulong)secondCount));
            field.Append(_Owner._SdEntryOptionsReferencedFieldId, FieldValue.NewString(references));
            field.AppendWithCustomText(_Owner._SdEntryServiceIdFieldId, FieldValue.NewU64(service), new HexId(service), serviceSuffix);
            if (serviceName is not null && !special)
            {
                field.Append(_Owner._SdEntryServiceNameFieldId, FieldValue.NewString(serviceName));
            }

            field.AppendWithCustomText(_Owner._SdEntryInstanceIdFieldId, FieldValue.NewU64(instance), new HexId(instance), _Any(instance == 0xFFFF));
            field.AppendWithCustomText(_Owner._SdEntryMajorVerFieldId, FieldValue.NewU64(major), major, _Any(major == 0xFF));
            string ttlSuffix = ttl == 0xFFFFFF
                ? " (infinite)"
                : "";
            field.AppendWithCustomText(_Owner._SdEntryTtlFieldId, FieldValue.NewU64(ttl), ttl, ttlSuffix);

            if (eventgroupEntry)
            {
                // Eventgroup entries: byte 12 is reserved; byte 13 holds the initial data request in its top bit, three reserved
                // bits (4-6) and a counter in its low nibble; bytes 14 and 15 are the eventgroup id. A non-zero reserved value is
                // shown and not judged: the specification tells the sender to write zero and the receiver to ignore the field.
                field.Append(_Owner._SdEntryReservedFieldId, FieldValue.NewU64(entry[12]));
                field.Append(_Owner._SdEntryInitialEventsFieldId, FieldValue.NewBool((entry[13] & 0x80) != 0));
                field.Append(_Owner._SdEntryReserved2FieldId, FieldValue.NewU64((ulong)((entry[13] >> 4) & 0x07)));
                field.Append(_Owner._SdEntryCounterFieldId, FieldValue.NewU64((ulong)(entry[13] & 0x0F)));
                field.AppendWithCustomText(
                    _Owner._SdEntryEventgroupIdFieldId, FieldValue.NewU64(eventgroup), new HexId(eventgroup), _EventgroupSuffix(eventgroup, eventgroupName));
                if (eventgroupName is not null)
                {
                    field.Append(_Owner._SdEntryEventgroupNameFieldId, FieldValue.NewString(eventgroupName));
                }
            }
            else if (type is SomeIpSdDecoder.TypeFind or SomeIpSdDecoder.TypeOffer)
            {
                uint minor = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
                field.AppendWithCustomText(_Owner._SdEntryMinorVerFieldId, FieldValue.NewU64(minor), minor, _Any(minor == uint.MaxValue));
            }

            _AppendErrors(in field, errors);
        }

        public void EntriesRaw(int offset, ReadOnlySpan<byte> raw) =>
            _Entries.Append(_Owner._SdEntriesRawFieldId, FieldValue.NewBytes(_Bytes.Slice(offset, raw.Length)));

        public void OptionsLength(uint length) => _Sd.Append(_Owner._SdOptionsLengthFieldId, FieldValue.NewU64(length));

        public void OptionsBegin(int optionBytes) =>
            _Options = _Sd.AppendWithCustomText(_Owner._SdOptionsFieldId, FieldValue.None, "Options Array (", optionBytes, " bytes)");

        public void Option(int index, int offset, ReadOnlySpan<byte> option, SomeIpSdOptionKind kind, SomeIpSdErrors errors)
        {
            byte type = option[2];
            string typeName = SomeIpTables.SdOptionTypeName(type);
            MutField field;
            switch (kind)
            {
                case SomeIpSdOptionKind.Ipv4Endpoint:
                {
                    IPv4Address address = IPv4Address.FromBytes(option.Slice(4, 4));
                    byte protocol = option[9];
                    ushort port = BinaryPrimitives.ReadUInt16BigEndian(option[10..]);
                    field = _AppendOptionContainer(offset, option.Length, string.Create(CultureInfo.InvariantCulture, $"{typeName} ({address}:{port} ({_ProtocolWord(protocol)}))"));
                    _AppendOptionHead(in field, option, type);
                    field.Append(_Owner._SdOptionIpv4FieldId, FieldValue.NewIPv4(address));

                    // The reserved byte between the address and the protocol (byte 8): shown, not judged.
                    field.Append(_Owner._SdOptionReserved2FieldId, FieldValue.NewU64(option[8]));
                    field.AppendWithCustomText(_Owner._SdOptionProtoFieldId, FieldValue.NewU64(protocol), new LazyString(SomeIpTables.L4Protocol(protocol)));
                    field.Append(_Owner._SdOptionPortFieldId, FieldValue.NewU64(port));
                    break;
                }

                case SomeIpSdOptionKind.Ipv6Endpoint:
                {
                    IPv6Address address = IPv6Address.FromBytes(option.Slice(4, 16));
                    byte protocol = option[21];
                    ushort port = BinaryPrimitives.ReadUInt16BigEndian(option[22..]);
                    field = _AppendOptionContainer(offset, option.Length, string.Create(CultureInfo.InvariantCulture, $"{typeName} ([{address}]:{port} ({_ProtocolWord(protocol)}))"));
                    _AppendOptionHead(in field, option, type);
                    field.Append(_Owner._SdOptionIpv6FieldId, FieldValue.NewIPv6(address));

                    // The reserved byte between the address and the protocol (byte 20): shown, not judged.
                    field.Append(_Owner._SdOptionReserved2FieldId, FieldValue.NewU64(option[20]));
                    field.AppendWithCustomText(_Owner._SdOptionProtoFieldId, FieldValue.NewU64(protocol), new LazyString(SomeIpTables.L4Protocol(protocol)));
                    field.Append(_Owner._SdOptionPortFieldId, FieldValue.NewU64(port));
                    break;
                }

                case SomeIpSdOptionKind.LoadBalancing:
                {
                    ushort priority = BinaryPrimitives.ReadUInt16BigEndian(option[4..]);
                    ushort weight = BinaryPrimitives.ReadUInt16BigEndian(option[6..]);
                    field = _AppendOptionContainer(offset, option.Length, string.Create(CultureInfo.InvariantCulture, $"{typeName} (Priority {priority}, Weight {weight})"));
                    _AppendOptionHead(in field, option, type);
                    field.Append(_Owner._SdOptionLbPriorityFieldId, FieldValue.NewU64(priority));
                    field.Append(_Owner._SdOptionLbWeightFieldId, FieldValue.NewU64(weight));
                    break;
                }

                case SomeIpSdOptionKind.Configuration:
                    field = _AppendConfiguration(offset, option, typeName, type);
                    break;

                default:
                    // An undefined type, or a defined type of the wrong size: the bytes behind the type are shown raw, because
                    // guessing at a layout would show values that are not there.
                    field = _AppendOptionContainer(offset, option.Length, string.Create(CultureInfo.InvariantCulture, $"{typeName}"));
                    _AppendOptionHead(in field, option, type);
                    if (option.Length > 3)
                    {
                        field.Append(_Owner._SdOptionDataFieldId, FieldValue.NewBytes(_Bytes.Slice(offset + 3, option.Length - 3)));
                    }

                    break;
            }

            _AppendErrors(in field, errors);
        }

        public void OptionsRaw(int offset, ReadOnlySpan<byte> raw) =>
            _Options.Append(_Owner._SdOptionsRawFieldId, FieldValue.NewBytes(_Bytes.Slice(offset, raw.Length)));

        private MutField _AppendOptionContainer(int offset, int length, string summary) =>
            _Options.AppendWithCustomText(_Owner._SdOptionFieldId, FieldValue.NewBytes(_Bytes.Slice(offset, length)), new LazyString(summary));

        /// <summary>The fields every option starts with: the length, the type and, when the option has one, the reserved byte.</summary>
        private readonly void _AppendOptionHead(in MutField field, ReadOnlySpan<byte> option, byte type)
        {
            field.Append(_Owner._SdOptionLengthFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(option)));
            field.AppendWithCustomText(_Owner._SdOptionTypeFieldId, FieldValue.NewU64(type), new LazyString(SomeIpTables.SdOptionType(type)));
            if (option.Length > 3)
            {
                field.Append(_Owner._SdOptionReservedFieldId, FieldValue.NewU64(option[3]));
            }
        }

        /// <summary>A configuration option: the items as children, the whole text as one string.</summary>
        private MutField _AppendConfiguration(int offset, ReadOnlySpan<byte> option, string typeName, byte type)
        {
            // One pass over the items with the one item rule of the decoder, so the walk and the population agree where the list
            // ends. The item text comes from the network, so it goes through the text decoder like every payload string: control
            // characters and invalid bytes are escaped, and a capture cannot repaint a console that prints the tree (threat:
            // spoofing and tampering of what the analyst reads; rejected: a plain UTF-8 conversion, which passes an ESC sequence
            // through; residual risk: a backslash in the data is not escaped, see SomeIpTextDecoder). The decoder takes memory, so
            // each item is sliced from the payload by its position instead of copied.
            List<string> itemTexts = [];
            if (option.Length > 4)
            {
                int position = 0;
                ReadOnlySpan<byte> items = option[4..];
                while (true)
                {
                    int itemStart = position + 1;
                    if (!SomeIpSdDecoder.NextConfigItem(items, ref position, out ReadOnlySpan<byte> item, out _))
                    {
                        break;
                    }

                    itemTexts.Add(SomeIpTextDecoder.Decode(_Bytes.Slice(offset + 4 + itemStart, item.Length), SomeIpStringEncoding.Utf8));
                }
            }

            string text = string.Join(", ", itemTexts);
            MutField field = _AppendOptionContainer(offset, option.Length, string.Create(CultureInfo.InvariantCulture, $"{typeName} ({text})"));
            _AppendOptionHead(in field, option, type);
            field.Append(_Owner._SdOptionConfigFieldId, FieldValue.NewString(text));
            foreach (string itemText in itemTexts)
            {
                MutField itemField = field.AppendWithCustomText(_Owner._SdOptionConfigItemFieldId, FieldValue.None, new LazyString(itemText));

                // key=value: the key is everything before the first '=', the value everything after it (of the decoded text); an
                // item without '=' is a key without a value (a flag).
                int equals = itemText.IndexOf('=', StringComparison.Ordinal);
                if (equals < 0)
                {
                    itemField.Append(_Owner._SdOptionConfigItemKeyFieldId, FieldValue.NewString(itemText));
                }
                else
                {
                    itemField.Append(_Owner._SdOptionConfigItemKeyFieldId, FieldValue.NewString(itemText[..equals]));
                    itemField.Append(_Owner._SdOptionConfigItemValueFieldId, FieldValue.NewString(itemText[(equals + 1)..]));
                }
            }

            return field;
        }

        private readonly void _AppendErrors(in MutField parent, SomeIpSdErrors errors)
        {
            uint bits = (uint)errors;
            while (bits != 0)
            {
                _AppendError(in parent, (SomeIpSdErrors)(bits & (uint)-(int)bits));
                bits &= bits - 1;
            }
        }

        private readonly void _AppendError(in MutField parent, SomeIpSdErrors error)
        {
            int bit = BitOperations.TrailingZeroCount((uint)error);
            parent.Append(_Owner._SdErrorFieldIds[bit], FieldValue.NewString(_SdErrorText(error)));
        }

        private static NameSuffix _Any(bool isAny)
        {
            if (isAny)
            {
                return new NameSuffix("ANY", unknown: false);
            }

            return new NameSuffix(null, unknown: false);
        }

        private static NameSuffix _EventgroupSuffix(ushort eventgroup, string? name) =>
            eventgroup == 0xFFFF
                ? new NameSuffix("ANY", unknown: false)
                : new NameSuffix(name, unknown: false);

        private static string _ProtocolWord(byte protocol) => protocol switch
        {
            SomeIpTables.L4Udp => "UDP",
            SomeIpTables.L4Tcp => "TCP",
            _ => protocol.ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// The options an entry references, as the specification defines them: the run of <paramref name="firstCount"/> options from
        /// <paramref name="firstIndex"/> and the run of <paramref name="secondCount"/> options from <paramref name="secondIndex"/>.
        /// "0-2,5-6" for two runs, "5-6" when only the second exists, "None" for no run.
        /// </summary>
        private static string _OptionRuns(int firstIndex, int firstCount, int secondIndex, int secondCount)
        {
            if (firstCount == 0 && secondCount == 0)
            {
                return "None";
            }

            if (secondCount == 0)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{firstIndex}-{firstIndex + firstCount - 1}");
            }

            if (firstCount == 0)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{secondIndex}-{secondIndex + secondCount - 1}");
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{firstIndex}-{firstIndex + firstCount - 1},{secondIndex}-{secondIndex + secondCount - 1}");
        }
    }

    #endregion
}

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Single-writer RAM columnar cache of selected field values.
/// Create with a <see cref="Stack"/> and field/group configs (or <see cref="ValueCacheBuildOptions.RecordAllFields"/>),
/// then fill via <see cref="RecordPacket"/> or parse-time tee (<c>ParseFrameRecorded</c>).
/// Poll <see cref="ValueCacheSeries.Count"/> for growth; Core does not raise a growth event.
/// <para>
/// <b>Thread-safety:</b> Single-writer / multi-reader. One thread calls
/// <see cref="BeginPacket"/>, <see cref="RecordPacket"/>, <see cref="Tee"/>, and <see cref="EndPacket"/>.
/// Concurrent readers may load <see cref="ValueCacheSeries.Count"/>
/// and read rows/chunks for indices strictly below that count. A reader never observes a row from a
/// packet that has not finished <see cref="EndPacket"/> / <see cref="RecordPacket"/>.
/// After <see cref="Abandon"/>, writes throw <see cref="InvalidOperationException"/>; committed reads remain allowed.
/// </para>
/// <para>
/// Parse tee never uses a dictionary. With at most 16 recorded field ids the probe is a compact
/// parallel array and a linear scan. Otherwise the probe is dense in the stack field count and a
/// bitset rejects unrecorded ids before the slot is loaded.
/// Getters and <see cref="RecordPacket"/> walk the recorded series arrays. Custom text and custom
/// representation stay on separate series because one field can record both.
/// </para>
/// </summary>
public sealed class ValueCache
{
    #region Nested types

    /// <summary>
    /// Compact or dense probe slot. Payload type is stored so getters and
    /// <see cref="RecordPacket"/> do not call <see cref="Stack.GetField"/> on the fill path.
    /// </summary>
    private struct ValueCacheProbeSlot
    {
        public object? Payload;
        public ValueCacheStringSeries? CustomText;
        public ValueCacheStringSeries? CustomRepresentation;
        public FieldType PayloadType;
    }

    #endregion

    #region Constants

    /// <summary>
    /// Linear-scan probe when the recorded field-id set is this size or smaller.
    /// Larger sets use a dense probe plus a bitset miss.
    /// </summary>
    private const int _CompactTeeLimit = 16;

    #endregion

    #region Fields

    private readonly ValueCacheCapacity _Capacity;
    private readonly ValueCacheProbeSlot[] _Probe;
    private readonly int[] _CompactFieldIds;
    private readonly ulong[] _RecordedBits;
    private readonly bool _UseCompactTee;
    private readonly bool _AllSlotsRecorded;
    private readonly ValueCacheSeries[] _PayloadSeries;
    private readonly ValueCacheStringSeries[] _CustomTextSeries;
    private readonly ValueCacheStringSeries[] _CustomRepresentationSeries;
    private readonly IndexGroupId[] _MaterializeGroups;
    private readonly bool[] _MaterializeGroupMask;
    private readonly ValueCacheSeries[] _AllSeries;
    private readonly int[] _SeriesBegunEpoch;
    private int _PacketEpoch;

    private volatile int _PacketIdsStrict = 1;
    private volatile int _TimestampsStrict = 1;
    private volatile int _Abandoned;
    private volatile int _MaterializationIncomplete;

    private bool _HasActivePacket;
    private bool _MonotonicInitialized;
    private int _CurrentPacketId;
    private long _CurrentTimestampNanos;
    private int _LastPacketId;
    private long _LastTimestampNanos;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a RAM value cache for <paramref name="stack"/>. Throws on unknown field or group ids,
    /// empty configuration without <see cref="ValueCacheBuildOptions.RecordAllFields"/>, invalid limits,
    /// duplicate payload series, or a field/group config with all record flags false.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stack"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Configuration is empty, contradictory, or out of range for <paramref name="stack"/>.</exception>
    public ValueCache(
        Stack stack,
        ReadOnlySpan<ValueCacheFieldConfig> fields,
        ReadOnlySpan<ValueCacheGroupConfig> groups = default,
        ValueCacheBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stack);

        Stack = stack;
        ValueCacheBuildOptions resolved = options ?? new ValueCacheBuildOptions();
        RecordAllFields = resolved.RecordAllFields;
        _ValidateLimits(resolved.Limits);

        if (!RecordAllFields && fields.Length == 0 && groups.Length == 0)
        {
            throw new ArgumentException(
                "ValueCache requires at least one field or group, or RecordAllFields.",
                nameof(fields));
        }

        Dictionary<int, (FieldInfo Info, ValueCaptureMode Mode)>? payload = null;
        Dictionary<int, (FieldInfo Info, ValueCaptureMode Mode)>? customText = null;
        Dictionary<int, (FieldInfo Info, ValueCaptureMode Mode)>? customRep = null;

        for (int i = 0; i < fields.Length; i++)
        {
            ValueCacheFieldConfig config = fields[i];
            _ValidateRecordFlags(config.RecordValue, config.RecordCustomText, config.RecordCustomRepresentation, nameof(fields));
            FieldInfo info = _RequireField(stack, config.FieldId, nameof(fields));
            if (config.RecordValue)
            {
                payload ??= [];
                if (!payload.TryAdd(info.Id.Value, (info, config.CaptureMode)))
                {
                    throw new ArgumentException(
                        $"Duplicate payload series for field '{info.Name}'.",
                        nameof(fields));
                }
            }

            if (config.RecordCustomText)
            {
                customText ??= [];
                if (!customText.TryAdd(info.Id.Value, (info, config.CaptureMode)))
                {
                    throw new ArgumentException(
                        $"Duplicate custom-text series for field '{info.Name}'.",
                        nameof(fields));
                }
            }

            if (config.RecordCustomRepresentation)
            {
                customRep ??= [];
                if (!customRep.TryAdd(info.Id.Value, (info, config.CaptureMode)))
                {
                    throw new ArgumentException(
                        $"Duplicate custom-representation series for field '{info.Name}'.",
                        nameof(fields));
                }
            }
        }

        ReadOnlySpan<FieldInfo> stackFields = stack.Fields.Span;
        for (int g = 0; g < groups.Length; g++)
        {
            ValueCacheGroupConfig config = groups[g];
            _ValidateRecordFlags(config.RecordValue, config.RecordCustomText, config.RecordCustomRepresentation, nameof(groups));
            _ = _RequireGroup(stack, config.GroupId, nameof(groups));
            for (int f = 0; f < stackFields.Length; f++)
            {
                FieldInfo info = stackFields[f];
                if (info.IndexGroup is not IndexGroupId groupId || groupId != config.GroupId)
                {
                    continue;
                }

                if (config.RecordValue)
                {
                    payload ??= [];
                    _ = payload.TryAdd(info.Id.Value, (info, config.CaptureMode));
                }

                if (config.RecordCustomText)
                {
                    customText ??= [];
                    _ = customText.TryAdd(info.Id.Value, (info, config.CaptureMode));
                }

                if (config.RecordCustomRepresentation)
                {
                    customRep ??= [];
                    _ = customRep.TryAdd(info.Id.Value, (info, config.CaptureMode));
                }
            }
        }

        if (RecordAllFields)
        {
            payload ??= [];
            ValueCaptureMode defaultMode = resolved.DefaultCaptureMode;
            for (int f = 0; f < stackFields.Length; f++)
            {
                FieldInfo info = stackFields[f];
                _ = payload.TryAdd(info.Id.Value, (info, defaultMode));
            }
        }

        int payloadCount = payload?.Count ?? 0;
        int textCount = customText?.Count ?? 0;
        int repCount = customRep?.Count ?? 0;
        int uniqueUpper = payloadCount + textCount + repCount;
        bool useCompact = !RecordAllFields && uniqueUpper > 0 && uniqueUpper <= _CompactTeeLimit;

        _Capacity = new ValueCacheCapacity(resolved.Limits);
        ValueCacheProbeSlot[] dense = useCompact ? [] : new ValueCacheProbeSlot[stack.FieldCount];
        Dictionary<int, ValueCacheProbeSlot>? compactSlots = useCompact ? [] : null;
        ValueCacheSeries[] payloadSeries = payloadCount == 0 ? [] : new ValueCacheSeries[payloadCount];
        ValueCacheStringSeries[] textSeries = textCount == 0 ? [] : new ValueCacheStringSeries[textCount];
        ValueCacheStringSeries[] repSeries = repCount == 0 ? [] : new ValueCacheStringSeries[repCount];
        ValueCacheSeries[] all = new ValueCacheSeries[payloadCount + textCount + repCount];
        int allIndex = 0;
        HashSet<int>? materializeGroups = null;

        if (payload is not null)
        {
            int payloadIndex = 0;
            foreach (KeyValuePair<int, (FieldInfo Info, ValueCaptureMode Mode)> entry in payload)
            {
                ValueCacheSeries series = (ValueCacheSeries)_CreatePayloadSeries(entry.Value.Info, entry.Value.Mode);
                payloadSeries[payloadIndex++] = series;
                all[allIndex++] = series;
                _SetPayloadSlot(dense, compactSlots, entry.Key, series, entry.Value.Info.FieldType);
                if (!RecordAllFields)
                {
                    _CollectMaterializeGroup(ref materializeGroups, entry.Value.Info);
                }
            }
        }

        if (customText is not null)
        {
            int textIndex = 0;
            foreach (KeyValuePair<int, (FieldInfo Info, ValueCaptureMode Mode)> entry in customText)
            {
                ValueCacheStringSeries series = new(_Capacity, entry.Value.Info.Id, FieldType.String, entry.Value.Mode);
                textSeries[textIndex++] = series;
                all[allIndex++] = series;
                _SetCustomTextSlot(dense, compactSlots, entry.Key, series);
                if (!RecordAllFields)
                {
                    _CollectMaterializeGroup(ref materializeGroups, entry.Value.Info);
                }
            }
        }

        if (customRep is not null)
        {
            int repIndex = 0;
            foreach (KeyValuePair<int, (FieldInfo Info, ValueCaptureMode Mode)> entry in customRep)
            {
                ValueCacheStringSeries series = new(_Capacity, entry.Value.Info.Id, FieldType.String, entry.Value.Mode);
                repSeries[repIndex++] = series;
                all[allIndex++] = series;
                _SetCustomRepresentationSlot(dense, compactSlots, entry.Key, series);
                if (!RecordAllFields)
                {
                    _CollectMaterializeGroup(ref materializeGroups, entry.Value.Info);
                }
            }
        }

        _PayloadSeries = payloadSeries;
        _CustomTextSeries = textSeries;
        _CustomRepresentationSeries = repSeries;
        _AllSeries = all;
        _SeriesBegunEpoch = new int[all.Length];
        Array.Fill(_SeriesBegunEpoch, -1);

        if (compactSlots is not null)
        {
            int n = compactSlots.Count;
            int[] compactIds = new int[n];
            ValueCacheProbeSlot[] compact = new ValueCacheProbeSlot[n];
            int compactIndex = 0;
            foreach (KeyValuePair<int, ValueCacheProbeSlot> pair in compactSlots)
            {
                compactIds[compactIndex] = pair.Key;
                compact[compactIndex] = pair.Value;
                compactIndex++;
            }

            _UseCompactTee = true;
            _AllSlotsRecorded = false;
            _CompactFieldIds = compactIds;
            _Probe = compact;
            _RecordedBits = [];
        }
        else if (RecordAllFields)
        {
            _UseCompactTee = false;
            _CompactFieldIds = [];
            _Probe = dense;
            _AllSlotsRecorded = dense.Length > 0;
            _RecordedBits = [];
        }
        else
        {
            _UseCompactTee = false;
            _CompactFieldIds = [];
            _Probe = dense;
            _RecordedBits = _BuildRecordedBits(dense.Length, payload, customText, customRep, out bool allRecorded);
            _AllSlotsRecorded = allRecorded;
        }

        if (RecordAllFields)
        {
            ReadOnlySpan<IndexGroupInfo> groupsSpan = stack.IndexGroups.Span;
            if (groupsSpan.Length == 0)
            {
                _MaterializeGroups = [];
                _MaterializeGroupMask = [];
            }
            else
            {
                _MaterializeGroups = new IndexGroupId[groupsSpan.Length];
                _MaterializeGroupMask = new bool[Math.Max(stack.IndexGroupCount, 0)];
                for (int i = 0; i < groupsSpan.Length; i++)
                {
                    IndexGroupId id = groupsSpan[i].Id;
                    _MaterializeGroups[i] = id;
                    if ((uint)id.Value < (uint)_MaterializeGroupMask.Length)
                    {
                        _MaterializeGroupMask[id.Value] = true;
                    }
                }
            }
        }
        else if (materializeGroups is null || materializeGroups.Count == 0)
        {
            _MaterializeGroups = [];
            _MaterializeGroupMask = [];
        }
        else
        {
            _MaterializeGroups = new IndexGroupId[materializeGroups.Count];
            int groupIndex = 0;
            foreach (int raw in materializeGroups)
            {
                _MaterializeGroups[groupIndex++] = new IndexGroupId(raw);
            }

            _MaterializeGroupMask = new bool[Math.Max(stack.IndexGroupCount, 0)];
            for (int i = 0; i < _MaterializeGroups.Length; i++)
            {
                int raw = _MaterializeGroups[i].Value;
                if ((uint)raw < (uint)_MaterializeGroupMask.Length)
                {
                    _MaterializeGroupMask[raw] = true;
                }
            }
        }
    }

    #endregion

    #region Properties

    /// <summary>Stack this cache was built against.</summary>
    public Stack Stack { get; }

    /// <summary>Whether construction used <see cref="ValueCacheBuildOptions.RecordAllFields"/>.</summary>
    public bool RecordAllFields { get; }

    /// <summary>
    /// Sticky flag: committed packet ids have been strictly increasing so far.
    /// Starts true. The first committed packet records the baseline. A later id less than or equal
    /// to the previous committed id sets this false permanently.
    /// Equal timestamps do not affect this flag.
    /// </summary>
    public bool PacketIdsStrictlyIncreasing => _PacketIdsStrict != 0;

    /// <summary>
    /// Sticky flag: committed timestamps have been strictly increasing so far.
    /// Starts true. A later timestamp less than or equal to the previous committed timestamp
    /// (including equal timestamps) sets this false permanently.
    /// </summary>
    public bool TimestampsStrictlyIncreasing => _TimestampsStrict != 0;

    /// <summary>Sticky: a row or byte write was refused because <see cref="ValueCacheLimits"/> was exceeded.</summary>
    public bool IsCapacityReached => _Capacity.IsReached;

    /// <summary>
    /// Sticky: <see cref="EnsureMaterialized(Packet)"/> hit its iteration cap. Columns may be incomplete.
    /// </summary>
    public bool IsMaterializationIncomplete => _MaterializationIncomplete != 0;

    /// <summary>Sum of each series <see cref="ValueCacheSeries.ByteSize"/>.</summary>
    public long ByteSize
    {
        get
        {
            long sum = 0;
            ValueCacheSeries[] series = _AllSeries;
            for (int i = 0; i < series.Length; i++)
            {
                sum += series[i].ByteSize;
            }

            return sum;
        }
    }

    /// <summary>All payload and optional custom-text / custom-representation series, in construction order.</summary>
    public IReadOnlyList<ValueCacheSeries> Series => _AllSeries;

    #endregion

    #region Public API

    /// <summary>Returns a zero-allocation read-only view. Do not box the struct onto <c>object</c>.</summary>
    public ValueCacheReaderView AsReadOnlyView() => new(this);

    /// <summary>
    /// Returns the unmanaged payload series for <paramref name="fieldId"/>.
    /// IPv6, UUID, string, and bytes payloads use the named getters instead of this method.
    /// </summary>
    /// <exception cref="ArgumentException">No series, or <typeparamref name="T"/> does not match the stack <see cref="FieldType"/>.</exception>
    public ValueCacheSeries<T> GetSeries<T>(FieldId fieldId)
        where T : unmanaged
    {
        if (!TryGetSeries(fieldId, out ValueCacheSeries<T>? series) || series is null)
        {
            throw new ArgumentException("No payload series of the requested type for this field.", nameof(fieldId));
        }

        return series;
    }

    /// <summary>Try-get counterpart of <see cref="GetSeries{T}(FieldId)"/>.</summary>
    public bool TryGetSeries<T>(FieldId fieldId, out ValueCacheSeries<T>? series)
        where T : unmanaged
    {
        series = null;
        if (!_TryGetSlot(fieldId.Value, out ValueCacheProbeSlot slot) || slot.Payload is null)
        {
            return false;
        }

        if (!_UnmanagedTypeMatches(slot.PayloadType, typeof(T)))
        {
            return false;
        }

        series = Unsafe.As<ValueCacheSeries<T>>(slot.Payload);
        return series is not null;
    }

    /// <summary>Looks up a field by ordinal name, then <see cref="TryGetSeries{T}(FieldId, out ValueCacheSeries{T})"/>.</summary>
    public bool TryGetSeries<T>(string fieldName, out ValueCacheSeries<T>? series)
        where T : unmanaged
    {
        series = null;
        if (fieldName is null)
        {
            return false;
        }

        FieldId? id = Stack.GetFieldId(fieldName);
        if (id is null)
        {
            return false;
        }

        return TryGetSeries(id.Value, out series);
    }

    /// <summary>Custom-text series for <paramref name="fieldId"/>.</summary>
    /// <exception cref="ArgumentException">No custom-text series for this field.</exception>
    public ValueCacheStringSeries GetCustomTextSeries(FieldId fieldId)
    {
        if (!TryGetCustomTextSeries(fieldId, out ValueCacheStringSeries? series) || series is null)
        {
            throw new ArgumentException("No custom-text series for this field.", nameof(fieldId));
        }

        return series;
    }

    /// <summary>Try-get counterpart of <see cref="GetCustomTextSeries(FieldId)"/>.</summary>
    public bool TryGetCustomTextSeries(FieldId fieldId, out ValueCacheStringSeries? series) =>
        _TryGetStringSeries(fieldId.Value, customText: true, out series);

    /// <summary>Looks up custom-text series by field name.</summary>
    public bool TryGetCustomTextSeries(string fieldName, out ValueCacheStringSeries? series)
    {
        series = null;
        FieldId? id = fieldName is null ? null : Stack.GetFieldId(fieldName);
        return id is not null && TryGetCustomTextSeries(id.Value, out series);
    }

    /// <summary>Custom-representation series for <paramref name="fieldId"/>.</summary>
    /// <exception cref="ArgumentException">No custom-representation series for this field.</exception>
    public ValueCacheStringSeries GetCustomRepresentationSeries(FieldId fieldId)
    {
        if (!TryGetCustomRepresentationSeries(fieldId, out ValueCacheStringSeries? series) || series is null)
        {
            throw new ArgumentException("No custom-representation series for this field.", nameof(fieldId));
        }

        return series;
    }

    /// <summary>Try-get counterpart of <see cref="GetCustomRepresentationSeries(FieldId)"/>.</summary>
    public bool TryGetCustomRepresentationSeries(FieldId fieldId, out ValueCacheStringSeries? series) =>
        _TryGetStringSeries(fieldId.Value, customText: false, out series);

    /// <summary>Looks up custom-representation series by field name.</summary>
    public bool TryGetCustomRepresentationSeries(string fieldName, out ValueCacheStringSeries? series)
    {
        series = null;
        FieldId? id = fieldName is null ? null : Stack.GetFieldId(fieldName);
        return id is not null && TryGetCustomRepresentationSeries(id.Value, out series);
    }

    /// <summary>IPv6 payload series for <paramref name="fieldId"/>.</summary>
    /// <exception cref="ArgumentException">No IPv6 series for this field.</exception>
    public ValueCacheIPv6Series GetIPv6Series(FieldId fieldId)
    {
        if (!TryGetIPv6Series(fieldId, out ValueCacheIPv6Series? series) || series is null)
        {
            throw new ArgumentException("No IPv6 series for this field.", nameof(fieldId));
        }

        return series;
    }

    /// <summary>Try-get counterpart of <see cref="GetIPv6Series"/>.</summary>
    public bool TryGetIPv6Series(FieldId fieldId, out ValueCacheIPv6Series? series) =>
        _TryGetPayloadAs(fieldId.Value, out series);

    /// <summary>Looks up an IPv6 series by field name.</summary>
    public bool TryGetIPv6Series(string fieldName, out ValueCacheIPv6Series? series)
    {
        series = null;
        FieldId? id = fieldName is null ? null : Stack.GetFieldId(fieldName);
        return id is not null && TryGetIPv6Series(id.Value, out series);
    }

    /// <summary>UUID payload series for <paramref name="fieldId"/>.</summary>
    /// <exception cref="ArgumentException">No UUID series for this field.</exception>
    public ValueCacheUuidSeries GetUuidSeries(FieldId fieldId)
    {
        if (!TryGetUuidSeries(fieldId, out ValueCacheUuidSeries? series) || series is null)
        {
            throw new ArgumentException("No UUID series for this field.", nameof(fieldId));
        }

        return series;
    }

    /// <summary>Try-get counterpart of <see cref="GetUuidSeries"/>.</summary>
    public bool TryGetUuidSeries(FieldId fieldId, out ValueCacheUuidSeries? series) =>
        _TryGetPayloadAs(fieldId.Value, out series);

    /// <summary>Looks up a UUID series by field name.</summary>
    public bool TryGetUuidSeries(string fieldName, out ValueCacheUuidSeries? series)
    {
        series = null;
        FieldId? id = fieldName is null ? null : Stack.GetFieldId(fieldName);
        return id is not null && TryGetUuidSeries(id.Value, out series);
    }

    /// <summary>Bytes payload series for <paramref name="fieldId"/>.</summary>
    /// <exception cref="ArgumentException">No bytes series for this field.</exception>
    public ValueCacheBytesSeries GetBytesSeries(FieldId fieldId)
    {
        if (!TryGetBytesSeries(fieldId, out ValueCacheBytesSeries? series) || series is null)
        {
            throw new ArgumentException("No bytes series for this field.", nameof(fieldId));
        }

        return series;
    }

    /// <summary>Try-get counterpart of <see cref="GetBytesSeries"/>.</summary>
    public bool TryGetBytesSeries(FieldId fieldId, out ValueCacheBytesSeries? series) =>
        _TryGetPayloadAs(fieldId.Value, out series);

    /// <summary>Looks up a bytes series by field name.</summary>
    public bool TryGetBytesSeries(string fieldName, out ValueCacheBytesSeries? series)
    {
        series = null;
        FieldId? id = fieldName is null ? null : Stack.GetFieldId(fieldName);
        return id is not null && TryGetBytesSeries(id.Value, out series);
    }

    /// <summary>
    /// Opens a packet session. Nested begin throws. Abandoned caches throw.
    /// <paramref name="packetId"/> must be a valid array-index id.
    /// </summary>
    /// <exception cref="InvalidOperationException">Already active, or this cache was evicted.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="packetId"/> is not a valid index.</exception>
    public void BeginPacket(int packetId, long timestampNanos)
    {
        _ThrowIfAbandoned();
        if (_HasActivePacket)
        {
            throw new InvalidOperationException("A packet is already active. Call EndPacket first.");
        }

        ArrayIndexIdRange.ValidateIndexOrThrow(packetId, nameof(packetId));
        _CurrentPacketId = packetId;
        _CurrentTimestampNanos = timestampNanos;
        _HasActivePacket = true;
        _PacketEpoch++;
        if (_PacketEpoch == 0)
        {
            Array.Fill(_SeriesBegunEpoch, -1);
            _PacketEpoch = 1;
        }
    }

    /// <summary>
    /// Commits staged rows for the active packet and updates monotonic flags from this packet's
    /// id and timestamp even when no series produced a row.
    /// </summary>
    /// <exception cref="InvalidOperationException">No active packet, or this cache was evicted.</exception>
    public void EndPacket()
    {
        if (_Abandoned != 0)
        {
            _HasActivePacket = false;
            throw new InvalidOperationException("ValueCache was evicted");
        }

        if (!_HasActivePacket)
        {
            throw new InvalidOperationException("EndPacket requires a matching BeginPacket.");
        }

        ValueCacheSeries[] series = _AllSeries;
        int epoch = _PacketEpoch;
        for (int i = 0; i < series.Length; i++)
        {
            if (_SeriesBegunEpoch[i] == epoch)
            {
                series[i].Commit();
                _SeriesBegunEpoch[i] = -1;
            }
        }

        _UpdateMonotonicFlags(_CurrentPacketId, _CurrentTimestampNanos);
        _HasActivePacket = false;
    }

    /// <summary>
    /// Pull ingest for a caller who owns the writer. Walks the sealed packet in lookup order.
    /// Always ends the packet session, including when recording throws: rows already staged
    /// for this packet are committed.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="packet"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Stack mismatch or the packet is not finalized.</exception>
    /// <exception cref="InvalidOperationException">Nested begin, or this cache was evicted.</exception>
    public void RecordPacket(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (!ReferenceEquals(packet.Stack, Stack))
        {
            throw new ArgumentException("Packet stack does not match this ValueCache.", nameof(packet));
        }

        if (!packet.IsFinalized)
        {
            throw new ArgumentException("RecordPacket requires a finalized packet.", nameof(packet));
        }

        BeginPacket(packet.Id.Value, packet.Timestamp.AsNanos);
        try
        {
            EnsureMaterialized(packet);
            _RecordPayloadSeries(packet);
            _RecordCustomTextSeries(packet);
            _RecordCustomRepresentationSeries(packet);
        }
        finally
        {
            if (_Abandoned != 0)
            {
                _HasActivePacket = false;
            }
            else
            {
                EndPacket();
            }
        }
    }

    #endregion

    #region Internal API

    /// <summary>
    /// Parse-time tee. Compact linear scan when few fields are recorded; otherwise a bitset miss
    /// before the dense slot is loaded. Predicted not-taken when this field has no series.
    /// Called only from <c>Packet</c>'s NoInlining stub so the probe cannot inflate <c>AppendChild</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Tee(FieldId fieldId, in FieldValue value, LazyString customText)
    {
        if (!_TryGetRecordedSlot(fieldId.Value, out int slotIndex))
        {
            return;
        }

        _TeeHitCold(in value, customText, ref _Probe[slotIndex]);
    }

    /// <summary>
    /// Custom-text tee after <see cref="MutField.SetCustomText"/> / append / clear.
    /// Null text is a no-op for FirstOccurrence and AllOccurrences. For LastOccurrence a null
    /// retracts the row staged in this packet.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void TeeCustomText(FieldId fieldId, LazyString customText)
    {
        if (!_TryGetRecordedSlot(fieldId.Value, out int slotIndex))
        {
            return;
        }

        if (_Probe[slotIndex].CustomText is not { } series)
        {
            return;
        }

        _ThrowIfAbandoned();
        _EnsureSeriesBegun(series);
        if (customText.IsNull)
        {
            series.RetractLastOccurrenceRow();
            return;
        }

        series.Stage(_CurrentPacketId, _CurrentTimestampNanos, customText);
    }

    /// <summary>
    /// Materializes lazy fields whose index group is configured (or every lazy field when
    /// <see cref="RecordAllFields"/>). Caps outer repeats at <see cref="ushort.MaxValue"/> and
    /// sets <see cref="IsMaterializationIncomplete"/> instead of throwing.
    /// </summary>
    internal void EnsureMaterialized(Packet packet) => EnsureMaterialized(packet, ushort.MaxValue);

    /// <summary>
    /// Materializes lazy fields, capping outer repeats at <paramref name="maxPasses"/>.
    /// Production callers use <see cref="EnsureMaterialized(Packet)"/> (<see cref="ushort.MaxValue"/>).
    /// </summary>
    internal void EnsureMaterialized(Packet packet, int maxPasses)
    {
        ArgumentNullException.ThrowIfNull(packet);
        _ThrowIfAbandoned();
        if (maxPasses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPasses), maxPasses, "maxPasses must be greater than zero.");
        }

        for (int pass = 0; pass < maxPasses; pass++)
        {
            if (!packet.HasUnpopulatedLazyFields)
            {
                return;
            }

            bool materialized = false;
            int count = packet.FieldCount(materialize: false);
            for (int i = 0; i < count; i++)
            {
                ref FieldBody body = ref packet.GetFieldRef(i);
                if (!body.NeedsMaterialization || !_ShouldMaterialize(body.FieldId))
                {
                    continue;
                }

                packet.MaterializeLazyField((ushort)i);
                materialized = true;
            }

            if (!materialized)
            {
                return;
            }
        }

        _MaterializationIncomplete = 1;
    }

    /// <summary>
    /// Evicts this writer. Further <see cref="BeginPacket"/>, <see cref="Tee"/>,
    /// <see cref="TeeCustomText"/>, and <see cref="EndPacket"/> throw.
    /// <see cref="RecordPacket"/> leaves an in-flight packet unpublished when eviction
    /// races the fill, then the next <see cref="BeginPacket"/> throws.
    /// Committed reads remain allowed.
    /// </summary>
    /// <remarks>
    /// Session calls this on Restart. Listeners receive <see cref="ValueCacheReaderView"/>,
    /// which cannot invoke this method.
    /// </remarks>
    public void Abandon() => _Abandoned = 1;

    /// <summary>Whether <see cref="Abandon"/> has been called (Restart eviction).</summary>
    public bool IsAbandoned => _Abandoned != 0;

    #endregion

    #region Private helpers

    private static void _SetPayloadSlot(
        ValueCacheProbeSlot[] dense,
        Dictionary<int, ValueCacheProbeSlot>? compactSlots,
        int fieldId,
        object series,
        FieldType fieldType)
    {
        if (compactSlots is not null)
        {
            _ = compactSlots.TryGetValue(fieldId, out ValueCacheProbeSlot slot);
            slot.Payload = series;
            slot.PayloadType = fieldType;
            compactSlots[fieldId] = slot;
            return;
        }

        ref ValueCacheProbeSlot denseSlot = ref dense[fieldId];
        denseSlot.Payload = series;
        denseSlot.PayloadType = fieldType;
    }

    private static void _SetCustomTextSlot(
        ValueCacheProbeSlot[] dense,
        Dictionary<int, ValueCacheProbeSlot>? compactSlots,
        int fieldId,
        ValueCacheStringSeries series)
    {
        if (compactSlots is not null)
        {
            _ = compactSlots.TryGetValue(fieldId, out ValueCacheProbeSlot slot);
            slot.CustomText = series;
            compactSlots[fieldId] = slot;
            return;
        }

        dense[fieldId].CustomText = series;
    }

    private static void _SetCustomRepresentationSlot(
        ValueCacheProbeSlot[] dense,
        Dictionary<int, ValueCacheProbeSlot>? compactSlots,
        int fieldId,
        ValueCacheStringSeries series)
    {
        if (compactSlots is not null)
        {
            _ = compactSlots.TryGetValue(fieldId, out ValueCacheProbeSlot slot);
            slot.CustomRepresentation = series;
            compactSlots[fieldId] = slot;
            return;
        }

        dense[fieldId].CustomRepresentation = series;
    }

    private static ulong[] _BuildRecordedBits(
        int fieldCount,
        Dictionary<int, (FieldInfo Info, ValueCaptureMode Mode)>? payload,
        Dictionary<int, (FieldInfo Info, ValueCaptureMode Mode)>? customText,
        Dictionary<int, (FieldInfo Info, ValueCaptureMode Mode)>? customRep,
        out bool allRecorded)
    {
        allRecorded = false;
        if (fieldCount <= 0)
        {
            return [];
        }

        int bitWords = (fieldCount + 63) >> 6;
        ulong[] bits = new ulong[bitWords];
        int recordedCount = 0;
        if (payload is not null)
        {
            recordedCount = _OrRecordedKeys(bits, payload, recordedCount);
        }

        if (customText is not null)
        {
            recordedCount = _OrRecordedKeys(bits, customText, recordedCount);
        }

        if (customRep is not null)
        {
            recordedCount = _OrRecordedKeys(bits, customRep, recordedCount);
        }

        if (recordedCount == 0)
        {
            return [];
        }

        allRecorded = recordedCount == fieldCount;
        if (allRecorded)
        {
            return [];
        }

        return bits;
    }

    private static int _OrRecordedKeys(
        ulong[] bits,
        Dictionary<int, (FieldInfo Info, ValueCaptureMode Mode)> map,
        int recordedCount)
    {
        foreach (int id in map.Keys)
        {
            int word = id >> 6;
            ulong mask = 1UL << (id & 63);
            if ((bits[word] & mask) != 0)
            {
                continue;
            }

            bits[word] |= mask;
            recordedCount++;
        }

        return recordedCount;
    }

    private object _CreatePayloadSeries(FieldInfo info, ValueCaptureMode mode)
    {
        FieldId id = info.Id;
        return info.FieldType switch
        {
            FieldType.None => new ValueCacheSeries<byte>(_Capacity, id, FieldType.None, mode),
            FieldType.Bool => new ValueCacheSeries<byte>(_Capacity, id, FieldType.Bool, mode),
            FieldType.I64 => new ValueCacheSeries<long>(_Capacity, id, FieldType.I64, mode),
            FieldType.U64 => new ValueCacheSeries<ulong>(_Capacity, id, FieldType.U64, mode),
            FieldType.F64 => new ValueCacheSeries<double>(_Capacity, id, FieldType.F64, mode),
            FieldType.String => new ValueCacheStringSeries(_Capacity, id, FieldType.String, mode),
            FieldType.Bytes => new ValueCacheBytesSeries(_Capacity, id, mode),
            FieldType.MacAddress => new ValueCacheSeries<ulong>(_Capacity, id, FieldType.MacAddress, mode),
            FieldType.IPv4Address => new ValueCacheSeries<uint>(_Capacity, id, FieldType.IPv4Address, mode),
            FieldType.IPv6Address => new ValueCacheIPv6Series(_Capacity, id, mode),
            FieldType.Eui64 => new ValueCacheSeries<ulong>(_Capacity, id, FieldType.Eui64, mode),
            FieldType.Uuid => new ValueCacheUuidSeries(_Capacity, id, mode),
            FieldType.Timestamp => new ValueCacheSeries<long>(_Capacity, id, FieldType.Timestamp, mode),
            _ => throw new ArgumentException(
                $"Unsupported field type '{info.FieldType.ToString()}' for field '{info.Name}'.",
                nameof(info)),
        };
    }

    private void _RecordPayloadSeries(Packet packet)
    {
        ValueCacheSeries[] series = _PayloadSeries;
        for (int i = 0; i < series.Length; i++)
        {
            ValueCacheSeries item = series[i];
            FieldId fieldId = item.FieldId;
            FieldType fieldType = item.FieldType;
            FieldLookupCookie cookie = FieldLookupCookie.Start;
            while (packet.TryGetNextField(fieldId, ref cookie, out Field field, materialize: false))
            {
                _StagePayload(item, fieldType, packet.Id.Value, packet.Timestamp.AsNanos, field.Value);
            }
        }
    }

    private void _RecordCustomTextSeries(Packet packet) =>
        _RecordStringSeries(_CustomTextSeries, packet, customText: true);

    private void _RecordCustomRepresentationSeries(Packet packet) =>
        _RecordStringSeries(_CustomRepresentationSeries, packet, customText: false);

    private static void _RecordStringSeries(ValueCacheStringSeries[] series, Packet packet, bool customText)
    {
        for (int i = 0; i < series.Length; i++)
        {
            ValueCacheStringSeries column = series[i];
            FieldId fieldId = column.FieldId;
            FieldLookupCookie cookie = FieldLookupCookie.Start;
            while (packet.TryGetNextField(fieldId, ref cookie, out Field field, materialize: false))
            {
                LazyString text = customText ? field.CustomText : field.Value.CustomRepresentation;
                if (text.IsNull)
                {
                    continue;
                }

                column.Stage(packet.Id.Value, packet.Timestamp.AsNanos, text);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _StagePayload(object boxed, FieldType fieldType, int packetId, long timestampNanos, in FieldValue value)
    {
        if (boxed is ValueCacheSeries series)
        {
            _EnsureSeriesBegun(series);
        }

        switch (fieldType)
        {
            case FieldType.None:
                Unsafe.As<ValueCacheSeries<byte>>(boxed).Stage(packetId, timestampNanos, 0);
                return;
            case FieldType.Bool:
            {
                _ = value.Data.TryGetAsBool(out bool flag);
                Unsafe.As<ValueCacheSeries<byte>>(boxed).Stage(packetId, timestampNanos, flag ? (byte)1 : (byte)0);
                return;
            }
            case FieldType.I64:
            {
                _ = value.Data.TryGetAsI64(out long i64);
                Unsafe.As<ValueCacheSeries<long>>(boxed).Stage(packetId, timestampNanos, i64);
                return;
            }
            case FieldType.U64:
            {
                _ = value.Data.TryGetAsU64(out ulong u64);
                Unsafe.As<ValueCacheSeries<ulong>>(boxed).Stage(packetId, timestampNanos, u64);
                return;
            }
            case FieldType.F64:
            {
                _ = value.Data.TryGetAsF64(out double f64);
                Unsafe.As<ValueCacheSeries<double>>(boxed).Stage(packetId, timestampNanos, f64);
                return;
            }
            case FieldType.String:
            {
                if (value.Data.TryGetAsString(out string text))
                {
                    Unsafe.As<ValueCacheStringSeries>(boxed).Stage(packetId, timestampNanos, text);
                }

                return;
            }
            case FieldType.Bytes:
            {
                if (value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytes))
                {
                    Unsafe.As<ValueCacheBytesSeries>(boxed).Stage(packetId, timestampNanos, bytes.Span);
                }

                return;
            }
            case FieldType.MacAddress:
            {
                _ = value.Data.TryGetAsMacAddress(out MacAddress mac);
                Unsafe.As<ValueCacheSeries<ulong>>(boxed).Stage(packetId, timestampNanos, mac.RawValue);
                return;
            }
            case FieldType.IPv4Address:
            {
                _ = value.Data.TryGetAsIPv4(out IPv4Address ipv4);
                Unsafe.As<ValueCacheSeries<uint>>(boxed).Stage(packetId, timestampNanos, ipv4.RawValue);
                return;
            }
            case FieldType.IPv6Address:
            {
                _ = value.Data.TryGetAsIPv6(out IPv6Address ipv6);
                Unsafe.As<ValueCacheIPv6Series>(boxed).Stage(packetId, timestampNanos, ipv6.High, ipv6.Low);
                return;
            }
            case FieldType.Eui64:
            {
                _ = value.Data.TryGetAsEui64(out Eui64 eui);
                Unsafe.As<ValueCacheSeries<ulong>>(boxed).Stage(packetId, timestampNanos, eui.RawValue);
                return;
            }
            case FieldType.Uuid:
            {
                _ = value.Data.TryGetAsUuid(out Uuid uuid);
                Unsafe.As<ValueCacheUuidSeries>(boxed).Stage(packetId, timestampNanos, uuid.High, uuid.Low);
                return;
            }
            case FieldType.Timestamp:
            {
                _ = value.Data.TryGetAsTimestamp(out Timestamp timestamp);
                Unsafe.As<ValueCacheSeries<long>>(boxed).Stage(packetId, timestampNanos, timestamp.AsNanos);
                return;
            }
            default:
                return;
        }
    }

    private bool _ShouldMaterialize(FieldId fieldId)
    {
        if (RecordAllFields)
        {
            return true;
        }

        FieldInfo? info = Stack.GetField(fieldId);
        if (info?.IndexGroup is not IndexGroupId group || !group.IsValid)
        {
            return false;
        }

        int raw = group.Value;
        return (uint)raw < (uint)_MaterializeGroupMask.Length && _MaterializeGroupMask[raw];
    }

    private void _UpdateMonotonicFlags(int packetId, long timestampNanos)
    {
        if (!_MonotonicInitialized)
        {
            _LastPacketId = packetId;
            _LastTimestampNanos = timestampNanos;
            _MonotonicInitialized = true;
            return;
        }

        if (packetId <= _LastPacketId)
        {
            _PacketIdsStrict = 0;
        }

        if (timestampNanos <= _LastTimestampNanos)
        {
            _TimestampsStrict = 0;
        }

        _LastPacketId = packetId;
        _LastTimestampNanos = timestampNanos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _TeeHit(in FieldValue value, LazyString customText, ref ValueCacheProbeSlot slot)
    {
        _ThrowIfAbandoned();
        int packetId = _CurrentPacketId;
        long timestampNanos = _CurrentTimestampNanos;
        if (slot.Payload is not null)
        {
            _StagePayload(slot.Payload, slot.PayloadType, packetId, timestampNanos, in value);
        }

        if (slot.CustomRepresentation is { } representation && !value.CustomRepresentation.IsNull)
        {
            _EnsureSeriesBegun(representation);
            representation.Stage(packetId, timestampNanos, value.CustomRepresentation);
        }

        if (slot.CustomText is { } text && !customText.IsNull)
        {
            _EnsureSeriesBegun(text);
            text.Stage(packetId, timestampNanos, customText);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _TeeHitCold(in FieldValue value, LazyString customText, ref ValueCacheProbeSlot slot)
        => _TeeHit(in value, customText, ref slot);

    private void _EnsureSeriesBegun(ValueCacheSeries series)
    {
        ValueCacheSeries[] all = _AllSeries;
        for (int i = 0; i < all.Length; i++)
        {
            if (!ReferenceEquals(all[i], series))
            {
                continue;
            }

            if (_SeriesBegunEpoch[i] == _PacketEpoch)
            {
                return;
            }

            _SeriesBegunEpoch[i] = _PacketEpoch;
            series.BeginPacket();
            return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _TryGetRecordedSlot(int fieldId, out int slotIndex)
    {
        if (_UseCompactTee)
        {
            int[] ids = _CompactFieldIds;
            int count = ids.Length;
            if (count == 1)
            {
                slotIndex = 0;
                return ids[0] == fieldId;
            }

            if (count == 2)
            {
                if (ids[0] == fieldId)
                {
                    slotIndex = 0;
                    return true;
                }

                if (ids[1] == fieldId)
                {
                    slotIndex = 1;
                    return true;
                }

                slotIndex = 0;
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                if (ids[i] != fieldId)
                {
                    continue;
                }

                slotIndex = i;
                return true;
            }

            slotIndex = 0;
            return false;
        }

        if ((uint)fieldId >= (uint)_Probe.Length)
        {
            slotIndex = 0;
            return false;
        }

        ulong[] bits = _RecordedBits;
        if (bits.Length != 0)
        {
            if ((bits[(uint)fieldId >> 6] & (1UL << (fieldId & 63))) == 0)
            {
                slotIndex = 0;
                return false;
            }

            slotIndex = fieldId;
            return true;
        }

        if (_AllSlotsRecorded)
        {
            slotIndex = fieldId;
            return true;
        }

        ref ValueCacheProbeSlot slot = ref _Probe[fieldId];
        if (slot.Payload is null && slot.CustomText is null && slot.CustomRepresentation is null)
        {
            slotIndex = 0;
            return false;
        }

        slotIndex = fieldId;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _TryGetSlot(int fieldId, out ValueCacheProbeSlot slot)
    {
        if (!_TryGetRecordedSlot(fieldId, out int slotIndex))
        {
            slot = default;
            return false;
        }

        slot = _Probe[slotIndex];
        return true;
    }

    private bool _TryGetStringSeries(int fieldId, bool customText, out ValueCacheStringSeries? series)
    {
        series = null;
        if (!_TryGetSlot(fieldId, out ValueCacheProbeSlot slot))
        {
            return false;
        }

        series = customText ? slot.CustomText : slot.CustomRepresentation;
        return series is not null;
    }

    private bool _TryGetPayloadAs<T>(int fieldId, out T? series)
        where T : class
    {
        series = null;
        if (!_TryGetSlot(fieldId, out ValueCacheProbeSlot slot) || slot.Payload is not T typed)
        {
            return false;
        }

        series = typed;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _ThrowIfAbandoned()
    {
        if (_Abandoned != 0)
        {
            throw new InvalidOperationException("ValueCache was evicted");
        }
    }

    private static void _ValidateLimits(ValueCacheLimits limits)
    {
        if (limits.MaxRowCount is int rows && rows <= 0)
        {
            throw new ArgumentException("MaxRowCount must be greater than zero when set.", nameof(limits));
        }

        if (limits.MaxBytes is long bytes && bytes <= 0)
        {
            throw new ArgumentException("MaxBytes must be greater than zero when set.", nameof(limits));
        }
    }

    private static void _ValidateRecordFlags(bool recordValue, bool recordText, bool recordRep, string paramName)
    {
        if (!recordValue && !recordText && !recordRep)
        {
            throw new ArgumentException(
                "A field or group config must set RecordValue, RecordCustomText, or RecordCustomRepresentation.",
                paramName);
        }
    }

    private static FieldInfo _RequireField(Stack stack, FieldId fieldId, string paramName)
    {
        FieldInfo? info = stack.GetField(fieldId);
        if (info is null)
        {
            throw new ArgumentException(
                $"Unknown field id {fieldId.Value.ToString(CultureInfo.InvariantCulture)}.",
                paramName);
        }

        return info;
    }

    private static IndexGroupInfo _RequireGroup(Stack stack, IndexGroupId groupId, string paramName)
    {
        IndexGroupInfo? info = stack.GetIndexGroup(groupId);
        if (info is null)
        {
            throw new ArgumentException(
                $"Unknown index group id {groupId.Value.ToString(CultureInfo.InvariantCulture)}.",
                paramName);
        }

        return info;
    }

    private static void _CollectMaterializeGroup(ref HashSet<int>? groups, FieldInfo info)
    {
        if (info.IndexGroup is IndexGroupId group && group.IsValid)
        {
            groups ??= [];
            _ = groups.Add(group.Value);
        }
    }

    private static bool _UnmanagedTypeMatches(FieldType fieldType, Type type) =>
        fieldType switch
        {
            FieldType.None or FieldType.Bool => type == typeof(byte),
            FieldType.I64 or FieldType.Timestamp => type == typeof(long),
            FieldType.U64 or FieldType.MacAddress or FieldType.Eui64 => type == typeof(ulong),
            FieldType.F64 => type == typeof(double),
            FieldType.IPv4Address => type == typeof(uint),
            _ => false,
        };

    #endregion
}

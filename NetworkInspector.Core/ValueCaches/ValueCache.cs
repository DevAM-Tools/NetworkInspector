// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Single-writer RAM columnar cache of selected field values.
/// Create with a <see cref="Stack"/> and field/group configs (or <see cref="ValueCacheBuildOptions.RecordAllFields"/>),
/// then fill via <see cref="RecordPacket"/> or parse-time record (<c>Packet.TryParse</c> with a cache).
/// Implements <see cref="IReadOnlyValueCache"/>. Listeners should take
/// <see cref="ReadOnly"/> / <see cref="AsReadOnlyView"/> (keep the compile-time struct)
/// so they cannot call <see cref="RecordPacket"/> or <see cref="Abandon"/>.
/// Poll <see cref="ValueCacheSeries.Count"/> for row growth; Core does not raise a growth event.
/// Under <see cref="RecordAllFields"/>, <see cref="Series"/> itself can also grow: payload columns
/// are created on the first record / <see cref="RecordPacket"/> hit for that field, not for every
/// <see cref="Stack.Fields"/> entry. Re-read <see cref="IReadOnlyCollection{ValueCacheSeries}.Count"/> on the list
/// returned by <see cref="Series"/>; a cached count is not final while the writer is still filling.
/// <para>
/// <b>Thread-safety:</b> Single-writer / multi-reader. One thread calls
/// <see cref="RecordPacket"/> and parse-time <see cref="Record"/>. Concurrent readers may load
/// <see cref="ValueCacheSeries.Count"/> and read rows/chunks for indexes strictly below that count.
/// A reader may observe a row from a packet that has not finished parsing. Readers may also observe
/// new entries appearing in <see cref="Series"/> after a later packet introduces a field id that had
/// not appeared yet. The <see cref="Series"/> list object is stable; its
/// <see cref="IReadOnlyCollection{ValueCacheSeries}.Count"/> is published with a volatile write after each append.
/// After <see cref="Abandon"/>, writes throw <see cref="InvalidOperationException"/>; published reads remain allowed.
/// Concurrent <see cref="ValueCacheSeries{T}.Record"/> on the same series waits on a CAS gate
/// (serialized, no throw) so published <see cref="ValueCacheSeries.Count"/> cannot outrun a value cell.
/// </para>
/// <para>
/// Parse-time record never uses a dictionary. With at most 16 recorded field ids the probe is a compact
/// parallel array and a linear scan. Otherwise the probe is dense in the stack field count and a
/// bitset rejects unrecorded ids before the slot is loaded. <see cref="RecordAllFields"/> always
/// uses the dense probe so a first-seen field can grow a series without a compact rebuild.
/// Explicit configs are walked from the series arrays. <see cref="RecordAllFields"/> pull fill
/// walks the sealed packet and grows payload series on demand. Custom text and custom
/// representation stay on separate <see cref="ValueCacheSeries{T}"/> string series because one field can record both.
/// </para>
/// </summary>
public sealed class ValueCache : IReadOnlyValueCache
{
    #region Nested types

    /// <summary>
    /// Compact or dense probe slot. Payload type is stored so getters and
    /// <see cref="RecordPacket"/> do not call <see cref="Stack.GetField"/> on the fill path.
    /// <para>
    /// A mutable struct in an array, always reached through <c>ref</c> so the writer updates the array element in place.
    /// <c>Payload</c> is typed <see cref="object"/> because the payload series is generic over a type that depends on the
    /// stack <see cref="FieldType"/>; the slot stores that field type next to it, and the code casts with
    /// <c>Unsafe.As</c> only after the field type has fixed the generic argument (see
    /// <c>_CreatePayloadSeries</c>, <c>_RecordPayload</c>, and <c>_PayloadTypeMatches</c>, which must stay in step).
    /// </para>
    /// </summary>
    private struct ValueCacheProbeSlot
    {
        public object? Payload;
        public ValueCacheSeries<string>? CustomText;
        public ValueCacheSeries<string>? CustomRepresentation;
        public FieldType PayloadType;

        /// <summary>
        /// Writer-only: payload series was created or declined for this slot.
        /// Stops <see cref="Stack.GetField"/> on later <see cref="RecordAllFields"/> records
        /// for <see cref="FieldType.None"/> containers when presence is not recorded.
        /// </summary>
        public bool PayloadGrowResolved;
    }

    /// <summary>
    /// Live <see cref="ValueCache.Series"/> façade. Count grows under <see cref="ValueCache.RecordAllFields"/>;
    /// the object identity stays stable so listeners can hold the list.
    /// </summary>
    private sealed class GrowingSeriesList : IReadOnlyList<ValueCacheSeries>
    {
        private readonly ValueCache _Cache;

        /// <summary>Aliases the owning cache. Construction only.</summary>
        internal GrowingSeriesList(ValueCache cache) => _Cache = cache;

        /// <inheritdoc />
        public int Count => _Cache._AllSeriesCount;

        /// <inheritdoc />
        public ValueCacheSeries this[int index]
        {
            get
            {
                // The index is checked against the published count, not the array length: the array may hold unpublished
                // slots, and count-then-array order guarantees the array is long enough for the count just read.
                int count = _Cache._AllSeriesCount;
                if ((uint)index >= (uint)count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return _Cache._AllSeries[index];
            }
        }

        /// <inheritdoc />
        public IEnumerator<ValueCacheSeries> GetEnumerator()
        {
            // Count first, array second: the writer swaps in a larger array before it publishes a larger count, so the
            // array read here is at least as long as the count read before it. The clamp below is a defensive guard.
            // Entries added after this snapshot are not visited.
            int count = _Cache._AllSeriesCount;
            ValueCacheSeries[] items = _Cache._AllSeries;
            if (count > items.Length)
            {
                count = items.Length;
            }

            for (int i = 0; i < count; i++)
            {
                yield return items[i];
            }
        }

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Live <see cref="IReadOnlyValueCache.AllSeries"/> façade. Count tracks
    /// <see cref="GrowingSeriesList"/>; indexer wraps each entry as
    /// <see cref="ReadOnlyValueCacheSeries"/> without copying column data.
    /// </summary>
    private sealed class GrowingReadOnlySeriesList : IReadOnlyList<ReadOnlyValueCacheSeries>
    {
        private readonly ValueCache _Cache;

        /// <summary>Aliases the owning cache. Construction only.</summary>
        internal GrowingReadOnlySeriesList(ValueCache cache) => _Cache = cache;

        /// <inheritdoc />
        public int Count => _Cache._AllSeriesCount;

        /// <inheritdoc />
        public ReadOnlyValueCacheSeries this[int index]
        {
            get
            {
                int count = _Cache._AllSeriesCount;
                if ((uint)index >= (uint)count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return _Cache._AllSeries[index].AsReadOnlyView();
            }
        }

        /// <inheritdoc />
        public IEnumerator<ReadOnlyValueCacheSeries> GetEnumerator()
        {
            // Same count-then-array order and clamp as GrowingSeriesList.GetEnumerator.
            int count = _Cache._AllSeriesCount;
            ValueCacheSeries[] items = _Cache._AllSeries;
            if (count > items.Length)
            {
                count = items.Length;
            }

            for (int i = 0; i < count; i++)
            {
                yield return items[i].AsReadOnlyView();
            }
        }

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    #endregion

    #region Constants

    /// <summary>
    /// Linear-scan probe when the recorded field-id set is this size or smaller.
    /// Larger sets use a dense probe plus a bitset miss.
    /// Internal tuning constant, not a limit on what can be recorded: a cache with more fields simply uses the dense probe
    /// (memory proportional to the stack's field count). The compact form keeps the per-field fill path of a cache with a few
    /// fields on a small array instead of a stack-sized one, and never uses a dictionary on that path.
    /// </summary>
    private const int _CompactRecordLimit = 16;

    #endregion
    #region Fields

    // Probe structures, built once in the constructor and never reassigned (the slots inside are updated in place by the
    // writer under RecordAllFields). Three shapes, chosen in the constructor:
    //  - compact: _CompactFieldIds[i] is the field id of _Probe[i]; found by linear scan. Chosen for few explicit fields.
    //  - dense + bitset: _Probe is indexed by field id; _RecordedBits has one bit per stack field and rejects fields that
    //    are not recorded before the (large) slot is touched.
    //  - dense, everything recorded (_AllSlotsRecorded, and always for RecordAllFields): no bit test; a first-seen field
    //    grows its series in place without rebuilding anything.
    private readonly ValueCacheProbeSlot[] _Probe;
    private readonly int[] _CompactFieldIds;
    private readonly ulong[] _RecordedBits;
    private readonly bool _UseCompactRecord;
    private readonly bool _AllSlotsRecorded;
    private readonly ValueCaptureMode _DefaultCaptureMode;
    private readonly bool _RecordContainerPresence;
    private readonly GrowingSeriesList _SeriesList;
    private readonly GrowingReadOnlySeriesList _ReadOnlySeriesList;
    private readonly ValueCacheSeries<string>[] _CustomTextSeries;
    private readonly ValueCacheSeries<string>[] _CustomRepresentationSeries;
    private readonly IndexGroupId[] _MaterializeGroups;

    // One flag per stack index group: whether lazily populated containers of that group must be expanded so that the
    // configured fields exist when the cache records. Indexed by group id; read by _ShouldMaterialize.
    private readonly bool[] _MaterializeGroupMask;

    // Writer-grown under RecordAllFields. _AllSeries / _AllSeriesCount are published to readers
    // (volatile: array swap then count). _PayloadSeries is writer-only.
    private ValueCacheSeries[] _PayloadSeries;
    private int _PayloadSeriesCount;
    private volatile ValueCacheSeries[] _AllSeries;
    private volatile int _AllSeriesCount;

    // Cache-level sticky flags, 1 = still true. Written only by the single writer, read by anyone, hence volatile ints
    // (plain loads and stores are enough: there is no read-modify-write). They only ever go from 1 to 0 (and _Abandoned
    // and _MaterializationIncomplete only from 0 to 1), so a reader that sees the changed value needs no further ordering.
    private volatile int _PacketIdsStrict = 1;
    private volatile int _TimestampsStrict = 1;
    private volatile int _Abandoned;
    private volatile int _MaterializationIncomplete;

    // Writer-only state of the strictly-increasing flags: the previous recorded packet id and timestamp.
    private bool _MonotonicInitialized;
    private int _LastPacketId;
    private long _LastTimestampNanos;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a RAM value cache for <paramref name="stack"/>. Throws on unknown field or group ids,
    /// empty configuration without <see cref="ValueCacheBuildOptions.RecordAllFields"/>,
    /// duplicate payload series, or a field/group config with all record flags false.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stack"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Configuration is empty, contradictory, or out of range for <paramref name="stack"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ValueCacheBuildOptions.ChunkShift"/> or capture mode is out of range.</exception>
    public ValueCache(
        Stack stack,
        ReadOnlySpan<ValueCacheFieldConfig> fields,
        ReadOnlySpan<ValueCacheGroupConfig> groups = default,
        ValueCacheBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stack);

        // Options: a null argument means the defaults. A default(ValueCacheBuildOptions) struct bypasses the parameterless
        // constructor and has ChunkShift 0, which the range check below rejects instead of silently building tiny chunks.
        Stack = stack;
        ValueCacheBuildOptions resolved = options ?? new ValueCacheBuildOptions();
        RecordAllFields = resolved.RecordAllFields;
        _DefaultCaptureMode = resolved.DefaultCaptureMode;
        _RecordContainerPresence = resolved.RecordContainerPresence;
        _ValidateCaptureMode(_DefaultCaptureMode);
        ValueCacheBuildOptions.ThrowIfChunkShiftOutOfRange(resolved.ChunkShift, nameof(options));
        ChunkShift = resolved.ChunkShift;

        if (!RecordAllFields && fields.Length == 0 && groups.Length == 0)
        {
            throw new ArgumentException(
                "ValueCache requires at least one field or group, or RecordAllFields.",
                nameof(fields));
        }

        // Phase 1: collect what to record, keyed by field id (one payload, one custom-text, and one custom-representation
        // series per field at most). Explicit field configs are processed first and a duplicate is an error; group
        // expansion below uses TryAdd, so an explicit config always wins over a group for the same series. These
        // dictionaries are construction-time only; the per-packet path never uses a dictionary.
        Dictionary<int, (FieldInfo Info, ValueCaptureMode Mode)>? payload = null;
        Dictionary<int, (FieldInfo Info, ValueCaptureMode Mode)>? customText = null;
        Dictionary<int, (FieldInfo Info, ValueCaptureMode Mode)>? customRep = null;

        for (int i = 0; i < fields.Length; i++)
        {
            ValueCacheFieldConfig config = fields[i];
            _ValidateRecordFlags(config.RecordValue, config.RecordCustomText, config.RecordCustomRepresentation, nameof(fields));
            _ValidateCaptureMode(config.CaptureMode);
            FieldInfo info = _RequireField(stack, config.FieldId, nameof(fields));
            if (config.RecordValue)
            {
                payload ??= [];
                if (!payload.TryAdd(info.Id.Value, (info, config.CaptureMode)))
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.InvariantCulture, "Duplicate payload series for field '{0}'.", info.Name),
                        nameof(fields));
                }
            }

            if (config.RecordCustomText)
            {
                customText ??= [];
                if (!customText.TryAdd(info.Id.Value, (info, config.CaptureMode)))
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.InvariantCulture, "Duplicate custom-text series for field '{0}'.", info.Name),
                        nameof(fields));
                }
            }

            if (config.RecordCustomRepresentation)
            {
                customRep ??= [];
                if (!customRep.TryAdd(info.Id.Value, (info, config.CaptureMode)))
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.InvariantCulture, "Duplicate custom-representation series for field '{0}'.", info.Name),
                        nameof(fields));
                }
            }
        }

        // Group configs expand to every stack field whose index group matches. The stack is final, so the expansion at
        // construction time is complete; a group that matches no field creates no series.
        ReadOnlySpan<FieldInfo> stackFields = stack.Fields.Span;
        for (int g = 0; g < groups.Length; g++)
        {
            ValueCacheGroupConfig config = groups[g];
            _ValidateRecordFlags(config.RecordValue, config.RecordCustomText, config.RecordCustomRepresentation, nameof(groups));
            _ValidateCaptureMode(config.CaptureMode);
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

        // Phase 2: pick the probe shape. The sum of the three series counts is an upper bound of the number of distinct
        // field ids (one field can have up to three series), so it is a safe size for the compact test. RecordAllFields
        // never uses the compact form because its set of fields is not known up front.
        int payloadCount = payload?.Count ?? 0;
        int textCount = customText?.Count ?? 0;
        int repCount = customRep?.Count ?? 0;
        int uniqueUpper = payloadCount + textCount + repCount;
        bool useCompact = !RecordAllFields && uniqueUpper > 0 && uniqueUpper <= _CompactRecordLimit;

        ValueCacheProbeSlot[] dense = useCompact ? [] : new ValueCacheProbeSlot[stack.FieldCount];
        Dictionary<int, ValueCacheProbeSlot>? compactSlots = useCompact ? [] : null;
        ValueCacheSeries[] payloadSeries = payloadCount == 0 ? [] : new ValueCacheSeries[payloadCount];
        ValueCacheSeries<string>[] textSeries = textCount == 0 ? [] : new ValueCacheSeries<string>[textCount];
        ValueCacheSeries<string>[] repSeries = repCount == 0 ? [] : new ValueCacheSeries<string>[repCount];
        ValueCacheSeries[] all = new ValueCacheSeries[payloadCount + textCount + repCount];
        int allIndex = 0;
        HashSet<int>? materializeGroups = null;

        // Phase 3: create the configured series and register each in its probe slot. For explicit configs (not
        // RecordAllFields) the index groups of the recorded fields are collected as well: those groups' lazily populated
        // containers have to be expanded for the fields to exist when a packet is recorded.
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
                ValueCacheSeries<string> series = new(entry.Value.Info.Id, FieldType.String, entry.Value.Mode, ChunkShift);
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
                ValueCacheSeries<string> series = new(entry.Value.Info.Id, FieldType.String, entry.Value.Mode, ChunkShift);
                repSeries[repIndex++] = series;
                all[allIndex++] = series;
                _SetCustomRepresentationSlot(dense, compactSlots, entry.Key, series);
                if (!RecordAllFields)
                {
                    _CollectMaterializeGroup(ref materializeGroups, entry.Value.Info);
                }
            }
        }

        // Phase 4: publish the series arrays. The object is not shared with any other thread until the constructor returns,
        // so plain assignments are enough here; later growth (RecordAllFields) goes through _AppendSeries.
        _PayloadSeries = payloadSeries;
        _PayloadSeriesCount = payloadCount;
        _CustomTextSeries = textSeries;
        _CustomRepresentationSeries = repSeries;
        _AllSeries = all;
        _AllSeriesCount = allIndex;
        _SeriesList = new GrowingSeriesList(this);
        _ReadOnlySeriesList = new GrowingReadOnlySeriesList(this);

        // Phase 5: freeze the probe. The compact form turns the construction-time dictionary into two parallel arrays
        // (field ids and slots) in the same order. The dense form keeps the stack-sized slot array; without
        // RecordAllFields a bitset marks the recorded fields (it is left empty when every field is recorded, see
        // _BuildRecordedBits).
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

            _UseCompactRecord = true;
            _AllSlotsRecorded = false;
            _CompactFieldIds = compactIds;
            _Probe = compact;
            _RecordedBits = [];
        }
        else if (RecordAllFields)
        {
            _UseCompactRecord = false;
            _CompactFieldIds = [];
            _Probe = dense;
            _AllSlotsRecorded = dense.Length > 0;
            _RecordedBits = [];
        }
        else
        {
            _UseCompactRecord = false;
            _CompactFieldIds = [];
            _Probe = dense;
            _RecordedBits = _BuildRecordedBits(dense.Length, payload, customText, customRep, out bool allRecorded);
            _AllSlotsRecorded = allRecorded;
        }

        // Phase 6: the set of index groups whose lazy containers must be expanded. RecordAllFields needs every group; with
        // explicit configs it is the groups collected in phase 3. Only the mask is read later (by _ShouldMaterialize).
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

    /// <summary>Log₂ of rows per inner column chunk used by every series of this cache.</summary>
    public int ChunkShift { get; }

    /// <summary>
    /// Sticky flag: recorded packet ids have been strictly increasing so far.
    /// Starts true. The first recorded packet records the baseline. A later packet id less than the
    /// previous recorded id sets this false permanently. Repeated records of the same packet id (one packet usually
    /// records several fields) are one packet and never change the flag, so "less than or equal" between distinct
    /// packets is the same as "less than".
    /// Equal timestamps do not affect this flag. This cache-level flag is stricter and covers all series; the per-series
    /// <see cref="ValueCacheSeries.PacketIdsMonotonic"/> (non-decreasing, per column) is what binary search uses.
    /// </summary>
    public bool PacketIdsStrictlyIncreasing => _PacketIdsStrict != 0;

    /// <summary>
    /// Sticky flag: recorded timestamps have been strictly increasing so far.
    /// Starts true. When the packet id changes, a timestamp less than or equal to the previous recorded timestamp
    /// (including equal timestamps) sets this false permanently. Records of the same packet id are not compared.
    /// </summary>
    public bool TimestampsStrictlyIncreasing => _TimestampsStrict != 0;

    /// <summary>
    /// Sticky: <see cref="EnsureMaterialized(Packet)"/> hit its iteration cap. Columns may be incomplete.
    /// </summary>
    public bool IsMaterializationIncomplete => _MaterializationIncomplete != 0;

    /// <summary>
    /// All payload and optional custom-text / custom-representation series.
    /// Explicit field/group configs are present after construction. Under
    /// <see cref="RecordAllFields"/>, payload series are created when a field first appears
    /// (parse record or <see cref="RecordPacket"/>), so this list can grow. The list object is stable;
    /// re-read <see cref="IReadOnlyCollection{ValueCacheSeries}.Count"/>. Never-seen field ids have no column.
    /// </summary>
    public IReadOnlyList<ValueCacheSeries> Series => _SeriesList;

    /// <summary>Live read-only series list for <see cref="IReadOnlyValueCache.AllSeries"/> and <see cref="ReadOnlyValueCache"/>.</summary>
    public IReadOnlyList<ReadOnlyValueCacheSeries> AllSeries => _ReadOnlySeriesList;

    #endregion

    #region Public API

    /// <summary>
    /// Gets a zero-allocation read-only view of this cache.
    /// Keep the compile-time type as <see cref="ReadOnlyValueCache"/> or pass it to a
    /// generic <c>where TCache : IReadOnlyValueCache</c> parameter. Do not assign the
    /// result to <see cref="IReadOnlyValueCache"/> — that boxes.
    /// </summary>
    public ReadOnlyValueCache ReadOnly => new(this);

    /// <summary>Alias for <see cref="ReadOnly"/>.</summary>
    public ReadOnlyValueCache AsReadOnlyView() => new(this);

    /// <summary>
    /// Returns the payload series for <paramref name="fieldId"/>.
    /// Custom text and custom representation use the named getters instead of this method.
    /// </summary>
    /// <exception cref="ArgumentException">No series, or <typeparamref name="T"/> does not match the stack <see cref="FieldType"/>.</exception>
    public ValueCacheSeries<T> GetSeries<T>(FieldId fieldId)
    {
        if (!TryGetSeries(fieldId, out ValueCacheSeries<T>? series) || series is null)
        {
            throw new ArgumentException("No payload series of the requested type for this field.", nameof(fieldId));
        }

        return series;
    }

    /// <summary>Try-get counterpart of <see cref="GetSeries{T}(FieldId)"/>.</summary>
    public bool TryGetSeries<T>(FieldId fieldId, out ValueCacheSeries<T>? series)
    {
        series = null;
        if (!_TryGetSlot(fieldId.Value, out ValueCacheProbeSlot slot) || slot.Payload is null)
        {
            return false;
        }

        // The requested T must be exactly the CLR type that _CreatePayloadSeries chose for this field's FieldType. That check
        // is what makes the Unsafe.As below sound: it skips the type check of a normal cast, which would otherwise be paid on
        // every lookup, and the slot's Payload is always a ValueCacheSeries of the matching type.
        if (!_PayloadTypeMatches(slot.PayloadType, typeof(T)))
        {
            return false;
        }

        series = Unsafe.As<ValueCacheSeries<T>>(slot.Payload);
        return series is not null;
    }

    /// <summary>Looks up a field by ordinal name, then <see cref="TryGetSeries{T}(FieldId, out ValueCacheSeries{T})"/>.</summary>
    public bool TryGetSeries<T>(string fieldName, out ValueCacheSeries<T>? series)
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
    public ValueCacheSeries<string> GetCustomTextSeries(FieldId fieldId)
    {
        if (!TryGetCustomTextSeries(fieldId, out ValueCacheSeries<string>? series) || series is null)
        {
            throw new ArgumentException("No custom-text series for this field.", nameof(fieldId));
        }

        return series;
    }

    /// <summary>Try-get counterpart of <see cref="GetCustomTextSeries(FieldId)"/>.</summary>
    public bool TryGetCustomTextSeries(FieldId fieldId, out ValueCacheSeries<string>? series) =>
        _TryGetStringSeries(fieldId.Value, customText: true, out series);

    /// <summary>Looks up custom-text series by field name.</summary>
    public bool TryGetCustomTextSeries(string fieldName, out ValueCacheSeries<string>? series)
    {
        series = null;
        FieldId? id = fieldName is null ? null : Stack.GetFieldId(fieldName);
        return id is not null && TryGetCustomTextSeries(id.Value, out series);
    }

    /// <summary>Custom-representation series for <paramref name="fieldId"/>.</summary>
    /// <exception cref="ArgumentException">No custom-representation series for this field.</exception>
    public ValueCacheSeries<string> GetCustomRepresentationSeries(FieldId fieldId)
    {
        if (!TryGetCustomRepresentationSeries(fieldId, out ValueCacheSeries<string>? series) || series is null)
        {
            throw new ArgumentException("No custom-representation series for this field.", nameof(fieldId));
        }

        return series;
    }

    /// <summary>Try-get counterpart of <see cref="GetCustomRepresentationSeries(FieldId)"/>.</summary>
    public bool TryGetCustomRepresentationSeries(FieldId fieldId, out ValueCacheSeries<string>? series) =>
        _TryGetStringSeries(fieldId.Value, customText: false, out series);

    /// <summary>Looks up custom-representation series by field name.</summary>
    public bool TryGetCustomRepresentationSeries(string fieldName, out ValueCacheSeries<string>? series)
    {
        series = null;
        FieldId? id = fieldName is null ? null : Stack.GetFieldId(fieldName);
        return id is not null && TryGetCustomRepresentationSeries(id.Value, out series);
    }

    #endregion

    #region IReadOnlyValueCache

    /// <inheritdoc/>
    ReadOnlyValueCacheSeries<T> IReadOnlyValueCache.GetSeries<T>(FieldId fieldId) =>
        GetSeries<T>(fieldId).AsReadOnlyView();

    /// <inheritdoc/>
    bool IReadOnlyValueCache.TryGetSeries<T>(FieldId fieldId, out ReadOnlyValueCacheSeries<T> series) =>
        _TryAsReadOnly(TryGetSeries(fieldId, out ValueCacheSeries<T>? live), live, out series);

    /// <inheritdoc/>
    bool IReadOnlyValueCache.TryGetSeries<T>(string fieldName, out ReadOnlyValueCacheSeries<T> series) =>
        _TryAsReadOnly(TryGetSeries(fieldName, out ValueCacheSeries<T>? live), live, out series);

    /// <inheritdoc/>
    ReadOnlyValueCacheSeries<string> IReadOnlyValueCache.GetCustomTextSeries(FieldId fieldId) =>
        GetCustomTextSeries(fieldId).AsReadOnlyView();

    /// <inheritdoc/>
    bool IReadOnlyValueCache.TryGetCustomTextSeries(FieldId fieldId, out ReadOnlyValueCacheSeries<string> series) =>
        _TryAsReadOnly(TryGetCustomTextSeries(fieldId, out ValueCacheSeries<string>? live), live, out series);

    /// <inheritdoc/>
    bool IReadOnlyValueCache.TryGetCustomTextSeries(string fieldName, out ReadOnlyValueCacheSeries<string> series) =>
        _TryAsReadOnly(TryGetCustomTextSeries(fieldName, out ValueCacheSeries<string>? live), live, out series);

    /// <inheritdoc/>
    ReadOnlyValueCacheSeries<string> IReadOnlyValueCache.GetCustomRepresentationSeries(FieldId fieldId) =>
        GetCustomRepresentationSeries(fieldId).AsReadOnlyView();

    /// <inheritdoc/>
    bool IReadOnlyValueCache.TryGetCustomRepresentationSeries(FieldId fieldId, out ReadOnlyValueCacheSeries<string> series) =>
        _TryAsReadOnly(TryGetCustomRepresentationSeries(fieldId, out ValueCacheSeries<string>? live), live, out series);

    /// <inheritdoc/>
    bool IReadOnlyValueCache.TryGetCustomRepresentationSeries(string fieldName, out ReadOnlyValueCacheSeries<string> series) =>
        _TryAsReadOnly(TryGetCustomRepresentationSeries(fieldName, out ValueCacheSeries<string>? live), live, out series);

    #endregion

    #region Mutation

    /// <summary>
    /// Pull ingest for a caller who owns the writer. Walks the sealed packet in storage order
    /// when <see cref="RecordAllFields"/> is set (grows payload series on first sight);
    /// otherwise walks the pre-created series arrays.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="packet"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Stack mismatch, the packet is not finalized, or the packet was parsed with <see cref="FieldTreeMode.Skip"/>.</exception>
    /// <exception cref="InvalidOperationException">This cache was evicted.</exception>
    public void RecordPacket(Packet packet)
    {
        // Preconditions are programming errors of the caller (wrong stack, unsealed or tree-less packet), so they throw.
        // A packet of another stack would index the probe with foreign field ids, and a skip packet has no tree to walk.
        ArgumentNullException.ThrowIfNull(packet);
        _ThrowIfAbandoned();
        if (!ReferenceEquals(packet.Stack, Stack))
        {
            throw new ArgumentException("Packet stack does not match this ValueCache.", nameof(packet));
        }

        if (!packet.IsFinalized)
        {
            throw new ArgumentException("RecordPacket requires a finalized packet.", nameof(packet));
        }

        if (!packet.HasFieldTree)
        {
            throw new ArgumentException("RecordPacket requires a packet parsed with FieldTreeMode.Build.", nameof(packet));
        }

        // Expand lazy containers first so the configured fields exist, then update the cache-level flags once for the packet.
        EnsureMaterialized(packet);
        _UpdateMonotonicFlags(packet.Id.Value, packet.Timestamp.AsNanos);

        // RecordAllFields walks the packet in storage order and lets Record grow series on first sight. Otherwise each
        // pre-created series looks up its own field occurrences, which touches only the configured fields.
        if (RecordAllFields)
        {
            foreach (Field field in packet.IterFieldsFlat(materialize: false))
            {
                Record(packet.Id.Value, packet.Timestamp.AsNanos, field.FieldId, in field.Value, in field.CustomText);
            }

            return;
        }

        _RecordPayloadSeries(packet);
        _RecordCustomTextSeries(packet);
        _RecordCustomRepresentationSeries(packet);
    }

    /// <summary>
    /// Evicts this writer. Further <see cref="Record"/>, <see cref="RecordCustomText"/>, and
    /// <see cref="RecordPacket"/> throw. Published reads remain allowed.
    /// </summary>
    /// <remarks>
    /// Session calls this on Restart. Listeners receive <see cref="ReadOnlyValueCache"/>,
    /// which cannot invoke this method.
    /// </remarks>
    public void Abandon() => _Abandoned = 1;

    /// <summary>Whether <see cref="Abandon"/> has been called (Restart eviction).</summary>
    public bool IsAbandoned => _Abandoned != 0;

    #endregion

    #region Internal API

    /// <summary>
    /// Parse-time record. Compact linear scan when few fields are recorded; otherwise a bitset miss
    /// before the dense slot is loaded. Predicted not-taken when this field has no series.
    /// Called only from <c>Packet</c>'s NoInlining stub so the probe cannot inflate <c>AppendChild</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Record(int packetId, long timestampNanos, FieldId fieldId, in FieldValue value, in LazyString customText)
    {
        // Called once per appended field of a parse that has this cache attached, so the miss path (field not recorded)
        // is the common one and must stay cheap: no allocation, no dictionary, and the hit logic lives in a separate
        // non-inlined method. 'value' is passed by reference to avoid copying the FieldValue struct per field.
        _ThrowIfAbandoned();
        _UpdateMonotonicFlags(packetId, timestampNanos);
        if (!_TryGetRecordedSlot(fieldId.Value, out int slotIndex))
        {
            return;
        }

        _RecordHitCold(fieldId, packetId, timestampNanos, in value, in customText, ref _Probe[slotIndex]);
    }

    /// <summary>
    /// Custom-text record after <see cref="MutField.SetCustomText"/> / append / clear.
    /// Null text is a no-op.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void RecordCustomText(int packetId, long timestampNanos, FieldId fieldId, in LazyString customText)
    {
        _ThrowIfAbandoned();
        if (customText.IsNull)
        {
            return;
        }

        _UpdateMonotonicFlags(packetId, timestampNanos);
        if (!_TryGetRecordedSlot(fieldId.Value, out int slotIndex))
        {
            return;
        }

        if (_Probe[slotIndex].CustomText is { } series)
        {
            series.Record(packetId, timestampNanos, customText.AsString);
        }
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
        if (!packet.HasFieldTree)
        {
            return;
        }

        if (maxPasses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPasses), maxPasses, "maxPasses must be greater than zero.");
        }

        // Each pass expands the lazy containers that exist right now; expanding one can create children that are lazy
        // themselves, which the next pass picks up, so a pass descends one nesting level. Threat: a hostile or broken
        // capture whose populators keep producing lazy fields would loop forever. Mitigation: a hard cap on passes; hitting
        // it sets IsMaterializationIncomplete (an expected failure, so a flag and not an exception). The production cap is
        // ushort.MaxValue, the packet's field capacity (storage indexes are ushort), so legitimate nesting cannot reach it.
        // Constant, not configurable by users; the internal overload takes the cap as a parameter so that a caller can
        // pass a smaller one. The check for remaining lazy fields runs at the start of a pass, so a packet that needs
        // exactly maxPasses passes is flagged as incomplete too (conservative).
        for (int pass = 0; pass < maxPasses; pass++)
        {
            if (!packet.HasUnpopulatedLazyFields)
            {
                return;
            }

            // The field count is read once per pass, so children created by this pass are visited in the next pass, not in
            // this loop (that is what bounds the work of one pass and makes the pass cap meaningful).
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
    /// Whether skip-tree should invoke the lazy populator for this container so configured series can record.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ShouldMaterialize(FieldId fieldId) => _ShouldMaterialize(fieldId);

    /// <summary>
    /// True when this cache records custom text or custom representation for <paramref name="fieldId"/>.
    /// <see cref="RecordAllFields"/> does not force display text for every field.
    /// </summary>
    internal bool WantsDisplayText(FieldId fieldId)
    {
        if (!_TryGetRecordedSlot(fieldId.Value, out int slotIndex))
        {
            return false;
        }

        ref ValueCacheProbeSlot slot = ref _Probe[slotIndex];
        return slot.CustomText is not null || slot.CustomRepresentation is not null;
    }

    #endregion

    #region Private helpers

    /// <summary>Wraps a live series as <see cref="ReadOnlyValueCacheSeries{T}"/> when <paramref name="found"/>.</summary>
    /// <param name="found">Whether the writer lookup succeeded.</param>
    /// <param name="live">Writer series when <paramref name="found"/>; otherwise ignored.</param>
    /// <param name="series">Read-only view when found; otherwise <c>default</c>.</param>
    private static bool _TryAsReadOnly<T>(
        bool found,
        ValueCacheSeries<T>? live,
        out ReadOnlyValueCacheSeries<T> series)
    {
        if (!found || live is null)
        {
            series = default;
            return false;
        }

        series = live.AsReadOnlyView();
        return true;
    }

    // The _Set*Slot helpers write into whichever probe form is being built: the compact dictionary (a struct value, so it is
    // read, modified, and stored back) or the dense array (updated in place through a ref). A payload slot is marked
    // PayloadGrowResolved because its series exists; a text/representation slot has no growth question.

    /// <summary>Registers the payload series of one field in the probe being built.</summary>
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
            slot.PayloadGrowResolved = true;
            compactSlots[fieldId] = slot;
            return;
        }

        ref ValueCacheProbeSlot denseSlot = ref dense[fieldId];
        denseSlot.Payload = series;
        denseSlot.PayloadType = fieldType;
        denseSlot.PayloadGrowResolved = true;
    }

    /// <summary>Registers the custom-text series of one field in the probe being built.</summary>
    private static void _SetCustomTextSlot(
        ValueCacheProbeSlot[] dense,
        Dictionary<int, ValueCacheProbeSlot>? compactSlots,
        int fieldId,
        ValueCacheSeries<string> series)
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

    /// <summary>Registers the custom-representation series of one field in the probe being built.</summary>
    private static void _SetCustomRepresentationSlot(
        ValueCacheProbeSlot[] dense,
        Dictionary<int, ValueCacheProbeSlot>? compactSlots,
        int fieldId,
        ValueCacheSeries<string> series)
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

    /// <summary>
    /// Builds the one-bit-per-stack-field set of recorded fields used by the dense probe to reject unrecorded fields before
    /// the slot (several references plus flags) is loaded. Returns an empty array, and no bit test is needed, in two cases: nothing is
    /// recorded, or every field is recorded (<paramref name="allRecorded"/>), where the bit test would always pass.
    /// </summary>
    /// <param name="fieldCount">Number of stack fields (size of the dense probe).</param>
    /// <param name="payload">Fields with a payload series.</param>
    /// <param name="customText">Fields with a custom-text series.</param>
    /// <param name="customRep">Fields with a custom-representation series.</param>
    /// <param name="allRecorded">Whether every stack field has at least one series.</param>
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

        // One bit per field id, 64 per word (rounded up).
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

    /// <summary>
    /// Sets the bit of every field id in <paramref name="map"/> and returns the running count of distinct set bits (a field
    /// that appears in several maps is counted once, which is what the all-recorded test compares with the field count).
    /// </summary>
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

    /// <summary>
    /// Creates the payload series for a field. The mapping from <see cref="FieldType"/> to the stored CLR type is part of the
    /// storage format of the cache and has to agree with <c>_RecordPayload</c> (what is written) and
    /// <c>_PayloadTypeMatches</c> (what a reader may ask for): <c>None</c> and <c>Bool</c> are one byte (0 for presence,
    /// 0 or 1 for a flag); I64 and Timestamp are <c>long</c> (a timestamp as nanoseconds); U64, MacAddress, and Eui64 are
    /// <c>ulong</c> (addresses as their raw 64-bit value); IPv4 is the raw <c>uint</c>; the remaining types are stored as
    /// they are. Typed series keep the columns unboxed and contiguous (SoA), which the chunk scans rely on.
    /// </summary>
    /// <exception cref="ArgumentException">The field type has no series representation.</exception>
    private object _CreatePayloadSeries(FieldInfo info, ValueCaptureMode mode)
    {
        FieldId id = info.Id;
        int shift = ChunkShift;
        return info.FieldType switch
        {
            FieldType.None => new ValueCacheSeries<byte>(id, FieldType.None, mode, shift),
            FieldType.Bool => new ValueCacheSeries<byte>(id, FieldType.Bool, mode, shift),
            FieldType.I64 => new ValueCacheSeries<long>(id, FieldType.I64, mode, shift),
            FieldType.U64 => new ValueCacheSeries<ulong>(id, FieldType.U64, mode, shift),
            FieldType.F64 => new ValueCacheSeries<double>(id, FieldType.F64, mode, shift),
            FieldType.String => new ValueCacheSeries<string>(id, FieldType.String, mode, shift),
            FieldType.Bytes => new ValueCacheSeries<byte[]>(id, FieldType.Bytes, mode, shift),
            FieldType.MacAddress => new ValueCacheSeries<ulong>(id, FieldType.MacAddress, mode, shift),
            FieldType.IPv4Address => new ValueCacheSeries<uint>(id, FieldType.IPv4Address, mode, shift),
            FieldType.IPv6Address => new ValueCacheSeries<IPv6Address>(id, FieldType.IPv6Address, mode, shift),
            FieldType.Eui64 => new ValueCacheSeries<ulong>(id, FieldType.Eui64, mode, shift),
            FieldType.Uuid => new ValueCacheSeries<Uuid>(id, FieldType.Uuid, mode, shift),
            FieldType.Timestamp => new ValueCacheSeries<long>(id, FieldType.Timestamp, mode, shift),
            _ => throw new ArgumentException(
                string.Format(CultureInfo.InvariantCulture, "Unsupported field type '{0}' for field '{1}'.", info.FieldType, info.Name),
                nameof(info)),
        };
    }

    /// <summary>
    /// Pull fill of the payload series (no RecordAllFields): for every configured series, visits each occurrence of its
    /// field in the packet via the field lookup cookie. Reads fields without materializing; the lazy containers that matter
    /// were expanded by <see cref="EnsureMaterialized(Packet)"/> before. Writer thread only.
    /// </summary>
    private void _RecordPayloadSeries(Packet packet)
    {
        ValueCacheSeries[] series = _PayloadSeries;
        int count = _PayloadSeriesCount;
        int packetId = packet.Id.Value;
        long timestampNanos = packet.Timestamp.AsNanos;
        for (int i = 0; i < count; i++)
        {
            ValueCacheSeries item = series[i];
            FieldId fieldId = item.FieldId;
            FieldType fieldType = item.FieldType;
            FieldLookupCookie cookie = FieldLookupCookie.Start;
            while (packet.TryGetNextField(fieldId, ref cookie, out Field field, materialize: false))
            {
                _RecordPayload(item, fieldType, packetId, timestampNanos, field.Value);
            }
        }
    }

    private void _RecordCustomTextSeries(Packet packet) =>
        _RecordStringSeries(_CustomTextSeries, packet, customText: true);

    private void _RecordCustomRepresentationSeries(Packet packet) =>
        _RecordStringSeries(_CustomRepresentationSeries, packet, customText: false);

    /// <summary>
    /// Pull fill of the custom-text or custom-representation series: records the display text of each occurrence of the
    /// field, skipping occurrences that have none (a null text is not a row). Writer thread only.
    /// </summary>
    private static void _RecordStringSeries(ValueCacheSeries<string>[] series, Packet packet, bool customText)
    {
        int packetId = packet.Id.Value;
        long timestampNanos = packet.Timestamp.AsNanos;
        for (int i = 0; i < series.Length; i++)
        {
            ValueCacheSeries<string> column = series[i];
            FieldId fieldId = column.FieldId;
            FieldLookupCookie cookie = FieldLookupCookie.Start;
            while (packet.TryGetNextField(fieldId, ref cookie, out Field field, materialize: false))
            {
                // The custom text is read through its reference so that evaluating it caches the string in the field, where the
                // tree and the exporters read it later; the representation is read through the reference to the stored value for the same reason.
                if (customText)
                {
                    ref readonly LazyString text = ref field.CustomText;
                    if (!text.IsNull)
                    {
                        column.Record(packetId, timestampNanos, text.AsString);
                    }

                    continue;
                }

                ref readonly LazyString representation = ref field.Value.CustomRepresentation;
                if (!representation.IsNull)
                {
                    column.Record(packetId, timestampNanos, representation.AsString);
                }
            }
        }
    }

    /// <summary>
    /// Appends one value to the payload series <paramref name="boxed"/>, converting it to the series' stored type (see
    /// <c>_CreatePayloadSeries</c> for the mapping). A value that does not hold the expected kind (the field was set to
    /// another type) is skipped, not recorded as a default. The parameter is named "boxed" but is a reference to the series
    /// object, not a boxed value.
    /// </summary>
    /// <param name="boxed">The series object stored in the probe slot.</param>
    /// <param name="fieldType">The stack field type; it fixes the series' generic argument and so the cast target.</param>
    /// <param name="packetId">Owning packet id.</param>
    /// <param name="timestampNanos">Packet timestamp in nanoseconds.</param>
    /// <param name="value">The field value to store.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _RecordPayload(object boxed, FieldType fieldType, int packetId, long timestampNanos, in FieldValue value)
    {
        // Dispatch on the stored FieldType with one switch instead of a virtual call per type. Each case casts with
        // Unsafe.As, which is sound because the slot's series was created for exactly this FieldType and its generic
        // argument, and skips the type check a normal cast would repeat for every recorded value.
        switch (fieldType)
        {
            case FieldType.None:
                Unsafe.As<ValueCacheSeries<byte>>(boxed).Record(packetId, timestampNanos, 0);
                return;
            case FieldType.Bool:
            {
                if (value.Data.TryGetAsBool(out bool flag))
                {
                    Unsafe.As<ValueCacheSeries<byte>>(boxed).Record(packetId, timestampNanos, flag ? (byte)1 : (byte)0);
                }

                return;
            }
            case FieldType.I64:
            {
                if (value.Data.TryGetAsI64(out long i64))
                {
                    Unsafe.As<ValueCacheSeries<long>>(boxed).Record(packetId, timestampNanos, i64);
                }

                return;
            }
            case FieldType.U64:
            {
                if (value.Data.TryGetAsU64(out ulong u64))
                {
                    Unsafe.As<ValueCacheSeries<ulong>>(boxed).Record(packetId, timestampNanos, u64);
                }

                return;
            }
            case FieldType.F64:
            {
                if (value.Data.TryGetAsF64(out double f64))
                {
                    Unsafe.As<ValueCacheSeries<double>>(boxed).Record(packetId, timestampNanos, f64);
                }

                return;
            }
            case FieldType.String:
            {
                if (value.Data.TryGetAsString(out string text))
                {
                    Unsafe.As<ValueCacheSeries<string>>(boxed).Record(packetId, timestampNanos, text);
                }

                return;
            }
            case FieldType.Bytes:
            {
                if (value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytes))
                {
                    // The series keeps the bytes for the lifetime of the cache, so it must own them: the value may alias the
                    // frame or packet buffer, whose lifetime is not tied to the cache (packets are recycled).
                    byte[] copy = bytes.Length == 0 ? [] : bytes.ToArray();
                    Unsafe.As<ValueCacheSeries<byte[]>>(boxed).Record(packetId, timestampNanos, copy);
                }

                return;
            }
            case FieldType.MacAddress:
            {
                if (value.Data.TryGetAsMacAddress(out MacAddress mac))
                {
                    Unsafe.As<ValueCacheSeries<ulong>>(boxed).Record(packetId, timestampNanos, mac.RawValue);
                }

                return;
            }
            case FieldType.IPv4Address:
            {
                if (value.Data.TryGetAsIPv4(out IPv4Address ipv4))
                {
                    Unsafe.As<ValueCacheSeries<uint>>(boxed).Record(packetId, timestampNanos, ipv4.RawValue);
                }

                return;
            }
            case FieldType.IPv6Address:
            {
                if (value.Data.TryGetAsIPv6(out IPv6Address ipv6))
                {
                    Unsafe.As<ValueCacheSeries<IPv6Address>>(boxed).Record(packetId, timestampNanos, ipv6);
                }

                return;
            }
            case FieldType.Eui64:
            {
                if (value.Data.TryGetAsEui64(out Eui64 eui))
                {
                    Unsafe.As<ValueCacheSeries<ulong>>(boxed).Record(packetId, timestampNanos, eui.RawValue);
                }

                return;
            }
            case FieldType.Uuid:
            {
                if (value.Data.TryGetAsUuid(out Uuid uuid))
                {
                    Unsafe.As<ValueCacheSeries<Uuid>>(boxed).Record(packetId, timestampNanos, uuid);
                }

                return;
            }
            case FieldType.Timestamp:
            {
                if (value.Data.TryGetAsTimestamp(out Timestamp timestamp))
                {
                    Unsafe.As<ValueCacheSeries<long>>(boxed).Record(packetId, timestampNanos, timestamp.AsNanos);
                }

                return;
            }
            default:
                return;
        }
    }

    /// <summary>
    /// Whether the lazy container <paramref name="fieldId"/> has to be expanded for this cache: always under
    /// <see cref="RecordAllFields"/>, otherwise only when its index group is one of the groups of the recorded fields.
    /// Lazy containers outside those groups stay unexpanded, which keeps the fill cheap.
    /// </summary>
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

    /// <summary>
    /// Maintains the cache-level strictly-increasing flags. Writer thread only (plain state, no synchronization); the flags
    /// themselves are volatile ints that only ever change from 1 to 0.
    /// </summary>
    private void _UpdateMonotonicFlags(int packetId, long timestampNanos)
    {
        // The first record only sets the baseline.
        if (!_MonotonicInitialized)
        {
            _LastPacketId = packetId;
            _LastTimestampNanos = timestampNanos;
            _MonotonicInitialized = true;
            return;
        }

        // The same packet id again is another field of the packet already seen (Record is called once per field), not a
        // regression, and its timestamp is the same packet's; nothing to compare.
        if (packetId == _LastPacketId)
        {
            return;
        }

        // A different id that is not larger breaks "strictly increasing"; a timestamp that is not larger does too (so equal
        // timestamps of consecutive packets clear the timestamp flag).
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

    /// <summary>
    /// Records the value, the custom representation, and the custom text of a field that has a probe slot. A first-seen
    /// field under <see cref="RecordAllFields"/> creates its payload series here (once, then the slot is resolved). Each
    /// part is optional: a slot may hold only a custom-text series, for example. Inlined only into <c>_RecordHitCold</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _RecordHit(FieldId fieldId, int packetId, long timestampNanos, in FieldValue value, in LazyString customText, ref ValueCacheProbeSlot slot)
    {
        if (slot.Payload is null && RecordAllFields && !slot.PayloadGrowResolved)
        {
            _GrowPayloadSeriesOnRecord(fieldId, ref slot);
        }

        if (slot.Payload is not null)
        {
            _RecordPayload(slot.Payload, slot.PayloadType, packetId, timestampNanos, in value);
        }

        if (slot.CustomRepresentation is { } representation && !value.CustomRepresentation.IsNull)
        {
            representation.Record(packetId, timestampNanos, value.CustomRepresentation.AsString);
        }

        if (slot.CustomText is { } text && !customText.IsNull)
        {
            text.Record(packetId, timestampNanos, customText.AsString);
        }
    }

    /// <summary>
    /// Out-of-line wrapper around <c>_RecordHit</c>: the hit logic is compiled once into this method instead of being
    /// inlined into <c>Record</c>, so the frequent miss path in <c>Record</c> stays small.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _RecordHitCold(FieldId fieldId, int packetId, long timestampNanos, in FieldValue value, in LazyString customText, ref ValueCacheProbeSlot slot)
        => _RecordHit(fieldId, packetId, timestampNanos, in value, in customText, ref slot);

    /// <summary>
    /// First-seen <see cref="RecordAllFields"/> payload. Writer thread only. Declines
    /// <see cref="FieldType.None"/> unless <see cref="ValueCacheBuildOptions.RecordContainerPresence"/>
    /// or an explicit config already filled the slot.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _GrowPayloadSeriesOnRecord(FieldId fieldId, ref ValueCacheProbeSlot slot)
    {
        FieldInfo? info = Stack.GetField(fieldId);
        if (info is null || (info.FieldType == FieldType.None && !_RecordContainerPresence))
        {
            slot.PayloadGrowResolved = true;
            return;
        }

        // Fill the slot completely, publish the series reference last (release), then add it to the Series list. A reader
        // that finds Payload non-null through a lookup therefore sees an initialized series; the Series list catches up one
        // step later, so a lookup by field id can succeed slightly before the series shows up in the list.
        ValueCacheSeries series = (ValueCacheSeries)_CreatePayloadSeries(info, _DefaultCaptureMode);
        slot.PayloadType = info.FieldType;
        slot.PayloadGrowResolved = true;
        Volatile.Write(ref slot.Payload, series);
        _AppendSeries(series);
    }

    /// <summary>
    /// Doubles series buffers and publishes the new <see cref="Series"/> count.
    /// Order: grow arrays, store the item, then volatile-write count so readers never
    /// observe a count past a live slot.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _AppendSeries(ValueCacheSeries series)
    {
        // Reader-visible list: when full, copy into a larger array and swap the volatile reference; a reader that still
        // holds the old array keeps a valid (shorter) snapshot, which is why the array is replaced and never resized in
        // place. The item is stored before the count is published (release), so a reader never sees a count past a filled slot.
        int count = _AllSeriesCount;
        ValueCacheSeries[] all = _AllSeries;
        if (count == all.Length)
        {
            all = _GrowSeriesBuffer(all);
            _AllSeries = all;
        }

        all[count] = series;
        _AllSeriesCount = count + 1;

        // Writer-only list of payload series for the pull fill: plain fields, no publication needed.
        int payloadCount = _PayloadSeriesCount;
        ValueCacheSeries[] payload = _PayloadSeries;
        if (payloadCount == payload.Length)
        {
            payload = _GrowSeriesBuffer(payload);
            _PayloadSeries = payload;
        }

        payload[payloadCount] = series;
        _PayloadSeriesCount = payloadCount + 1;
    }

    /// <summary>Returns a copy of <paramref name="current"/> in an array of twice the length (4 when empty): amortized O(1) per append.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueCacheSeries[] _GrowSeriesBuffer(ValueCacheSeries[] current)
    {
        int length = current.Length;
        // Series count stays ≤ stack field count plus explicit text/rep; doubling cannot overflow int.
        int cap = length == 0
            ? 4
            : length * 2;
        ValueCacheSeries[] next = new ValueCacheSeries[cap];
        if (length != 0)
        {
            Array.Copy(current, next, length);
        }

        return next;
    }

    /// <summary>
    /// Finds the probe slot of a field id, or reports that the field is not recorded. This is the per-field gate of the fill
    /// path, so it allocates nothing and uses no dictionary. Safe for any thread: it only reads data that is fixed after
    /// construction (the slot contents may still change under RecordAllFields).
    /// </summary>
    /// <param name="fieldId">Raw field id; any value is accepted (out-of-range ids are simply not recorded).</param>
    /// <param name="slotIndex">Index into the probe array when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the field has a slot.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _TryGetRecordedSlot(int fieldId, out int slotIndex)
    {
        // Compact probe: a linear scan over a few ids. One and two ids (the usual case) are unrolled, which avoids the loop
        // setup for them.
        if (_UseCompactRecord)
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

        // Dense probe. The unsigned compare rejects negative and too-large ids in one branch. Then, cheapest test first:
        // the bitset (one bit per field, a far smaller memory footprint than the slot array) rejects unrecorded fields
        // without loading the slot; when every field is recorded no test is needed; the slot check below is the fallback.
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

    /// <summary>
    /// Reader-side lookup of a slot copy. Under RecordAllFields the writer may create the payload series concurrently: it
    /// stores <c>PayloadType</c> first and then publishes <c>Payload</c> with release semantics. The reader therefore reads
    /// <c>Payload</c> first, with acquire semantics, and copies the rest of the slot afterwards, so a non-null payload is
    /// always paired with the <c>PayloadType</c> that was stored for it. The other series references are fixed at
    /// construction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _TryGetSlot(int fieldId, out ValueCacheProbeSlot slot)
    {
        if (!_TryGetRecordedSlot(fieldId, out int slotIndex))
        {
            slot = default;
            return false;
        }

        // The acquire read must come before the struct copy. Copying first could capture a stale PayloadType (still the
        // default) and then pair it with the series the writer published in between; a reader asking for the wrong element
        // type would pass the type check and Unsafe.As would reinterpret a series of another type (memory unsafe).
        ref ValueCacheProbeSlot live = ref _Probe[slotIndex];
        object? payload = Volatile.Read(ref live.Payload);
        slot = live;
        slot.Payload = payload;
        return true;
    }

    private bool _TryGetStringSeries(int fieldId, bool customText, out ValueCacheSeries<string>? series)
    {
        series = null;
        if (!_TryGetSlot(fieldId, out ValueCacheProbeSlot slot))
        {
            return false;
        }

        series = customText ? slot.CustomText : slot.CustomRepresentation;
        return series is not null;
    }

    /// <summary>
    /// Guards every write path: after <see cref="Abandon"/> (Session restart) a stale writer must not keep appending to a
    /// cache that readers were told is evicted. Reads stay allowed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The cache was abandoned.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _ThrowIfAbandoned()
    {
        if (_Abandoned != 0)
        {
            throw new InvalidOperationException("ValueCache was evicted");
        }
    }

    private static void _ValidateCaptureMode(ValueCaptureMode mode)
    {
        if ((uint)mode > (uint)ValueCaptureMode.AllOccurrences)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "CaptureMode must be FirstOccurrence or AllOccurrences.");
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
                string.Format(CultureInfo.InvariantCulture, "Unknown field id {0}.", fieldId.Value),
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
                string.Format(CultureInfo.InvariantCulture, "Unknown index group id {0}.", groupId.Value),
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

    /// <summary>
    /// Whether <paramref name="type"/> is the CLR type that <c>_CreatePayloadSeries</c> stores for
    /// <paramref name="fieldType"/>. This is the gate in front of <c>Unsafe.As</c> in <see cref="TryGetSeries{T}(FieldId, out ValueCacheSeries{T})"/>: it
    /// must list exactly the mapping of <c>_CreatePayloadSeries</c>, and any field type that is missing here (the default
    /// arm) can never be looked up.
    /// </summary>
    private static bool _PayloadTypeMatches(FieldType fieldType, Type type) =>
        fieldType switch
        {
            FieldType.None or FieldType.Bool => type == typeof(byte),
            FieldType.I64 or FieldType.Timestamp => type == typeof(long),
            FieldType.U64 or FieldType.MacAddress or FieldType.Eui64 => type == typeof(ulong),
            FieldType.F64 => type == typeof(double),
            FieldType.IPv4Address => type == typeof(uint),
            FieldType.String => type == typeof(string),
            FieldType.Bytes => type == typeof(byte[]),
            FieldType.IPv6Address => type == typeof(IPv6Address),
            FieldType.Uuid => type == typeof(Uuid),
            _ => false,
        };

    #endregion
}

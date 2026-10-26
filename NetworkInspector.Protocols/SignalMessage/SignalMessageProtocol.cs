// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// One immutable, stateless signal-message protocol instance compiled from JSON.
/// <see cref="IProtocol.Parse"/> appends a lazy protocol container; signals and mux children are
/// built in the container populator from stored payload bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Length contract:</b> <see cref="IProtocol.Parse"/> requires
/// <c>data.Length &gt;= RequiredByteLength</c> (see <see cref="SignalMessageBits"/>),
/// stores and extracts only that prefix, and returns <see cref="RequiredByteLength"/> as
/// consumed. Trailing bytes at the end of the capture frame are shown as
/// <c>packet.unparsed_data</c> by <c>PacketProtocol</c> when the frame protocol
/// reports a consumed count shorter than the frame.
/// <see cref="SignalMessageBits.ExtractRawUnchecked"/> performs no per-signal bounds checks —
/// Debug and Release behave identically.
/// </para>
/// <para>
/// <b>Field tree</b> (sibling of the caller protocol, same as Ethernet / UDP):
/// </para>
/// <code>
/// parent (e.g. same parent as pdu_transport)
/// └── {name}: {ui_name}                 [lazy container]
///     ├── {signal}: physical F64
///     │   ├── {signal}.raw              [optional, signal_message.show_raw]
///     │   └── {signal}.enum             [optional, signal_message.show_enum]
///     └── {mux}                         [optional]
///         ├── {mux}.value
///         └── mux-group signals
/// </code>
/// <para>
/// Signals are children of this container, never of <c>pdu_transport.pdu</c>.
/// Mux-group signals hang under the mux field.
/// </para>
/// <para>
/// <b>Pipeline (inside populator):</b> extract raw <see cref="ulong"/> → physical =
/// <c>raw × factor + offset</c> → optional enum name → CustomText →
/// append signal under the container with <see cref="FieldValue.NewF64"/>.
/// </para>
/// <para>
/// <b>Best effort:</b> compile and registration skip invalid or colliding signals with a
/// warning so siblings still appear. Parse never invokes a null container populator.
/// </para>
/// <para><b>Thread safety:</b> Immutable after <see cref="RegisterFields"/> completes.
/// Designed for single-threaded parse ownership per <see cref="Stack"/>; concurrent
/// <see cref="IProtocol.Parse"/> on the same instance is safe because there is no mutable parse state.</para>
/// </remarks>
internal sealed class SignalMessageProtocol : IProtocol
{
    #region Constants

    /// <summary>
    /// Mux selectors up to this bit length get a dense <c>muxValue → signals</c> table
    /// (256 slots at 8 bits). Wider selectors keep a linear group scan — typical DBC mux
    /// is 8 bits or less, and a 2^16 pointer table would dwarf the group list.
    /// Constant (not configurable): it is a memory/lookup-speed trade-off of the data structure, not a trust-boundary
    /// limit, because the selector width is already bounded to 1..64 bits by the compiler.
    /// </summary>
    private const byte _DenseMuxMaxBitLength = 8;

    /// <summary>
    /// CustomText is precomputed for every raw value when bit length is at most this
    /// (4096 strings at 12 bits). Wider signals format on the materialize path.
    /// Registration pays up to 4096 strings per signal once so that materializing a frame
    /// costs one array index instead of a number-format pass and a string allocation per signal.
    /// The table (4096 references = 32 KiB) also stays below the large-object-heap threshold. Constant, not configurable:
    /// it only bounds a registration-time memory/speed trade-off and does not guard against untrusted input.
    /// </summary>
    private const byte _PrecomputedCustomTextMaxBitLength = 12;

    #endregion

    #region Protocol identity

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public string UiName { get; }

    /// <inheritdoc/>
    public string? Description { get; }

    /// <summary>
    /// Minimum payload length in bytes: the exclusive end byte of the farthest signal
    /// (static, mux selector, and every mux-group signal). Because Parse guarantees at least this many
    /// bytes, extraction needs no per-signal bounds check.
    /// </summary>
    internal int RequiredByteLength { get; }

    /// <summary>Dispatch-table bindings (table name and key) that route payloads to this protocol; wired by the registration type.</summary>
    internal DispatchBinding[] DispatchBindings { get; }

    private readonly bool _ShowRaw;
    private readonly bool _ShowEnum;

    #endregion

    #region Runtime tables

    // The compiled signals arrive without FieldIds because fields can only be registered once the builder
    // and ProtocolId exist. RegisterFields turns the "pending" arrays into the registered runtime arrays
    // below (SignalInfo is an immutable record struct, so the registered copies are made with 'with').
    private SignalInfo[] _PendingStaticSignals;
    private CompiledMuxGroup[] _PendingMuxGroups;
    private SignalInfo? _PendingMuxSignal;

    // Runtime tables are written only by RegisterFields during the single-threaded stack build and are
    // read-only afterwards, so Parse and the populator read them without synchronization.
    private SignalInfo[] _StaticSignals;
    private MuxGroupRuntime[] _MuxGroups;
    /// <summary>
    /// Dense mux lookup when the selector is ≤ <see cref="_DenseMuxMaxBitLength"/> bits.
    /// Index is the raw mux value; a null slot means no group for that value.
    /// </summary>
    private SignalInfo[]?[]? _MuxByValue;
    private SignalInfo _MuxSignal;

    // False when the message has no (usable) mux selector, or when registering the mux fields failed.
    private bool _HasMux;

    // Invalid until registration succeeds for the respective field; Parse checks _ContainerFieldId so that
    // a failed registration yields "not parsed" instead of appending an unregistered field.
    private FieldId _ContainerFieldId = FieldId.Invalid;
    private FieldId _MuxFieldId = FieldId.Invalid;
    private FieldId _MuxValueFieldId = FieldId.Invalid;
    private ProtocolId _ProtocolId = ProtocolId.Invalid;
    private IndexGroupId _IndexGroupId = IndexGroupId.Invalid;

    /// <summary>
    /// Container materializer. The constructor assigns <see cref="_NoOpPopulator"/> so
    /// <see cref="IProtocol.Parse"/> never invokes a null delegate if field registration is incomplete.
    /// </summary>
    private LazyPopulator _ContainerPopulator = _NoOpPopulator;

    #endregion

    #region Construction

    /// <summary>
    /// No-op populator so <see cref="IProtocol.Parse"/> never sees a null delegate when
    /// <see cref="RegisterFields"/> does not finish (best-effort registration).
    /// </summary>
    private static ParseResult _NoOpPopulator(in MutField _) => 0;

    /// <summary>
    /// Creates a message protocol from a compiled config. Field IDs are assigned in
    /// <see cref="RegisterFields"/>. Compiled arrays are treated as empty when null.
    /// </summary>
    /// <param name="compiled">Validated message definition produced by the compiler (layout already checked).</param>
    /// <param name="settings">Compile settings; only the show-raw and show-enum switches are used here.</param>
    /// <exception cref="ArgumentException">Thrown when the compiled message name is null, empty, or whitespace.</exception>
    internal SignalMessageProtocol(CompiledSignalMessage compiled, in SignalMessageCompileSettings settings)
    {
        if (string.IsNullOrWhiteSpace(compiled.Name))
        {
            throw new ArgumentException("Compiled message name is required.", nameof(compiled));
        }

        Name = compiled.Name;
        UiName = compiled.UiName;
        Description = compiled.Description;
        RequiredByteLength = compiled.RequiredByteLength;
        DispatchBindings = compiled.DispatchBindings ?? [];
        _ShowRaw = settings.ShowRaw;
        _ShowEnum = settings.ShowEnum;
        _PendingStaticSignals = compiled.StaticSignals ?? [];
        _PendingMuxGroups = compiled.MuxGroups ?? [];
        _PendingMuxSignal = compiled.MuxSignal;
        _HasMux = compiled.MuxSignal is not null;
        _StaticSignals = [];
        _MuxGroups = [];
        _MuxSignal = default;
    }

    #endregion

    #region Protocol

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Eager/lazy contract (PROTOCOL_GUIDE §9 / §13b): <see cref="IProtocol.Parse"/> only
    /// records presence and appends the lazy message container
    /// (<see cref="MutField.AppendLazyWithCustomText"/> with <see cref="FieldValue.NewBytes"/>).
    /// All signal and mux fields are appended inside <see cref="_PopulateContainerFields"/>.
    /// Optional <c>.raw</c>/<c>.enum</c> children are appended with their parent signal
    /// in that same materialization pass.
    /// </para>
    /// </remarks>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // Best-effort registration: never append with an unregistered container field.
        if (!_ContainerFieldId.IsValid)
        {
            return 0;
        }

        if (data.Length < RequiredByteLength)
        {
            return ParseError.InsufficientDataWithInfo(Name, (ulong)RequiredByteLength, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_IndexGroupId);

        // Store only the bytes extractors need. Slicing a ReadOnlyMemory does not copy: the container
        // references the frame buffer, and the signals are decoded later (only if the field tree is
        // actually materialized) instead of for every parsed frame.
        ReadOnlyMemory<byte> stored = data[..RequiredByteLength];
        parentField.AppendLazyWithCustomText(
            _ContainerFieldId,
            FieldValue.NewBytes(stored),
            UiName,
            _ContainerPopulator);

        return RequiredByteLength;
    }

    #endregion

    #region Lazy Populator

    /// <summary>
    /// Materializes signal and mux children from the bytes stored on the container.
    /// </summary>
    private ParseResult _PopulateContainerFields(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> payload))
        {
            return ParseError.InvalidData(Name, "Container value is not of type Bytes");
        }

        // Parse stored exactly RequiredByteLength bytes, which covers every static, mux and mux-group
        // signal, so the extractors skip per-signal bounds checks. Static signals first, then the
        // mux container: that is the child order shown in the field tree.
        ReadOnlySpan<byte> span = payload.Span;
        _AppendStaticSignals(in container, span);
        _AppendMuxSignals(in container, span);
        return 0;
    }

    /// <summary>
    /// Extracts each always-present signal and appends it under the container.
    /// Inlined into the populator: it is the per-signal loop of the lazy materialization path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _AppendStaticSignals(in MutField container, ReadOnlySpan<byte> span)
    {
        SignalInfo[] staticSignals = _StaticSignals;
        for (int i = 0; i < staticSignals.Length; i++)
        {
            ref readonly SignalInfo signal = ref staticSignals[i];
            ulong raw = SignalMessageBits.ExtractRawUnchecked(span, in signal);
            _AppendSignal(in container, raw, in signal);
        }
    }

    /// <summary>
    /// Reads the mux selector, appends the mux container with <c>.value</c>, then group signals.
    /// Dense tables (selector ≤ 8 bits) are indexed by raw value; wider selectors scan groups.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _AppendMuxSignals(in MutField container, ReadOnlySpan<byte> span)
    {
        if (!_HasMux)
        {
            return;
        }

        // The selector is always appended (even when no group matches) so the tree shows which
        // multiplexer value the frame carried. Its field id is _MuxFieldId, not a SignalFieldId:
        // _MuxSignal is stored with invalid signal ids because it is not a regular signal.
        ref readonly SignalInfo mux = ref _MuxSignal;
        ulong muxValue = SignalMessageBits.ExtractRawUnchecked(span, in mux);
        MutField muxField = container.AppendWithCustomText(
            _MuxFieldId,
            FieldValue.NewU64(muxValue),
            mux.UiName, ": ", muxValue);
        muxField.Append(_MuxValueFieldId, FieldValue.NewU64(muxValue));

        // Only the group whose mux_value equals the selector is decoded: signals of other groups are
        // defined to be present only for their own selector value, so decoding them would show noise.
        SignalInfo[]? groupSignals = _LookupMuxGroup(muxValue);
        if (groupSignals is null)
        {
            return;
        }

        for (int s = 0; s < groupSignals.Length; s++)
        {
            ref readonly SignalInfo signal = ref groupSignals[s];
            ulong raw = SignalMessageBits.ExtractRawUnchecked(span, in signal);
            _AppendSignal(in muxField, raw, in signal);
        }
    }

    /// <summary>
    /// Resolves mux-group signals for <paramref name="muxValue"/>; <see langword="null"/> when unmatched.
    /// Dense table: O(1) index with no comparison loop. Linear scan fallback: for selectors wider than
    /// <see cref="_DenseMuxMaxBitLength"/> bits, where a table indexed by value would be too large.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SignalInfo[]? _LookupMuxGroup(ulong muxValue)
    {
        SignalInfo[]?[]? dense = _MuxByValue;
        if (dense is not null)
        {
            // Defensive: the extracted selector has at most the selector bit length bits, so it is
            // always below dense.Length; the check keeps an out-of-range index impossible by construction.
            if (muxValue >= (ulong)dense.Length)
            {
                return null;
            }

            return dense[(int)muxValue];
        }

        MuxGroupRuntime[] groups = _MuxGroups;
        for (int g = 0; g < groups.Length; g++)
        {
            ref readonly MuxGroupRuntime group = ref groups[g];
            if (group.MuxValue == muxValue)
            {
                return group.Signals;
            }
        }

        return null;
    }

    /// <summary>
    /// Appends a signal field (physical F64 + CustomText) and optional <c>.raw</c>/<c>.enum</c> children.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _AppendSignal(in MutField parent, ulong raw, in SignalInfo signal)
    {
        // Physical value and enum name are resolved up front because both the CustomText and the optional
        // .enum child need the name; the enum lookup is skipped entirely for signals without value_names.
        double phys = SignalMessageBits.ToPhysical(raw, in signal);
        string? enumName = null;
        if (signal.Enums.Kind != SignalEnumKind.None)
        {
            if (signal.Enums.TryGetName(raw, out string? resolved))
            {
                enumName = resolved;
            }
        }

        // CustomText is built only when a field tree stores it or a value cache records it (WantsCustomText);
        // otherwise only the typed F64 value is appended and no text work happens at all.
        MutField signalField;
        if (parent.WantsCustomText(signal.SignalFieldId))
        {
            LazyString text;
            string[]? cached = signal.CustomTextByRaw;
            if (cached is not null)
            {
                // Precomputed table (bit length <= 12): raw < 2^BitLength is guaranteed by the extractor
                // masking, so the index is in range. Reuses one shared string per raw value, no formatting.
                text = new LazyString(cached[(int)raw]);
            }
            else
            {
                // Wide signal: a table over every raw value would be too large, so the text is composed
                // per signal through ZA.Lazy (a LazyString) instead.
                text = _BuildCustomText(in signal, raw, phys, enumName);
            }

            signalField = parent.AppendWithCustomText(
                signal.SignalFieldId,
                FieldValue.NewF64(phys),
                text);
        }
        else
        {
            signalField = parent.Append(signal.SignalFieldId, FieldValue.NewF64(phys));
        }

        // Optional children: their ids are Invalid unless the show_raw / show_enum setting was on and
        // registration succeeded, so a disabled child costs only this check.
        if (signal.RawFieldId.IsValid)
        {
            signalField.Append(signal.RawFieldId, FieldValue.NewU64(raw));
        }

        if (signal.EnumFieldId.IsValid && enumName is not null)
        {
            signalField.Append(signal.EnumFieldId, FieldValue.NewString(enumName));
        }
    }

    #endregion

    #region Custom Text

    /// <summary>
    /// Builds CustomText: <c>{ui_name}: {phys:F}[ {unit}] ({raw})[ [{enum}]]</c>.
    /// Numbers use <see cref="CultureInfo.InvariantCulture"/> via ZeroAlloc's culture-first
    /// <c>ZA.Lazy</c> overload; only physical uses <see cref="Formatted{T}"/> for the <c>F</c> format.
    /// </summary>
    private static LazyString _BuildCustomText(in SignalInfo signal, ulong raw, double phys, string? enumName)
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        Formatted<double> physFormatted = new(phys, "F");
        string unit = signal.Unit;
        string uiName = signal.UiName;

        // One ZA.Lazy call per shape (unit and/or enum present) instead of passing empty strings: the
        // argument list is fixed per call, and omitting absent parts avoids stray separators in the text.
        if (enumName is not null)
        {
            if (unit.Length > 0)
            {
                return ZA.Lazy(culture, uiName, ": ", physFormatted, " ", unit, " (", raw, ") [", enumName, "]");
            }

            return ZA.Lazy(culture, uiName, ": ", physFormatted, " (", raw, ") [", enumName, "]");
        }

        if (unit.Length > 0)
        {
            return ZA.Lazy(culture, uiName, ": ", physFormatted, " ", unit, " (", raw, ")");
        }

        return ZA.Lazy(culture, uiName, ": ", physFormatted, " (", raw, ")");
    }

    /// <summary>
    /// Builds a raw→CustomText table when <see cref="SignalInfo.BitLength"/> is
    /// ≤ <see cref="_PrecomputedCustomTextMaxBitLength"/>. Returns <see langword="null"/> otherwise.
    /// Setup-only; the materialize path indexes the result with no formatting.
    /// </summary>
    private static string[]? _TryBuildCustomTextTable(in SignalInfo signal)
    {
        byte bitLength = signal.BitLength;
        if (bitLength == 0 || bitLength > _PrecomputedCustomTextMaxBitLength)
        {
            return null;
        }

        // bitLength is 1..12 → slotCount is 2..4096 (fits in int, below LOH for the string[] header).
        // The loop below assigns every slot, so skipping the zero-fill of the uninitialized array is safe.
        int slotCount = 1 << bitLength;
        string[] table = GC.AllocateUninitializedArray<string>(slotCount);

        // Formats once per possible raw value, with exactly the same text _AppendSignal would compose at
        // parse time (physical value, unit, raw, enum name), so both paths stay interchangeable.
        for (int raw = 0; raw < slotCount; raw++)
        {
            ulong rawU = (ulong)raw;
            double phys = SignalMessageBits.ToPhysical(rawU, in signal);
            string? enumName = null;
            if (signal.Enums.Kind != SignalEnumKind.None)
            {
                if (signal.Enums.TryGetName(rawU, out string? resolved))
                {
                    enumName = resolved;
                }
            }

            LazyString lazy = _BuildCustomText(in signal, rawU, phys, enumName);
            table[raw] = lazy.AsString;
        }

        return table;
    }

    #endregion

    #region Registration

    /// <summary>
    /// Registers container/signal/mux fields and finalizes <see cref="SignalInfo"/> FieldIds.
    /// Invalid or colliding fields are skipped with a warning; siblings still register.
    /// The container populator is assigned only after the container field succeeds.
    /// </summary>
    /// <param name="builder">Stack builder. Caller guarantees non-null.</param>
    /// <param name="protocolId">Id returned by <see cref="IStackBuilder.RegisterProtocol"/>.</param>
    /// <param name="warnings">Receives per-field skip warnings. Caller guarantees non-null.</param>
    internal void RegisterFields(IStackBuilder builder, ProtocolId protocolId, ICollection<SettingsLoadWarning> warnings)
    {
        _ProtocolId = protocolId;
        _IndexGroupId = builder.GetOrCreateIndexGroup(Name);

        // Container first: without it nothing can be appended, so on failure every table is cleared and
        // _ContainerFieldId stays Invalid, which makes Parse return "not parsed" (the no-op populator stays).
        if (!_TryRegisterField(
                builder,
                protocolId,
                Name,
                UiName,
                FieldType.Bytes,
                "Signal message container",
                warnings,
                out _ContainerFieldId))
        {
            _HasMux = false;
            _StaticSignals = [];
            _PendingStaticSignals = [];
            _PendingMuxGroups = [];
            _PendingMuxSignal = null;
            return;
        }

        // Static signals: a failed signal is dropped, the others stay usable (best effort).
        _StaticSignals = _RegisterSignalArray(builder, protocolId, _PendingStaticSignals, warnings);
        _PendingStaticSignals = [];

        // Mux: the selector needs both its container field and the ".value" child; if either fails the
        // whole mux (including its groups) is disabled, because group signals hang under the mux field.
        if (_HasMux && _PendingMuxSignal is SignalInfo muxCompiled)
        {
            string muxName = muxCompiled.Name;
            bool muxOk = _TryRegisterField(
                builder,
                protocolId,
                muxName,
                muxCompiled.UiName,
                FieldType.U64,
                "Multiplexer",
                warnings,
                out _MuxFieldId);
            if (muxOk)
            {
                muxOk = _TryRegisterField(
                    builder,
                    protocolId,
                    $"{muxName}.value",
                    "Mux Value",
                    FieldType.U64,
                    "Multiplexer selector value",
                    warnings,
                    out _MuxValueFieldId);
            }

            if (!muxOk)
            {
                _HasMux = false;
            }
            else
            {
                // The selector is decoded with the shared extractor, which only needs the bit layout; the
                // signal field ids are cleared because the selector is appended under _MuxFieldId instead.
                _MuxSignal = muxCompiled with
                {
                    SignalFieldId = FieldId.Invalid,
                    RawFieldId = FieldId.Invalid,
                    EnumFieldId = FieldId.Invalid,
                };

                MuxGroupRuntime[] groups = new MuxGroupRuntime[_PendingMuxGroups.Length];
                for (int g = 0; g < _PendingMuxGroups.Length; g++)
                {
                    SignalInfo[] registered = _RegisterSignalArray(
                        builder,
                        protocolId,
                        _PendingMuxGroups[g].Signals,
                        warnings);
                    groups[g] = new MuxGroupRuntime(_PendingMuxGroups[g].MuxValue, registered);
                }

                _MuxGroups = groups;
                _MuxByValue = _TryBuildDenseMuxIndex(_MuxSignal.BitLength, groups);
            }
        }

        _PendingMuxGroups = [];
        _PendingMuxSignal = null;

        // Bind the method group to a delegate once here (it captures 'this') instead of converting it on
        // every Parse call, which would allocate a delegate per frame. Assigned last so Parse can never
        // see a half-registered instance with a real populator.
        _ContainerPopulator = _PopulateContainerFields;
    }

    /// <summary>
    /// Precomputes a dense mux lookup when the selector domain is small enough.
    /// Returns <see langword="null"/> for wider selectors (linear scan in <see cref="_LookupMuxGroup"/>).
    /// </summary>
    private static SignalInfo[]?[]? _TryBuildDenseMuxIndex(byte muxBitLength, MuxGroupRuntime[] groups)
    {
        if (muxBitLength > _DenseMuxMaxBitLength)
        {
            return null;
        }

        // muxBitLength is 1..8 here, so at most 256 slots (2 KiB of references). Slots without a group stay
        // null, which the lookup returns as "no group for this selector value".
        int slotCount = 1 << muxBitLength;
        SignalInfo[]?[] table = new SignalInfo[]?[slotCount];
        for (int g = 0; g < groups.Length; g++)
        {
            // The compiler already rejects mux_value above the selector's maximum raw value and duplicate
            // values; the bound check only protects the array index.
            ulong muxValue = groups[g].MuxValue;
            if (muxValue < (ulong)slotCount)
            {
                table[(int)muxValue] = groups[g].Signals;
            }
        }

        return table;
    }

    /// <summary>
    /// Registers each compiled signal. A failed parent field skips that signal; a failed
    /// optional <c>.raw</c>/<c>.enum</c> child leaves the parent registered without that child.
    /// </summary>
    private SignalInfo[] _RegisterSignalArray(
        IStackBuilder builder,
        ProtocolId protocolId,
        SignalInfo[] compiled,
        ICollection<SettingsLoadWarning> warnings)
    {
        // Each signal registers independently: the parent field is mandatory (skip the signal on failure),
        // the optional .raw / .enum children are best effort (the signal stays, only the child is missing).
        // The raw-to-text table is built here, once, so the parse path never formats small signals.
        List<SignalInfo> result = new(compiled.Length);
        for (int i = 0; i < compiled.Length; i++)
        {
            ref readonly SignalInfo src = ref compiled[i];
            string fieldName = src.Name;
            if (!_TryRegisterField(
                    builder,
                    protocolId,
                    fieldName,
                    src.UiName,
                    FieldType.F64,
                    "Physical signal value (raw × factor + offset)",
                    warnings,
                    out FieldId signalId))
            {
                continue;
            }

            FieldId rawId = FieldId.Invalid;
            FieldId enumId = FieldId.Invalid;
            if (_ShowRaw)
            {
                if (!_TryRegisterField(
                        builder,
                        protocolId,
                        $"{fieldName}.raw",
                        "Raw Value",
                        FieldType.U64,
                        "Raw signal bits",
                        warnings,
                        out rawId))
                {
                    rawId = FieldId.Invalid;
                }
            }

            if (_ShowEnum && src.Enums.Kind != SignalEnumKind.None)
            {
                if (!_TryRegisterField(
                        builder,
                        protocolId,
                        $"{fieldName}.enum",
                        "Enum Value",
                        FieldType.String,
                        "Named enum value",
                        warnings,
                        out enumId))
                {
                    enumId = FieldId.Invalid;
                }
            }

            result.Add(src with
            {
                SignalFieldId = signalId,
                RawFieldId = rawId,
                EnumFieldId = enumId,
                CustomTextByRaw = _TryBuildCustomTextTable(in src),
            });
        }

        return result.ToArray();
    }

    /// <summary>
    /// Registers one field when the name is valid and unused. Collisions and invalid names
    /// produce a warning and return <see langword="false"/> without throwing.
    /// </summary>
    private bool _TryRegisterField(
        IStackBuilder builder,
        ProtocolId protocolId,
        string name,
        string uiName,
        FieldType fieldType,
        string description,
        ICollection<SettingsLoadWarning> warnings,
        out FieldId id)
    {
        // Names come from a user-supplied JSON file (trust boundary). Re-validating here, right before the
        // builder call, and pre-checking for collisions turns bad or duplicate names into per-field
        // warnings instead of a RegistrationException that would abort the whole message.
        id = FieldId.Invalid;
        if (!NameValidation.IsValidName(name))
        {
            _AddFieldWarning(warnings, name, "invalid name.");
            return false;
        }

        if (!NameValidation.IsValidUiName(uiName))
        {
            _AddFieldWarning(warnings, name, "invalid ui_name.");
            return false;
        }

        if (builder.GetFieldId(name) is not null)
        {
            _AddFieldWarning(warnings, name, "name already registered.");
            return false;
        }

        try
        {
            id = builder.RegisterFieldInGroup(
                protocolId,
                name,
                uiName,
                fieldType,
                Name,
                description);
            return true;
        }
        catch (RegistrationException ex)
        {
            _AddFieldWarning(warnings, name, ex.Message);
            return false;
        }
    }

    /// <summary>Appends a per-field registration warning for this message.</summary>
    private void _AddFieldWarning(ICollection<SettingsLoadWarning> warnings, string fieldName, string detail)
    {
        warnings.Add(new SettingsLoadWarning(
            SettingsLoadWarningKind.ExternalConfigUnavailable,
            "signal_message",
            SignalMessageRegistration.ConfigFileSetting,
            $"Skipping field '{fieldName}' in message '{Name}': {detail}"));
    }

    #endregion
}

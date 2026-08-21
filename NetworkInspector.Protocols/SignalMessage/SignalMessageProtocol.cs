// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// One immutable, stateless signal-message protocol instance compiled from JSON.
/// <see cref="Parse"/> appends a lazy protocol container; signals and mux children are
/// built in the container populator from stored payload bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Length contract:</b> <see cref="Parse"/> requires
/// <c>data.Length &gt;= RequiredByteLength</c> (see <see cref="SignalMessageBits"/>),
/// stores and extracts only that prefix, and returns <see cref="RequiredByteLength"/> as
/// consumed. Trailing bytes at the end of the capture frame are shown as
/// <c>packet.unparsed_data</c> by <c>PacketProtocol</c> when the frame protocol
/// reports a consumed count shorter than the frame.
/// <see cref="SignalMessageBits.ExtractRawUnchecked"/> performs no per-signal bounds checks —
/// Debug and Release behave identically.
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
/// <see cref="Parse"/> on the same instance is safe because there is no mutable parse state.</para>
/// </remarks>
internal sealed class SignalMessageProtocol : IProtocol
{
    #region Constants

    /// <summary>
    /// Mux selectors up to this bit length get a dense <c>muxValue → signals</c> table
    /// (256 slots at 8 bits). Wider selectors keep a linear group scan — typical DBC mux
    /// is 8 bits or less, and a 2^16 pointer table would dwarf the group list.
    /// </summary>
    private const byte _DenseMuxMaxBitLength = 8;

    /// <summary>
    /// CustomText is precomputed for every raw value when bit length is at most this
    /// (4096 strings at 12 bits). Wider signals format on the materialize path.
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

    internal int RequiredByteLength { get; }

    internal DispatchBinding[] DispatchBindings { get; }

    private readonly bool _ShowRaw;
    private readonly bool _ShowEnum;

    #endregion

    #region Runtime tables

    private SignalInfo[] _PendingStaticSignals;
    private CompiledMuxGroup[] _PendingMuxGroups;
    private SignalInfo? _PendingMuxSignal;

    private SignalInfo[] _StaticSignals;
    private MuxGroupRuntime[] _MuxGroups;
    /// <summary>
    /// Dense mux lookup when the selector is ≤ <see cref="_DenseMuxMaxBitLength"/> bits.
    /// Index is the raw mux value; a null slot means no group for that value.
    /// </summary>
    private SignalInfo[]?[]? _MuxByValue;
    private SignalInfo _MuxSignal;
    private bool _HasMux;

    private FieldId _ContainerFieldId = FieldId.Invalid;
    private FieldId _MuxFieldId = FieldId.Invalid;
    private FieldId _MuxValueFieldId = FieldId.Invalid;
    private ProtocolId _ProtocolId = ProtocolId.Invalid;
    private IndexGroupId _IndexGroupId = IndexGroupId.Invalid;

    /// <summary>
    /// Container materializer. The constructor assigns <see cref="_NoOpPopulator"/> so
    /// <see cref="Parse"/> never invokes a null delegate if field registration is incomplete.
    /// </summary>
    private LazyPopulator _ContainerPopulator = _NoOpPopulator;

    #endregion

    #region Construction

    /// <summary>
    /// No-op populator so <see cref="Parse"/> never sees a null delegate when
    /// <see cref="RegisterFields"/> does not finish (best-effort registration).
    /// </summary>
    private static ParseResult _NoOpPopulator(in MutField _) => 0;

    /// <summary>
    /// Creates a message protocol from a compiled config. Field IDs are assigned in
    /// <see cref="RegisterFields"/>. Compiled arrays are treated as empty when null.
    /// </summary>
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

    #region IProtocol

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Eager/lazy contract (PROTOCOL_GUIDE §9 / §13b): <see cref="Parse"/> only
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

        // Store only the bytes extractors need.
        ReadOnlyMemory<byte> stored = data[..RequiredByteLength];
        parentField.AppendLazyWithCustomText(
            _ContainerFieldId,
            FieldValue.NewBytes(stored),
            ZA.Lazy(UiName),
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

        // Length was validated in Parse; extractors are bounds-free.
        ReadOnlySpan<byte> span = payload.Span;
        _AppendStaticSignals(in container, span);
        _AppendMuxSignals(in container, span);
        return 0;
    }

    /// <summary>
    /// Extracts each always-present signal and appends it under the container.
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

        ref readonly SignalInfo mux = ref _MuxSignal;
        ulong muxValue = SignalMessageBits.ExtractRawUnchecked(span, in mux);
        MutField muxField = container.AppendWithCustomText(
            _MuxFieldId,
            FieldValue.NewU64(muxValue),
            ZA.Lazy(CultureInfo.InvariantCulture, mux.UiName, ": ", muxValue));
        muxField.Append(_MuxValueFieldId, FieldValue.NewU64(muxValue));

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
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SignalInfo[]? _LookupMuxGroup(ulong muxValue)
    {
        SignalInfo[]?[]? dense = _MuxByValue;
        if (dense is not null)
        {
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
        double phys = SignalMessageBits.ToPhysical(raw, in signal);
        string? enumName = null;
        LazyString text;
        string[]? cached = signal.CustomTextByRaw;
        if (cached is not null)
        {
            // ExtractRawUnchecked yields at most BitLength bits; the table has 1<<BitLength slots.
            text = new LazyString(cached[(int)raw]);
            if (signal.EnumFieldId.IsValid)
            {
                if (signal.Enums.TryGetName(raw, out string? resolved))
                {
                    enumName = resolved;
                }
            }
        }
        else
        {
            if (signal.Enums.Kind != SignalEnumKind.None)
            {
                if (signal.Enums.TryGetName(raw, out string? resolved))
                {
                    enumName = resolved;
                }
            }

            text = _BuildCustomText(in signal, raw, phys, enumName);
        }
        MutField signalField = parent.AppendWithCustomText(
            signal.SignalFieldId,
            FieldValue.NewF64(phys),
            text);

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
        int slotCount = 1 << bitLength;
        string[] table = GC.AllocateUninitializedArray<string>(slotCount);
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

        _StaticSignals = _RegisterSignalArray(builder, protocolId, _PendingStaticSignals, warnings);
        _PendingStaticSignals = [];

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

        // Capture 'this' once during registration so Parse never sees a null delegate.
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

        int slotCount = 1 << muxBitLength;
        SignalInfo[]?[] table = new SignalInfo[]?[slotCount];
        for (int g = 0; g < groups.Length; g++)
        {
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

// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The core-tier builder factory and the one store of all core-tier contributions of the CLI, the host, and the app: stack
/// contributions, plugin settings, source kinds, and export formats. Builders stage a plugin's contributions; the commit of
/// a plugin (after every configure call of every tier returned) checks them against what is published, probes stack
/// contributions on one cumulative probe builder, settings on one probe manager per scope, and every option list on a fresh
/// manager, and then publishes one new immutable snapshot. A plugin whose commit throws publishes nothing.
/// <para>
/// Thread safety: <see cref="Create"/>, the commits, <see cref="Disable"/>, and <see cref="Dispose"/> run on one thread (the
/// loading and application thread). Every read member is thread-safe: it reads one snapshot object with
/// <see cref="Volatile"/>, so a reader never sees a source without the exporter of the same plugin,
/// and publication uses <see cref="Volatile"/> writes of a new object (copy on write: contributions change rarely,
/// reads happen for every request).
/// </para>
/// </summary>
public sealed class PluginRegistry : IPluginBuilderFactory, IDisposable
{
    #region Types

    // A contribution together with the name of the plugin that owns it.
    private readonly record struct Owned<T>(string PluginName, T Item);

    // What one plugin staged while it was configured; handed to the commit.
    private sealed class StagedContributions
    {
        internal List<Action<IStackBuilder>> Stack { get; } = [];

        internal List<(SettingScope Scope, Action<SettingsRegistrar> Register)> Settings { get; } = [];

        internal List<SourceRegistration> Sources { get; } = [];

        internal List<ExporterRegistration<IFrameListener>> FrameExporters { get; } = [];

        internal List<ExporterRegistration<IPacketListener>> PacketExporters { get; } = [];
    }

    #endregion

    #region Fields

    // Read with Volatile.Read, replaced with Volatile.Write; the object itself is immutable.
    private RegistrySnapshot _Current = RegistrySnapshot.Empty;

    // Probe managers: stack settings, application settings, and profile settings of everything committed so far. They only
    // detect conflicts and name contributions; the real managers are filled later by ApplyStackContributions/ApplySettings.
    private SettingsManager _ProbeStackSettings = new();

    private SettingsManager _ProbeApplicationSettings = new();

    private SettingsManager _ProbeProfileSettings = new();

    private StackBuilder? _ProbeBuilder;

    private bool _Disposed;

    #endregion

    #region Lifecycle

    /// <summary>
    /// Creates an empty registry. The settings that the loader itself puts into the application settings are registered in
    /// the application probe up front, so a plugin that tries to register the same name fails at its commit with the
    /// duplicate-name message instead of failing silently later when the real application settings are filled.
    /// </summary>
    public PluginRegistry()
    {
        PluginPathsSetting.Register(_ProbeApplicationSettings);
        PluginTimeoutSettings.Register(_ProbeApplicationSettings);
    }

    #endregion

    #region IPluginBuilderFactory

    /// <inheritdoc/>
    public IPluginBuilder Create(PluginRecordDraft plugin, ContributionRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(recorder);
        ObjectDisposedException.ThrowIf(_Disposed, this);

        // The commit runs after all configure calls of all tiers; the builder seals itself when it runs.
        StagingBuilder builder = new(this, plugin.Info.Name, recorder);
        recorder.OnCommit(builder.Commit);
        return builder;
    }

    /// <summary>The CLI is a core-tier application: it has no tier methods above <c>Configure</c>.</summary>
    public IReadOnlyList<PluginTierStep> TierSteps => [];

    #endregion

    #region Reads

    /// <summary>Enabled source registrations in load order (precomputed per snapshot; no allocation per read).</summary>
    public IReadOnlyList<SourceRegistration> Sources => Volatile.Read(ref _Current).SourceList;

    /// <summary>Enabled frame exporter registrations in load order.</summary>
    public IReadOnlyList<ExporterRegistration<IFrameListener>> FrameExporters => Volatile.Read(ref _Current).FrameExporterList;

    /// <summary>Enabled packet exporter registrations in load order.</summary>
    public IReadOnlyList<ExporterRegistration<IPacketListener>> PacketExporters => Volatile.Read(ref _Current).PacketExporterList;

    /// <summary>True when <paramref name="pluginName"/> was disabled by <see cref="Disable"/> (failed or timed-out start).</summary>
    /// <param name="pluginName">Plugin name, any case.</param>
    public bool IsDisabled(string pluginName)
    {
        ArgumentNullException.ThrowIfNull(pluginName);
        return Volatile.Read(ref _Current).IsDisabled(pluginName);
    }

    /// <summary>Plugin that registered the source <paramref name="kind"/> (any case), also when it is disabled; else <see langword="null"/>.</summary>
    /// <param name="kind">Source kind.</param>
    public string? SourceOwner(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return Volatile.Read(ref _Current).FindSourceOwner(kind);
    }

    /// <summary>Plugin that registered the frame or packet format <paramref name="format"/> (any case), also when disabled; else <see langword="null"/>.</summary>
    /// <param name="format">Export format.</param>
    public string? FormatOwner(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return Volatile.Read(ref _Current).FindFormatOwner(format);
    }

    /// <summary>
    /// Looks up a source kind (any case). Problems: <c>Unknown source kind 'x'. Known kinds: A, B.</c> and, for a plugin that
    /// failed to start, <c>Source kind 'x' is not available: plugin P failed to start.</c> (the disable reason).
    /// </summary>
    /// <param name="kind">Source kind.</param>
    /// <param name="source">The registration; <see langword="null"/> on failure.</param>
    /// <param name="problem">One-line English problem; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="source"/> is set.</returns>
    public bool TryGetSource(string kind, [NotNullWhen(true)] out SourceRegistration? source, [NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(kind);
        RegistrySnapshot snapshot = Volatile.Read(ref _Current);
        source = null;
        if (snapshot.FindSource(kind) is not { } entry)
        {
            string known = snapshot.SourceList.Count == 0
                ? "none"
                : string.Join(", ", snapshot.SourceList.Select(static registration => registration.Kind));
            problem = Invariant($"Unknown source kind '{kind}'. Known kinds: {known}.");
            return false;
        }

        if (snapshot.DisabledReason(entry.PluginName) is { } reason)
        {
            problem = Invariant($"Source kind '{kind}' is not available: {reason}.");
            return false;
        }

        source = entry.Item;
        problem = null;
        return true;
    }

    /// <summary>
    /// Looks up a frame format (any case). A packet format gives <c>Format 'X' writes packets.</c>; a disabled plugin gives
    /// <c>Format 'X' is not available: …</c>.
    /// </summary>
    /// <param name="format">Export format.</param>
    /// <param name="exporter">The registration; <see langword="null"/> on failure.</param>
    /// <param name="problem">One-line English problem; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="exporter"/> is set.</returns>
    public bool TryGetFrameExporter(string format, [NotNullWhen(true)] out ExporterRegistration<IFrameListener>? exporter,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(format);
        RegistrySnapshot snapshot = Volatile.Read(ref _Current);
        exporter = null;
        if (snapshot.FindFrameExporter(format) is { } entry)
        {
            if (snapshot.DisabledReason(entry.PluginName) is { } reason)
            {
                problem = Invariant($"Format '{format}' is not available: {reason}.");
                return false;
            }

            exporter = entry.Item;
            problem = null;
            return true;
        }

        problem = snapshot.FindPacketExporter(format) is not null
            ? Invariant($"Format '{format}' writes packets.")
            : Invariant($"Unknown format '{format}'. Known formats: {snapshot.FormatNames()}.");
        return false;
    }

    /// <summary>
    /// Looks up a packet format (any case). A frame format gives <c>Format 'X' writes frames.</c>; a disabled plugin gives
    /// <c>Format 'X' is not available: …</c>.
    /// </summary>
    /// <param name="format">Export format.</param>
    /// <param name="exporter">The registration; <see langword="null"/> on failure.</param>
    /// <param name="problem">One-line English problem; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="exporter"/> is set.</returns>
    public bool TryGetPacketExporter(string format, [NotNullWhen(true)] out ExporterRegistration<IPacketListener>? exporter,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(format);
        RegistrySnapshot snapshot = Volatile.Read(ref _Current);
        exporter = null;
        if (snapshot.FindPacketExporter(format) is { } entry)
        {
            if (snapshot.DisabledReason(entry.PluginName) is { } reason)
            {
                problem = Invariant($"Format '{format}' is not available: {reason}.");
                return false;
            }

            exporter = entry.Item;
            problem = null;
            return true;
        }

        problem = snapshot.FindFrameExporter(format) is not null
            ? Invariant($"Format '{format}' writes frames.")
            : Invariant($"Unknown format '{format}'. Known formats: {snapshot.FormatNames()}.");
        return false;
    }

    #endregion

    #region Applying contributions

    /// <summary>
    /// Runs every enabled stack contribution in load order; a throwing contribution is reported and skipped (what it
    /// registered before it threw may remain in that stack). <paramref name="afterEach"/> is called with the plugin name after
    /// each enabled plugin's contribution ran or failed (a probe build uses it to attribute what was registered).
    /// </summary>
    /// <param name="builder">The builder of the real stack.</param>
    /// <param name="afterEach">Optional callback after each plugin.</param>
    /// <returns>The failures; empty when every contribution ran.</returns>
    public IReadOnlyList<PluginContributionFailure> ApplyStackContributions(IStackBuilder builder, Action<string>? afterEach = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RegistrySnapshot snapshot = Volatile.Read(ref _Current);
        List<PluginContributionFailure>? failures = null;
        foreach (Owned<Action<IStackBuilder>> contribution in snapshot.StackContributions)
        {
            if (snapshot.IsDisabled(contribution.PluginName))
            {
                continue;
            }

            try
            {
                contribution.Item(builder);
            }
            catch (Exception ex) when (_IsContainable(ex))
            {
                // Plugin code: one failing contribution must not stop the others or the stack build.
                (failures ??= []).Add(new PluginContributionFailure(contribution.PluginName, PluginActivator.Describe(ex), ex));
            }

            afterEach?.Invoke(contribution.PluginName);
        }

        return failures is null
            ? []
            : failures;
    }

    /// <summary>
    /// Registers every committed plugin's settings of <paramref name="scope"/> on <paramref name="manager"/>. Settings of
    /// disabled plugins are registered too, so persisted values stay reachable; only sources, exporters, and stack
    /// contributions of a disabled plugin are refused. Failures are reported, not thrown.
    /// </summary>
    /// <param name="scope">Application or profile.</param>
    /// <param name="manager">The manager that receives the settings.</param>
    /// <returns>The failures; empty when every registration ran.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scope"/> is Session or not defined.</exception>
    public IReadOnlyList<PluginContributionFailure> ApplySettings(SettingScope scope, SettingsManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (scope is not (SettingScope.Application or SettingScope.Profile))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Plugin settings are application or profile settings.");
        }

        List<PluginContributionFailure>? failures = null;
        foreach (Owned<(SettingScope Scope, Action<SettingsRegistrar> Register)> registration in Volatile.Read(ref _Current).Settings)
        {
            if (registration.Item.Scope != scope)
            {
                continue;
            }

            try
            {
                registration.Item.Register(new SettingsRegistrar(manager));
            }
            catch (Exception ex) when (_IsContainable(ex))
            {
                (failures ??= []).Add(new PluginContributionFailure(registration.PluginName, PluginActivator.Describe(ex), ex));
            }
        }

        return failures is null
            ? []
            : failures;
    }

    #endregion

    #region Disable and dispose

    /// <summary>
    /// Refuses the sources, exporters, and stack contributions of <paramref name="pluginName"/> from now on, with a reason that
    /// appears in the refusal texts. Idempotent: the first reason stays.
    /// </summary>
    /// <param name="pluginName">Plugin name, any case.</param>
    /// <param name="reason">Short reason such as <c>plugin P failed to start</c>.</param>
    public void Disable(string pluginName, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentNullException.ThrowIfNull(reason);
        Volatile.Write(ref _Current, Volatile.Read(ref _Current).WithDisabled(pluginName, reason));
    }

    /// <summary>Disposes the three probe managers once; later calls do nothing.</summary>
    public void Dispose()
    {
        if (_Disposed)
        {
            return;
        }

        _Disposed = true;
        _ProbeStackSettings.Dispose();
        _ProbeApplicationSettings.Dispose();
        _ProbeProfileSettings.Dispose();
    }

    #endregion

    #region Commit

    // Commit of one plugin, called through the recorder after every configure call of every tier returned. Any throw leaves
    // the published snapshot untouched and the probes as they were before this plugin, so the plugin contributes nothing and
    // leaves no trace for the next one.
    private void _Commit(string pluginName, ContributionRecorder recorder, StagedContributions staged)
    {
        RegistrySnapshot current = Volatile.Read(ref _Current);
        try
        {
            _CommitChecked(pluginName, recorder, staged, current);
        }
        catch
        {
            // The probes already hold what this plugin registered before it failed; rebuild them from the plugins that did
            // commit.
            _RebuildProbes(current);
            throw;
        }
    }

    // Replays the published contributions on fresh probes. They passed once, so a replay that throws is a plugin whose
    // contribution is not repeatable; the probe then stays as far as it got and the next commit reports its own conflicts.
    private void _RebuildProbes(RegistrySnapshot published)
    {
        _ProbeStackSettings.Dispose();
        _ProbeApplicationSettings.Dispose();
        _ProbeProfileSettings.Dispose();
        _ProbeStackSettings = new();
        _ProbeApplicationSettings = new();
        _ProbeProfileSettings = new();
        PluginPathsSetting.Register(_ProbeApplicationSettings);
        PluginTimeoutSettings.Register(_ProbeApplicationSettings);
        _ProbeBuilder = null;
        if (published.StackContributions.Length > 0)
        {
            FrameInterfaceRegistry probeFrames = new();
            _ProbeBuilder = new StackBuilder(_ProbeStackSettings, probeFrames);
            foreach (Owned<Action<IStackBuilder>> contribution in published.StackContributions)
            {
                try
                {
                    contribution.Item(_ProbeBuilder);
                }
                catch (Exception ex) when (_IsContainable(ex))
                {
                    // Passed at commit time; see the comment of this method.
                }
            }
        }

        foreach (Owned<(SettingScope Scope, Action<SettingsRegistrar> Register)> registration in published.Settings)
        {
            SettingsManager probe = registration.Item.Scope == SettingScope.Application
                ? _ProbeApplicationSettings
                : _ProbeProfileSettings;
            try
            {
                registration.Item.Register(new SettingsRegistrar(probe));
            }
            catch (Exception ex) when (_IsContainable(ex))
            {
                // Passed at commit time; see the comment of this method.
            }
        }
    }

    private void _CommitChecked(string pluginName, ContributionRecorder recorder, StagedContributions staged, RegistrySnapshot current)
    {
        // 1. Duplicates against everything published, also of disabled plugins: refused before anything runs. Formats share
        //    one name space across frame and packet exporters.
        foreach (SourceRegistration source in staged.Sources)
        {
            if (current.FindSourceOwner(source.Kind) is { } owner)
            {
                throw new InvalidOperationException(Invariant($"Source kind '{source.Kind}' is already registered by plugin '{owner}'."));
            }
        }

        foreach (ExporterRegistration<IFrameListener> exporter in staged.FrameExporters)
        {
            _ThrowIfFormatTaken(current, exporter.Format);
        }

        foreach (ExporterRegistration<IPacketListener> exporter in staged.PacketExporters)
        {
            _ThrowIfFormatTaken(current, exporter.Format);
        }

        // 2. Probes. Stack contributions run on the cumulative probe builder (earlier plugins, for example the standard
        //    protocols, are registered there already) and are recorded per kind; settings run on the probe manager of their
        //    scope (duplicate names across plugins fail here); every option list runs once on a fresh manager.
        if (staged.Stack.Count > 0)
        {
            FrameInterfaceRegistry probeFrames = new();
            _ProbeBuilder ??= new StackBuilder(_ProbeStackSettings, probeFrames);
            RecordingStackBuilder recording = new(_ProbeBuilder, recorder);
            foreach (Action<IStackBuilder> contribution in staged.Stack)
            {
                contribution(recording);
            }

            recording.RecordNewSettings();
        }

        foreach ((SettingScope scope, Action<SettingsRegistrar> register) in staged.Settings)
        {
            SettingsManager probe = scope == SettingScope.Application
                ? _ProbeApplicationSettings
                : _ProbeProfileSettings;
            HashSet<string> before = [.. probe.ReadOnly.AllSettings.Select(static setting => setting.Name)];
            register(new SettingsRegistrar(probe));
            foreach (ReadOnlySettingView setting in probe.ReadOnly.AllSettings)
            {
                if (!before.Contains(setting.Name))
                {
                    recorder.Record(ContributionKind.Setting, setting.Name, setting.Description ?? setting.UiName);
                }
            }
        }

        foreach (SourceRegistration source in staged.Sources)
        {
            using SettingsManager probe = RegistrationOptions.CreateDefaults(source.RegisterOptions);
        }

        foreach (ExporterRegistration<IFrameListener> exporter in staged.FrameExporters)
        {
            using SettingsManager probe = RegistrationOptions.CreateDefaults(exporter.RegisterOptions);
        }

        foreach (ExporterRegistration<IPacketListener> exporter in staged.PacketExporters)
        {
            using SettingsManager probe = RegistrationOptions.CreateDefaults(exporter.RegisterOptions);
        }

        // 3. Name sources and exporters, then publish one new snapshot (readers see the old or the new one, never a mix).
        foreach (SourceRegistration source in staged.Sources)
        {
            recorder.Record(ContributionKind.Source, source.Kind, source.Description);
        }

        foreach (ExporterRegistration<IFrameListener> exporter in staged.FrameExporters)
        {
            recorder.Record(ContributionKind.Exporter, exporter.Format, exporter.Description);
        }

        foreach (ExporterRegistration<IPacketListener> exporter in staged.PacketExporters)
        {
            recorder.Record(ContributionKind.Exporter, exporter.Format, exporter.Description);
        }

        Volatile.Write(ref _Current, current.Add(pluginName, staged));
    }

    private static void _ThrowIfFormatTaken(RegistrySnapshot snapshot, string format)
    {
        if (snapshot.FindFormatOwner(format) is { } owner)
        {
            throw new InvalidOperationException(Invariant($"Format '{format}' is already registered by plugin '{owner}'."));
        }
    }

    // Plugin code may throw anything; only conditions that nothing can recover from are not contained.
    private static bool _IsContainable(Exception exception) => exception is not OutOfMemoryException;

    #endregion

    #region Staging builder

    // The builder of one plugin: validates every registration when it is made (so the plugin fails inside Configure), stages
    // it, and publishes it through the registry's commit. Single-threaded; sealed by the commit.
    private sealed class StagingBuilder : IPluginBuilder
    {
        private readonly PluginRegistry _Registry;

        private readonly ContributionRecorder _Recorder;

        private readonly StagedContributions _Staged = new();

        private bool _Sealed;

        internal StagingBuilder(PluginRegistry registry, string pluginName, ContributionRecorder recorder)
        {
            _Registry = registry;
            PluginName = pluginName;
            _Recorder = recorder;
        }

        public string PluginName { get; }

        public void AddStackContribution(Action<IStackBuilder> register)
        {
            ArgumentNullException.ThrowIfNull(register);
            _ThrowIfSealed();
            _Staged.Stack.Add(register);
        }

        public void AddSettings(SettingScope scope, Action<SettingsRegistrar> register)
        {
            ArgumentNullException.ThrowIfNull(register);
            _ThrowIfSealed();
            if (scope is not (SettingScope.Application or SettingScope.Profile))
            {
                throw new ArgumentOutOfRangeException(nameof(scope), scope,
                    "Plugin settings are application or profile settings; session settings belong to the host.");
            }

            _Staged.Settings.Add((scope, register));
        }

        public void AddSource(SourceRegistration source)
        {
            ArgumentNullException.ThrowIfNull(source);
            _ThrowIfSealed();
            _ValidateName(source.Kind, "Source kind");
            _ValidateText(source.UiName, source.Description, "Source kind", source.Kind);
            ArgumentNullException.ThrowIfNull(source.FileExtensions);
            foreach (string extension in source.FileExtensions)
            {
                if (extension is null || !extension.StartsWith('.'))
                {
                    throw new ArgumentException(Invariant($"File extension '{extension}' of source kind '{source.Kind}' must start with '.'."));
                }
            }

            ArgumentNullException.ThrowIfNull(source.RegisterOptions);
            ArgumentNullException.ThrowIfNull(source.Create);
            foreach (SourceRegistration other in _Staged.Sources)
            {
                if (string.Equals(other.Kind, source.Kind, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(Invariant($"Source kind '{source.Kind}' is registered twice by this plugin."));
                }
            }

            _Staged.Sources.Add(source);
        }

        public void AddFrameExporter(ExporterRegistration<IFrameListener> exporter)
        {
            ArgumentNullException.ThrowIfNull(exporter);
            _ThrowIfSealed();
            _ValidateExporter(exporter.Format, exporter.UiName, exporter.Description, exporter.FileExtension, exporter.RegisterOptions,
                exporter.Create);
            _Staged.FrameExporters.Add(exporter);
        }

        public void AddPacketExporter(ExporterRegistration<IPacketListener> exporter)
        {
            ArgumentNullException.ThrowIfNull(exporter);
            _ThrowIfSealed();
            _ValidateExporter(exporter.Format, exporter.UiName, exporter.Description, exporter.FileExtension, exporter.RegisterOptions,
                exporter.Create);
            _Staged.PacketExporters.Add(exporter);
        }

        // Called by the recorder after every configure call returned; seals the builder first so that a plugin that kept the
        // builder cannot add contributions that were never probed.
        internal void Commit()
        {
            _Sealed = true;
            _Registry._Commit(PluginName, _Recorder, _Staged);
        }

        private void _ThrowIfSealed()
        {
            if (_Sealed)
            {
                throw new InvalidOperationException("The plugin builder can only be used while the plugin is configured.");
            }
        }

        private static void _ValidateName(string name, string what)
        {
            if (!PluginInfoRules.IsValidRegistrationName(name))
            {
                throw new ArgumentException(
                    Invariant($"{what} '{name}' is not valid; use 1–{PluginInfoRules.MaxNameLength} characters: a letter, then letters, digits, '_', '.', or '-'."));
            }
        }

        private static void _ValidateText(string uiName, string description, string what, string name)
        {
            if (string.IsNullOrWhiteSpace(uiName))
            {
                throw new ArgumentException(Invariant($"{what} '{name}' needs a UI name."));
            }

            ArgumentNullException.ThrowIfNull(description);
        }

        private void _ValidateExporter(string format, string uiName, string description, string fileExtension, Action<SettingsRegistrar> registerOptions,
            Delegate create)
        {
            _ValidateName(format, "Format");
            _ValidateText(uiName, description, "Format", format);
            ArgumentNullException.ThrowIfNull(fileExtension);
            if (fileExtension.Length != 0 && !fileExtension.StartsWith('.'))
            {
                throw new ArgumentException(Invariant($"File extension '{fileExtension}' of format '{format}' must be empty or start with '.'."));
            }

            ArgumentNullException.ThrowIfNull(registerOptions);
            ArgumentNullException.ThrowIfNull(create);

            // Formats share one name space across frame and packet exporters.
            foreach (string staged in _Staged.FrameExporters.Select(static e => e.Format).Concat(_Staged.PacketExporters.Select(static e => e.Format)))
            {
                if (string.Equals(staged, format, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(Invariant($"Format '{format}' is registered twice by this plugin."));
                }
            }
        }
    }

    #endregion

    #region Snapshot

    // The published state: immutable, so readers need no lock. Disabled plugins keep their entries (the owner lookups and the
    // refusal texts need them); the precomputed enabled lists are what the public list properties return.
    private sealed class RegistrySnapshot
    {
        internal static readonly RegistrySnapshot Empty = new([], [], [], [], [], FrozenDictionary<string, string>.Empty);

        private readonly ImmutableArray<Owned<SourceRegistration>> _AllSources;

        private readonly ImmutableArray<Owned<ExporterRegistration<IFrameListener>>> _AllFrameExporters;

        private readonly ImmutableArray<Owned<ExporterRegistration<IPacketListener>>> _AllPacketExporters;

        private readonly FrozenDictionary<string, string> _Disabled;

        private RegistrySnapshot(ImmutableArray<Owned<Action<IStackBuilder>>> stackContributions,
            ImmutableArray<Owned<SourceRegistration>> sources,
            ImmutableArray<Owned<ExporterRegistration<IFrameListener>>> frameExporters,
            ImmutableArray<Owned<ExporterRegistration<IPacketListener>>> packetExporters,
            ImmutableArray<Owned<(SettingScope Scope, Action<SettingsRegistrar> Register)>> settings,
            FrozenDictionary<string, string> disabled)
        {
            StackContributions = stackContributions;
            _AllSources = sources;
            _AllFrameExporters = frameExporters;
            _AllPacketExporters = packetExporters;
            Settings = settings;
            _Disabled = disabled;

            // Enabled lists are computed once per snapshot, so reads allocate nothing.
            SourceList = [.. sources.Where(owned => !disabled.ContainsKey(owned.PluginName)).Select(static owned => owned.Item)];
            FrameExporterList = [.. frameExporters.Where(owned => !disabled.ContainsKey(owned.PluginName)).Select(static owned => owned.Item)];
            PacketExporterList = [.. packetExporters.Where(owned => !disabled.ContainsKey(owned.PluginName)).Select(static owned => owned.Item)];
        }

        internal ImmutableArray<Owned<Action<IStackBuilder>>> StackContributions { get; }

        internal ImmutableArray<Owned<(SettingScope Scope, Action<SettingsRegistrar> Register)>> Settings { get; }

        internal IReadOnlyList<SourceRegistration> SourceList { get; }

        internal IReadOnlyList<ExporterRegistration<IFrameListener>> FrameExporterList { get; }

        internal IReadOnlyList<ExporterRegistration<IPacketListener>> PacketExporterList { get; }

        internal RegistrySnapshot Add(string pluginName, StagedContributions staged) => new(
            StackContributions.AddRange(staged.Stack.Select(item => new Owned<Action<IStackBuilder>>(pluginName, item))),
            _AllSources.AddRange(staged.Sources.Select(item => new Owned<SourceRegistration>(pluginName, item))),
            _AllFrameExporters.AddRange(staged.FrameExporters.Select(item => new Owned<ExporterRegistration<IFrameListener>>(pluginName, item))),
            _AllPacketExporters.AddRange(staged.PacketExporters.Select(item => new Owned<ExporterRegistration<IPacketListener>>(pluginName, item))),
            Settings.AddRange(staged.Settings.Select(item => new Owned<(SettingScope Scope, Action<SettingsRegistrar> Register)>(pluginName, item))),
            _Disabled);

        internal RegistrySnapshot WithDisabled(string pluginName, string reason)
        {
            if (_Disabled.ContainsKey(pluginName))
            {
                return this;
            }

            Dictionary<string, string> disabled = new(_Disabled, StringComparer.OrdinalIgnoreCase)
            {
                [pluginName] = reason,
            };
            return new RegistrySnapshot(StackContributions, _AllSources, _AllFrameExporters, _AllPacketExporters, Settings,
                disabled.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
        }

        internal bool IsDisabled(string pluginName) => _Disabled.ContainsKey(pluginName);

        internal string? DisabledReason(string pluginName) =>
            _Disabled.TryGetValue(pluginName, out string? reason)
                ? reason
                : null;

        internal Owned<SourceRegistration>? FindSource(string kind)
        {
            foreach (Owned<SourceRegistration> owned in _AllSources)
            {
                if (string.Equals(owned.Item.Kind, kind, StringComparison.OrdinalIgnoreCase))
                {
                    return owned;
                }
            }

            return null;
        }

        internal Owned<ExporterRegistration<IFrameListener>>? FindFrameExporter(string format)
        {
            foreach (Owned<ExporterRegistration<IFrameListener>> owned in _AllFrameExporters)
            {
                if (string.Equals(owned.Item.Format, format, StringComparison.OrdinalIgnoreCase))
                {
                    return owned;
                }
            }

            return null;
        }

        internal Owned<ExporterRegistration<IPacketListener>>? FindPacketExporter(string format)
        {
            foreach (Owned<ExporterRegistration<IPacketListener>> owned in _AllPacketExporters)
            {
                if (string.Equals(owned.Item.Format, format, StringComparison.OrdinalIgnoreCase))
                {
                    return owned;
                }
            }

            return null;
        }

        internal string? FindSourceOwner(string kind) => FindSource(kind)?.PluginName;

        internal string? FindFormatOwner(string format)
        {
            string? frameOwner = FindFrameExporter(format)?.PluginName;
            return frameOwner is null
                ? FindPacketExporter(format)?.PluginName
                : frameOwner;
        }

        // Enabled formats for the "Known formats" text.
        internal string FormatNames()
        {
            IEnumerable<string> names = FrameExporterList.Select(static e => e.Format).Concat(PacketExporterList.Select(static e => e.Format));
            string joined = string.Join(", ", names);
            return joined.Length == 0
                ? "none"
                : joined;
        }
    }

    #endregion
}

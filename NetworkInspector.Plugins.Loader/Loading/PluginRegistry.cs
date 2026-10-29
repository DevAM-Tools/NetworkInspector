// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The core-tier builder factory and the one store of all core-tier contributions of an application: stack
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

    /// <summary>
    /// A contribution together with the name of the plugin that owns it. The owner is needed for the disabled check, for the
    /// duplicate messages, and to attribute a failure of the contribution to its plugin.
    /// </summary>
    /// <typeparam name="T">Type of the contribution.</typeparam>
    /// <param name="PluginName">Name of the owning plugin.</param>
    /// <param name="Item">The contribution.</param>
    private readonly record struct Owned<T>(string PluginName, T Item);

    /// <summary>
    /// What one plugin staged while it was configured; handed to the commit. The lists are filled on the loading thread by
    /// the staging builder and are not read by anyone else before the commit, so they need no synchronization.
    /// </summary>
    private sealed class StagedContributions
    {
        /// <summary>Stack contributions, in registration order.</summary>
        internal List<Action<IStackBuilder>> Stack { get; } = [];

        /// <summary>Settings registrations with their scope, in registration order.</summary>
        internal List<(SettingScope Scope, Action<SettingsRegistrar> Register)> Settings { get; } = [];

        /// <summary>Source registrations.</summary>
        internal List<SourceRegistration> Sources { get; } = [];

        /// <summary>Frame exporter registrations.</summary>
        internal List<ExporterRegistration<IFrameListener>> FrameExporters { get; } = [];

        /// <summary>Packet exporter registrations.</summary>
        internal List<ExporterRegistration<IPacketListener>> PacketExporters { get; } = [];
    }

    #endregion

    #region Fields

    // Read with Volatile.Read, replaced with Volatile.Write; the object itself is immutable. A reader that holds an older
    // snapshot keeps a consistent view; the single writer (loading thread) never mutates a published snapshot.
    private RegistrySnapshot _Current = RegistrySnapshot.Empty;

    // Probe managers: stack settings, application settings, and profile settings of everything committed so far. They only
    // detect conflicts and name contributions; the real managers are filled later by ApplyStackContributions/ApplySettings.
    // Only the loading thread touches the probes (commits, Dispose), so they need no synchronization.
    private SettingsManager _ProbeStackSettings = new();

    private SettingsManager _ProbeApplicationSettings = new();

    private SettingsManager _ProbeProfileSettings = new();

    // The cumulative probe stack: every committed plugin's stack contributions ran on it, so a later plugin that registers
    // a protocol or field name twice fails at its commit. Created lazily by the first plugin that contributes to the stack.
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
        // The same two registrations are repeated in _RebuildProbes; both places must register what the application registers.
        PluginPathsSetting.Register(_ProbeApplicationSettings);
        PluginTimeoutSettings.Register(_ProbeApplicationSettings);
    }

    #endregion

    #region IPluginBuilderFactory

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The registry was disposed.</exception>
    public IPluginBuilder Create(PluginRecordDraft plugin, ContributionRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(recorder);
        ObjectDisposedException.ThrowIf(_Disposed, this);

        // The commit runs after all configure calls of all tiers; the builder seals itself when it runs. Registering it with the
        // recorder (and not calling it from the loader) is what lets a failed plugin publish nothing: Discard drops the action.
        // The recorder runs it, together with any further actions that the application's own builders register.
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

    /// <summary>
    /// Plugin that registered the frame or packet format <paramref name="format"/> (any case), also when disabled; else
    /// <see langword="null"/>.
    /// </summary>
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
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<PluginContributionFailure> ApplyStackContributions(IStackBuilder builder, Action<string>? afterEach = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // One snapshot for the whole loop: a plugin disabled or committed meanwhile does not change the list under iteration.
        // The failure list is created on the first failure only, so the normal case (no failure) allocates nothing here.
        RegistrySnapshot snapshot = Volatile.Read(ref _Current);
        List<PluginContributionFailure>? failures = null;
        foreach (Owned<Action<IStackBuilder>> contribution in snapshot.StackContributions)
        {
            // A plugin whose start failed contributes nothing to stacks built from now on.
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
                // Plugin code: one failing contribution must not stop the others or the stack build. The core builder has no
                // rollback, so what the contribution registered before it threw stays in this stack (documented above).
                (failures ??= []).Add(new PluginContributionFailure(contribution.PluginName, PluginActivator.Describe(ex), ex));
            }

            // Called after a success and after a failure alike, so an observer can attribute whatever the contribution
            // registered up to the point where it ended. The callback is the application's own code, not a plugin's.
            afterEach?.Invoke(contribution.PluginName);
        }

        return failures is null
            ? []
            : failures;
    }

    /// <summary>
    /// Registers every committed plugin's settings of <paramref name="scope"/> on <paramref name="manager"/>. Settings of
    /// disabled plugins are registered too, so persisted values stay reachable; only sources, exporters, and stack
    /// contributions of a disabled plugin are refused. Failures are reported, not thrown; what a failing registration set up
    /// before it threw stays in <paramref name="manager"/> (a manager cannot unregister a setting).
    /// </summary>
    /// <param name="scope">Application or profile.</param>
    /// <param name="manager">The manager that receives the settings.</param>
    /// <returns>The failures; empty when every registration ran.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manager"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scope"/> is Session or not defined.</exception>
    public IReadOnlyList<PluginContributionFailure> ApplySettings(SettingScope scope, SettingsManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        // Session settings belong to the host (a plugin cannot even stage them), so only the two scopes of the loader pass.
        if (scope is not (SettingScope.Application or SettingScope.Profile))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Plugin settings are application or profile settings.");
        }

        // Disabled plugins are not skipped on purpose: their settings stay registered so that values stored for them stay
        // attached to a registered setting (reachable) instead of turning into orphaned values.
        List<PluginContributionFailure>? failures = null;
        foreach (Owned<(SettingScope Scope, Action<SettingsRegistrar> Register)> registration in Volatile.Read(ref _Current).Settings)
        {
            if (registration.Item.Scope != scope)
            {
                continue;
            }

            try
            {
                // A new registrar per call: the staged action must create new Setting instances each time, because a setting
                // binds to the first manager that registers it.
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
    /// <exception cref="ArgumentException"><paramref name="pluginName"/> is <see langword="null"/>, empty, or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is <see langword="null"/>.</exception>
    public void Disable(string pluginName, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentNullException.ThrowIfNull(reason);

        // Read-copy-publish without a compare-exchange: Disable has one caller thread by contract (the class summary), so no
        // concurrent writer can lose this update. Readers see the old or the new snapshot, never a mix.
        Volatile.Write(ref _Current, Volatile.Read(ref _Current).WithDisabled(pluginName, reason));
    }

    /// <summary>Disposes the three probe managers once; later calls do nothing. Not thread-safe (loading thread only).</summary>
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

    /// <summary>
    /// Commit of one plugin, called through the recorder after every configure call of every tier returned. Any throw leaves
    /// the published snapshot untouched and the probes as they were before this plugin, so the plugin contributes nothing and
    /// leaves no trace for the next one.
    /// </summary>
    /// <param name="pluginName">Name of the committing plugin.</param>
    /// <param name="recorder">The plugin's recorder, which receives the names of the contributions.</param>
    /// <param name="staged">What the plugin staged.</param>
    private void _Commit(string pluginName, ContributionRecorder recorder, StagedContributions staged)
    {
        // The snapshot is read once: it is both the base of the new snapshot and the state the probes are rebuilt from.
        RegistrySnapshot current = Volatile.Read(ref _Current);
        try
        {
            _CommitChecked(pluginName, recorder, staged, current);
        }
        catch
        {
            // The probes already hold what this plugin registered before it failed; rebuild them from the plugins that did
            // commit. A bare catch is intended: whatever the failure (a plugin's exception, a duplicate name), the probes must be
            // restored before the exception continues to the loader, which marks the plugin as failed.
            _RebuildProbes(current);
            throw;
        }
    }

    /// <summary>
    /// Replays the published contributions on fresh probes. They passed once, so a replay that throws is a plugin whose
    /// contribution is not repeatable; the probe then stays as far as it got and the next commit reports its own conflicts.
    /// </summary>
    /// <remarks>
    /// Rebuilding instead of rolling back: the probe builder and the probe managers only grow, they have no way to remove
    /// the registrations of one plugin. The replay costs the registrations of all committed plugins, but only on the rare
    /// failing commit.
    /// </remarks>
    /// <param name="published">The snapshot of the plugins that did commit.</param>
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

    /// <summary>
    /// The checks and the publication of one plugin's commit. The only write to the published snapshot is the last statement,
    /// so every earlier throw leaves the published state as it was (the caller rebuilds the probes).
    /// </summary>
    /// <param name="pluginName">Name of the committing plugin.</param>
    /// <param name="recorder">The plugin's recorder.</param>
    /// <param name="staged">What the plugin staged.</param>
    /// <param name="current">The snapshot published before this commit.</param>
    private void _CommitChecked(string pluginName, ContributionRecorder recorder, StagedContributions staged, RegistrySnapshot current)
    {
        // 1. Duplicates against everything published, also of disabled plugins: refused before anything runs. Formats share
        //    one name space across frame and packet exporters. A disabled plugin still owns its names (the refusal
        //    texts and the owner lookups rely on that). Duplicates inside one plugin were already refused by the staging
        //    builder.
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
        //    scope (duplicate names across plugins fail here); every option list runs once on a fresh manager. The point is to
        //    surface every conflict and every throwing registration now, as a failed plugin, instead of at every later stack
        //    build or settings load. This is the first of many runs of the callbacks (each stack build runs them again), so a
        //    contribution must be repeatable.
        if (staged.Stack.Count > 0)
        {
            // The frame registry is only needed when the probe builder is created; later commits reuse the existing builder, so the
            // registry is created inside this branch and not on every commit (it would be garbage that is never used).
            if (_ProbeBuilder is null)
            {
                FrameInterfaceRegistry probeFrames = new();
                _ProbeBuilder = new StackBuilder(_ProbeStackSettings, probeFrames);
            }

            RecordingStackBuilder recording = new(_ProbeBuilder, recorder);
            foreach (Action<IStackBuilder> contribution in staged.Stack)
            {
                contribution(recording);
            }

            // Settings registered through the registrar are found by comparing names before and after (see the wrapper).
            recording.RecordNewSettings();
        }

        // Plugin settings are probed on the manager of their scope, and new names are found by comparing the names before and
        // after, because the registrar the plugin gets is a plain Core registrar.
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

        // Option lists are probed on a throwaway manager each: they run again for every source open and export, and a
        // throwing or conflicting option list must fail the plugin at load time rather than at the first use. The manager is
        // disposed at once; only the fact that the action ran without throwing matters.
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

        // 3. Name sources and exporters, then publish one new snapshot (readers see the old or the new one, never a mix). The
        //    names go to the recorder first; if the plugin fails after this point the recorder is discarded, so the entries
        //    never reach a record. The Volatile.Write is the publication: everything the snapshot refers to is complete and was
        //    written before the reference is, so a reader that sees the new snapshot sees all of it.
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

    /// <summary>Throws when a published plugin (enabled or disabled) already owns the export format, frame or packet alike.</summary>
    /// <param name="snapshot">The published snapshot.</param>
    /// <param name="format">The format name of the staged exporter.</param>
    /// <exception cref="InvalidOperationException">The format is taken; the message names the owning plugin.</exception>
    private static void _ThrowIfFormatTaken(RegistrySnapshot snapshot, string format)
    {
        if (snapshot.FindFormatOwner(format) is { } owner)
        {
            throw new InvalidOperationException(Invariant($"Format '{format}' is already registered by plugin '{owner}'."));
        }
    }

    /// <summary>
    /// Plugin code may throw anything; only conditions that nothing can recover from are not contained. This is the single
    /// place that decides it, so the containing catch blocks of this class agree.
    /// </summary>
    /// <param name="exception">The exception thrown by plugin code.</param>
    /// <returns><see langword="true"/> when the exception becomes a failure record instead of propagating.</returns>
    private static bool _IsContainable(Exception exception) => exception is not OutOfMemoryException;

    #endregion

    #region Staging builder

    /// <summary>
    /// The builder of one plugin: validates every registration when it is made (so the plugin fails inside Configure), stages
    /// it, and publishes it through the registry's commit. Single-threaded; sealed by the commit.
    /// </summary>
    /// <remarks>
    /// Validation happens here, at the call, and not at the commit: the exception then points into the plugin's own
    /// <c>Configure</c> call, which is the code that made the mistake. Nothing is visible to readers until the commit.
    /// </remarks>
    private sealed class StagingBuilder : IPluginBuilder
    {
        private readonly PluginRegistry _Registry;

        private readonly ContributionRecorder _Recorder;

        private readonly StagedContributions _Staged = new();

        // Set by Commit. A plain bool: the builder is single-threaded by contract.
        private bool _Sealed;

        /// <summary>Creates the builder of one plugin.</summary>
        /// <param name="registry">The registry that publishes the staged contributions.</param>
        /// <param name="pluginName">Name of the plugin being configured.</param>
        /// <param name="recorder">The plugin's recorder.</param>
        internal StagingBuilder(PluginRegistry registry, string pluginName, ContributionRecorder recorder)
        {
            _Registry = registry;
            PluginName = pluginName;
            _Recorder = recorder;
        }

        /// <inheritdoc/>
        public string PluginName { get; }

        /// <inheritdoc/>
        public void AddStackContribution(Action<IStackBuilder> register)
        {
            ArgumentNullException.ThrowIfNull(register);
            _ThrowIfSealed();

            // Only stored: the callback runs later (probe at commit, then for every stack build), never at registration.
            _Staged.Stack.Add(register);
        }

        /// <inheritdoc/>
        public void AddSettings(SettingScope scope, Action<SettingsRegistrar> register)
        {
            ArgumentNullException.ThrowIfNull(register);
            _ThrowIfSealed();
            if (scope is not (SettingScope.Application or SettingScope.Profile))
            {
                throw new ArgumentOutOfRangeException(nameof(scope), scope,
                    "Plugin settings are application or profile settings; session settings belong to the host.");
            }

            // The action is stored, not run: it runs on the probe at commit and later on every real settings manager.
            _Staged.Settings.Add((scope, register));
        }

        /// <inheritdoc/>
        public void AddSource(SourceRegistration source)
        {
            ArgumentNullException.ThrowIfNull(source);
            _ThrowIfSealed();

            // The registration is a plain record: its members are validated here because a plugin can hand over null or an
            // invalid name, which the later consumers (command line, UI lists) would otherwise meet.
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

            // Kinds compare ignoring case (as every lookup does); the same kind twice in one plugin is a mistake of that plugin.
            // Clashes with other plugins are found at the commit.
            foreach (SourceRegistration other in _Staged.Sources)
            {
                if (string.Equals(other.Kind, source.Kind, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(Invariant($"Source kind '{source.Kind}' is registered twice by this plugin."));
                }
            }

            _Staged.Sources.Add(source);
        }

        /// <inheritdoc/>
        public void AddFrameExporter(ExporterRegistration<IFrameListener> exporter)
        {
            ArgumentNullException.ThrowIfNull(exporter);
            _ThrowIfSealed();
            _ValidateExporter(exporter.Format, exporter.UiName, exporter.Description, exporter.FileExtension, exporter.RegisterOptions,
                exporter.Create);
            _Staged.FrameExporters.Add(exporter);
        }

        /// <inheritdoc/>
        public void AddPacketExporter(ExporterRegistration<IPacketListener> exporter)
        {
            ArgumentNullException.ThrowIfNull(exporter);
            _ThrowIfSealed();
            _ValidateExporter(exporter.Format, exporter.UiName, exporter.Description, exporter.FileExtension, exporter.RegisterOptions,
                exporter.Create);
            _Staged.PacketExporters.Add(exporter);
        }

        /// <summary>
        /// Called by the recorder after every configure call returned; seals the builder first so that a plugin that kept the
        /// builder cannot add contributions that were never probed.
        /// </summary>
        internal void Commit()
        {
            // Seal before the commit, not after: even when the commit throws, the builder stays unusable for a plugin that
            // kept a reference to it.
            _Sealed = true;
            _Registry._Commit(PluginName, _Recorder, _Staged);
        }

        /// <summary>Refuses a registration made after the plugin was configured.</summary>
        /// <exception cref="InvalidOperationException">The builder is sealed.</exception>
        private void _ThrowIfSealed()
        {
            if (_Sealed)
            {
                throw new InvalidOperationException("The plugin builder can only be used while the plugin is configured.");
            }
        }

        /// <summary>Checks a source kind or format name against the registration name rules.</summary>
        /// <param name="name">The name to check.</param>
        /// <param name="what">What the name names, for the message (<c>Source kind</c>, <c>Format</c>).</param>
        /// <exception cref="ArgumentException">The name breaks the rules of <see cref="PluginInfoRules.IsValidRegistrationName"/>.</exception>
        private static void _ValidateName(string name, string what)
        {
            if (!PluginInfoRules.IsValidRegistrationName(name))
            {
                throw new ArgumentException(
                    Invariant($"{what} '{name}' is not valid; use 1–{PluginInfoRules.MaxNameLength} characters: a letter, then letters, digits, '_', '.', or '-'."));
            }
        }

        /// <summary>Checks the display texts of a registration: a UI name that is not blank and a description that is not null.</summary>
        /// <param name="uiName">The UI name.</param>
        /// <param name="description">The description; may be empty.</param>
        /// <param name="what">What is registered, for the message.</param>
        /// <param name="name">The name of the registration, for the message.</param>
        private static void _ValidateText(string uiName, string description, string what, string name)
        {
            if (string.IsNullOrWhiteSpace(uiName))
            {
                throw new ArgumentException(Invariant($"{what} '{name}' needs a UI name."));
            }

            ArgumentNullException.ThrowIfNull(description);
        }

        /// <summary>Checks one exporter registration; frame and packet exporters share the rules and one name space.</summary>
        /// <param name="format">Format name.</param>
        /// <param name="uiName">UI name.</param>
        /// <param name="description">Description.</param>
        /// <param name="fileExtension">File extension with the leading dot; empty for a directory output.</param>
        /// <param name="registerOptions">The option registration.</param>
        /// <param name="create">The listener factory.</param>
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

    /// <summary>
    /// The published state: immutable, so readers need no lock. Disabled plugins keep their entries (the owner lookups and the
    /// refusal texts need them); the precomputed enabled lists are what the public list properties return.
    /// </summary>
    /// <remarks>
    /// Copy on write: every commit and every disable builds a whole new snapshot. Contributions change a handful of times
    /// per process while every request reads, so the cost sits on the rare write. The lookups are linear scans over the
    /// registrations (the built-ins register 5 sources and 9 exporters) with an ordinal ignore-case compare, which allocates
    /// nothing and needs no index to keep in sync.
    /// </remarks>
    private sealed class RegistrySnapshot
    {
        /// <summary>The snapshot before any plugin committed.</summary>
        internal static readonly RegistrySnapshot Empty = new([], [], [], [], [], FrozenDictionary<string, string>.Empty);

        // Every registration including those of disabled plugins, in load order; the lookups and owner texts use these.
        private readonly ImmutableArray<Owned<SourceRegistration>> _AllSources;

        private readonly ImmutableArray<Owned<ExporterRegistration<IFrameListener>>> _AllFrameExporters;

        private readonly ImmutableArray<Owned<ExporterRegistration<IPacketListener>>> _AllPacketExporters;

        // Disabled plugin name (ignore case) to the reason that appears in the refusal texts.
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

        /// <summary>Stack contributions of all committed plugins in load order, disabled ones included (the caller filters).</summary>
        internal ImmutableArray<Owned<Action<IStackBuilder>>> StackContributions { get; }

        /// <summary>Settings registrations of all committed plugins in load order, disabled ones included on purpose.</summary>
        internal ImmutableArray<Owned<(SettingScope Scope, Action<SettingsRegistrar> Register)>> Settings { get; }

        /// <summary>Source registrations of enabled plugins, precomputed.</summary>
        internal IReadOnlyList<SourceRegistration> SourceList { get; }

        /// <summary>Frame exporter registrations of enabled plugins, precomputed.</summary>
        internal IReadOnlyList<ExporterRegistration<IFrameListener>> FrameExporterList { get; }

        /// <summary>Packet exporter registrations of enabled plugins, precomputed.</summary>
        internal IReadOnlyList<ExporterRegistration<IPacketListener>> PacketExporterList { get; }

        /// <summary>A new snapshot that additionally holds the staged contributions of one plugin.</summary>
        /// <param name="pluginName">The committing plugin.</param>
        /// <param name="staged">What the plugin staged.</param>
        internal RegistrySnapshot Add(string pluginName, StagedContributions staged) => new(
            StackContributions.AddRange(staged.Stack.Select(item => new Owned<Action<IStackBuilder>>(pluginName, item))),
            _AllSources.AddRange(staged.Sources.Select(item => new Owned<SourceRegistration>(pluginName, item))),
            _AllFrameExporters.AddRange(staged.FrameExporters.Select(item => new Owned<ExporterRegistration<IFrameListener>>(pluginName, item))),
            _AllPacketExporters.AddRange(staged.PacketExporters.Select(item => new Owned<ExporterRegistration<IPacketListener>>(pluginName, item))),
            Settings.AddRange(staged.Settings.Select(item => new Owned<(SettingScope Scope, Action<SettingsRegistrar> Register)>(pluginName, item))),
            _Disabled);

        /// <summary>A new snapshot in which the plugin is disabled; the same snapshot when it already is (the first reason stays).</summary>
        /// <param name="pluginName">Plugin name, any case.</param>
        /// <param name="reason">Reason for the refusal texts.</param>
        internal RegistrySnapshot WithDisabled(string pluginName, string reason)
        {
            if (_Disabled.ContainsKey(pluginName))
            {
                return this;
            }

            // The comparer is ignore-case on both the copy and the frozen result: the names that come from different callers
            // (record names, registrations) are compared the way plugin names are everywhere else.
            Dictionary<string, string> disabled = new(_Disabled, StringComparer.OrdinalIgnoreCase)
            {
                [pluginName] = reason,
            };
            return new RegistrySnapshot(StackContributions, _AllSources, _AllFrameExporters, _AllPacketExporters, Settings,
                disabled.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>Whether the plugin is disabled.</summary>
        /// <param name="pluginName">Plugin name, any case.</param>
        internal bool IsDisabled(string pluginName) => _Disabled.ContainsKey(pluginName);

        /// <summary>The reason the plugin was disabled, or <see langword="null"/> when it is enabled.</summary>
        /// <param name="pluginName">Plugin name, any case.</param>
        internal string? DisabledReason(string pluginName) =>
            _Disabled.TryGetValue(pluginName, out string? reason)
                ? reason
                : null;

        /// <summary>Finds a source kind (any case) among all registrations, disabled plugins included.</summary>
        /// <param name="kind">Source kind.</param>
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

        /// <summary>Finds a frame format (any case) among all registrations, disabled plugins included.</summary>
        /// <param name="format">Export format.</param>
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

        /// <summary>Finds a packet format (any case) among all registrations, disabled plugins included.</summary>
        /// <param name="format">Export format.</param>
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

        /// <summary>The plugin that registered the source kind, or <see langword="null"/>.</summary>
        /// <param name="kind">Source kind, any case.</param>
        internal string? FindSourceOwner(string kind) => FindSource(kind)?.PluginName;

        /// <summary>The plugin that registered the format as frame or packet exporter (one name space), or <see langword="null"/>.</summary>
        /// <param name="format">Export format, any case.</param>
        internal string? FindFormatOwner(string format)
        {
            string? frameOwner = FindFrameExporter(format)?.PluginName;
            return frameOwner is null
                ? FindPacketExporter(format)?.PluginName
                : frameOwner;
        }

        /// <summary>Enabled formats for the "Known formats" text; this allocates, which is fine on the error path that uses it.</summary>
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

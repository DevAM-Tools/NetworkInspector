// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// Loads the built-in and the configured plugins up to the application's tier and configures them in the one fixed order:
/// <list type="number">
/// <item>pre-scan the configured paths by metadata (no assembly is loaded to look at it);</item>
/// <item>admit built-ins first, then the entry types of the plugin assemblies in path order (each assembly in its own
/// collectible load context, each entry type checked and created);</item>
/// <item><see cref="IPlugin.Configure"/> for every admitted plugin in load order;</item>
/// <item>the application's tier steps (<c>ConfigureHost</c>, <c>ConfigureApp</c>) for the plugins of that tier or above;</item>
/// <item>the commit of each plugin's contributions in load order.</item>
/// </list>
/// Every failure of a plugin — a missing path, an assembly that cannot be loaded, a misdeclared entry type, a throwing
/// constructor, configure method, or commit — becomes a record with state <see cref="PluginLoadState.Failed"/> and a reason;
/// <see cref="Load"/> throws only for invalid arguments and cancellation. Plugins cannot be unloaded while the process runs
/// (see the guide); an assembly without a loaded plugin is unloaded at once. Only the lifecycle (<see cref="PluginLifecycle"/>)
/// bounds plugin code by time; the calls of this class (constructor, <c>Info</c>, <c>Configure</c>, tier steps, commit) are not
/// bounded and run on the calling thread.
/// <para>
/// Isolation and all-or-nothing publication: a plugin's contributions are only staged while it is configured and are
/// published by its commit, which runs after every configure call and every tier step of every plugin. A plugin that fails
/// anywhere before its commit publishes nothing, and one plugin's failure never stops the others. Cancellation is checked up
/// to the configure calls only: once the tier steps begin, the load runs to its end, so a cancelled load either throws
/// before any commit or finishes with every commit done, and never leaves some plugins committed and others not.
/// </para>
/// <para>Thread safety: not thread-safe per instance (one <see cref="Load"/> at a time); independent instances may run concurrently.</para>
/// </summary>
public sealed class PluginLoader
{
    #region Types

    /// <summary>
    /// One plugin on its way through the load: admitted, configured, committed. Holds the state the record is built from.
    /// Single-threaded (the loading thread); state moves only forward: Loaded to Failed or Skipped, never back.
    /// </summary>
    private sealed class Pending
    {
        // Set by Activate; null for plugins that failed or were skipped before they were admitted.
        private IPlugin? _Instance;

        private ContributionRecorder? _Recorder;

        private PluginRecordDraft? _Draft;

        // The staging builder of Configure; the tier steps reuse the same builder, which is why it is kept.
        private IPluginBuilder? _Builder;

        // Stopwatch ticks of all phases (assembly load, activation, info, configure, tier steps, commit).
        private long _Ticks;

        /// <summary>Creates the entry of a plugin that is not yet admitted.</summary>
        /// <param name="name">Entry type name (the plugin name replaces it when the info is read).</param>
        /// <param name="version">Assembly version text from metadata, or empty.</param>
        /// <param name="tier">Tier of the entry type.</param>
        /// <param name="path">Assembly path, or the configured path.</param>
        /// <param name="builtIn"><see langword="true"/> for a built-in plugin.</param>
        internal Pending(string name, string version, PluginTier tier, string path, bool builtIn)
        {
            Name = name;
            Version = version;
            Tier = tier;
            Path = path;
            BuiltIn = builtIn;
        }

        /// <summary>The entry type name until the info is read, then the plugin name.</summary>
        internal string Name { get; private set; }

        /// <summary>Assembly version from metadata until the info is read, then the plugin's version text.</summary>
        internal string Version { get; private set; }

        /// <summary>Tier of the entry type.</summary>
        internal PluginTier Tier { get; }

        /// <summary>Assembly path, or the configured path for a missing path.</summary>
        internal string Path { get; }

        /// <summary>Whether the application handed the plugin in.</summary>
        internal bool BuiltIn { get; }

        /// <summary>License text of the plugin info; empty until the info is read.</summary>
        internal string License { get; private set; } = string.Empty;

        /// <summary>Dependencies of the plugin info; empty until the info is read.</summary>
        internal IReadOnlyList<PluginDependency> Dependencies { get; private set; } = [];

        /// <summary>Loaded until the first failure or skip.</summary>
        internal PluginLoadState State { get; private set; } = PluginLoadState.Loaded;

        /// <summary>Why the plugin failed or was skipped.</summary>
        internal string? Reason { get; private set; }

        /// <summary>The load context of the plugin's assembly; shared by the entry types of one assembly.</summary>
        internal AssemblyLoadContext? Context { get; init; }

        /// <summary>
        /// Whether the plugin still takes part in configure, tier steps, and commit. Failed and skipped records stay in the
        /// list but are passed over; the property is evaluated per step, so a plugin that failed in step n is skipped in n+1.
        /// </summary>
        internal bool IsActive => State == PluginLoadState.Loaded && _Instance is not null;

        /// <summary>Adds the time since a phase began; the record reports the sum of all phases.</summary>
        /// <param name="startTimestamp"><see cref="Stopwatch.GetTimestamp"/> value taken when the phase began.</param>
        internal void AddTime(long startTimestamp) => _Ticks += Stopwatch.GetTimestamp() - startTimestamp;

        /// <summary>Adds an already measured time, in stopwatch ticks.</summary>
        /// <param name="ticks">Ticks to add.</param>
        internal void AddTicks(long ticks) => _Ticks += ticks;

        /// <summary>Takes over the validated identity and instance of an admitted plugin.</summary>
        /// <param name="instance">The created plugin object.</param>
        /// <param name="info">The validated info (see <see cref="PluginInfoRules.TryValidate"/>).</param>
        internal void Activate(IPlugin instance, PluginInfo info)
        {
            _Instance = instance;
            Name = info.Name;
            Version = info.Version;
            License = info.License;
            Dependencies = info.Dependencies;
            _Recorder = new ContributionRecorder(info.Name);
            _Draft = new PluginRecordDraft(info, Tier, Path, BuiltIn);
        }

        /// <summary>
        /// Marks the plugin as failed. The recorder is discarded first, which drops the staged commit: a failed plugin
        /// publishes nothing, whichever step it failed in.
        /// </summary>
        /// <param name="reason">One-line reason for the record.</param>
        internal void Fail(string reason)
        {
            _Recorder?.Discard();
            State = PluginLoadState.Failed;
            Reason = reason;
        }

        /// <summary>Marks the plugin as skipped (it needs a higher tier). Nothing was staged, so nothing is discarded.</summary>
        /// <param name="reason">One-line reason for the record.</param>
        internal void Skip(string reason)
        {
            State = PluginLoadState.Skipped;
            Reason = reason;
        }

        /// <summary>Calls the plugin's own Configure with a fresh staging builder (the configure stage of <see cref="Load"/>).</summary>
        /// <param name="builders">The application's builder factory.</param>
        internal void Configure(IPluginBuilderFactory builders)
        {
            // The factory call is inside the boundary too: an application builder that throws while it is created fails this
            // plugin and not the load.
            _Run("Configure", () =>
            {
                _Builder = builders.Create(_Draft ?? throw _NotActive(), _Recorder ?? throw _NotActive());
                (_Instance ?? throw _NotActive()).Configure(_Builder);
            });
        }

        /// <summary>Calls a tier method above Configure, with the same builder (the tier stage of <see cref="Load"/>).</summary>
        /// <param name="step">The tier step; its method name appears in a failure reason.</param>
        internal void RunTierStep(PluginTierStep step)
        {
            _Run(step.MethodName, () => step.Configure(_Instance ?? throw _NotActive(), _Builder ?? throw _NotActive()));
        }

        /// <summary>
        /// Publishes the staged contributions (the commit stage of <see cref="Load"/>). A throw (a conflict with an earlier plugin, a failing probe)
        /// fails the plugin with the label "Registration", and the registry has already restored its probes by then.
        /// </summary>
        internal void Commit()
        {
            _Run("Registration", () => (_Recorder ?? throw _NotActive()).Commit());
        }

        /// <summary>Builds the immutable record from the final state.</summary>
        /// <returns>The record; contributions and instance are present only for a Loaded plugin.</returns>
        internal PluginRecord ToRecord()
        {
            // Failed and skipped plugins expose no instance and no contributions: nothing of them was published, and a record
            // must not hand out a plugin object that the application will never start.
            bool loaded = State == PluginLoadState.Loaded;
            return new PluginRecord(Name, Version, Tier, State, Reason, Path, BuiltIn, License,
                Stopwatch.GetElapsedTime(0, _Ticks), Dependencies,
                loaded && _Recorder is not null
                    ? _Recorder.Snapshot()
                    : FrozenDictionary<ContributionKind, IReadOnlyList<ContributionEntry>>.Empty,
                loaded
                    ? _Instance
                    : null);
        }

        /// <summary>Runs plugin code: any exception (except the unrecoverable ones) fails this plugin and nobody else.</summary>
        /// <param name="label">Name of the step, the start of the failure reason (<c>Configure failed: …</c>).</param>
        /// <param name="action">The work; it runs on the calling thread and is not bounded by time.</param>
        private void _Run(string label, Action action)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The containment boundary for plugin code: the exception becomes the reason of this record, nothing reaches the
                // other plugins or the caller, and Fail discards what the plugin staged.
                Fail(label + " failed: " + PluginActivator.Describe(ex));
            }
            finally
            {
                AddTime(start);
            }
        }

        /// <summary>Only active pending entries are run, and an active entry always has an instance, recorder, draft, and builder.</summary>
        /// <returns>The exception to throw when that invariant is broken (a bug of the loader, caught by the boundary).</returns>
        private static InvalidOperationException _NotActive() => new("The plugin is not active.");
    }

    #endregion

    #region Fields

    private readonly PluginLoaderOptions _Options;

    #endregion

    #region Lifecycle

    /// <summary>Creates a loader.</summary>
    /// <param name="options">Cache file and built-in plugins.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The cache file name is empty, or the built-in list or one of its entries is <see langword="null"/>.</exception>
    public PluginLoader(PluginLoaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ScanCacheFile, nameof(options));
        if (options.BuiltInPlugins is null || options.BuiltInPlugins.Any(static plugin => plugin is null))
        {
            throw new ArgumentException("The built-in plugin list and its entries must not be null.", nameof(options));
        }

        _Options = options;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Pre-scans the paths, loads the plugins of tiers up to <paramref name="applicationTier"/>, configures them, and records
    /// their contributions. A plugin of a higher tier is recorded as skipped and its assembly is not loaded.
    /// </summary>
    /// <param name="applicationTier">The tier of the application: Core for the CLI, Host for a host, App for the app.</param>
    /// <param name="pluginPaths">Configured folders and files.</param>
    /// <param name="builders">The application's builder factory; its tier steps must match <paramref name="applicationTier"/>.</param>
    /// <param name="cancellationToken">Cancels the load before anything is published.</param>
    /// <returns>One record per plugin; failures are records, not exceptions.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="applicationTier"/> is not a defined tier.</exception>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A path is blank, or the tier steps do not match the application tier.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public PluginLoadReport Load(PluginTier applicationTier, IReadOnlyList<string> pluginPaths, IPluginBuilderFactory builders,
        CancellationToken cancellationToken)
    {
        // Arguments, tier steps, and cancellation are the only exceptions; everything a plugin does wrong is a record.
        if (!Enum.IsDefined(applicationTier))
        {
            throw new ArgumentOutOfRangeException(nameof(applicationTier), applicationTier, "Unknown application tier.");
        }

        ArgumentNullException.ThrowIfNull(pluginPaths);
        ArgumentNullException.ThrowIfNull(builders);
        _ValidatePaths(pluginPaths);
        IReadOnlyList<PluginTierStep> steps = _ValidateSteps(builders.TierSteps, applicationTier);
        cancellationToken.ThrowIfCancellationRequested();

        List<Pending> pending = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        // 1. Built-ins first, then the pre-scanned entry types in path order: activation and info checks. Built-ins come first
        //    so that they win every name conflict with a configured plugin, and they take the same admission path as external
        //    plugins (same info rules, same builder), so a built-in is no special case for the contributions.
        foreach (IPlugin builtIn in _Options.BuiltInPlugins)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The tier of a built-in comes from reflection over the instance (there is no metadata scan for it). Reflection also
            // sees interfaces inherited from a base class, which the metadata scan of an external plugin does not. Location is
            // empty for assemblies of a single-file application.
            Type type = builtIn.GetType();
            PluginTier tier = TierDetection.FromType(type) ?? PluginTier.Core;
            Pending admitted = new(type.FullName ?? type.Name, string.Empty, tier, type.Assembly.Location, builtIn: true);
            _Admit(admitted, builtIn, applicationTier, names);
            pending.Add(admitted);
        }

        // The scan reads metadata only; no plugin assembly is loaded yet. A configured path that does not exist becomes a Failed
        // record (so the user sees the typo) and sits between the built-ins and the assemblies in the report.
        PrescanOutcome prescan = new AssemblyPrescanner(new PrescanCache(_Options.ScanCacheFile)).Scan(pluginPaths, cancellationToken);
        foreach (string missing in prescan.MissingPaths)
        {
            Pending failed = new(_NameOfPath(missing), string.Empty, PluginTier.Core, missing, builtIn: false);
            failed.Fail("Configured plugin path not found.");
            pending.Add(failed);
        }

        // Load order is the path order of the scan. The entries of one assembly are grouped, so that step 5 can release the
        // load context of an assembly in which no plugin survived.
        List<List<Pending>> assemblies = [];
        foreach (PrescanResult result in prescan.PluginAssemblies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<Pending> group = [];
            _AdmitAssembly(result, applicationTier, names, group);
            pending.AddRange(group);
            assemblies.Add(group);
        }

        // 2. Configure for every admitted plugin in load order, each with its own staging builder. Nothing is published here:
        //    the builders only stage. This is the last place that checks for cancellation (see the class summary).
        foreach (Pending plugin in pending.Where(static p => p.IsActive))
        {
            cancellationToken.ThrowIfCancellationRequested();
            plugin.Configure(builders);
        }

        // 3. Tier steps in ascending tier order; each step for every still-active plugin of that tier or above. All Configure
        //    calls are done before the first tier step, so the order is the same for every application: all Configure calls,
        //    then ConfigureHost for all, then ConfigureApp for all. IsActive is evaluated per step, so a plugin that failed in
        //    an earlier step is passed over.
        foreach (PluginTierStep step in steps)
        {
            foreach (Pending plugin in pending.Where(p => p.IsActive && p.Tier >= step.Tier))
            {
                plugin.RunTierStep(step);
            }
        }

        // 4. Commit in load order: duplicates against earlier plugins, probes, publication of the registry snapshot. The commit
        //    comes after every configure call and tier step so that a plugin which fails in any of them publishes nothing, and
        //    it runs in load order so that built-ins and earlier plugins own a contested name.
        foreach (Pending plugin in pending.Where(static p => p.IsActive))
        {
            plugin.Commit();
        }

        // 5. An assembly without any loaded plugin is of no use: release its load context. A context with a loaded plugin stays
        //    for the process lifetime. Unload only starts the release; the memory is returned when nothing refers to the types.
        foreach (List<Pending> group in assemblies)
        {
            if (group.Count > 0 && !group.Any(static p => p.IsActive))
            {
                group.Select(static p => p.Context).FirstOrDefault(static context => context is not null)?.Unload();
            }
        }

        return new PluginLoadReport(prescan.Summary, [.. pending.Select(static p => p.ToRecord())]);
    }

    #endregion

    #region Admission

    /// <summary>Reads and checks the info of a plugin object, applies the tier and name rules, and activates the pending entry.</summary>
    /// <param name="pending">The entry of the plugin; failed or skipped here when a rule is broken.</param>
    /// <param name="plugin">The plugin object (a built-in instance or a created entry type).</param>
    /// <param name="applicationTier">The tier of the application.</param>
    /// <param name="names">Plugin names taken so far (ignoring case); the plugin's name is added when it is admitted.</param>
    private static void _Admit(Pending pending, IPlugin plugin, PluginTier applicationTier, HashSet<string> names)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            // Info is plugin code (a property getter) and is read exactly once: the record keeps the validated values, so a
            // getter that returns different values per call cannot change the plugin after it was checked.
            PluginInfo? info;
            try
            {
                info = plugin.Info;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Plugin code: a throwing Info getter is a failed plugin.
                pending.Fail("reading Info threw: " + PluginActivator.Describe(ex));
                return;
            }

            if (!PluginInfoRules.TryValidate(info, out string? problem))
            {
                pending.Fail("invalid plugin info: " + problem);
                return;
            }

            // Only built-ins can reach this check with a tier above the application's: an external entry type of a higher tier is
            // skipped from its metadata before the assembly is loaded (see _AdmitAssembly).
            if (pending.Tier > applicationTier)
            {
                pending.Skip(_NeedsText(pending.Tier));
                return;
            }

            // The name stays reserved when the plugin fails later, so a later duplicate is still reported as a duplicate.
            if (!names.Add(info.Name))
            {
                pending.Fail(Invariant($"A plugin named '{info.Name}' is already loaded."));
                return;
            }

            pending.Activate(plugin, info);
        }
        finally
        {
            pending.AddTime(start);
        }
    }

    /// <summary>
    /// Admits the entry types of one scanned assembly: tier skips and metadata problems first (no load needed), then the load
    /// context, the assembly, and one activation per remaining entry type.
    /// </summary>
    /// <param name="result">The scan result of the assembly.</param>
    /// <param name="applicationTier">The tier of the application.</param>
    /// <param name="names">Plugin names taken so far (ignoring case).</param>
    /// <param name="group">
    /// Receives one entry per entry type of the assembly: first the skipped and misdeclared ones, then the activated ones,
    /// each part in the scan's name order.
    /// </param>
    private static void _AdmitAssembly(PrescanResult result, PluginTier applicationTier, HashSet<string> names, List<Pending> group)
    {
        // First sort the entry types by what metadata alone can decide: a tier above the application and a declaration
        // problem (not public, generic) never need the assembly. The tier is tested first, so an entry of a higher tier is
        // reported as skipped even when it is also misdeclared.
        string version = result.AssemblyVersion ?? string.Empty;
        List<PrescanEntry> loadable = [];
        foreach (PrescanEntry entry in result.Entries)
        {
            if (entry.Tier > applicationTier)
            {
                Pending skipped = new(entry.TypeName, version, entry.Tier, result.Path, builtIn: false);
                skipped.Skip(_NeedsText(entry.Tier));
                group.Add(skipped);
            }
            else if (entry.Problem is not null)
            {
                Pending misdeclared = new(entry.TypeName, version, entry.Tier, result.Path, builtIn: false);
                misdeclared.Fail(entry.Problem);
                group.Add(misdeclared);
            }
            else
            {
                loadable.Add(entry);
            }
        }

        if (loadable.Count == 0)
        {
            // Nothing in this assembly can run here, so the assembly itself is never loaded.
            return;
        }

        // One collectible context per assembly, shared by its entry types. The references of the assembly resolve lazily through
        // the context, while the entry types are created and used.
        long start = Stopwatch.GetTimestamp();
        PluginLoadContext context = new(result.Path);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(result.Path);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // The assembly is a file somebody else wrote: whatever the runtime says about it (a damaged image, a missing
            // dependency, a locked file, an unexpected type) fails the plugins of this assembly and nobody else.
            context.Unload();
            foreach (PrescanEntry entry in loadable)
            {
                Pending failed = new(entry.TypeName, version, entry.Tier, result.Path, builtIn: false);
                failed.Fail("assembly could not be loaded: " + PluginActivator.Describe(ex));
                group.Add(failed);
            }

            return;
        }

        // Loading the assembly is shared by its entry types; its time is booked on the first one. Each entry type is created
        // and admitted on its own, so one type that fails does not hide its siblings in the same assembly.
        long loadTicks = Stopwatch.GetTimestamp() - start;
        foreach (PrescanEntry entry in loadable)
        {
            Pending pending = new(entry.TypeName, version, entry.Tier, result.Path, builtIn: false)
            {
                Context = context,
            };
            pending.AddTicks(loadTicks);
            loadTicks = 0;

            // Only the creation is timed here. _Admit books its own time (the info read and the rules), so timing the whole
            // if-block would count that span twice in the load time of the plugin.
            long activation = Stopwatch.GetTimestamp();
            if (PluginActivator.TryCreate(assembly, entry.TypeName, out IPlugin? plugin, out string? reason))
            {
                pending.AddTime(activation);
                _Admit(pending, plugin, applicationTier, names);
            }
            else
            {
                pending.AddTime(activation);
                pending.Fail(reason);
            }

            group.Add(pending);
        }
    }

    /// <summary>The skip reason for a plugin of a tier that the application does not offer.</summary>
    /// <param name="tier">The tier the plugin needs (Host or App).</param>
    private static string _NeedsText(PluginTier tier) =>
        tier == PluginTier.Host
            ? "needs the host"
            : "needs the app";

    /// <summary>A short name for a configured path that does not exist: its last segment, or the whole path when it has none.</summary>
    /// <param name="path">The configured path.</param>
    private static string _NameOfPath(string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        string name = Path.GetFileName(trimmed);
        return name.Length == 0
            ? path
            : name;
    }

    #endregion

    #region Argument checks

    /// <summary>Rejects a blank configured path; the paths come from the application (the settings reader already drops blanks).</summary>
    /// <param name="pluginPaths">The configured paths.</param>
    /// <exception cref="ArgumentException">A path is <see langword="null"/>, empty, or white space.</exception>
    private static void _ValidatePaths(IReadOnlyList<string> pluginPaths)
    {
        foreach (string path in pluginPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(pluginPaths));
        }
    }

    /// <summary>
    /// The tier steps must be exactly the tier methods above Configure up to the application tier: one per tier, ascending.
    /// A wrong list is a programming error of the application (not of a plugin), so it throws before anything is loaded.
    /// </summary>
    /// <param name="steps">The steps of the builder factory.</param>
    /// <param name="applicationTier">The tier of the application.</param>
    /// <returns>The validated steps.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="steps"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The count, the tiers, or a step does not match the application tier.</exception>
    private static IReadOnlyList<PluginTierStep> _ValidateSteps(IReadOnlyList<PluginTierStep> steps, PluginTier applicationTier)
    {
        ArgumentNullException.ThrowIfNull(steps);

        // The expected count and tiers are derived from the numeric tier values (Core = 1, Host = 2, App = 3), which therefore
        // have to stay consecutive.
        int expected = (int)applicationTier - (int)PluginTier.Core;
        if (steps.Count != expected)
        {
            throw new ArgumentException(
                Invariant($"An application of tier {applicationTier} needs {expected} tier step(s), but the builder factory lists {steps.Count}."),
                nameof(steps));
        }

        for (int index = 0; index < steps.Count; index++)
        {
            PluginTierStep step = steps[index] ?? throw new ArgumentException("A tier step is null.", nameof(steps));
            PluginTier expectedTier = (PluginTier)((int)PluginTier.Host + index);
            if (step.Tier != expectedTier)
            {
                throw new ArgumentException(
                    Invariant($"Tier step {index + 1} must be for tier {expectedTier} (steps ascend, one per tier above Core), but it is for {step.Tier}."),
                    nameof(steps));
            }

            if (string.IsNullOrWhiteSpace(step.MethodName) || step.Configure is null)
            {
                throw new ArgumentException(Invariant($"Tier step for {step.Tier} needs a method name and a configure action."), nameof(steps));
            }
        }

        return steps;
    }

    #endregion
}

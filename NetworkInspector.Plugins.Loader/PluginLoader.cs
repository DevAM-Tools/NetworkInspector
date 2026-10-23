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
/// <para>Thread safety: not thread-safe per instance (one <see cref="Load"/> at a time); independent instances may run concurrently.</para>
/// </summary>
public sealed class PluginLoader
{
    #region Types

    // One plugin on its way through the load: admitted, configured, committed. Holds the state the record is built from.
    private sealed class Pending
    {
        private IPlugin? _Instance;

        private ContributionRecorder? _Recorder;

        private PluginRecordDraft? _Draft;

        private IPluginBuilder? _Builder;

        private long _Ticks;

        internal Pending(string name, string version, PluginTier tier, string path, bool builtIn)
        {
            Name = name;
            Version = version;
            Tier = tier;
            Path = path;
            BuiltIn = builtIn;
        }

        internal string Name { get; private set; }

        internal string Version { get; private set; }

        internal PluginTier Tier { get; }

        internal string Path { get; }

        internal bool BuiltIn { get; }

        internal string License { get; private set; } = string.Empty;

        internal IReadOnlyList<PluginDependency> Dependencies { get; private set; } = [];

        internal PluginLoadState State { get; private set; } = PluginLoadState.Loaded;

        internal string? Reason { get; private set; }

        // The load context of the plugin's assembly; shared by the entry types of one assembly.
        internal AssemblyLoadContext? Context { get; init; }

        // Failed and skipped records stay in the list but take no part in configure, tier steps, or commit.
        internal bool IsActive => State == PluginLoadState.Loaded && _Instance is not null;

        // Adds the time since a phase began; the record reports the sum of all phases.
        internal void AddTime(long startTimestamp) => _Ticks += Stopwatch.GetTimestamp() - startTimestamp;

        internal void AddTicks(long ticks) => _Ticks += ticks;

        // Takes over the validated identity and instance of an admitted plugin.
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

        internal void Fail(string reason)
        {
            _Recorder?.Discard();
            State = PluginLoadState.Failed;
            Reason = reason;
        }

        internal void Skip(string reason)
        {
            State = PluginLoadState.Skipped;
            Reason = reason;
        }

        // Step 2: the plugin's own Configure with a fresh staging builder.
        internal void Configure(IPluginBuilderFactory builders)
        {
            _Run("Configure", () =>
            {
                _Builder = builders.Create(_Draft ?? throw _NotActive(), _Recorder ?? throw _NotActive());
                (_Instance ?? throw _NotActive()).Configure(_Builder);
            });
        }

        // Step 3: a tier method above Configure, with the same builder.
        internal void RunTierStep(PluginTierStep step)
        {
            _Run(step.MethodName, () => step.Configure(_Instance ?? throw _NotActive(), _Builder ?? throw _NotActive()));
        }

        // Step 4: publication of the staged contributions.
        internal void Commit()
        {
            _Run("Registration", () => (_Recorder ?? throw _NotActive()).Commit());
        }

        internal PluginRecord ToRecord()
        {
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

        // Runs plugin code: any exception (except the unrecoverable ones) fails this plugin and nobody else.
        private void _Run(string label, Action action)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Fail(label + " failed: " + PluginActivator.Describe(ex));
            }
            finally
            {
                AddTime(start);
            }
        }

        // Only active pending entries are run, and an active entry always has an instance, recorder, draft, and builder.
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

        // 1. Built-ins first, then the pre-scanned entry types in path order: activation and info checks.
        foreach (IPlugin builtIn in _Options.BuiltInPlugins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Type type = builtIn.GetType();
            PluginTier tier = TierDetection.FromType(type) ?? PluginTier.Core;
            Pending admitted = new(type.FullName ?? type.Name, string.Empty, tier, type.Assembly.Location, builtIn: true);
            _Admit(admitted, builtIn, applicationTier, names);
            pending.Add(admitted);
        }

        PrescanOutcome prescan = new AssemblyPrescanner(new PrescanCache(_Options.ScanCacheFile)).Scan(pluginPaths, cancellationToken);
        foreach (string missing in prescan.MissingPaths)
        {
            Pending failed = new(_NameOfPath(missing), string.Empty, PluginTier.Core, missing, builtIn: false);
            failed.Fail("Configured plugin path not found.");
            pending.Add(failed);
        }

        List<List<Pending>> assemblies = [];
        foreach (PrescanResult result in prescan.PluginAssemblies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<Pending> group = [];
            _AdmitAssembly(result, applicationTier, names, group);
            pending.AddRange(group);
            assemblies.Add(group);
        }

        // 2. Configure for every admitted plugin in load order, each with its own staging builder.
        foreach (Pending plugin in pending.Where(static p => p.IsActive))
        {
            cancellationToken.ThrowIfCancellationRequested();
            plugin.Configure(builders);
        }

        // 3. Tier steps in ascending tier order; each step for every still-active plugin of that tier or above.
        foreach (PluginTierStep step in steps)
        {
            foreach (Pending plugin in pending.Where(p => p.IsActive && p.Tier >= step.Tier))
            {
                plugin.RunTierStep(step);
            }
        }

        // 4. Commit in load order: duplicates against earlier plugins, probes, publication of the registry snapshot.
        foreach (Pending plugin in pending.Where(static p => p.IsActive))
        {
            plugin.Commit();
        }

        // 5. An assembly without any loaded plugin is of no use: release its load context.
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

    // Reads and checks the info of a plugin object, applies the tier and name rules, and activates the pending entry.
    private static void _Admit(Pending pending, IPlugin plugin, PluginTier applicationTier, HashSet<string> names)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
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

    // Admits the entry types of one scanned assembly: tier skips and metadata problems first (no load needed), then the load
    // context, the assembly, and one activation per remaining entry type.
    private static void _AdmitAssembly(PrescanResult result, PluginTier applicationTier, HashSet<string> names, List<Pending> group)
    {
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

        // Loading the assembly is shared by its entry types; its time is booked on the first one.
        long loadTicks = Stopwatch.GetTimestamp() - start;
        foreach (PrescanEntry entry in loadable)
        {
            Pending pending = new(entry.TypeName, version, entry.Tier, result.Path, builtIn: false)
            {
                Context = context,
            };
            pending.AddTicks(loadTicks);
            loadTicks = 0;

            long activation = Stopwatch.GetTimestamp();
            if (PluginActivator.TryCreate(assembly, entry.TypeName, out IPlugin? plugin, out string? reason))
            {
                _Admit(pending, plugin, applicationTier, names);
            }
            else
            {
                pending.Fail(reason);
            }

            pending.AddTime(activation);
            group.Add(pending);
        }
    }

    private static string _NeedsText(PluginTier tier) =>
        tier == PluginTier.Host
            ? "needs the host"
            : "needs the app";

    // A short name for a configured path that does not exist: its last segment.
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

    private static void _ValidatePaths(IReadOnlyList<string> pluginPaths)
    {
        foreach (string path in pluginPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(pluginPaths));
        }
    }

    // The tier steps must be exactly the tier methods above Configure up to the application tier: one per tier, ascending.
    private static IReadOnlyList<PluginTierStep> _ValidateSteps(IReadOnlyList<PluginTierStep> steps, PluginTier applicationTier)
    {
        ArgumentNullException.ThrowIfNull(steps);
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

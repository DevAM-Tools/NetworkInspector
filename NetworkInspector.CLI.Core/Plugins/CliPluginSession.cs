// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI;

/// <summary>
/// Plugins, application settings, and profile settings for one command. <see cref="OpenAsync"/> resolves the settings base and
/// the profile, loads the settings, loads the built-in plugins and those of <c>plugins.paths</c>, builds the profile settings
/// (stack contributions and plugin settings), and starts the plugins; <see cref="DisposeAsync"/> stops them in reverse order
/// and releases everything. Starting and stopping is done by <see cref="PluginLifecycle"/>, which the host and the app use as
/// well, so the three applications cannot drift apart; the isolation of plugin code (every call on a thread-pool thread, bounded
/// by a timeout, a throwing plugin fails only itself) lives there. Single-threaded use by the command, except that plugins may
/// log from any thread (their lines go through <see cref="CliPluginLog"/>, which locks the error writer).
/// <para>
/// Order of <see cref="OpenAsync"/>, and why: the application settings load first because they hold <c>plugins.paths</c>, which
/// decides which plugins are found; the plugins are loaded and their application settings registered; only then the profile
/// settings are built, because the stack contributions and the profile settings of the plugins are known only after the load;
/// the plugins start last, when both settings managers are complete, because the context a plugin receives needs both.
/// </para>
/// </summary>
internal sealed class CliPluginSession : IAsyncDisposable
{
    #region Constants

    /// <summary>Folder of the application settings under the settings base; the name every layout reserves.</summary>
    internal const string ApplicationFolder = SettingsStorageLayout.ApplicationFolderName;

    /// <summary>Folder of the plugin data folders under the settings base.</summary>
    private const string _PluginDataFolder = "plugins-data";

    /// <summary>File name of the plugin scan cache under the settings base.</summary>
    private const string _ScanCacheFile = "plugin-scan-cache.json";

    /// <summary>
    /// Largest size of the log file before it rotates: 10 MiB. Limit record: value 10 MiB · threat: unbounded growth of the log
    /// file filling the disk of the user (together with <see cref="_LogMaxFiles"/> the log occupies at most about 50 MiB) · constant,
    /// not configurable by operator or user · posture: secure by default (a tight bound that needs no tuning).
    /// </summary>
    private const long _LogMaxBytes = 10_485_760;

    /// <summary>Number of log files kept, the current one included (rotation shifts <c>ni.log</c> to <c>ni.1.log</c> and so on).</summary>
    private const int _LogMaxFiles = 5;

    #endregion

    #region Fields

    private readonly CliIo _Io;

    // Null when the log file could not be opened; the error writer still gets every line then.
    private readonly CliLogFile? _LogFile;

    // One context per plugin name, created on first use and shared by start and stop. Names compare ignoring case. Not
    // thread-safe: only the lifecycle calls (sequential) and the command thread touch it.
    private readonly Dictionary<string, PluginContext> _Contexts = new(StringComparer.OrdinalIgnoreCase);

    // Lines of the profile settings that were reported already (level, source, and text). The profile files are loaded and the
    // plugin contributions applied twice per command, once for the profile manager of the session and once for the stack that
    // export and convert build; a problem found by both would otherwise be printed twice. Not thread-safe: only the command
    // thread reports profile problems.
    private readonly HashSet<string> _ReportedProfileLines = new(StringComparer.Ordinal);

    // Makes DisposeAsync idempotent; a plain field because the session is used by one thread at a time.
    private bool _Disposed;

    #endregion

    #region Lifecycle

    private CliPluginSession(CliSettingsOptions options, string settingsBase, string profile, SettingsManager application,
        SettingsLimitValues loaderLimits, PluginRegistry registry, PluginLoadReport report, CliIo io, CliLogFile? logFile)
    {
        Options = options;
        LoaderLimits = loaderLimits;
        SettingsBase = settingsBase;
        Profile = profile;
        ApplicationSettings = application;
        Registry = registry;
        Report = report;
        _Io = io;
        _LogFile = logFile;

        // Placeholder until OpenAsync builds the real one: the real manager is built by an instance method (it needs the
        // settings base, the profile, the registry, and the writers of this session), so the session has to exist first. A
        // manager without a storage path owns no files; OpenAsync disposes it when it replaces it.
        ProfileSettings = new();
    }

    /// <summary>
    /// Stops the plugins in reverse load order and releases the registry, the settings, and the log file. Safe to call twice.
    /// The stop runs first because a stopping plugin may still log and read its settings; everything it can reach is released
    /// only afterwards (contexts first, since they are subscribed to the settings managers).
    /// </summary>
    /// <returns>A task that completes when the plugins are stopped and the resources are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_Disposed)
        {
            return;
        }

        _Disposed = true;
        try
        {
            // After a failed or cancelled open the report is still the load report: stop only plugins whose start was attempted.
            await PluginLifecycle.StopAsync(OnlyAttempted(Report, _Contexts.Keys), _ContextFor,
                PluginTimeoutSettings.ReadStop(ApplicationSettings.ReadOnly)).ConfigureAwait(false);
        }
        finally
        {
            // Release even when the stop throws: contexts, then the registry, the settings managers, and the log file that the
            // plugin lines went to.
            foreach (PluginContext context in _Contexts.Values)
            {
                context.Dispose();
            }

            Registry.Dispose();
            ProfileSettings.Dispose();
            ApplicationSettings.Dispose();
            _LogFile?.Dispose();
        }
    }

    #endregion

    #region Properties

    /// <summary>The settings base folder (full path).</summary>
    internal string SettingsBase { get; }

    /// <summary>The profile in use, spelled as it exists on disk.</summary>
    internal string Profile { get; }

    /// <summary>The settings options of the command.</summary>
    internal CliSettingsOptions Options { get; }

    /// <summary>
    /// The values of the loader limits in effect for this run (stored values of the application settings and <c>--set</c>
    /// overrides included), given to every profile manager so that it loads with the same limits.
    /// </summary>
    internal SettingsLimitValues LoaderLimits { get; }

    /// <summary>
    /// The <c>--set</c> overrides of profile settings, parsed and validated. A stack gets its own profile manager, which has to
    /// start with these values (see <see cref="TryBuildStack"/>).
    /// </summary>
    private readonly List<KeyValuePair<string, SettingValue>> _ProfileOverrides = [];

    /// <summary>The application settings, with the plugin settings of scope application registered.</summary>
    internal SettingsManager ApplicationSettings { get; }

    /// <summary>The settings of the profile: every stack contribution and every plugin profile setting registered.</summary>
    internal SettingsManager ProfileSettings { get; private set; }

    /// <summary>
    /// Returns the stored value of a limit key: the profile's value when the profile stores one, otherwise the application's.
    /// Plan order: a limit set for the profile wins over the application default, so one profile can differ from the others.
    /// </summary>
    /// <param name="key">The full key of the limit.</param>
    /// <returns>The stored value, or <see langword="null"/> when neither scope stores the limit.</returns>
    internal LimitValue? StoredLimit(string key)
    {
        LimitValue? stored = ProfileSettings.GetLimit(key);
        stored ??= ApplicationSettings.GetLimit(key);
        return stored;
    }

    /// <summary>The registry of the loaded plugins' sources, exporters, settings, and stack contributions.</summary>
    internal PluginRegistry Registry { get; }

    /// <summary>The load report; after the start it shows the plugins that failed to start as failed.</summary>
    internal PluginLoadReport Report { get; private set; }

    #endregion

    #region Public API

    /// <summary>Opens the session; prints one error line and returns the exit code when it cannot open.</summary>
    /// <param name="options">The settings options of the command.</param>
    /// <param name="io">The writers of the command.</param>
    /// <param name="cancellationToken">Cancels the load and the start.</param>
    /// <returns>The session, or <see langword="null"/> and the exit code of the command.</returns>
    internal static async Task<(CliPluginSession? Session, int ExitCode)> OpenAsync(CliSettingsOptions options, CliIo io,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(io);

        // 1. Base and profile: the name rule first, then existence (default is created on first use); nothing is created
        //    for an invalid or unknown name.
        if (!TryResolveProfile(options, io, out string settingsBase, out string profile, out int resolveExit))
        {
            return (null, resolveExit);
        }

        // 1b. The overrides of the loader limits are read before anything is created: the manager enforces them while it loads its
        //     first file, and a value that is refused is a usage error that touches nothing.
        if (!CliSettingOverrides.TryReadLoaderLimits(options, out SettingsLimitValues startLimits, out bool limitsOverridden,
            out string? limitError))
        {
            io.Error.WriteLine("Error: " + limitError);
            return (null, (int)ExitCode.ArgumentError);
        }

        // 2. Log: stderr always, the file when it can be opened. The folder "logs" under the base is a reserved name, so no
        //    profile can collide with it. A log that cannot be used is a warning, never a reason to fail the command; once
        //    open, a failing append reports once through the problem sink and switches the file off.
        CliLogFile? logFile = null;
        if (CliLogFile.TryOpen(Path.Combine(settingsBase, "logs", "ni.log"), _LogMaxBytes, _LogMaxFiles, TimeProvider.System,
            out CliLogFile? opened, out string? logProblem))
        {
            logFile = opened;
            logFile.ProblemSink = text => WriteLogProblem(io, text);
        }
        else
        {
            io.Error.WriteLine("Warning: " + logProblem);
        }

        // 3. Application settings, plugins, profile settings; then the application is composed and the plugins start.
        //    Everything created before the session object exists is released by the catch blocks below (_AbandonAsync).
        SettingsManager application = SettingsManagerFactory.Create(
            CoreSettingsLayout.Instance, settingsBase, ApplicationFolder, startLimits);
        PluginRegistry registry = new();
        CliPluginSession? session = null;
        try
        {
            // 3a. Application settings first: they hold plugins.paths (which plugins exist) and the start and stop timeouts, and
            //     the loader limits themselves (group settings, file settings.json). A file that cannot be loaded (too large, too
            //     deep, malformed) is a warning; the setting keeps its default.
            //     Normally the limits are registered before the load, so a stored limit bounds every other file of the load.
            //     A --set for a loader limit has to win over the stored value, so the load then runs with the value of the
            //     override and the limits become settings afterwards (their stored values apply from then on, the override is
            //     applied again below). Their default is the normal default in both cases, never the value of the override.
            if (!limitsOverridden)
            {
                _ = application.RegisterOwnLimits();
            }

            foreach (SettingsLoadWarning warning in application.Load())
            {
                _LogSession(io, logFile, "Warning", "settings", warning.Message);
            }

            if (limitsOverridden)
            {
                foreach (SettingsLoadWarning warning in application.RegisterOwnLimits(SettingsLimitValues.Default))
                {
                    _LogSession(io, logFile, "Warning", "settings", warning.Message);
                }
            }

            PluginPathsSetting.Register(application);
            PluginTimeoutSettings.Register(application);

            // 3a2. The overrides of the settings that exist now (the limits, plugins.paths, the timeouts) are applied before the
            //      plugins are found, because plugins.paths decides which plugins exist. The others follow in 3d.
            HashSet<string> applied = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in options.Overrides)
            {
                if (application.TryGetSetting(pair.Key, out Setting? early))
                {
                    if (!CliSettingOverrides.TryApply(application, early, pair.Value, out _, out string? overrideError))
                    {
                        io.Error.WriteLine("Error: " + overrideError);
                        await _AbandonAsync(null, registry, application, logFile).ConfigureAwait(false);
                        return (null, (int)ExitCode.ArgumentError);
                    }

                    applied.Add(pair.Key);
                }
            }

            // 3b. Load the built-in plugins and those found in the configured folders into the registry. The application tier
            //     is Core: a plugin of a higher tier (it needs a host or the app) is recorded as skipped and its assembly is
            //     not loaded.
            IReadOnlyList<string> paths = PluginPathsSetting.Read(application.ReadOnly,
                text => _LogSession(io, logFile, "Warning", "settings", text));
            PluginLoader loader = new(new PluginLoaderOptions(Path.Combine(settingsBase, _ScanCacheFile),
                NetworkInspectorBuiltIns.CreateAll()));
            PluginLoadReport report = loader.Load(PluginTier.Core, paths, registry, cancellationToken);

            // 3c. Settings that plugins contribute to the application scope; a plugin whose contribution fails is reported
            //     and the others carry on.
            _LogFailures(io, logFile, registry.ApplySettings(SettingScope.Application, application));

            // 3d. The session object, then the real profile settings (they need the session's registry and writers), then the
            //     load problems, then the start. The start comes last so that a plugin sees complete settings in its context.
            session = new CliPluginSession(options, settingsBase, profile, application, application.Limits, registry, report, io, logFile);
            session.ProfileSettings.Dispose();
            session.ProfileSettings = session._OpenProfileManager();
            session._LogLoadProblems();

            // The remaining overrides: plugin settings of both scopes and the profile settings. They are resolved like the name of
            // `ni settings set` (same rules, same messages), so an unknown key gets the same "did you mean". Nothing was written
            // and no plugin has started, so a refusal ends the command here.
            string? lateError = session._ApplyLateOverrides(applied);
            if (lateError is not null)
            {
                io.Error.WriteLine("Error: " + lateError);
                await session.DisposeAsync().ConfigureAwait(false);
                return (null, (int)ExitCode.ArgumentError);
            }

            session.Report = await PluginLifecycle.StartAsync(report, registry.Disable, session._ContextFor,
                PluginTimeoutSettings.ReadStart(application.ReadOnly), cancellationToken).ConfigureAwait(false);

            // 0 is ExitCode.Success; the exit code is unused when the session opened.
            return (session, 0);
        }
        catch (PersistenceSettingsException ex)
        {
            // A settings folder that cannot be read or written is an expected failure of the environment: one error line, exit
            // code 3, and the half-built session is released.
            io.Error.WriteLine("Error: " + ex.Message);
            await _AbandonAsync(session, registry, application, logFile).ConfigureAwait(false);
            return (null, (int)ExitCode.RuntimeError);
        }
        catch
        {
            // Anything else (a bug, or cancellation) propagates to the command; the resources go first.
            await _AbandonAsync(session, registry, application, logFile).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Resolves the settings base and the profile of the options without loading anything. An invalid or unknown profile name
    /// prints one error line and returns <see langword="false"/> with the exit code; <c>default</c> is created on first use.
    /// </summary>
    /// <param name="options">The settings options.</param>
    /// <param name="io">The writers of the command.</param>
    /// <param name="settingsBase">The full path of the settings base.</param>
    /// <param name="profile">The profile, spelled as on disk.</param>
    /// <param name="exitCode">The exit code when the result is <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when base and profile are usable.</returns>
    internal static bool TryResolveProfile(CliSettingsOptions options, CliIo io, out string settingsBase, out string profile,
        out int exitCode)
    {
        // Defaults for the failure paths: a bad base or profile is a usage error (exit 1); only a failed creation of "default"
        // changes the code to 3 below.
        settingsBase = string.Empty;
        profile = SettingsProfiles.DefaultProfile;
        exitCode = (int)ExitCode.ArgumentError;

        // The base is made absolute once; an unusable path (invalid characters, too long) is refused with the runtime's message.
        try
        {
            settingsBase = Path.GetFullPath(SettingsManagerFactory.ResolvePath(CoreSettingsLayout.Instance, options.SettingsPath, null));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            io.Error.WriteLine("Error: " + ex.Message);
            return false;
        }

        // The profile name is a folder name under the base, so it is validated before it is used for any path (the rules refuse
        // separators, traversal, reserved folder names, and device names).
        string requested = string.IsNullOrEmpty(options.Profile)
            ? SettingsProfiles.DefaultProfile
            : options.Profile;
        if (!ProfileNames.TryValidate(requested, CoreSettingsLayout.Instance, out string? profileError))
        {
            io.Error.WriteLine(Invariant($"Error: Profile name '{requested}': {profileError}."));
            return false;
        }

        // An existing profile is used with the spelling it has on disk (names are unique ignoring case, so the match is exact);
        // a missing one is an error unless it is "default". An unknown profile is never created implicitly; the error
        // names the command that creates it.
        string? existing = SettingsProfiles.List(settingsBase, CoreSettingsLayout.Instance)
            .FirstOrDefault(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            profile = existing;
            return true;
        }

        if (!string.Equals(requested, SettingsProfiles.DefaultProfile, StringComparison.OrdinalIgnoreCase))
        {
            io.Error.WriteLine(Invariant($"Error: Profile '{requested}' does not exist; create it with ni profile create {requested}."));
            return false;
        }

        // Two commands that start together on a fresh base both try to create the profile; only one create can win, and the
        // loser has what it wanted once the profile exists, so only a create that left no profile behind is an error.
        if (!SettingsProfiles.TryCreate(settingsBase, CoreSettingsLayout.Instance, SettingsProfiles.DefaultProfile, null, out string? createError)
            && !SettingsProfiles.Exists(settingsBase, CoreSettingsLayout.Instance, SettingsProfiles.DefaultProfile))
        {
            io.Error.WriteLine("Error: " + createError);
            exitCode = (int)ExitCode.RuntimeError;
            return false;
        }

        profile = SettingsProfiles.DefaultProfile;
        return true;
    }

    /// <summary>
    /// Builds a stack for the profile: the profile files are loaded before the protocols register their settings, so the
    /// persisted values apply. The stack owns its own settings manager: a second manager over the same profile folder, separate
    /// from <see cref="ProfileSettings"/> (which serves the plugins and the settings command), so a value changed through one of
    /// them is not seen by the other within a run. Both managers load the same profile files and apply the same plugin
    /// contributions, so a load warning or a contribution failure is reported once per session, by whichever manager meets it
    /// first.
    /// </summary>
    /// <param name="frames">The frame interface registry the stack and its sources share.</param>
    /// <param name="stack">The stack; <see langword="null"/> on failure.</param>
    /// <param name="exitCode">The exit code of the command when the result is <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="stack"/> is set.</returns>
    internal bool TryBuildStack(FrameInterfaceRegistry frames, [NotNullWhen(true)] out Stack? stack, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(frames);
        stack = null;
        exitCode = (int)ExitCode.Success;
        SettingsManager? manager = null;
        try
        {
            // Order matters: the persisted values are loaded first, so that every setting registered afterwards (by the
            // protocols of the builder and by the plugins) starts with its stored value instead of its default. The stack
            // traces of exceptions stay out of the error text that a packet records for a failing protocol (explicitly off
            // here; off is also the default of the flag).
            manager = SettingsManagerFactory.Create(
                CoreSettingsLayout.Instance, SettingsBase, Profile, LoaderLimits);
            _LogLoadWarnings(manager);

            // The overrides of profile settings were applied to the profile manager of the session. This manager is a second one
            // over the same folder, and the protocols read their settings while the stack is built, so each override is
            // preloaded: it is applied when the setting registers and wins over the stored value.
            foreach (KeyValuePair<string, SettingValue> profileOverride in _ProfileOverrides)
            {
                manager.PreloadValue(profileOverride.Key, profileOverride.Value);
            }

            StackBuilder builder = new(manager, frames) { IncludeExceptionStackTrace = false };

            // Plugin contributions (protocols, fields) are plugin code; a contribution that fails is returned as a failure, reported
            // by plugin name, and does not stop the build. The profile manager of the session reported the same problems already.
            _LogProfileFailures(Registry.ApplyStackContributions(builder));
            _LogProfileFailures(Registry.ApplySettings(SettingScope.Profile, manager));
            Stack built = builder.Build();

            // The stack owns the manager now (so the finally block must not dispose it); build diagnostics of severity Error are
            // reported once, here.
            manager = null;
            foreach (BuildDiagnostic diagnostic in built.BuildDiagnostics.Span)
            {
                if (diagnostic.Severity == BuildDiagnosticSeverity.Error)
                {
                    _LogSession(_Io, _LogFile, "Error", "stack", diagnostic.Message);
                }
            }

            stack = built;
            return true;
        }
        catch (PersistenceSettingsException ex)
        {
            _Io.Error.WriteLine("Error: " + ex.Message);
            exitCode = (int)ExitCode.RuntimeError;
            return false;
        }
        finally
        {
            manager?.Dispose();
        }
    }


    /// <summary>
    /// Reports a problem of the log file itself (it stopped working after it was opened) on the error writer, in the same form
    /// as the warning for a log file that could not be opened: <c>Warning: &lt;text&gt;</c>. The line is written under the lock of
    /// the error writer, because the log file reports from whatever thread logged.
    /// </summary>
    /// <param name="io">The writers of the command.</param>
    /// <param name="text">The problem text of the log file.</param>
    internal static void WriteLogProblem(CliIo io, string text)
    {
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(text);
        _WriteLine(io, "Warning: " + text);
    }

    /// <summary>Keeps the records of plugins whose start was attempted (a context was made for them), in load order.</summary>
    /// <param name="report">The report to filter.</param>
    /// <param name="attempted">Names of the plugins with a context; compared ignoring case.</param>
    /// <returns>A report with only those records.</returns>
    internal static PluginLoadReport OnlyAttempted(PluginLoadReport report, IEnumerable<string> attempted)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(attempted);
        HashSet<string> names = new(attempted, StringComparer.OrdinalIgnoreCase);
        return report with { Plugins = [.. report.Plugins.Where(record => names.Contains(record.Name))] };
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Applies the <c>--set</c> overrides that were not applied before the plugins were found: plugin settings of both scopes and
    /// profile settings. Each is resolved with <see cref="SettingsLookup"/>, so the rules and the messages are those of
    /// <c>ni settings set</c>. Nothing is saved.
    /// </summary>
    /// <param name="applied">The keys that were applied already; they are skipped.</param>
    /// <returns>The refusal (one line), or <see langword="null"/> when every override is in effect.</returns>
    private string? _ApplyLateOverrides(HashSet<string> applied)
    {
        foreach (KeyValuePair<string, string> pair in Options.Overrides)
        {
            if (applied.Contains(pair.Key))
            {
                continue;
            }

            if (!SettingsLookup.TryFind(this, pair.Key, null, null, out SettingsLookup.Match? match, out string? lookupError))
            {
                return lookupError;
            }

            // A stored value of a plugin that is not loaded has no type to parse against; an override needs a loaded setting.
            if (match.Setting is null)
            {
                return Invariant($"Setting '{pair.Key}' is a stored value of a plugin that is not loaded; --set needs a loaded setting.");
            }

            if (!CliSettingOverrides.TryApply(match.Manager, match.Setting, pair.Value, out SettingValue value, out string? applyError))
            {
                return applyError;
            }

            if (match.Scope == SettingScope.Profile)
            {
                _ProfileOverrides.Add(new KeyValuePair<string, SettingValue>(pair.Key, value));
            }
        }

        return null;
    }

    /// <summary>
    /// Opens the profile manager that the settings command and the plugins read: files loaded, settings registered (the
    /// protocol settings through a stack builder that is never built, then the plugin settings of scope profile).
    /// </summary>
    /// <returns>The manager; the caller owns it.</returns>
    private SettingsManager _OpenProfileManager()
    {
        SettingsManager manager = SettingsManagerFactory.Create(
            CoreSettingsLayout.Instance, SettingsBase, Profile, LoaderLimits);
        try
        {
            _LogLoadWarnings(manager);

            // The stack builder registers the protocol settings into the manager; it is never built, it only fills the manager.
            // It gets a registry of its own that no source uses. Running the contributions again here (and once more for the
            // real stack in TryBuildStack) is what makes the protocol settings listable without building a stack.
            StackBuilder builder = new(manager, new FrameInterfaceRegistry());
            _LogProfileFailures(Registry.ApplyStackContributions(builder));
            _LogProfileFailures(Registry.ApplySettings(SettingScope.Profile, manager));
            return manager;
        }
        catch
        {
            // A manager that was not handed out is released before the failure propagates.
            manager.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Loads the persisted values of a manager and reports what was skipped (a file too large, too deep, or malformed). A
    /// warning that an earlier load of the same profile reported is not reported again (see <c>_ReportedProfileLines</c>).
    /// </summary>
    /// <param name="manager">The manager whose folder is loaded.</param>
    private void _LogLoadWarnings(SettingsManager manager)
    {
        foreach (SettingsLoadWarning warning in manager.Load())
        {
            if (_ReportedProfileLines.Add("Warning\nsettings\n" + warning.Message))
            {
                _LogSession(_Io, _LogFile, "Warning", "settings", warning.Message);
            }
        }
    }

    /// <summary>
    /// Reports contribution failures found while the profile settings or a stack are built, one error line each, but only the
    /// first time: both builds apply the same contributions to the same profile, so a failure shows up in both.
    /// </summary>
    /// <param name="failures">The failures returned by the registry.</param>
    private void _LogProfileFailures(IReadOnlyList<PluginContributionFailure> failures)
    {
        foreach (PluginContributionFailure failure in failures)
        {
            if (_ReportedProfileLines.Add("Error\n" + failure.PluginName + "\n" + failure.Message))
            {
                _LogSession(_Io, _LogFile, "Error", failure.PluginName, "contribution failed: " + failure.Message);
            }
        }
    }

    /// <summary>Reports plugins that failed to load (error) or were skipped (info), one line each.</summary>
    private void _LogLoadProblems()
    {
        foreach (PluginRecord record in Report.Plugins)
        {
            if (record.State == PluginLoadState.Failed)
            {
                _LogSession(_Io, _LogFile, "Error", record.Name, record.Reason ?? "failed to load");
            }
            else if (record.State == PluginLoadState.Skipped)
            {
                _LogSession(_Io, _LogFile, "Info", record.Name, record.Reason ?? "skipped");
            }
        }
    }

    /// <summary>
    /// The context of a plugin, created once per name and reused for the stop. It carries the plugin's log, its own data folder
    /// (<c>plugins-data/&lt;storage name&gt;</c> under the base, so a long or device-like plugin name stays a valid folder name),
    /// and read-only views of both settings managers.
    /// </summary>
    /// <param name="record">The plugin record.</param>
    /// <returns>The context.</returns>
    private IPluginContext _ContextFor(PluginRecord record)
    {
        if (!_Contexts.TryGetValue(record.Name, out PluginContext? context))
        {
            context = new PluginContext(record.Name, new CliPluginLog(record.Name, _Io.Error, _LogFile),
                Path.Combine(SettingsBase, _PluginDataFolder, StorageNames.ToStorageName(record.Name)), ApplicationSettings,
                ProfileSettings);
            _Contexts[record.Name] = context;
        }

        return context;
    }

    /// <summary>Reports contribution failures of plugins, one error line each.</summary>
    /// <param name="io">The writers of the command.</param>
    /// <param name="logFile">The log file, or <see langword="null"/>.</param>
    /// <param name="failures">The failures returned by the registry.</param>
    private static void _LogFailures(CliIo io, CliLogFile? logFile, IReadOnlyList<PluginContributionFailure> failures)
    {
        foreach (PluginContributionFailure failure in failures)
        {
            _LogSession(io, logFile, "Error", failure.PluginName, "contribution failed: " + failure.Message);
        }
    }

    /// <summary>
    /// Writes one session line to the error writer and the log file. The text can carry an exception message or the reason of a
    /// failed plugin, so control characters become spaces on both sinks (<see cref="CliLogFile.SingleLine"/>).
    /// </summary>
    private static void _LogSession(CliIo io, CliLogFile? logFile, string level, string source, string text)
    {
        string clean = CliLogFile.SingleLine(text);
        _WriteLine(io, level + " " + source + ": " + clean);
        logFile?.Append(level, source, clean);
    }

    /// <summary>
    /// Writes one line to the error writer. A text writer is not thread-safe and plugins log from other threads, so the writer
    /// itself is the lock; <see cref="CliPluginLog"/> locks the same object, which keeps lines from both paths whole.
    /// </summary>
    /// <param name="io">The writers of the command.</param>
    /// <param name="line">The line without line break.</param>
    private static void _WriteLine(CliIo io, string line)
    {
        lock (io.Error)
        {
            io.Error.WriteLine(line);
        }
    }

    /// <summary>
    /// Releases what a failed open already created: a complete session disposes everything itself (and stops the plugins that
    /// were started); before the session object exists only the registry, the application settings, and the log file are there.
    /// </summary>
    /// <param name="session">The session, or <see langword="null"/> when it was not created yet.</param>
    /// <param name="registry">The plugin registry.</param>
    /// <param name="application">The application settings.</param>
    /// <param name="logFile">The log file, or <see langword="null"/>.</param>
    /// <returns>A task that completes when everything is released.</returns>
    private static async Task _AbandonAsync(CliPluginSession? session, PluginRegistry registry, SettingsManager application,
        CliLogFile? logFile)
    {
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        registry.Dispose();
        application.Dispose();
        logFile?.Dispose();
    }

    #endregion
}

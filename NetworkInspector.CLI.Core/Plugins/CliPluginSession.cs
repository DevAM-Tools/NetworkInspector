// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI;

/// <summary>
/// Plugins, application settings, and profile settings for one command. <see cref="OpenAsync"/> resolves the settings base and
/// the profile, loads the settings, loads the built-in plugins and those of <c>plugins.paths</c>, builds the profile settings
/// (stack contributions and plugin settings), and starts the plugins; <see cref="DisposeAsync"/> stops them in reverse order
/// and releases everything. The same order runs in the host and the app. Single-threaded use by the command, except that
/// plugins may log from any thread.
/// </summary>
internal sealed class CliPluginSession : IAsyncDisposable
{
    #region Constants

    /// <summary>Folder of the application settings under the settings base.</summary>
    internal const string ApplicationFolder = "application";

    /// <summary>Folder of the plugin data folders under the settings base.</summary>
    private const string _PluginDataFolder = "plugins-data";

    /// <summary>File name of the plugin scan cache under the settings base.</summary>
    private const string _ScanCacheFile = "plugin-scan-cache.json";

    /// <summary>Largest size of the log file before it rotates: 10 MiB.</summary>
    private const long _LogMaxBytes = 10_485_760;

    /// <summary>Number of log files kept, the current one included.</summary>
    private const int _LogMaxFiles = 5;

    #endregion

    #region Fields

    private readonly CliIo _Io;

    private readonly CliLogFile? _LogFile;

    private readonly Dictionary<string, PluginContext> _Contexts = new(StringComparer.OrdinalIgnoreCase);

    private bool _Disposed;

    #endregion

    #region Lifecycle

    private CliPluginSession(CliSettingsOptions options, string settingsBase, string profile, SettingsManager application,
        PluginRegistry registry, PluginLoadReport report, CliIo io, CliLogFile? logFile)
    {
        Options = options;
        SettingsBase = settingsBase;
        Profile = profile;
        ApplicationSettings = application;
        Registry = registry;
        Report = report;
        _Io = io;
        _LogFile = logFile;

        // Placeholder until OpenAsync builds the real one; PluginContext needs both managers.
        ProfileSettings = new();
    }

    /// <summary>Stops the plugins in reverse load order and releases the registry, the settings, and the log file.</summary>
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

    /// <summary>The application settings, with the plugin settings of scope application registered.</summary>
    internal SettingsManager ApplicationSettings { get; }

    /// <summary>The settings of the profile: every stack contribution and every plugin profile setting registered.</summary>
    internal SettingsManager ProfileSettings { get; private set; }

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

        // 2. Log: stderr always, the file when it can be opened.
        CliLogFile? logFile = null;
        if (CliLogFile.TryOpen(Path.Combine(settingsBase, "logs", "ni.log"), _LogMaxBytes, _LogMaxFiles, TimeProvider.System,
            out CliLogFile? opened, out string? logProblem))
        {
            logFile = opened;
            logFile.ProblemSink = text => _WriteLine(io, "Warning " + text);
        }
        else
        {
            io.Error.WriteLine("Warning: " + logProblem);
        }

        // 3. Application settings, plugins, profile settings; then the application is composed and the plugins start.
        SettingsManager application = SettingsManagerFactory.Create(settingsBase, ApplicationFolder, options.MaxConfigFileBytes,
            options.MaxJsonDepth);
        PluginRegistry registry = new();
        CliPluginSession? session = null;
        try
        {
            foreach (SettingsLoadWarning warning in application.Load())
            {
                _LogSession(io, logFile, "Warning", "settings", warning.Message);
            }

            PluginPathsSetting.Register(application);
            PluginTimeoutSettings.Register(application);
            IReadOnlyList<string> paths = PluginPathsSetting.Read(application.ReadOnly,
                text => _LogSession(io, logFile, "Warning", "settings", text));
            PluginLoader loader = new(new PluginLoaderOptions(Path.Combine(settingsBase, _ScanCacheFile),
                NetworkInspectorBuiltIns.CreateAll()));
            PluginLoadReport report = loader.Load(PluginTier.Core, paths, registry, cancellationToken);
            _LogFailures(io, logFile, registry.ApplySettings(SettingScope.Application, application));

            session = new CliPluginSession(options, settingsBase, profile, application, registry, report, io, logFile);
            session.ProfileSettings.Dispose();
            session.ProfileSettings = session._OpenProfileManager();
            session._LogLoadProblems();
            session.Report = await PluginLifecycle.StartAsync(report, registry.Disable, session._ContextFor,
                PluginTimeoutSettings.ReadStart(application.ReadOnly), cancellationToken).ConfigureAwait(false);
            return (session, 0);
        }
        catch (PersistenceSettingsException ex)
        {
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
        settingsBase = string.Empty;
        profile = SettingsProfiles.DefaultProfile;
        exitCode = (int)ExitCode.ArgumentError;
        try
        {
            settingsBase = Path.GetFullPath(SettingsManagerFactory.ResolvePath(options.SettingsPath, null));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            io.Error.WriteLine("Error: " + ex.Message);
            return false;
        }

        string requested = string.IsNullOrEmpty(options.Profile)
            ? SettingsProfiles.DefaultProfile
            : options.Profile;
        if (!ProfileNames.TryValidate(requested, out string? profileError))
        {
            io.Error.WriteLine(Invariant($"Error: Profile name '{requested}': {profileError}."));
            return false;
        }

        // An existing profile is used with the spelling it has on disk; a missing one is an error unless it is "default".
        string? existing = SettingsProfiles.List(settingsBase)
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
        if (!SettingsProfiles.TryCreate(settingsBase, SettingsProfiles.DefaultProfile, null, out string? createError)
            && !SettingsProfiles.Exists(settingsBase, SettingsProfiles.DefaultProfile))
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
    /// persisted values apply. The stack owns its own settings manager.
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
            manager = SettingsManagerFactory.Create(SettingsBase, Profile, Options.MaxConfigFileBytes, Options.MaxJsonDepth);
            _LogLoadWarnings(manager);
            StackBuilder builder = new(manager, frames) { IncludeExceptionStackTrace = false };
            _LogFailures(_Io, _LogFile, Registry.ApplyStackContributions(builder));
            _LogFailures(_Io, _LogFile, Registry.ApplySettings(SettingScope.Profile, manager));
            Stack built = builder.Build();

            // The stack owns the manager now; its diagnostics are reported once.
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

    /// <summary>Opens the profile manager that the settings command and the plugins read: files loaded, settings registered.</summary>
    private SettingsManager _OpenProfileManager()
    {
        SettingsManager manager = SettingsManagerFactory.Create(SettingsBase, Profile, Options.MaxConfigFileBytes, Options.MaxJsonDepth);
        try
        {
            _LogLoadWarnings(manager);

            // The stack builder registers the protocol settings into the manager; it is never built, it only fills the manager.
            StackBuilder builder = new(manager, new FrameInterfaceRegistry());
            _LogFailures(_Io, _LogFile, Registry.ApplyStackContributions(builder));
            _LogFailures(_Io, _LogFile, Registry.ApplySettings(SettingScope.Profile, manager));
            return manager;
        }
        catch
        {
            manager.Dispose();
            throw;
        }
    }

    /// <summary>Loads the persisted values of a manager and reports what was skipped.</summary>
    private void _LogLoadWarnings(SettingsManager manager)
    {
        foreach (SettingsLoadWarning warning in manager.Load())
        {
            _LogSession(_Io, _LogFile, "Warning", "settings", warning.Message);
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

    /// <summary>The context of a plugin, created once per name.</summary>
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

    /// <summary>Writes one line to the error writer (the writer is not thread-safe, and plugins log from other threads).</summary>
    private static void _WriteLine(CliIo io, string line)
    {
        lock (io.Error)
        {
            io.Error.WriteLine(line);
        }
    }

    /// <summary>Releases what a failed open already created.</summary>
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

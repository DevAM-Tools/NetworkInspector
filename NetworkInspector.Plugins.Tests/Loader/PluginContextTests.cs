// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>The core-tier context: identity, data folder, settings views, stop signal, forwarded events, and the plugin settings helpers.</summary>
internal sealed class PluginContextTests
{
    #region Data

    [Test]
    public async Task TheContextExposesNameLogDataFolderAndSettingViews()
    {
        using TempFolder folder = new("context-data");
        using SettingsManager application = new();
        using SettingsManager profile = new();
        CapturingPluginLog log = new();
        string data = folder.FilePath("plugins-data/Sample.Protocol");

        using PluginContext context = new("Sample.Protocol", log, data, application, profile);

        await Assert.That(context.PluginName).IsEqualTo("Sample.Protocol");
        await Assert.That(context.Log).IsSameReferenceAs(log);
        await Assert.That(context.DataDirectory).IsEqualTo(data);
        await Assert.That(Directory.Exists(data)).IsTrue();
        await Assert.That(context.ApplicationSettings.SettingCount).IsEqualTo(application.SettingCount);
        await Assert.That(context.Stopping.IsCancellationRequested).IsFalse();
        context.SignalStopping();
        context.SignalStopping();
        await Assert.That(context.Stopping.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task ABlockedDataFolderFailsOnlyTheReadOfDataDirectory()
    {
        using TempFolder temp = new("context-blocked");
        string blocker = temp.FilePath("plugins-data");
        File.WriteAllText(blocker, "not a folder");
        CapturingPluginLog log = new();
        using SettingsManager application = new();
        using SettingsManager profile = new();

        using PluginContext context = new("Sample.Protocol", log, Path.Combine(blocker, "Sample.Protocol"), application, profile);

        // The context exists and works; one warning says why, and only the read of the folder fails.
        await Assert.That(log.Entries.Count).IsEqualTo(1);
        await Assert.That(log.Entries[0].Level).IsEqualTo("Warning");
        await Assert.That(() => context.DataDirectory).Throws<IOException>();
        await Assert.That(context.PluginName).IsEqualTo("Sample.Protocol");
        await Assert.That(context.Stopping.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task AThrowingStoppingCallbackIsOneErrorEntryAndNeverAnExceptionForTheCaller()
    {
        using TempFolder folder = new("context-stopping");
        using SettingsManager application = new();
        using SettingsManager profile = new();
        CapturingPluginLog log = new();
        using PluginContext context = new("Sample.Protocol", log, folder.FilePath("data"), application, profile);
        bool secondCallbackRan = false;
        using CancellationTokenRegistration first = context.Stopping.Register(static () => throw new InvalidOperationException("callback boom"));
        using CancellationTokenRegistration second = context.Stopping.Register(() => secondCallbackRan = true);

        context.SignalStopping();

        await Assert.That(context.Stopping.IsCancellationRequested).IsTrue();
        await Assert.That(secondCallbackRan).IsTrue();
        await Assert.That(log.Entries.Count).IsEqualTo(1);
        await Assert.That(log.Entries[0].Text).IsEqualTo("A Stopping callback failed: InvalidOperationException: callback boom");
    }

    [Test]
    public async Task AThrowingStoppingCallbackWithAThrowingLogNeverBreaksTheShutdown()
    {
        using TempFolder folder = new("context-stopping-log");
        using SettingsManager application = new();
        using SettingsManager profile = new();
        using PluginContext context = new("Sample.Protocol", new ThrowingPluginLog(), folder.FilePath("data"), application, profile);
        bool secondCallbackRan = false;
        using CancellationTokenRegistration first = context.Stopping.Register(static () => throw new InvalidOperationException("callback boom"));
        using CancellationTokenRegistration second = context.Stopping.Register(() => secondCallbackRan = true);

        // The log is broken, so the failure of the callback cannot be reported; the signal must still end normally.
        context.SignalStopping();

        await Assert.That(context.Stopping.IsCancellationRequested).IsTrue();
        await Assert.That(secondCallbackRan).IsTrue();
    }

    [Test]
    public async Task AStoppingCallbackWithAHostileExceptionIsStillOneErrorEntry()
    {
        using TempFolder folder = new("context-stopping-hostile");
        using SettingsManager application = new();
        using SettingsManager profile = new();
        CapturingPluginLog log = new();
        using PluginContext context = new("Sample.Protocol", log, folder.FilePath("data"), application, profile);
        using CancellationTokenRegistration registration = context.Stopping.Register(static () => throw new HostileException());

        context.SignalStopping();

        await Assert.That(log.Entries.Count).IsEqualTo(1);
        await Assert.That(log.Entries[0].Text).IsEqualTo("A Stopping callback failed: HostileException: (the message could not be read)");
    }

    [Test]
    public async Task InvalidArgumentsAreRefused()
    {
        using TempFolder folder = new("context-args");
        using SettingsManager application = new();
        using SettingsManager profile = new();
        CapturingPluginLog log = new();

        await Assert.That(() => new PluginContext(string.Empty, log, folder.Path, application, profile)).Throws<ArgumentException>();
        await Assert.That(() => new PluginContext("A.B", log, "relative/data", application, profile)).Throws<ArgumentException>();
        await Assert.That(() => new PluginContext("A.B", null!, folder.Path, application, profile)).Throws<ArgumentNullException>(); // deliberate null
        await Assert.That(() => new PluginContext("A.B", log, folder.Path, null!, profile)).Throws<ArgumentNullException>(); // deliberate null
        await Assert.That(() => new PluginContext("A.B", log, folder.Path, application, null!)).Throws<ArgumentNullException>(); // deliberate null
    }

    [Test]
    public async Task PluginSettingsAreRegisteredOnceWithTheirDefaults()
    {
        using SettingsManager manager = new();

        PluginPathsSetting.Register(manager);
        PluginPathsSetting.Register(manager);
        PluginTimeoutSettings.Register(manager);
        PluginTimeoutSettings.Register(manager);

        ReadOnlySettingsManagerView view = manager.ReadOnly;
        await Assert.That(manager.SettingCount).IsEqualTo(3);
        await Assert.That(manager.GetGroup("plugins")).IsNotNull();
        await Assert.That(view.GetStringArraySetting("plugins.paths")).IsEmpty();
        await Assert.That(view.GetI64Setting("plugins.startTimeoutSeconds")).IsEqualTo(120L);
        await Assert.That(view.GetI64Setting("plugins.stopTimeoutSeconds")).IsEqualTo(60L);
        await Assert.That(PluginTimeoutSettings.ReadStart(view)).IsEqualTo(TimeSpan.FromSeconds(120));
        await Assert.That(PluginTimeoutSettings.ReadStop(view)).IsEqualTo(TimeSpan.FromSeconds(60));
        Setting start = Expect.NotNull(manager.GetSetting("plugins.startTimeoutSeconds"));
        await Assert.That(start.MinValue).IsEqualTo(SettingValue.I64(1));
        await Assert.That(start.MaxValue).IsEqualTo(SettingValue.I64(2_147_483));
    }

    [Test]
    public async Task ThePathsSettingKeepsFullPathsAndReportsRelativeOnesOnce()
    {
        using TempFolder folder = new("context-paths");
        using SettingsManager manager = new();
        PluginPathsSetting.Register(manager);
        Setting paths = Expect.NotNull(manager.GetSetting("plugins.paths"));
        paths.SetPendingValue(SettingValue.StringArray([folder.Path + " ", "relative/folder", " ", folder.FilePath("a")]));
        manager.ApplyChanges();
        List<string> warnings = [];

        IReadOnlyList<string> read = PluginPathsSetting.Read(manager.ReadOnly, warnings.Add);

        await Assert.That(read).IsEquivalentTo([folder.Path, folder.FilePath("a")]);
        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0]).IsEqualTo("plugins.paths: 'relative/folder' is not a full path and is ignored");
    }

    #endregion

    #region Events

    [Test]
    public async Task SettingsAppliedIsForwardedFromBothManagersAndAThrowingHandlerIsOneErrorEntry()
    {
        using TempFolder folder = new("context-events");
        using SettingsManager application = new();
        using SettingsManager profile = new();
        PluginTimeoutSettings.Register(application);
        new SettingsRegistrar(profile).RegisterI64Setting("profile.level", "Level", string.Empty, 1);
        CapturingPluginLog log = new();
        using PluginContext context = new("Sample.Protocol", log, folder.FilePath("data"), application, profile);
        List<string> second = [];
        context.SettingsApplied += (_, _) => throw new InvalidOperationException("handler boom");
        context.SettingsApplied += (_, args) => second.AddRange(args.ChangedSettings);

        Expect.NotNull(application.GetSetting("plugins.startTimeoutSeconds")).SetPendingValue(SettingValue.I64(5));
        application.ApplyChanges();
        Expect.NotNull(profile.GetSetting("profile.level")).SetPendingValue(SettingValue.I64(7));
        profile.ApplyChanges();

        // Both applies raised the event; the first handler's failure is one error entry per raise, the second handler still ran.
        IReadOnlyList<LogEntry> errors = [.. log.Entries.Where(static entry => entry.Level == "Error")];
        await Assert.That(errors.Count).IsEqualTo(2);
        await Assert.That(errors[0].Text).IsEqualTo("SettingsApplied handler failed: InvalidOperationException: handler boom");
        await Assert.That(second).IsEquivalentTo(_ChangedNamesBoth, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AThrowingHandlerWithAThrowingLogNeverBreaksTheCallerOrTheOtherHandlers()
    {
        using TempFolder folder = new("context-events-log");
        using SettingsManager application = new();
        using SettingsManager profile = new();
        PluginTimeoutSettings.Register(application);
        using PluginContext context = new("Sample.Protocol", new ThrowingPluginLog(), folder.FilePath("data"), application, profile);
        int secondRan = 0;
        context.SettingsApplied += (_, _) => throw new InvalidOperationException("handler boom");
        context.SettingsApplied += (_, _) => secondRan++;

        // The log is broken, so the failure of the first handler cannot be reported; the apply must still end normally.
        Expect.NotNull(application.GetSetting("plugins.startTimeoutSeconds")).SetPendingValue(SettingValue.I64(5));
        application.ApplyChanges();

        await Assert.That(secondRan).IsEqualTo(1);
    }

    [Test]
    public async Task NothingIsRaisedAfterDispose()
    {
        using TempFolder folder = new("context-dispose");
        using SettingsManager application = new();
        using SettingsManager profile = new();
        PluginTimeoutSettings.Register(application);
        CapturingPluginLog log = new();
        PluginContext context = new("Sample.Protocol", log, folder.FilePath("data"), application, profile);
        int raised = 0;
        context.SettingsApplied += (_, _) => raised++;
        context.Dispose();

        Expect.NotNull(application.GetSetting("plugins.startTimeoutSeconds")).SetPendingValue(SettingValue.I64(5));
        application.ApplyChanges();

        await Assert.That(raised).IsEqualTo(0);
        context.Dispose();
    }

    [Test]
    public async Task SubscribingAndUnsubscribingFromManyThreadsWhileSettingsAreAppliedIsSafe()
    {
        using TempFolder folder = new("context-threads");
        using SettingsManager application = new();
        using SettingsManager profile = new();
        PluginTimeoutSettings.Register(application);
        CapturingPluginLog log = new();
        using PluginContext context = new("Sample.Protocol", log, folder.FilePath("data"), application, profile);
        using Barrier start = new(9);

        Task[] subscribers = new Task[8];
        for (int worker = 0; worker < subscribers.Length; worker++)
        {
            subscribers[worker] = Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                for (int loop = 0; loop < 500; loop++)
                {
                    EventHandler<SettingsAppliedEventArgs> handler = static (_, _) => { };
                    context.SettingsApplied += handler;
                    context.SettingsApplied -= handler;
                }
            }, TaskCreationOptions.LongRunning);
        }

        Task applier = Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            Setting setting = Expect.NotNull(application.GetSetting("plugins.startTimeoutSeconds"));
            for (int loop = 0; loop < 500; loop++)
            {
                setting.SetPendingValue(SettingValue.I64(2 + (loop % 2)));
                application.ApplyChanges();
            }
        }, TaskCreationOptions.LongRunning);

        await Task.WhenAll([.. subscribers, applier]);

        await Assert.That(log.Entries.Count(static entry => entry.Level == "Error")).IsEqualTo(0);
    }

    #endregion

    #region Constants

    private static readonly string[] _ChangedNamesBoth = ["plugins.startTimeoutSeconds", "profile.level"];

    #endregion
}

